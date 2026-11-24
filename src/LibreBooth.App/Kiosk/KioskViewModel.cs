using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LibreBooth.App.Infrastructure;
using LibreBooth.App.Services;
using LibreBooth.Core.Admin;
using LibreBooth.Core.Events;
using LibreBooth.Core.Hardware;
using LibreBooth.Core.Layouts;
using LibreBooth.Core.Session;
using LibreBooth.Core.Util;
using Serilog;

namespace LibreBooth.App.Kiosk;

/// <summary>
/// Chef d'orchestre du parcours invité : accueil, choix du modèle, prise de vue, montage, revue,
/// remerciements. Toute erreur mène à un écran d'erreur clair puis à un retour automatique à l'accueil.
/// </summary>
public sealed partial class KioskViewModel : ObservableObject
{
    private readonly ILogger _log = Log.ForContext<KioskViewModel>();
    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private readonly SessionStateMachine _machine = new();
    private readonly DispatcherTimer _inactivityTimer;
    private readonly DispatcherTimer _errorCountdown;
    private ICamera? _attachedCamera;
    private LiveViewDecoder? _decoder;
    private CancellationTokenSource? _sessionCancellation;
    private SessionData? _session;
    private bool _paused;
    private bool _diskFull;

    [ObservableProperty] private object? _currentScreen;

    public KioskViewModel(AppServices services)
    {
        _services = services;
        _dispatcher = services.Dispatcher;

        Attract = new AttractViewModel(StartSession);
        Choose = new ChooseTemplateViewModel(BeginWithTemplate, () => _machine.Reset());
        Shooting = new ShootingViewModel();
        Processing = new ProcessingViewModel();
        Review = new ReviewViewModel(this);
        Thanks = new ThanksViewModel();
        Error = new ErrorViewModel(() => _machine.Reset());

        _inactivityTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
        _inactivityTimer.Tick += (_, _) =>
        {
            _inactivityTimer.Stop();
            _machine.OnTimeout();
        };
        _errorCountdown = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _errorCountdown.Tick += (_, _) =>
        {
            if (Error.SecondsLeft > 0) Error.SecondsLeft--;
        };

        _machine.StateChanged += OnStateChanged;
        services.CameraChanged += (_, _) => _dispatcher.BeginInvoke(AttachCamera);
        services.EventChanged += (_, _) => _dispatcher.BeginInvoke(RefreshFromSettings);
        services.PrinterMonitor.Changed += (_, _) => RefreshOperatorWarning();

        AttachCamera();
        RefreshFromSettings();
        CurrentScreen = Attract;
    }

    public AttractViewModel Attract { get; }
    public ChooseTemplateViewModel Choose { get; }
    public ShootingViewModel Shooting { get; }
    public ProcessingViewModel Processing { get; }
    public ReviewViewModel Review { get; }
    public ThanksViewModel Thanks { get; }
    public ErrorViewModel Error { get; }

    public KioskState State => _machine.State;
    public SessionData? CurrentSession => _session;

    /// <summary>Accueil affiché, sans session en cours : le bouton « Quitter » du photobooth peut apparaître.</summary>
    public bool IsIdle => _machine.State == KioskState.Attract && !_paused;

    // ---------------------------------------------------------------- Appareil photo

    private void AttachCamera()
    {
        if (_attachedCamera != null) _attachedCamera.StatusChanged -= OnCameraStatusChanged;
        _decoder?.Dispose();

        var camera = _services.Camera;
        _attachedCamera = camera;
        camera.StatusChanged += OnCameraStatusChanged;
        _decoder = new LiveViewDecoder(camera, _dispatcher, image =>
        {
            if (_machine.State != KioskState.Shooting) return;
            Shooting.LiveImage = image;
            Shooting.IsWaitingForCamera = false;
        });
        _decoder.Attach();
        UpdateCameraReadiness();
    }

    private void OnCameraStatusChanged(object? sender, CameraStatusEventArgs e) => _dispatcher.BeginInvoke(UpdateCameraReadiness);

    /// <summary>L'accueil n'accepte de session qu'avec l'appareil prêt et assez de place sur le disque des photos.</summary>
    private void UpdateCameraReadiness()
    {
        var camera = _services.Camera;
        Attract.IsReady = camera.State == CameraState.Ready && !_diskFull;
        Attract.NotReadyTitle = _diskFull ? "Le photobooth fait une pause" : "Le photobooth se prépare…";
        Attract.NotReadyMessage = _diskFull
            ? "Plus assez de place pour enregistrer les photos : prévenez l'animateur."
            : camera.StatusMessage;
    }

