using System.Runtime.InteropServices;
using System.Windows.Threading;
using LibreBooth.Core.Config;
using LibreBooth.Core.Hardware;
using Serilog;
using static LibreBooth.App.Hardware.Canon.EdsdkNative;

namespace LibreBooth.App.Hardware.Canon;

/// <summary>
/// Appareil Canon EOS (testé pour le 1300D) piloté par le SDK Canon.
/// Tous les appels au SDK passent par un seul thread dédié, avec sa propre boucle de messages (exigence Canon).
/// La connexion est surveillée en permanence : un débranchement est détecté et l'appareil se reconnecte seul.
/// </summary>
internal sealed class CanonCamera : ICamera
{
    private readonly ILogger _log = Log.ForContext<CanonCamera>();
    private readonly Func<CameraSettings> _settings;
    private readonly ManualResetEventSlim _threadReady = new(false);

    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _eventTimer;
    private DispatcherTimer? _detectTimer;
    private DispatcherTimer? _keepAliveTimer;
    private DispatcherTimer? _evfTimer;

    private IntPtr _camera = IntPtr.Zero;
    private bool _sdkReady;
    private bool _sessionOpen;
    private volatile bool _liveView;
    private volatile bool _disposed;
    private int _evfErrors;
    private bool _ignoredNonJpeg;
    private string _lastDetectMessage = "";
    private TaskCompletionSource<byte[]>? _pendingCapture;

    // Les fonctions de rappel restent référencées tant que le SDK est actif (sinon le ramasse-miettes les supprimerait).
    private EdsObjectEventHandler? _objectHandler;
    private EdsPropertyEventHandler? _propertyHandler;
    private EdsStateEventHandler? _stateHandler;
    private EdsCameraAddedHandler? _cameraAddedHandler;

    public CanonCamera(Func<CameraSettings> settings)
    {
        _settings = settings;
    }

    public string Name { get; private set; } = "Canon EOS";
    public CameraState State { get; private set; } = CameraState.Connecting;
    public string StatusMessage { get; private set; } = "Recherche de l'appareil photo…";
    public int? BatteryLevel { get; private set; }
    public bool IsLiveViewActive => _liveView;

