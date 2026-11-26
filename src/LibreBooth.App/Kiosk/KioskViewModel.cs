using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LibreBooth.App.Infrastructure;
using LibreBooth.App.Services;
using LibreBooth.Core.Admin;
using LibreBooth.Core.Config;
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

    // Papier vérifié par l'invité quand le compteur est à 0 (3.2.0) : l'étape en cours et l'impression concernée.
    private enum PaperStep { None, WaitingFirst, AskingFirst, WaitingRetry, AskingRetry, Notice }
    private sealed record PaperPrint(SessionData Session, string Composite, string Profile, int Copies);
    private readonly DispatcherTimer _paperTimer;
    private PaperStep _paperStep;
    private PaperPrint? _paperPrint;

    /// <summary>Attente avant la question sur le papier, raccourcie par le parcours de vérification (--captures).</summary>
    internal static TimeSpan? PaperDelayForCapture { get; set; }

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
        PaperQuestion = new PaperQuestionViewModel(AnswerPaperQuestion, ClosePaperQuestion);

        _paperTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
        _paperTimer.Tick += (_, _) =>
        {
            _paperTimer.Stop();
            OnPaperTimer();
        };

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
        services.PaperReportChanged += (_, _) => RefreshOperatorWarning();

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
    /// <summary>« Votre photo est-elle sortie ? », par-dessus l'écran en cours (compteur de papier à 0).</summary>
    public PaperQuestionViewModel PaperQuestion { get; }

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
        else if (_services.PaperDeclaredEmpty)
        {
            Attract.OperatorWarning = "Plus de papier (signalé par un invité) : changez le rouleau, puis admin › « J'ai installé un nouveau rouleau »";
        }
        else if (!health.CanPrint && health.Condition != PrinterCondition.Unknown)
        {
            Attract.OperatorWarning = health.Message.StartsWith("Imprimante", StringComparison.OrdinalIgnoreCase)
                ? health.Message
                : "Imprimante : " + health.Message;
        }
        else if (health.RemainingPrints is <= 0)
        {
            Attract.OperatorWarning = settings.PaperReport == PaperReport.StillPrinting
                ? "Compteur de papier à 0, mais les tirages sortent encore : prévoyez un rouleau"
                : "Compteur de papier à 0 : changez le rouleau si possible";
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

        // Un nouvel invité commence : la question sur le papier, restée sans réponse, sera posée à la prochaine impression.
        if (e.Current == KioskState.ChooseTemplate && _paperStep != PaperStep.None) DropPaperCheck("un nouvel invité commence");

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

        var paperEmpty = _services.PaperDeclaredEmpty;
        Review.ShowCopiesSelector = print.Enabled && allowed > 1 && !paperEmpty;

        if (!print.Enabled)
        {
            Review.CanPrint = false;
            Review.PrintHint = null;
        }
        else if (paperEmpty)
        {
            Review.CanPrint = false;
            Review.PrintHint = "Plus de papier : vos photos sont bien enregistrées, l'animateur va changer le rouleau.";
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
        // Compteur de papier avant l'impression : à 0, elle part quand même, puis on demande à l'invité si elle est sortie.
        var remainingBefore = _services.PrinterMonitor.Current.RemainingPrints;
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
            if (_paperStep == PaperStep.None && PaperCheck.ShouldAsk(remainingBefore, _services.Settings.Print.PaperReport))
            {
                StartPaperCheck(new PaperPrint(session, session.CompositePath, session.Template.Layout.PrintProfile, copies));
            }
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

    // ---------------------------------------------------------------- Papier vérifié par l'invité (3.2.0)

    /// <summary>Le compteur était à 0 : on laisse le temps à l'impression de sortir, puis on pose la question.</summary>
    private void StartPaperCheck(PaperPrint print)
    {
        _paperPrint = print;
        _paperStep = PaperStep.WaitingFirst;
        var delay = PaperCheck.QuestionDelay(print.Copies);
        _log.Information("Compteur de papier à 0 : impression envoyée quand même ({Copies} ex.), question à l'invité dans {Seconds} s",
            print.Copies, (int)delay.TotalSeconds);
        StartPaperTimer(PaperDelayForCapture ?? delay);
    }

    private void StartPaperTimer(TimeSpan delay)
    {
        _paperTimer.Stop();
        _paperTimer.Interval = delay;
        _paperTimer.Start();
    }

    private void OnPaperTimer()
    {
        switch (_paperStep)
        {
            case PaperStep.WaitingFirst or PaperStep.WaitingRetry:
                // Seulement sur la revue, la fin ou l'accueil, et pour la session concernée (pas pendant la photo d'un autre).
                var screenFree = _machine.State is KioskState.Review or KioskState.Thanks or KioskState.Attract;
                if (_paused || !screenFree || (_session != null && _session != _paperPrint?.Session))
                {
                    DropPaperCheck("l'écran est occupé par un autre invité");
                    return;
                }
                var retry = _paperStep == PaperStep.WaitingRetry;
                _paperStep = retry ? PaperStep.AskingRetry : PaperStep.AskingFirst;
                ShowPaper(retry ? "Et cette fois, votre photo est-elle sortie ?" : "Votre photo est-elle sortie de l'imprimante ?",
                    retry
                        ? "Nous venons de la réimprimer."
                        : "Le rouleau de papier est peut-être terminé : dites-nous si vous avez bien reçu votre photo.",
                    answers: true);
                _log.Information("Question à l'invité : sa photo est-elle sortie ?{Retry}", retry ? " (après le nouvel essai)" : "");
                StartPaperTimer(PaperCheck.AnswerTimeout);
                break;
            case PaperStep.AskingFirst or PaperStep.AskingRetry:
                DropPaperCheck("pas de réponse");
                break;
            case PaperStep.Notice:
                ClosePaperQuestion();
                break;
        }
    }

    /// <summary>« Oui » ou « Non » de l'invité.</summary>
    private void AnswerPaperQuestion(bool printed)
    {
        var print = _paperPrint;
        if (print == null || _paperStep is not (PaperStep.AskingFirst or PaperStep.AskingRetry)) return;
        _paperTimer.Stop();
        NotifyActivity();

        if (printed)
        {
            _services.SetPaperReport(PaperReport.StillPrinting,
                _paperStep == PaperStep.AskingRetry ? "photo sortie au nouvel essai" : "un invité a confirmé que sa photo est sortie");
            ClosePaperQuestion();
            return;
        }

        if (_paperStep == PaperStep.AskingFirst)
        {
            _log.Information("L'invité dit que sa photo n'est pas sortie : nouvel essai");
            _ = RetryPaperPrintAsync(print);
            return;
        }

        _log.Warning("Rien n'est sorti, même au nouvel essai : rouleau déclaré vide");
        _services.DeclarePaperEmpty(print.Session.Event, print.Session.Id, print.Copies);
        if (_machine.State == KioskState.Review && _session == print.Session) Review.StatusMessage = null;
        UpdateReviewPrintState();
        ShowNotice("Plus de papier",
            "Votre photo est bien enregistrée : l'animateur va changer le rouleau et pourra ensuite vous l'imprimer.");
    }

    /// <summary>« Non » à la première question : la photo de l'invité est réimprimée une fois.</summary>
    private async Task RetryPaperPrintAsync(PaperPrint print)
    {
        var health = _services.PrinterMonitor.Current;
        if (!health.CanPrint && health.Condition != PrinterCondition.Unknown)
        {
            // L'imprimante signale elle-même un problème (capot, bourrage…) : pas de nouvel essai, le rouleau n'est pas en cause.
            _log.Information("Pas de nouvel essai : l'imprimante signale « {Message} »", health.Message);
            ShowNotice("L'imprimante a un problème", "Votre photo est bien enregistrée : prévenez l'animateur.");
            return;
        }

        _paperStep = PaperStep.WaitingRetry;
        ShowPaper("Nous réimprimons votre photo…", "Patientez quelques secondes devant l'imprimante.", busy: true);
        try
        {
            await _services.PrintPaperRetryAsync(print.Session.Event, print.Session.Id, print.Composite, print.Profile);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Nouvel essai d'impression impossible");
            if (_paperStep == PaperStep.WaitingRetry)
            {
                ShowNotice("L'impression n'a pas pu être relancée", "Votre photo est bien enregistrée : prévenez l'animateur.");
            }
            return;
        }
        if (_paperStep != PaperStep.WaitingRetry) return; // abandonné entre-temps (admin ouverte, nouvel invité)
        StartPaperTimer(PaperDelayForCapture ?? PaperCheck.QuestionDelay(1));
    }

    private void ShowPaper(string title, string? message, bool answers = false, bool busy = false, bool ok = false)
    {
        PaperQuestion.Title = title;
        PaperQuestion.Message = message;
        PaperQuestion.ShowAnswers = answers;
        PaperQuestion.IsBusy = busy;
        PaperQuestion.ShowOk = ok;
        PaperQuestion.IsVisible = true;
    }

    /// <summary>Message final (bouton « OK »), qui disparaît seul au bout de quelques secondes.</summary>
    private void ShowNotice(string title, string message)
    {
        _paperStep = PaperStep.Notice;
        ShowPaper(title, message, ok: true);
        StartPaperTimer(PaperCheck.EmptyNoticeDuration);
    }

    private void ClosePaperQuestion()
    {
        _paperTimer.Stop();
        _paperStep = PaperStep.None;
        _paperPrint = null;
        PaperQuestion.IsVisible = false;
        PaperQuestion.ShowAnswers = false;
        PaperQuestion.ShowOk = false;
        PaperQuestion.IsBusy = false;
    }

    /// <summary>Question abandonnée sans réponse : rien ne change, elle sera posée à la prochaine impression.</summary>
    private void DropPaperCheck(string reason)
    {
        if (_paperStep is PaperStep.None) return;
        if (_paperStep is not PaperStep.Notice)
        {
            _log.Information("Question sur le papier abandonnée ({Reason}) : elle sera posée à la prochaine impression", reason);
        }
        ClosePaperQuestion();
    }

    // ---------------------------------------------------------------- Fin

    public void Finish() => _machine.Finish();

    // ---------------------------------------------------------------- Admin et erreurs

    public async Task PauseForAdminAsync()
    {
        _paused = true;
        OnPropertyChanged(nameof(IsIdle));
        DropPaperCheck("admin ouverte");
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