    // ---------------------------------------------------------------- Réglages et imprimante

    /// <summary>Titre de l'accueil : celui de l'admin › Écrans, ou à défaut le nom de l'événement.</summary>
    public static string AttractTitleFor(AppServices services) =>
        string.IsNullOrWhiteSpace(services.Settings.Screens.Title) ? services.CurrentEvent.Name : services.Settings.Screens.Title;

    public void RefreshFromSettings()
    {
        var settings = _services.Settings;
        Attract.Title = AttractTitleFor(_services);
        Attract.StartText = settings.Screens.StartText;
        Attract.Background = ImageLoader.FromFile(_services.CurrentAttractBackground(), decodePixelWidth: 1920);
        Shooting.MirrorScaleX = settings.Camera.MirrorLiveView ? -1 : 1;
        Shooting.RotationAngle = settings.Camera.Rotation;
        RefreshOperatorWarning();
        UpdateCameraReadiness();
    }

    /// <summary>
    /// Avertissement de l'animateur sur l'accueil : disque presque plein (les sessions s'arrêtent sous 300 Mo libres),
    /// puis imprimante, puis papier. Recalculé au retour à l'accueil, avant chaque session et à chaque bilan de
    /// l'imprimante (toutes les 15 s) : dès que de la place est libérée, les sessions reprennent.
    /// </summary>
    private void RefreshOperatorWarning()
    {
        var disk = _services.ProbePhotoDisk();
        var diskFull = disk is { } probe && !DiskSpace.AllowsSession(probe.FreeBytes);
        if (diskFull != _diskFull)
        {
            _diskFull = diskFull;
            if (diskFull)
                _log.Warning("Disque presque plein ({Free} libres sur {Drive}) : plus de nouvelle session", Readiness.Size(disk!.Value.FreeBytes), disk.Value.DriveName);
            else
                _log.Information("Place libérée sur le disque des photos : les sessions reprennent");
            UpdateCameraReadiness();
        }

        var settings = _services.Settings.Print;
        var health = _services.PrinterMonitor.Current;
        if (diskFull)
        {
            Attract.OperatorWarning = $"Disque presque plein : sessions en pause ({Readiness.Size(disk!.Value.FreeBytes)} libres)";
        }
        else if (!settings.Enabled)
        {
            Attract.OperatorWarning = null;
        }
        else if (!health.CanPrint && health.Condition != PrinterCondition.Unknown)
        {
            Attract.OperatorWarning = health.Message.StartsWith("Imprimante", StringComparison.OrdinalIgnoreCase)
                ? health.Message
                : "Imprimante : " + health.Message;
        }
        else if (health.LowMedia)
        {
            Attract.OperatorWarning = $"Papier bientôt terminé : {health.RemainingPrints} tirages restants";
        }
        else
        {
            Attract.OperatorWarning = null;
        }

        if (_machine.State == KioskState.Review) UpdateReviewPrintState();
    }

    // ---------------------------------------------------------------- Activité et délais

    /// <summary>L'invité a touché l'écran : on relance le délai d'inactivité de l'écran courant.</summary>
    public void NotifyActivity()
    {
        if (!_inactivityTimer.IsEnabled) return;
        _inactivityTimer.Stop();
        _inactivityTimer.Start();
    }

    private void OnStateChanged(object? sender, StateChangedEventArgs e)
    {
        _log.Information("Écran : {Previous} → {Current}", e.Previous, e.Current);
        _inactivityTimer.Stop();
        _errorCountdown.Stop();
        OnPropertyChanged(nameof(IsIdle));

        CurrentScreen = e.Current switch
        {
            KioskState.ChooseTemplate => Choose,
            KioskState.Shooting => Shooting,
            KioskState.Processing => Processing,
            KioskState.Review => Review,
            KioskState.Thanks => Thanks,
            KioskState.Error => Error,
            _ => Attract,
        };

        var screens = _services.Settings.Screens;
        switch (e.Current)
        {
            case KioskState.Attract:
                _sessionCancellation?.Cancel();
                _session = null;
                Shooting.Reset();
                _ = SafeStopLiveViewAsync();
                RefreshFromSettings();
                break;
            case KioskState.Thanks:
                Thanks.Title = string.IsNullOrWhiteSpace(screens.ThanksText) ? "Merci !" : screens.ThanksText;
                Thanks.Message = _session?.PrintedCopies > 0 ? screens.ThanksPrintText : null;
                break;
            case KioskState.Error:
                Error.Message = _machine.ErrorMessage ?? "Une erreur est survenue.";
                Error.SecondsLeft = screens.ErrorSeconds;
                _errorCountdown.Start();
                break;
        }

        var timeout = SessionStateMachine.TimeoutFor(e.Current, screens);
        if (timeout != null && !_paused)
        {
            _inactivityTimer.Interval = timeout.Value;
            _inactivityTimer.Start();
        }
    }