    public event EventHandler<CameraStatusEventArgs>? StatusChanged;
    public event EventHandler<LiveViewFrameEventArgs>? LiveViewFrame;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "Canon EDSDK" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _threadReady.Wait(TimeSpan.FromSeconds(5));
    }

    private void ThreadMain()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _dispatcher.UnhandledException += (_, e) =>
        {
            _log.Error(e.Exception, "Erreur sur le fil de l'appareil photo");
            e.Handled = true;
        };
        _threadReady.Set();
        _dispatcher.BeginInvoke(InitializeSdk);
        Dispatcher.Run();
    }

    private void InitializeSdk()
    {
        try
        {
            var error = EdsInitializeSDK();
            if (error != EDS_ERR_OK)
            {
                SetState(CameraState.Error, "Le pilote Canon n'a pas démarré : " + EdsdkErrors.Describe(error));
                return;
            }

            _sdkReady = true;
            _objectHandler = OnObjectEvent;
            _propertyHandler = OnPropertyEvent;
            _stateHandler = OnStateEvent;
            _cameraAddedHandler = OnCameraAdded;
            EdsSetCameraAddedHandler(_cameraAddedHandler, IntPtr.Zero);

            _eventTimer = StartTimer(TimeSpan.FromMilliseconds(100), () => EdsGetEvent());
            _detectTimer = StartTimer(TimeSpan.FromSeconds(2), TryConnect);
            _keepAliveTimer = StartTimer(TimeSpan.FromSeconds(60), KeepAlive);
            // Intervalle minimal : on redemande une image dès que la précédente est arrivée (le transfert USB fixe le rythme).
            _evfTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher!) { Interval = TimeSpan.FromMilliseconds(15) };
            _evfTimer.Tick += (_, _) => DownloadEvfFrame();

            _log.Information("Pilote Canon (EDSDK) initialisé");
            TryConnect();
        }
        catch (DllNotFoundException ex)
        {
            _log.Error(ex, "EDSDK.dll introuvable");
            SetState(CameraState.Error,
                $"Pilote Canon introuvable : copiez EDSDK.dll et EdsImage.dll (SDK Canon EDSDK, version 32 bits) dans {NativeDrivers.UserFolder}, puis relancez LibreBooth.");
        }
        catch (BadImageFormatException ex)
        {
            _log.Error(ex, "EDSDK.dll incompatible");
            SetState(CameraState.Error, "Pilote Canon incompatible : il faut la version 32 bits d'EDSDK.dll.");
        }
        catch (EntryPointNotFoundException ex)
        {
            _log.Error(ex, "Version d'EDSDK.dll incompatible");
            SetState(CameraState.Error, "Version du pilote Canon incompatible (EDSDK 13.x attendu).");
        }
    }

    private DispatcherTimer StartTimer(TimeSpan interval, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher!) { Interval = interval };
        timer.Tick += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Erreur dans une tâche périodique de l'appareil photo");
            }
        };
        timer.Start();
        return timer;
    }

    // ---------------------------------------------------------------- Connexion

    private uint OnCameraAdded(IntPtr context)
    {
        _dispatcher?.BeginInvoke(TryConnect);
        return EDS_ERR_OK;
    }

    private void TryConnect()
    {
        if (!_sdkReady || _sessionOpen || _disposed) return;

        var list = IntPtr.Zero;
        var camera = IntPtr.Zero;
        try
        {
            var error = EdsGetCameraList(out list);
            if (error != EDS_ERR_OK)
            {
                ReportDetection(CameraState.Disconnected, "Recherche de l'appareil impossible : " + EdsdkErrors.Describe(error));
                return;
            }

            EdsGetChildCount(list, out var count);
            if (count <= 0)
            {
                ReportDetection(CameraState.Disconnected, "Aucun appareil Canon détecté : vérifiez qu'il est allumé et branché en USB (Wi-Fi coupé).");
                return;
            }

            error = EdsGetChildAtIndex(list, 0, out camera);
            if (error != EDS_ERR_OK || camera == IntPtr.Zero)
            {
                ReportDetection(CameraState.Disconnected, "Appareil détecté mais inaccessible : " + EdsdkErrors.Describe(error));
                return;
            }

            EdsGetDeviceInfo(camera, out var info);
            var description = string.IsNullOrWhiteSpace(info.szDeviceDescription) ? "Canon EOS" : info.szDeviceDescription.Trim();
            SetState(CameraState.Connecting, $"Connexion à {description}…");

            EdsSetObjectEventHandler(camera, ObjectEvent_All, _objectHandler!, IntPtr.Zero);
            EdsSetPropertyEventHandler(camera, PropertyEvent_All, _propertyHandler!, IntPtr.Zero);
            EdsSetCameraStateEventHandler(camera, StateEvent_All, _stateHandler!, IntPtr.Zero);

            error = EdsOpenSession(camera);
            if (error != EDS_ERR_OK)
            {
                ReportDetection(CameraState.Disconnected, EdsdkErrors.ForSession(error));
                return;
            }

            _camera = camera;
            camera = IntPtr.Zero;
            _sessionOpen = true;
            Name = description;
            ConfigureSession();
            ReadBattery();
            _lastDetectMessage = "";
            _log.Information("Appareil connecté : {Name}", Name);
            LogCameraSettings();
            SetState(CameraState.Ready, $"{Name} prêt");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Connexion à l'appareil impossible");
            ReportDetection(CameraState.Error, "Connexion à l'appareil impossible : " + ex.Message);
        }
        finally
        {
            if (camera != IntPtr.Zero) EdsRelease(camera);
            if (list != IntPtr.Zero) EdsRelease(list);
        }
    }

    private void ConfigureSession()
    {
        var saveTo = _settings().SaveToCardToo ? SaveTo_Both : SaveTo_Host;
        var error = SetUInt32(PropID_SaveTo, saveTo);
        if (error != EDS_ERR_OK) _log.Warning("Réglage « enregistrer sur le PC » refusé : {Error}", EdsdkErrors.Describe(error));

        // Le SDK exige d'annoncer de la place libre côté PC, sinon l'appareil refuse de déclencher.
        var capacity = new EdsCapacity { NumberOfFreeClusters = 0x7FFFFFFF, BytesPerSector = 0x1000, Reset = 1 };
        error = EdsSetCapacity(_camera, capacity);
        if (error != EDS_ERR_OK) _log.Warning("Annonce de la capacité refusée : {Error}", EdsdkErrors.Describe(error));
    }

    private void ReportDetection(CameraState state, string message)
    {
        if (message == _lastDetectMessage && state == State) return;
        _lastDetectMessage = message;
        _log.Information("Appareil photo : {Message}", message);
        SetState(state, message);
    }

    private void CloseSession(string reason)
    {
        _evfTimer?.Stop();
        _liveView = false;
        FailPendingCapture(reason);
        if (_camera != IntPtr.Zero)
        {
            if (_sessionOpen) EdsCloseSession(_camera);
            EdsRelease(_camera);
        }
        _camera = IntPtr.Zero;
        _sessionOpen = false;
        BatteryLevel = null;
        _lastDetectMessage = reason;
        _log.Warning("Appareil déconnecté : {Reason}", reason);
        SetState(CameraState.Disconnected, reason + " Reconnexion automatique…");
    }

    private void KeepAlive()
    {
        if (!_sessionOpen) return;
        EdsSendCommand(_camera, CameraCommand_ExtendShutDownTimer, 0);
        ReadBattery();
    }

    private void ReadBattery()
    {
        if (!_sessionOpen) return;
        if (EdsGetPropertyData(_camera, PropID_BatteryLevel, 0, sizeof(uint), out uint level) == EDS_ERR_OK)
        {
            BatteryLevel = level == BatteryLevel_AC ? -1 : (int)Math.Min(level, 100u);
        }
    }

    // ---------------------------------------------------------------- Événements du SDK

    private uint OnStateEvent(uint inEvent, uint parameter, IntPtr context)
    {
        switch (inEvent)
        {
            case StateEvent_Shutdown:
                _dispatcher?.BeginInvoke(() => CloseSession("L'appareil photo a été éteint ou débranché."));
                break;
            case StateEvent_WillSoonShutDown:
                _dispatcher?.BeginInvoke(() =>
                {
                    if (_sessionOpen) EdsSendCommand(_camera, CameraCommand_ExtendShutDownTimer, 0);
                });
                break;
            case StateEvent_CaptureError:
                _dispatcher?.BeginInvoke(() => FailPendingCapture("L'appareil n'a pas pu prendre la photo (mise au point ?)."));
                break;
            case StateEvent_InternalError:
                _log.Warning("Erreur interne signalée par l'appareil ({Parameter})", parameter);
                break;
        }
        return EDS_ERR_OK;
    }

    private uint OnObjectEvent(uint inEvent, IntPtr inRef, IntPtr context)
    {
        if (inEvent == ObjectEvent_DirItemRequestTransfer)
        {
            // Traité après le retour du rappel : on ne rappelle jamais le SDK depuis l'intérieur d'un rappel.
            _dispatcher?.BeginInvoke(() => DownloadDirectoryItem(inRef));
        }
        else if (inRef != IntPtr.Zero)
        {
            EdsRelease(inRef);
        }
        return EDS_ERR_OK;
    }

    private uint OnPropertyEvent(uint inEvent, uint propertyId, uint parameter, IntPtr context) => EDS_ERR_OK;

    // ---------------------------------------------------------------- Prise de vue

    public async Task<byte[]> CaptureAsync(bool allowAutofocus, CancellationToken cancellationToken)
    {
        var dispatcher = _dispatcher ?? throw new CameraException("Appareil photo non démarré.");
        var useAutofocus = allowAutofocus && _settings().UseAutofocus;
        var capture = await dispatcher.InvokeAsync(() => BeginCapture(useAutofocus));
        var timeout = TimeSpan.FromSeconds(_settings().CaptureTimeoutSeconds);
        try
        {
            return await capture.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var rawOnly = await dispatcher.InvokeAsync(() =>
            {
                _pendingCapture = null;
                var ignored = _ignoredNonJpeg;
                _ignoredNonJpeg = false;
                return ignored;
            });
            throw new CameraException(rawOnly
                ? "L'appareil est réglé en RAW seul : choisissez une qualité JPEG dans son menu."
                : "L'appareil n'a pas envoyé la photo à temps (mise au point impossible ou appareil occupé).");
        }
    }

    private Task<byte[]> BeginCapture(bool autofocus)
    {
        if (!_sessionOpen) throw new CameraException("Appareil photo non connecté.");

        _pendingCapture?.TrySetCanceled();
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCapture = completion;
        _ignoredNonJpeg = false;

        var press = autofocus ? ShutterButton_Completely : ShutterButton_Completely_NonAF;
        var error = SendCommand(CameraCommand_PressShutterButton, press);
        if (error == EDS_ERR_NOT_SUPPORTED)
        {
            error = SendCommand(CameraCommand_TakePicture, 0);
        }
        else
        {
            // On relâche toujours le déclencheur, même si l'appui a échoué.
            SendCommand(CameraCommand_PressShutterButton, ShutterButton_OFF);
        }

        if (error != EDS_ERR_OK)
        {
            _pendingCapture = null;
            _log.Warning("Déclenchement refusé : {Error}", EdsdkErrors.Describe(error));
            throw new CameraException(EdsdkErrors.ForCapture(error));
        }

        _log.Information("Déclenchement envoyé (autofocus : {Autofocus})", autofocus);
        return completion.Task;
    }

    private uint SendCommand(uint command, int parameter)
    {
        uint error = EDS_ERR_OK;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            error = EdsSendCommand(_camera, command, parameter);
            if (error != EDS_ERR_DEVICE_BUSY) return error;
            Thread.Sleep(100);
            EdsGetEvent();
        }
        return error;
    }

    private void DownloadDirectoryItem(IntPtr item)
    {
        var stream = IntPtr.Zero;
        try
        {
            var error = EdsGetDirectoryItemInfo(item, out var info);
            if (error != EDS_ERR_OK) throw new CameraException("Lecture de la photo impossible : " + EdsdkErrors.Describe(error));

            var fileName = info.szFileName ?? "";
            var isJpeg = fileName.EndsWith(".JPG", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".JPEG", StringComparison.OrdinalIgnoreCase);
            if (!isJpeg)
            {
                // RAW+JPEG : on ignore le RAW et on attend le JPEG qui suit.
                _ignoredNonJpeg = true;
                _log.Information("Fichier ignoré (pas un JPEG) : {File}", fileName);
                EdsDownloadCancel(item);
                return;
            }

            error = EdsCreateMemoryStream(0, out stream);
            if (error != EDS_ERR_OK) throw new CameraException("Mémoire insuffisante pour recevoir la photo.");
            error = EdsDownload(item, info.Size, stream);
            if (error != EDS_ERR_OK) throw new CameraException("Transfert de la photo interrompu : " + EdsdkErrors.Describe(error));
            error = EdsDownloadComplete(item);
            if (error != EDS_ERR_OK) _log.Warning("Fin de transfert signalée avec : {Error}", EdsdkErrors.Describe(error));

            EdsGetPointer(stream, out var pointer);
            EdsGetLength(stream, out var length);
            if (pointer == IntPtr.Zero || length == 0) throw new CameraException("L'appareil a envoyé une photo vide.");

            var bytes = new byte[checked((int)length)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            _log.Information("Photo reçue : {File} ({Size} Ko)", fileName, bytes.Length / 1024);

            var pending = _pendingCapture;
            _pendingCapture = null;
            if (pending != null)
            {
                pending.TrySetResult(bytes);
            }
            else
            {
                _log.Information("Photo prise en dehors d'une session (déclencheur de l'appareil ?) : ignorée");
            }

            if (_liveView) ReapplyEvfOutput();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Transfert de la photo impossible");
            FailPendingCapture(ex is CameraException ? ex.Message : "Transfert de la photo impossible : " + ex.Message);
        }
        finally
        {
            if (stream != IntPtr.Zero) EdsRelease(stream);
            EdsRelease(item);
        }
    }

    private void FailPendingCapture(string message)
    {
        var pending = _pendingCapture;
        _pendingCapture = null;
        pending?.TrySetException(new CameraException(message));
    }

    // ---------------------------------------------------------------- Live view

    public Task StartLiveViewAsync() => RunOnCameraThread(() =>
    {
        if (!_sessionOpen) throw new CameraException("Appareil photo non connecté.");
        var error = EnableEvfOutput();
        if (error != EDS_ERR_OK)
        {
            throw new CameraException("Le live view n'a pas démarré : " + EdsdkErrors.Describe(error)
                                      + ". Vérifiez dans le menu de l'appareil que la prise de vue avec visée par l'écran (live view) est activée.");
        }
        _evfErrors = 0;
        _liveView = true;
        _evfTimer?.Start();
    });

    /// <summary>
    /// Envoie l'image du live view vers le PC. Les reflex répondent souvent « occupé » juste après la connexion
    /// ou une photo : on insiste quelques secondes. Si la fonction live view est désactivée dans le menu de
    /// l'appareil (Evf_Mode = 0), on l'active par le SDK.
    /// </summary>
    private uint EnableEvfOutput()
    {
        var mode = GetUInt32(PropID_Evf_Mode);
        if (mode == 0)
        {
            var modeError = SetUInt32Patiently(PropID_Evf_Mode, 1, TimeSpan.FromSeconds(3));
            _log.Information("Fonction live view désactivée dans l'appareil : activation par le SDK → {Result}", EdsdkErrors.Describe(modeError));
        }

        var device = GetUInt32(PropID_Evf_OutputDevice) ?? 0;
        var error = SetUInt32Patiently(PropID_Evf_OutputDevice, device | EvfOutputDevice_PC, TimeSpan.FromSeconds(6));
        if (error == EDS_ERR_DEVICE_BUSY && mode != 0)
        {
            // Dernier essai : certains boîtiers ne débloquent le live view qu'après une nouvelle activation.
            SetUInt32Patiently(PropID_Evf_Mode, 1, TimeSpan.FromSeconds(2));
            error = SetUInt32Patiently(PropID_Evf_OutputDevice, device | EvfOutputDevice_PC, TimeSpan.FromSeconds(4));
        }

        _log.Information("Démarrage du live view : {Result} (Evf_Mode={Mode}, sortie={Device})",
            EdsdkErrors.Describe(error), Hex(GetUInt32(PropID_Evf_Mode)), Hex(GetUInt32(PropID_Evf_OutputDevice)));
        return error;
    }

    public Task StopLiveViewAsync() => RunOnCameraThread(() =>
    {
        _evfTimer?.Stop();
        if (!_liveView) return;
        _liveView = false;
        if (!_sessionOpen) return;
        var device = GetUInt32(PropID_Evf_OutputDevice) ?? EvfOutputDevice_PC;
        SetUInt32(PropID_Evf_OutputDevice, device & ~EvfOutputDevice_PC);
    });

    private void DownloadEvfFrame()
    {
        if (!_liveView || !_sessionOpen) return;
        var stream = IntPtr.Zero;
        var evfImage = IntPtr.Zero;
        try
        {
            if (EdsCreateMemoryStream(0, out stream) != EDS_ERR_OK) return;
            if (EdsCreateEvfImageRef(stream, out evfImage) != EDS_ERR_OK) return;

            var error = EdsDownloadEvfImage(_camera, evfImage);
            if (error is EDS_ERR_OBJECT_NOTREADY or EDS_ERR_DEVICE_BUSY) return;
            if (error != EDS_ERR_OK)
            {
                if (++_evfErrors % 40 == 0)
                {
                    _log.Warning("Live view interrompu : {Error}", EdsdkErrors.Describe(error));
                    ReapplyEvfOutput();
                }
                return;
            }

            _evfErrors = 0;
            EdsGetPointer(stream, out var pointer);
            EdsGetLength(stream, out var length);
            if (pointer == IntPtr.Zero || length == 0 || length > 20_000_000) return;
            var bytes = new byte[(int)length];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            LiveViewFrame?.Invoke(this, new LiveViewFrameEventArgs(bytes));
        }
        finally
        {
            if (evfImage != IntPtr.Zero) EdsRelease(evfImage);
            if (stream != IntPtr.Zero) EdsRelease(stream);
        }
    }

    /// <summary>Après une photo, certains boîtiers rendent l'affichage à leur écran : on le redemande.</summary>
    private void ReapplyEvfOutput()
    {
        if (!_sessionOpen || !_liveView) return;
        var device = GetUInt32(PropID_Evf_OutputDevice) ?? 0;
        if ((device & EvfOutputDevice_PC) == 0) SetUInt32(PropID_Evf_OutputDevice, device | EvfOutputDevice_PC);
    }

    // ---------------------------------------------------------------- Outils

    private uint SetUInt32(uint propertyId, uint value)
    {
        uint error = EDS_ERR_OK;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var data = value;
            error = EdsSetPropertyData(_camera, propertyId, 0, sizeof(uint), ref data);
            if (error != EDS_ERR_DEVICE_BUSY) return error;
            Thread.Sleep(100);
        }
        return error;
    }

    /// <summary>Comme SetUInt32, mais en insistant plus longtemps tant que l'appareil répond « occupé ».</summary>
    private uint SetUInt32Patiently(uint propertyId, uint value, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        uint error;
        do
        {
            var data = value;
            error = EdsSetPropertyData(_camera, propertyId, 0, sizeof(uint), ref data);
            if (error != EDS_ERR_DEVICE_BUSY) return error;
            Thread.Sleep(150);
            EdsGetEvent();
        }
        while (DateTime.UtcNow < deadline);
        return error;
    }

    private uint? GetUInt32(uint propertyId) =>
        EdsGetPropertyData(_camera, propertyId, 0, sizeof(uint), out uint value) == EDS_ERR_OK ? value : null;

    private static string Hex(uint? value) => value.HasValue ? $"0x{value.Value:X}" : "?";

    /// <summary>Note dans le journal les réglages utiles au diagnostic (mode, qualité, live view).</summary>
    private void LogCameraSettings() =>
        _log.Information("Réglages de l'appareil : mode={Mode}, qualité={Quality}, Evf_Mode={EvfMode}, sortie live view={Device}",
            Hex(GetUInt32(PropID_AEMode)), Hex(GetUInt32(PropID_ImageQuality)), Hex(GetUInt32(PropID_Evf_Mode)), Hex(GetUInt32(PropID_Evf_OutputDevice)));

    private Task RunOnCameraThread(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null || _disposed) return Task.CompletedTask;
        return dispatcher.InvokeAsync(action).Task;
    }

    private void SetState(CameraState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke(this, new CameraStatusEventArgs(state, message));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var dispatcher = _dispatcher;
        if (dispatcher != null)
        {
            try
            {
                dispatcher.Invoke(() =>
                {
                    _eventTimer?.Stop();
                    _detectTimer?.Stop();
                    _keepAliveTimer?.Stop();
                    _evfTimer?.Stop();
                    if (_sessionOpen && _liveView) SetUInt32(PropID_Evf_OutputDevice, EvfOutputDevice_TFT);
                    _liveView = false;
                    if (_camera != IntPtr.Zero)
                    {
                        if (_sessionOpen) EdsCloseSession(_camera);
                        EdsRelease(_camera);
                        _camera = IntPtr.Zero;
                        _sessionOpen = false;
                    }
                    if (_sdkReady)
                    {
                        EdsTerminateSDK();
                        _sdkReady = false;
                    }
                }, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Arrêt propre du pilote Canon impossible");
            }
            dispatcher.InvokeShutdown();
        }
        _thread?.Join(TimeSpan.FromSeconds(3));
    }
}
