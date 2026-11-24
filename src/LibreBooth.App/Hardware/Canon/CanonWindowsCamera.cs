using System.Collections.Concurrent;
using System.Diagnostics;
using LibreBooth.Core.Config;
using LibreBooth.Core.Hardware;
using Serilog;
using static LibreBooth.Core.Hardware.CanonPtp;

namespace LibreBooth.App.Hardware.Canon;

/// <summary>
/// Canon EOS piloté par le pilote intégré à Windows, sans le SDK Canon (choix par défaut depuis la 3.1.0). Les
/// commandes propres aux Canon EOS, celles qu'utilise gPhoto2, passent par les « extensions MTP » du pilote de
/// Microsoft (<see cref="WpdPtpDevice"/>). Essayé sur un EOS 1300D : live view, photos avec et sans autofocus.
/// Tout passe par un seul fil dédié, qui enchaîne les travaux demandés (live view, photo), les images du live view,
/// la lecture des événements de l'appareil et son maintien en éveil. Tant que l'appareil n'est pas connecté, ce fil
/// le cherche toutes les 2 secondes : il se reconnecte seul après un débranchement.
/// </summary>
internal sealed class CanonWindowsCamera : ICamera
{
    private const int DetectIntervalMs = 2000;
    private const int IdlePollMs = 500;
    private const int LiveViewPollMs = 400;
    private const int KeepAliveMs = 60_000;
    /// <summary>Sans image pendant ce temps, le live view est redemandé à l'appareil.</summary>
    private const int LiveViewStallMs = 3000;
    private const int MaxFailures = 3;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    private const string NotSupportedMessage =
        "Appareil Canon détecté, mais Windows ne sait pas le piloter : choisissez « SDK Canon » dans admin › Appareil photo.";

    private readonly ILogger _log = Log.ForContext<CanonWindowsCamera>();
    private readonly Func<CameraSettings> _settings;
    private readonly ConcurrentQueue<Action> _jobs = new();
    private readonly AutoResetEvent _wake = new(false);
    // Réglages de l'appareil, tenus à jour par ses événements (le premier relevé les donne tous).
    private readonly Dictionary<uint, uint> _properties = new();
    // Fichiers que l'appareil demande d'envoyer au PC, dans l'ordre de ses annonces.
    private readonly List<CanonPtpObject> _transfers = new();

    private Thread? _thread;
    private volatile bool _stopping;
    private volatile bool _liveView;
    private WpdPtpDevice? _device;
    private HashSet<ushort> _operations = new();
    private string? _rejectedDeviceId;
    private string _lastDetectMessage = "";
    private uint? _cameraStatus;
    private int _failures;
    private int _liveViewRefusals;
    private long _nextDetect;
    private long _nextPoll;
    private long _nextKeepAlive;
    private long _lastFrame;
    private long _lastReapply;
    private bool _unknownBatteryLogged;

    public CanonWindowsCamera(Func<CameraSettings> settings)
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

    private static long Now => Environment.TickCount64;