    // ---------------------------------------------------------------- Début de session

    public void StartSession()
    {
        if (_paused || _machine.State != KioskState.Attract) return;
        if (_services.Camera.State != CameraState.Ready)
        {
            _log.Information("Démarrage refusé : appareil pas prêt ({Message})", _services.Camera.StatusMessage);
            return;
        }
        RefreshOperatorWarning();
        if (_diskFull)
        {
            _log.Information("Démarrage refusé : disque presque plein");
            return;
        }

        var templates = _services.ActiveTemplates();
        if (templates.Count == 0)
        {
            _machine.Fail("Aucun format proposé : cochez-en au moins un dans l'admin (page Personnalisation).");
            return;
        }
        if (templates.Count == 1)
        {
            BeginWithTemplate(templates[0]);
            return;
        }

        Choose.Cards.Clear();
        var previewContext = _services.CurrentPreviewContext();
        foreach (var template in templates) Choose.Cards.Add(new TemplateCard(template, LoadPreview(template, previewContext)));
        _machine.ChooseTemplate();
    }

    private ImageSource? LoadPreview(Template template, PreviewContext context)
    {
        try
        {
            return ImageLoader.FromFile(TemplatePreview.GetOrCreate(_services.Compositor, template, context), decodePixelHeight: 700);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Aperçu indisponible pour {Template}", template.Id);
            return null;
        }
    }

    private void BeginWithTemplate(Template template)
    {
        var session = new SessionData(EventStore.NewSessionId(), template, _services.CurrentEvent, _services.CurrentDesign());
        _sessionCancellation?.Cancel();
        _sessionCancellation = new CancellationTokenSource();
        Shooting.Reset();
        _session = session;
        if (!_machine.BeginShooting(template.ShotCount)) return;
        _log.Information("Nouvelle session {Id} avec le modèle {Template}", session.Id, template.Id);
        _ = RunShootingAsync(session, _sessionCancellation.Token);
    }

    // ---------------------------------------------------------------- Prise de vue

    private async Task RunShootingAsync(SessionData session, CancellationToken token)
    {
        var settings = _services.Settings;
        var camera = _services.Camera;
        try
        {
            await camera.StartLiveViewAsync();
            var shots = session.Template.ShotCount;
            for (var index = 0; index < shots; index++)
            {
                token.ThrowIfCancellationRequested();
                Shooting.ShotLabel = shots > 1 ? $"Photo {index + 1} sur {shots}" : "Votre photo";
                Shooting.IsCountdownVisible = true;
                for (var second = settings.Screens.CountdownSeconds; second >= 1; second--)
                {
                    Shooting.CountdownText = second.ToString();
                    await Task.Delay(1000, token);
                }
                Shooting.CountdownText = "Souriez !";
                Shooting.Flash();

                var bytes = await CaptureWithRetryAsync(camera, token);
                Shooting.IsCountdownVisible = false;

                // La photo est enregistrée immédiatement : rien n'est perdu, même en cas de plantage.
                var path = Path.Combine(session.Event.OriginalsFolder, $"{session.Id}_{index + 1}.jpg");
                await Task.Run(() => AtomicFile.WriteAllBytes(path, bytes), token);
                session.Photos.Add(path);
                _services.Events.UpdateStats(session.Event, s => s.Photos++);

                var rotation = settings.Camera.Rotation;
                Shooting.PreviewImage = await Task.Run(() => ImageLoader.FromFileOriented(path, 900, rotation), token);
                Shooting.IsPreviewVisible = true;
                await Task.Delay(settings.Screens.PreviewMilliseconds, token);
                Shooting.IsPreviewVisible = false;
                _machine.ShotCompleted();
            }
        }
        catch (OperationCanceledException)
        {
            await SafeStopLiveViewAsync();
            return;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Prise de vue interrompue");
            await SafeStopLiveViewAsync();
            if (_session == session)
            {
                _machine.Fail(camera.State != CameraState.Ready
                    ? "L'appareil photo s'est déconnecté. Le photobooth repart tout seul dès qu'il est rebranché."
                    : ex is CameraException
                        ? ex.Message
                        : "La photo n'a pas pu être prise. On recommence dans un instant !");
            }
            return;
        }

        await SafeStopLiveViewAsync();
        await ComposeAsync(session, token);
    }

    private async Task<byte[]> CaptureWithRetryAsync(ICamera camera, CancellationToken token)
    {
        try
        {
            return await camera.CaptureAsync(allowAutofocus: true, token);
        }
        catch (CameraException first) when (!token.IsCancellationRequested)
        {
            _log.Warning("Photo ratée ({Message}) : nouvel essai sans autofocus", first.Message);
            Shooting.StatusText = "Encore une petite seconde…";
            await Task.Delay(800, token);
            if (camera.State != CameraState.Ready) throw;
            var bytes = await camera.CaptureAsync(allowAutofocus: false, token);
            Shooting.StatusText = null;
            return bytes;
        }
    }

    private async Task SafeStopLiveViewAsync()
    {
        try
        {
            await _services.Camera.StopLiveViewAsync();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Arrêt du live view");
        }
    }

    // ---------------------------------------------------------------- Montage et revue

    private async Task ComposeAsync(SessionData session, CancellationToken token)
    {
        try
        {
            var settings = _services.Settings;
            var output = Path.Combine(session.Event.CompositesFolder, session.Id + ".jpg");
            var context = new CompositionContext(session.Event.Name, session.Event.Date, settings.Camera.MirrorPhotos, settings.Camera.Rotation, session.Design);
            var photos = session.Photos.ToList();
            await Task.Run(() => _services.Compositor.ComposeToFile(session.Template, photos, context, output), token);
            session.CompositePath = output;

            _services.Events.SaveSession(session.Event, new SessionRecord
            {
                Id = session.Id,
                TemplateId = session.Template.Id,
                TemplateName = session.Template.Name,
                PrintProfile = session.Template.Layout.PrintProfile,
                Photos = photos.Select(p => Path.GetFileName(p)).ToList(),
                Composite = Path.GetFileName(output),
                CreatedAt = DateTime.Now,
            });
            _services.Events.UpdateStats(session.Event, s => s.Sessions++);

            var image = await Task.Run(() => ImageLoader.FromFile(output, decodePixelHeight: 1000), token);
            if (_session != session) return;
            PrepareReview(image);
            _machine.CompositionReady();

            if (settings.Print.AutoPrint && Review.CanPrint)
            {
                await PrintCurrentAsync(settings.Print.AutoPrintCopies);
            }
        }
        catch (OperationCanceledException)
        {
            // Session abandonnée (admin, retour à l'accueil).
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Montage impossible");
            if (_session == session) _machine.Fail("Le montage de la photo a échoué. Vos photos sont bien enregistrées.");
        }
    }

    private void PrepareReview(ImageSource? image)
    {
        Review.CompositeImage = image;
        Review.StatusMessage = null;
        Review.IsPrinting = false;
        Review.Copies = 1;
        Review.Pieces = _session?.Template.Layout.DuplicateStrip == true
            ? ReviewViewModel.PiecesNoun(_session.Template.Layout)
            : null;
        UpdateReviewPrintState();
    }

    private void UpdateReviewPrintState()
    {
        var session = _session;
        if (session == null) return;
        var print = _services.Settings.Print;
        var health = _services.PrinterMonitor.Current;

        var leftForSession = _services.Customization.MaxCopies - session.PrintedCopies;
        var leftForEvent = print.EventPrintLimit > 0 ? print.EventPrintLimit - session.Event.Info.Stats.Prints : int.MaxValue;
        var allowed = Math.Min(leftForSession, leftForEvent);

        Review.ShowRetakeButton = CanRetake(session) && !Review.IsPrinting;
        Review.ShowPrintButton = print.Enabled;
        Review.MaxCopies = Math.Max(1, allowed);
        if (Review.Copies > Review.MaxCopies) Review.Copies = Review.MaxCopies;
        Review.ShowCopiesSelector = print.Enabled && allowed > 1;

        if (!print.Enabled)
        {
            Review.CanPrint = false;
            Review.PrintHint = null;
        }
        else if (allowed <= 0)
        {
            Review.CanPrint = false;
            Review.PrintHint = leftForEvent <= 0
                ? "Le nombre maximal d'impressions de l'événement est atteint."
                : "Nombre maximal d'impressions atteint pour cette photo.";
        }
        else if (!health.CanPrint)
        {
            Review.CanPrint = false;
            Review.PrintHint = "L'impression est momentanément indisponible.";
        }
        else
        {
            Review.CanPrint = true;
            Review.PrintHint = null;
        }
    }