    public void Start()
    {
        if (_thread != null || _stopping) return;
        // Les objets du pilote Windows acceptent les deux modèles de fil : en MTA, pas besoin de boucle de messages.
        _thread = new Thread(Run) { IsBackground = true, Name = "Canon (pilote Windows)" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    // ---------------------------------------------------------------- Boucle du fil de l'appareil

    private void Run()
    {
        _log.Information("Appareil photo Canon piloté par le pilote de Windows (sans SDK Canon)");
        while (!_stopping)
        {
            while (!_stopping && _jobs.TryDequeue(out var job)) job();
            if (_stopping) break;

            var wait = 100;
            try
            {
                wait = _device == null ? Detect() : Serve();
            }
            catch (Exception ex)
            {
                OnCommunicationError(ex);
            }
            if (wait > 0) _wake.WaitOne(wait);
        }
        Shutdown();
    }

    /// <summary>Recherche de l'appareil, toutes les 2 secondes. Renvoie l'attente avant le tour suivant (ms).</summary>
    private int Detect()
    {
        if (Now >= _nextDetect)
        {
            _nextDetect = Now + DetectIntervalMs;
            TryConnect();
            if (_device != null) return 0;
        }
        return (int)Math.Clamp(_nextDetect - Now, 10, DetectIntervalMs);
    }

    /// <summary>Appareil connecté : image du live view, événements, maintien en éveil. Renvoie l'attente (ms).</summary>
    private int Serve()
    {
        var gotFrame = _liveView && FetchLiveViewFrame();

        if (_device != null && Now >= _nextPoll)
        {
            _nextPoll = Now + (_liveView ? LiveViewPollMs : IdlePollMs);
            PollEvents();
            if (_transfers.Count > 0) DiscardTransfers();
            if (_liveView) CheckLiveViewOutput();
        }

        if (_device != null && Now >= _nextKeepAlive)
        {
            _nextKeepAlive = Now + KeepAliveMs;
            KeepAlive();
        }

        if (_device != null && _liveView) return gotFrame ? 0 : 25;
        return (int)Math.Clamp(_nextPoll - Now, 10, IdlePollMs);
    }

    // ---------------------------------------------------------------- Connexion

    private void TryConnect()
    {
        var devices = Wpd.ListDevices();
        if (_rejectedDeviceId != null && devices.All(d => d.Id != _rejectedDeviceId)) _rejectedDeviceId = null;

        var canons = devices.Where(d => d.IsCanon).ToList();
        if (canons.Count == 0)
        {
            ReportDetection(CameraState.Disconnected, "Aucun appareil Canon détecté : vérifiez qu'il est allumé et branché en USB (Wi-Fi coupé).");
            return;
        }

        foreach (var info in canons.Where(d => d.Id != _rejectedDeviceId))
        {
            if (Connect(info)) return;
        }
        if (_rejectedDeviceId != null) ReportDetection(CameraState.Error, NotSupportedMessage);
    }

    private bool Connect(WpdDeviceInfo info)
    {
        var name = string.IsNullOrWhiteSpace(info.FriendlyName) ? "Canon EOS" : info.FriendlyName.Trim();
        WpdPtpDevice? device = null;
        try
        {
            device = WpdPtpDevice.Open(info.Id, "LibreBooth");
            var operations = device.VendorOperations();
            if (!IsUsableEos(operations))
            {
                if (_rejectedDeviceId != info.Id)
                {
                    _log.Warning("{Name} : le pilote de Windows ne laisse pas passer les commandes Canon EOS ({Count} commandes Canon)",
                        name, operations.Count);
                }
                _rejectedDeviceId = info.Id;
                device.Dispose();
                return false;
            }

            SetState(CameraState.Connecting, $"Connexion à {name}…");
            _device = device;
            device = null;
            _operations = operations;
            _properties.Clear();
            _transfers.Clear();
            _cameraStatus = null;

            Require(_device.Execute(OpSetRemoteMode, 1), "le pilotage par le PC");
            Require(_device.Execute(OpSetEventMode, 1), "l'envoi de ses événements");
            PollEvents();
            ConfigureSession();
            PollEvents();

            Name = name;
            _failures = 0;
            _lastDetectMessage = "";
            _nextPoll = Now + IdlePollMs;
            _nextKeepAlive = Now + KeepAliveMs;
            _log.Information("Appareil connecté par le pilote de Windows : {Name}", Name);
            LogCameraSettings();
            SetState(CameraState.Ready, $"{Name} prêt");
            return true;
        }
        catch (Exception ex)
        {
            device?.Dispose();
            CloseDevice();
            var message = ex is CameraException
                ? ex.Message
                : "Connexion à l'appareil photo impossible : " + ex.Message
                  + " Fermez EOS Utility et l'application Photos de Windows s'ils sont ouverts.";
            if (message != _lastDetectMessage) _log.Warning(ex, "Connexion à {Name} impossible", name);
            ReportDetection(CameraState.Error, message);
            return false;
        }
    }

    private void ConfigureSession()
    {
        var result = SetProperty(PropCaptureDestination, DestinationPc);
        if (result != RcOk) _log.Warning("Réglage « photos envoyées au PC » refusé : {Result}", Describe(result));

        // Comme avec le SDK Canon : l'appareil ne déclenche pas tant que le PC n'a pas annoncé de place libre.
        var capacity = _device!.Execute(OpPcHddCapacity, 0x0FFFFFFF, 0x00001000, 0x00000001);
        if (!capacity.IsOk) _log.Warning("Annonce de la place libre sur le PC refusée : {Result}", Describe(capacity.Code));
    }

    private static void Require(PtpResponse response, string what)
    {
        if (!response.IsOk) throw new CameraException($"L'appareil photo a refusé {what} ({Describe(response.Code)}).");
    }

    private void ReportDetection(CameraState state, string message)
    {
        if (message == _lastDetectMessage && state == State) return;
        _lastDetectMessage = message;
        _log.Information("Appareil photo : {Message}", message);
        SetState(state, message);
    }

    private void OnCommunicationError(Exception ex)
    {
        if (_device == null)
        {
            var message = "Recherche de l'appareil photo impossible : " + ex.Message;
            if (message != _lastDetectMessage) _log.Warning(ex, "Recherche de l'appareil photo impossible");
            ReportDetection(CameraState.Error, message);
            return;
        }

        _failures++;
        _log.Warning("Échange avec l'appareil photo impossible ({Count}) : {Message}", _failures, ex.Message);
        if (_failures >= MaxFailures || !IsStillConnected()) CloseSession("L'appareil photo a été éteint ou débranché.");
    }

    private bool IsStillConnected()
    {
        var id = _device?.Id;
        if (id == null) return false;
        try
        {
            return Wpd.ListDeviceIds().Contains(id);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void CloseSession(string reason)
    {
        CloseDevice();
        _lastDetectMessage = reason;
        _nextDetect = Now + DetectIntervalMs;
        _log.Warning("Appareil déconnecté : {Reason}", reason);
        SetState(CameraState.Disconnected, reason + " Reconnexion automatique…");
    }

    private void CloseDevice()
    {
        var device = _device;
        _device = null;
        _liveView = false;
        _operations = new();
        _properties.Clear();
        _transfers.Clear();
        _cameraStatus = null;
        _failures = 0;
        BatteryLevel = null;
        if (device != null) TryRun("Fermeture de la connexion à l'appareil", device.Dispose);
    }

    private void KeepAlive()
    {
        if (_device == null || !_operations.Contains(OpKeepDeviceOn)) return;
        var response = _device.Execute(OpKeepDeviceOn);
        if (!response.IsOk) _log.Debug("Maintien en éveil de l'appareil : {Result}", Describe(response.Code));
    }

    // ---------------------------------------------------------------- Événements de l'appareil

    private void PollEvents()
    {
        var response = _device!.ExecuteRead(OpGetEvent, out var data);
        if (!response.IsOk)
        {
            _log.Debug("Lecture des événements de l'appareil : {Result}", Describe(response.Code));
            return;
        }
        _failures = 0;
        foreach (var e in ParseEvents(data.Span)) Handle(e);
    }

    private void Handle(CanonPtpEvent e)
    {
        switch (e.Type)
        {
            case EventPropValueChanged:
                _properties[e.Property] = e.Value;
                if (e.Property == PropBatteryPower) UpdateBattery(e.Value);
                break;
            case EventCameraStatus:
                _cameraStatus = e.Value;
                break;
            case EventRequestObjectTransfer when ObjectFrom(e) is { } item:
                _transfers.Add(item);
                break;
            case EventWillSoonShutdown:
                _log.Information("L'appareil annonce sa mise en veille : maintien en éveil");
                KeepAlive();
                break;
        }
    }

    private void UpdateBattery(uint value)
    {
        var level = BatteryPercent(value);
        if (level == null && !_unknownBatteryLogged)
        {
            _unknownBatteryLogged = true;
            _log.Information("Niveau de batterie inconnu (valeur 0x{Value:X}) : appareil sur secteur ?", value);
        }
        BatteryLevel = level;
    }

    // ---------------------------------------------------------------- Prise de vue

    public async Task<byte[]> CaptureAsync(bool allowAutofocus, CancellationToken cancellationToken)
    {
        if (_thread == null || _stopping) throw new CameraException("Appareil photo non démarré.");
        var settings = _settings();
        var autofocus = allowAutofocus && settings.UseAutofocus;
        var timeout = TimeSpan.FromSeconds(settings.CaptureTimeoutSeconds);
        var capture = RunOnCameraThread(() => Capture(autofocus, timeout, cancellationToken));
        try
        {
            // Filet de sécurité : si le pilote de Windows ne répond plus, l'invité n'attend pas indéfiniment.
            return await capture.WaitAsync(timeout + TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CameraException("L'appareil n'a pas envoyé la photo à temps (mise au point impossible ou appareil occupé).");
        }
    }

    private byte[] Capture(bool autofocus, TimeSpan timeout, CancellationToken token)
    {
        if (_device == null) throw new CameraException("Appareil photo non connecté.");
        var watch = Stopwatch.StartNew();
        try
        {
            // Les fichiers annoncés avant l'appui ne sont pas cette photo.
            PollEvents();
            if (_transfers.Count > 0) DiscardTransfers();
            _cameraStatus = null;

            Press(autofocus, token);
            _log.Information("Déclenchement envoyé (autofocus : {Autofocus})", autofocus);

            var ignoredNonJpeg = false;
            while (true)
            {
                while (_transfers.Count > 0)
                {
                    var item = _transfers[0];
                    _transfers.RemoveAt(0);
                    var bytes = Download(item);
                    if (IsJpeg(bytes))
                    {
                        _log.Information("Photo reçue : {Item}, {Size} Ko, {Elapsed} ms après l'appui",
                            item, bytes.Length / 1024, watch.ElapsedMilliseconds);
                        return bytes;
                    }
                    // RAW+JPEG : on ignore le RAW et on attend le JPEG qui l'accompagne.
                    ignoredNonJpeg = true;
                    _log.Information("Fichier ignoré (pas un JPEG) : {Item}", item);
                }

                if (watch.Elapsed > timeout)
                {
                    throw new CameraException(ignoredNonJpeg
                        ? "L'appareil est réglé en RAW seul : choisissez une qualité JPEG dans son menu."
                        : "L'appareil n'a pas envoyé la photo à temps (mise au point impossible ou appareil occupé).");
                }
                token.ThrowIfCancellationRequested();
                Thread.Sleep(80);
                PollEvents();
            }
        }
        finally
        {
            // Pas de relance du live view pour une image manquée pendant la photo.
            _lastFrame = Now;
        }
    }

    /// <summary>
    /// Appui sur le déclencheur. Sans autofocus : appui complet puis relâchement aussitôt. Avec autofocus, l'appui ne
    /// rend la main qu'après la mise au point (2 à 4 s) ; si elle a réussi, la prise de vue commence tout de suite.
    /// Le déclencheur reste enfoncé jusque-là : le relâcher plus tôt annule la photo.
    /// </summary>
    private void Press(bool autofocus, CancellationToken token)
    {
        var device = _device!;
        if (!_operations.Contains(OpRemoteReleaseOn))
        {
            // Boîtiers plus anciens : un seul ordre « prendre la photo », autofocus compris.
            var shot = Patiently(() => device.Execute(OpRemoteRelease), what: "Déclenchement");
            if (!shot.IsOk) throw Refused(shot.Code);
            return;
        }

        var started = true;
        var refused = false;
        var watch = Stopwatch.StartNew();
        long pressMs = 0;
        try
        {
            var press = Patiently(() => device.Execute(OpRemoteReleaseOn, ReleaseFull,
                autofocus ? ReleaseWithAutofocus : ReleaseWithoutAutofocus), what: "Appui sur le déclencheur");
            pressMs = watch.ElapsedMilliseconds;
            if (!press.IsOk)
            {
                refused = true;
                throw Refused(press.Code);
            }
            if (autofocus) started = WaitUntil(() => _cameraStatus == 1 || _transfers.Count > 0, Patience, token);
        }
        finally
        {
            // Toujours relâcher, même si l'appui a échoué. Une mise au point ratée, ou restée engagée (l'appareil répond
            // alors « occupé »), est annulée : sinon l'appareil peut refuser les photos suivantes.
            TryRun("Relâchement du déclencheur", () => device.Execute(OpRemoteReleaseOff, ReleaseFull));
            if ((refused || !started) && _operations.Contains(OpAfCancel)) TryRun("Fin de la mise au point", () => device.Execute(OpAfCancel));
        }

        if (autofocus && started)
        {
            _log.Information("Mise au point faite : appui rendu en {Press} ms, prise de vue {Start} ms après l'appui (live view : {LiveView})",
                pressMs, watch.ElapsedMilliseconds, _liveView);
        }
        if (!started)
        {
            _log.Warning("Mise au point impossible : appui rendu en {Press} ms, pas de prise de vue dans les {Patience} s suivantes (live view : {LiveView}, état : {Status})",
                pressMs, Patience.TotalSeconds, _liveView, _cameraStatus?.ToString() ?? "aucun");
            throw new CameraException("Mise au point impossible : la photo n'a pas été prise.");
        }
    }

    private CameraException Refused(ushort code)
    {
        _log.Warning("Photo refusée par l'appareil : {Result}", Describe(code));
        return new CameraException(CaptureRefused(code));
    }

    private static string CaptureRefused(ushort code) => code switch
    {
        RcDeviceBusy or RcNotReady => "L'appareil photo est occupé : la photo n'a pas été prise.",
        0x200C => "La carte mémoire de l'appareil est pleine.",
        0x2013 => "Problème de carte mémoire : vérifiez la carte SD ou réglez « Déclencher sans carte ».",
        _ => "L'appareil n'a pas pu prendre la photo : " + Describe(code) + ".",
    };

    /// <summary>Lit un fichier annoncé par l'appareil, puis lui confirme la fin du transfert (il le garde en mémoire jusque-là).</summary>
    private byte[] Download(CanonPtpObject item)
    {
        var device = _device ?? throw new CameraException("Appareil photo non connecté.");
        var response = device.ExecuteRead(OpGetObject, out var data, item.Handle);
        if ((!response.IsOk || data.Length == 0) && _operations.Contains(OpEosGetObject))
        {
            _log.Information("Lecture standard de {Item} refusée ({Result}) : essai avec la commande Canon", item, Describe(response.Code));
            response = device.ExecuteRead(OpEosGetObject, out data, item.Handle);
        }
        var bytes = response.IsOk ? data.ToArray() : Array.Empty<byte>();

        var done = device.Execute(OpTransferComplete, item.Handle);
        if (!done.IsOk) _log.Warning("Fin de transfert de {Item} : {Result}", item, Describe(done.Code));
        if (bytes.Length == 0) throw new CameraException("Transfert de la photo impossible : " + Describe(response.Code) + ".");
        return bytes;
    }

    /// <summary>Fichiers arrivés hors d'une prise de vue : lus puis ignorés, pour libérer la mémoire de l'appareil.</summary>
    private void DiscardTransfers()
    {
        while (_transfers.Count > 0 && _device != null)
        {
            var item = _transfers[0];
            _transfers.RemoveAt(0);
            try
            {
                if (IsJpeg(Download(item)))
                {
                    _log.Information("Photo prise en dehors d'une session (déclencheur de l'appareil ?) : ignorée");
                }
                else
                {
                    _log.Information("Fichier ignoré (pas un JPEG) : {Item}", item);
                }
            }
            catch (CameraException ex)
            {
                _log.Warning("{Message}", ex.Message);
            }
        }
    }

    private static bool IsJpeg(byte[] bytes) => bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8;

    // ---------------------------------------------------------------- Live view

    public async Task StartLiveViewAsync()
    {
        if (_thread == null || _stopping) return;
        try
        {
            await RunOnCameraThread(StartLiveView).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CameraException("Le live view n'a pas démarré : l'appareil photo ne répond pas.");
        }
    }

    private void StartLiveView()
    {
        if (_device == null) throw new CameraException("Appareil photo non connecté.");
        if (_properties.TryGetValue(PropEvfMode, out var mode) && mode == 0)
        {
            var modeResult = SetProperty(PropEvfMode, 1);
            _log.Information("Fonction live view désactivée dans l'appareil : activation → {Result}", Describe(modeResult));
        }

        // Image envoyée au PC seulement : l'écran de l'appareil reste éteint, ce qui ménage la batterie.
        var result = SetProperty(PropEvfOutputDevice, EvfOutputPc, TimeSpan.FromSeconds(6));
        if (result != RcOk)
        {
            throw new CameraException("Le live view n'a pas démarré : " + Describe(result)
                                      + ". Vérifiez dans le menu de l'appareil que la prise de vue avec visée par l'écran (live view) est activée.");
        }
        _liveViewRefusals = 0;
        _lastFrame = Now;
        _liveView = true;
        _log.Information("Live view démarré");
    }

    public async Task StopLiveViewAsync()
    {
        if (_thread == null || _stopping) return;
        try
        {
            await RunOnCameraThread(StopLiveView).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Warning("Arrêt du live view : l'appareil photo ne répond pas");
        }
    }

    private void StopLiveView()
    {
        if (!_liveView) return;
        _liveView = false;
        if (_device == null) return;
        try
        {
            var result = SetProperty(PropEvfOutputDevice, 0);
            if (result != RcOk) _log.Warning("Arrêt du live view refusé par l'appareil : {Result}", Describe(result));
        }
        catch (Exception ex)
        {
            // Comme avec le SDK Canon, l'arrêt du live view ne signale pas d'erreur (appareil débranché entre-temps…).
            _log.Warning("Arrêt du live view impossible : {Message}", ex.Message);
        }
    }

    private bool FetchLiveViewFrame()
    {
        var response = _device!.ExecuteRead(OpGetViewFinderData, out var data, ViewFinderBuffer);
        if (!response.IsOk)
        {
            // « Pas prêt » est normal (environ une demande sur trois) ; le reste est noté de temps en temps.
            if (response.Code is not (RcNotReady or RcDeviceBusy) && ++_liveViewRefusals % 40 == 1)
            {
                _log.Warning("Live view : {Result}", Describe(response.Code));
            }
            return false;
        }

        _failures = 0;
        var jpeg = ExtractLiveViewJpeg(data.Span);
        if (jpeg == null) return false;
        _lastFrame = Now;
        try
        {
            LiveViewFrame?.Invoke(this, new LiveViewFrameEventArgs(jpeg));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Erreur dans l'affichage du live view");
        }
        return true;
    }

    /// <summary>
    /// Après une photo, certains boîtiers rendent l'image à leur écran ; d'autres cessent d'en envoyer. On redemande
    /// alors le live view (au plus une fois toutes les 3 secondes).
    /// </summary>
    private void CheckLiveViewOutput()
    {
        if (Now - _lastReapply < LiveViewStallMs) return;
        var lostOutput = _properties.TryGetValue(PropEvfOutputDevice, out var output) && (output & EvfOutputPc) == 0;
        var stalled = Now - _lastFrame > LiveViewStallMs;
        if (!lostOutput && !stalled) return;

        _lastReapply = Now;
        var result = SetProperty(PropEvfOutputDevice, EvfOutputPc);
        _log.Information("Live view redemandé à l'appareil ({Reason}) : {Result}",
            lostOutput ? "image rendue à son écran" : "plus d'image", Describe(result));
    }

    // ---------------------------------------------------------------- Outils

    /// <summary>Change un réglage de l'appareil, en insistant tant qu'il répond « occupé » ou « pas prêt ».</summary>
    private ushort SetProperty(uint property, uint value, TimeSpan? patience = null)
    {
        var payload = PropertyPayload(property, value);
        var device = _device ?? throw new CameraException("Appareil photo non connecté.");
        var response = Patiently(() => device.ExecuteWrite(OpSetDevicePropValueEx, payload), patience, $"Réglage 0x{property:X4}");
        if (response.IsOk) _properties[property] = value;
        return response.Code;
    }

    /// <summary>
    /// Renvoie la commande tant que l'appareil répond « occupé » ou « pas prêt » (refus passagers), dans la limite de
    /// <paramref name="patience"/>. Les nouveaux essais sont notés dans le journal.
    /// </summary>
    private PtpResponse Patiently(Func<PtpResponse> command, TimeSpan? patience = null, string what = "Commande")
    {
        var watch = Stopwatch.StartNew();
        var limit = patience ?? Patience;
        var retries = 0;
        while (true)
        {
            var response = command();
            if (response.Code is not (RcDeviceBusy or RcNotReady) || watch.Elapsed > limit)
            {
                if (retries > 0)
                {
                    _log.Information("{What} : appareil occupé, {Retries} nouvel(s) essai(s) en {Elapsed} ms → {Result}",
                        what, retries, watch.ElapsedMilliseconds, Describe(response.Code));
                }
                return response;
            }
            retries++;
            Thread.Sleep(150);
            PollEvents();
        }
    }

    private bool WaitUntil(Func<bool> condition, TimeSpan max, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > max) return false;
            token.ThrowIfCancellationRequested();
            Thread.Sleep(80);
            PollEvents();
        }
        return true;
    }

    private void LogCameraSettings() =>
        _log.Information(
            "Réglages de l'appareil : mode={Mode}, mise au point={Focus}, qualité={Quality}, destination={Destination}, live view autorisé={EvfMode}, batterie={Battery}",
            Hex(PropAeMode), Hex(PropFocusMode), Hex(PropImageFormat), Hex(PropCaptureDestination), Hex(PropEvfMode), Hex(PropBatteryPower));

    private string Hex(uint property) => _properties.TryGetValue(property, out var value) ? $"0x{value:X}" : "?";

    private void TryRun(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.Debug("{What} : {Message}", what, ex.Message);
        }
    }

    private Task<T> RunOnCameraThread<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Enqueue(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled();
            }
            catch (CameraException ex)
            {
                completion.TrySetException(ex);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Erreur de l'appareil photo");
                completion.TrySetException(new CameraException("L'appareil photo ne répond pas : " + ex.Message, ex));
                OnCommunicationError(ex);
            }
        });
        _wake.Set();
        return completion.Task;
    }

    private Task RunOnCameraThread(Action work) => RunOnCameraThread(() =>
    {
        work();
        return true;
    });

    private void SetState(CameraState state, string message)
    {
        State = state;
        StatusMessage = message;
        try
        {
            StatusChanged?.Invoke(this, new CameraStatusEventArgs(state, message));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Erreur dans l'affichage de l'état de l'appareil photo");
        }
    }

    /// <summary>Sur le fil de l'appareil, à l'arrêt : l'appareil retrouve son fonctionnement habituel.</summary>
    private void Shutdown()
    {
        var device = _device;
        if (device != null)
        {
            if (_liveView) TryRun("Arrêt du live view", () => SetProperty(PropEvfOutputDevice, 0, TimeSpan.FromSeconds(2)));
            _liveView = false;
            TryRun("Photos de nouveau sur la carte", () => SetProperty(PropCaptureDestination, DestinationCard, TimeSpan.FromSeconds(2)));
            TryRun("Fin des événements", () => device.Execute(OpSetEventMode, 0));
            TryRun("Fin du pilotage par le PC", () => device.Execute(OpSetRemoteMode, 0));
        }
        CloseDevice();
        // Les demandes restées en file échouent proprement (« Appareil photo non connecté »).
        while (_jobs.TryDequeue(out var job)) job();
        _log.Information("Appareil photo libéré");
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        _wake.Set();
        if (_thread != null && !_thread.Join(TimeSpan.FromSeconds(8)))
        {
            _log.Warning("L'appareil photo n'a pas été libéré à temps");
        }
    }
}