    // ---------------------------------------------------------------- Recommencer

    /// <summary>
    /// L'invité peut refaire sa série tant qu'il n'a rien imprimé (sinon il garderait la photo refusée en plus de la
    /// nouvelle), et seulement le nombre de fois réglé dans l'admin.
    /// </summary>
    private bool CanRetake(SessionData session)
    {
        var settings = _services.Settings;
        return session.RetakeCount < settings.Screens.RetakeLimit
               && session.PrintedCopies == 0
               && !(settings.Print.Enabled && settings.Print.AutoPrint);
    }

    /// <summary>Bouton « Recommencer » : la série refusée est rangée à part, puis on refait toutes les photos.</summary>
    public void Retake()
    {
        var session = _session;
        if (_paused || session == null || _machine.State != KioskState.Review || Review.IsPrinting || !CanRetake(session)) return;

        _log.Information("Série {Id} refusée par l'invité : on recommence ({Count}/{Limit})",
            session.Id, session.RetakeCount + 1, _services.Settings.Screens.RetakeLimit);
        try
        {
            _services.Events.SetAsideRetakenSession(session.Event, session.Id, session.Photos, session.CompositePath);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Série refusée non rangée dans « refusees »");
        }

        Review.CompositeImage = null;
        var next = new SessionData(EventStore.NewSessionId(), session.Template, session.Event, session.Design)
        {
            RetakeCount = session.RetakeCount + 1,
        };
        _sessionCancellation?.Cancel();
        _sessionCancellation = new CancellationTokenSource();
        Shooting.Reset();
        _session = next;
        if (!_machine.Retake()) return;
        _ = RunShootingAsync(next, _sessionCancellation.Token);
    }

    // ---------------------------------------------------------------- Impression

    public async Task PrintCurrentAsync(int copies)
    {
        var session = _session;
        if (session?.CompositePath == null || Review.IsPrinting) return;
        UpdateReviewPrintState();
        if (!Review.CanPrint) return;

        copies = Math.Clamp(copies, 1, Review.MaxCopies);
        Review.IsPrinting = true;
        Review.ShowRetakeButton = false;
        Review.StatusMessage = "Impression en cours…";
        NotifyActivity();
        try
        {
            await _services.PrintCompositeAsync(session.Event, session.Id, session.CompositePath, session.Template.Layout.PrintProfile, copies);
            session.PrintedCopies += copies;
            Review.StatusMessage = Review.Pieces != null
                ? $"Impression lancée ! Vos {copies * 2} {Review.Pieces} arrivent dans quelques secondes."
                : copies > 1
                    ? $"{copies} impressions lancées ! Elles arrivent dans quelques secondes."
                    : "Impression lancée ! Elle arrive dans quelques secondes.";
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Impression impossible");
            Review.StatusMessage = "L'impression n'a pas pu être lancée. Prévenez l'animateur.";
        }
        finally
        {
            Review.IsPrinting = false;
            UpdateReviewPrintState();
            NotifyActivity();
        }
    }

    // ---------------------------------------------------------------- Fin

    public void Finish() => _machine.Finish();

    // ---------------------------------------------------------------- Admin et erreurs

    public async Task PauseForAdminAsync()
    {
        _paused = true;
        OnPropertyChanged(nameof(IsIdle));
        _inactivityTimer.Stop();
        _sessionCancellation?.Cancel();
        await SafeStopLiveViewAsync();
        _machine.Reset();
    }

    public void ResumeAfterAdmin()
    {
        _paused = false;
        _machine.Reset();
        OnPropertyChanged(nameof(IsIdle));
        RefreshOperatorWarning();
    }

    public void ShowError(string message)
    {
        _sessionCancellation?.Cancel();
        _ = SafeStopLiveViewAsync();
        _machine.Fail(message);
    }

    /// <summary>Appelé par le gestionnaire d'erreurs global : on affiche un message puis on repart de l'accueil.</summary>
    public void RecoverFromCrash(Exception exception)
    {
        _log.Error(exception, "Reprise après une erreur inattendue");
        ShowError("Oups, un petit souci technique. Le photobooth redémarre…");
    }
}
