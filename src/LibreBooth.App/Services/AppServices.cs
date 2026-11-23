using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using LibreBooth.App.Hardware;
using LibreBooth.App.Hardware.Canon;
using LibreBooth.App.Hardware.Demo;
using LibreBooth.App.Hardware.Printing;
using LibreBooth.App.Infrastructure;
using LibreBooth.App.Kiosk;
using LibreBooth.Core.Config;
using LibreBooth.Core.Design;
using LibreBooth.Core.Events;
using LibreBooth.Core.Hardware;
using LibreBooth.Core.Layouts;
using LibreBooth.Core.Updates;
using LibreBooth.Core.Util;
using Serilog;

namespace LibreBooth.App.Services;

/// <summary>Tous les services de LibreBooth, créés une fois au démarrage.</summary>
public sealed class AppServices : IDisposable
{
    public const string DemoPrinterName = "Microsoft Print to PDF";

    private readonly ILogger _log = Log.ForContext<AppServices>();

    private AppServices(AppPaths paths, StartupOptions options, Dispatcher dispatcher)
    {
        Paths = paths;
        Options = options;
        Dispatcher = dispatcher;

        SettingsStore = new SettingsStore(paths.ConfigFile);
        Settings = SettingsStore.Load();

        Events = new EventStore(paths.EventsFolder);
        UpgradeOldEvents(Events, Settings);
        // L'événement choisi au lancement (ou le dernier ouvert, après un plantage ou pour les vérifications).
        CurrentEvent = Events.Open(Settings.CurrentEventFolder) ?? Events.ListAll().FirstOrDefault() ?? Events.Create(null, DateTime.Today);
        Settings.CurrentEventFolder = CurrentEvent.Folder;

        Templates = new TemplateLibrary(paths.TemplatesFolder);
        TemplateSeeder.SeedIfNeeded(Templates);
        Customization = CustomizationStore.Load(CurrentEvent.Folder);
        EnsureActiveTemplates();

        Printer = new WindowsPrinterService();
        AutoConfigurePrinter();
        PrinterMonitor = new PrinterMonitor(this);
        Camera = CreateCamera();
        SaveSettings();
    }

    public AppPaths Paths { get; }
    public StartupOptions Options { get; }
    public Dispatcher Dispatcher { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; }
    public EventStore Events { get; }
    public BoothEvent CurrentEvent { get; private set; }
    /// <summary>Personnalisation de l'événement en cours : formats proposés, exemplaires, thème, textes, fond, logo.</summary>
    public EventCustomization Customization { get; private set; }
    public TemplateLibrary Templates { get; }
    public LayoutCompositor Compositor { get; } = new();
    public ICamera Camera { get; private set; }
    public IPrinterService Printer { get; }
    public PrinterMonitor PrinterMonitor { get; }
    public KioskViewModel? Kiosk { get; set; }

    /// <summary>Nom de l'imprimante réellement utilisée (en démo : l'imprimante PDF de Windows).</summary>
    public string? EffectivePrinterName => Options.PrintToPdf ? DemoPrinterName : Settings.Print.PrinterName;

    public event EventHandler? CameraChanged;
    public event EventHandler? EventChanged;

    public static AppServices Create(AppPaths paths, StartupOptions options, Dispatcher dispatcher) => new(paths, options, dispatcher);

    public void Start()
    {
        Camera.Start();
        PrinterMonitor.Start();
        PregeneratePreviewsInBackground();
        if (Settings.CheckForUpdates && !Options.IsCaptureRun) _ = CheckForUpdatesAtStartupAsync();
    }

    // ---------------------------------------------------------------- Appareil photo

    private ICamera CreateCamera() =>
        Options.Demo || Settings.Camera.Kind == CameraKind.Demo
            ? new DemoCamera()
            : new CanonCamera(() => Settings.Camera);

    /// <summary>
    /// Ajoute le SDK Canon (zip téléchargé chez Canon) dans le dossier « Pilotes ». Renvoie la version d'EDSDK.dll.
    /// Peut tourner hors du fil de l'interface ; appeler ensuite <see cref="RestartCamera"/> pour l'utiliser tout de suite.
    /// </summary>
    public string ImportCanonSdk(string source)
    {
        var version = CanonSdkImport.Import(source, NativeDrivers.UserFolder);
        _log.Information("SDK Canon ajouté dans {Folder} (EDSDK {Version})", NativeDrivers.UserFolder, version);
        NativeDrivers.Refresh();
        return version;
    }

    /// <summary>Relance l'appareil Canon (après l'ajout du SDK), sans redémarrer LibreBooth. Sur le fil de l'interface.</summary>
    public void RestartCamera()
    {
        if (Camera is DemoCamera) return;
        // Le SDK Canon est commun à tout le programme : l'ancien appareil le libère avant que le nouveau ne le démarre.
        Camera.Dispose();
        Camera = CreateCamera();
        _log.Information("Appareil photo relancé : {Camera}", Camera.Name);
        Camera.Start();
        CameraChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Remplace l'appareil si le type choisi dans l'admin a changé (Canon ↔ démo).</summary>
    public void SwitchCameraIfNeeded()
    {
        var wantDemo = Options.Demo || Settings.Camera.Kind == CameraKind.Demo;
        if (Camera is DemoCamera == wantDemo) return;

        var previous = Camera;
        Camera = CreateCamera();
        _log.Information("Changement d'appareil : {Camera}", Camera.Name);
        Camera.Start();
        CameraChanged?.Invoke(this, EventArgs.Empty);
        previous.Dispose();
    }

    // ---------------------------------------------------------------- Modèles et personnalisation

    /// <summary>Modèles proposés aux invités pour l'événement en cours, dans l'ordre choisi.</summary>
    public IReadOnlyList<Template> ActiveTemplates()
    {
        var all = Templates.LoadAll();
        return Customization.Templates
            .Select(id => all.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
            .Where(t => t is { ShotCount: > 0 })
            .Select(t => t!)
            .ToList();
    }

    /// <summary>Retire les modèles supprimés ; sans aucun modèle, propose les deux modèles de LibreBooth.</summary>
    public void EnsureActiveTemplates()
    {
        var all = Templates.LoadAll();
        var list = Customization.Templates;
        var removed = list.RemoveAll(id => all.All(t => !string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)));
        if (list.Count == 0 && all.Count > 0)
        {
            var defaults = TemplateSeeder.DefaultTemplateIds.Where(id => all.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))).ToList();
            list.AddRange(defaults.Count > 0 ? defaults : new List<string> { all[0].Id });
        }
        if (removed > 0 && !Customization.IsNew) SaveCustomization();
    }

    /// <summary>Dossier de personnalisation de l'événement en cours (fonds, logo, aperçus).</summary>
    public string CustomizationFolder => CustomizationStore.FolderFor(CurrentEvent.Folder);

    /// <summary>Copie figée de l'apparence de l'événement : un montage en cours ne bouge pas si l'admin la modifie.</summary>
    public DesignContext CurrentDesign() => new(JsonDefaults.Clone(Customization.Design), CustomizationFolder);

    public PreviewContext CurrentPreviewContext() =>
        new(CurrentEvent.Name, CurrentEvent.Date, CurrentDesign(), CustomizationStore.PreviewsFolderFor(CurrentEvent.Folder));

    /// <summary>
    /// Couleurs des écrans du photobooth pour l'événement en cours : celles du fond PNG d'un format proposé aux
    /// invités s'il y en a un, sinon celles du cadre importé du premier format, sinon celles du thème.
    /// </summary>
    public KioskPalette CurrentKioskPalette()
    {
        try
        {
            var design = Customization.Design;
            var templates = ActiveTemplates();
            foreach (var template in templates.Where(t => t.Layout.IsCustomizable))
            {
                var file = design.BackgroundFileFor(template.Layout.DesignStyle);
                var path = file == null ? null : Path.Combine(CustomizationFolder, file);
                if (path == null || !File.Exists(path)) continue;
                var name = DesignStyles.IsBookmark(template.Layout.DesignStyle) ? "votre fond du marque-page" : "votre fond du 10x15";
                return KioskPalette.Create(design, path, name);
            }
            var first = templates.FirstOrDefault();
            if (first is { Layout.IsCustomizable: false, FramePath: { } frame } && File.Exists(frame))
            {
                return KioskPalette.Create(design, frame, $"le cadre « {first.Name} »");
            }
            return KioskPalette.Create(design);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couleurs des écrans impossibles à calculer : couleurs de LibreBooth utilisées");
            return KioskPalette.Default;
        }
    }

    /// <summary>Image de l'écran d'accueil : celle de l'événement, sinon l'ancienne image commune (versions 1.0 à 1.2).</summary>
    public string? CurrentAttractBackground()
    {
        var own = CustomizationStore.AttractBackgroundPath(CurrentEvent.Folder, Customization);
        if (own != null) return own;
        var legacy = Settings.Screens.AttractBackground;
        return !string.IsNullOrWhiteSpace(legacy) && File.Exists(legacy) ? legacy : null;
    }

    public void SaveCustomization()
    {
        try
        {
            CustomizationStore.Save(CurrentEvent.Folder, Customization);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Impossible d'enregistrer la personnalisation de l'événement");
        }
    }

    /// <summary>Remplace la personnalisation (copiée d'un autre événement par exemple).</summary>
    public void ReplaceCustomization(EventCustomization customization)
    {
        Customization = customization;
        EnsureActiveTemplates();
    }

    /// <summary>Prépare les aperçus des modèles pour l'événement en cours (à appeler depuis le thread de l'interface).</summary>
    public void PregeneratePreviewsInBackground()
    {
        PreviewContext context;
        try
        {
            context = CurrentPreviewContext();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Aperçus non préparés");
            return;
        }
        _ = Task.Run(() =>
        {
            foreach (var template in Templates.LoadAll())
            {
                try
                {
                    TemplatePreview.GetOrCreate(Compositor, template, context);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Aperçu impossible pour le modèle {Template}", template.Id);
                }
            }
        });
    }

    // ---------------------------------------------------------------- Événements

    /// <summary>
    /// Convertit les événements des versions 1.x au rangement de la 2.0.0 (Photos, Montages, dossier caché) et suit
    /// le dossier de l'événement en cours s'il a été renommé.
    /// </summary>
    public static void UpgradeOldEvents(EventStore events, AppSettings settings) =>
        settings.CurrentEventFolder = EventUpgrade.Follow(events.UpgradeOldEvents(), settings.CurrentEventFolder);

    /// <summary>Renomme l'événement en cours ; son dossier suit. False si le dossier a dû garder son ancien nom.</summary>
    public bool RenameCurrentEvent(string name, DateTime date)
    {
        var renamed = Events.Rename(CurrentEvent, name, date);
        Settings.CurrentEventFolder = CurrentEvent.Folder;
        SaveSettings();
        _log.Information("Événement renommé : {Event} ({Date:dd.MM.yyyy})", CurrentEvent.Name, CurrentEvent.Date);
        return renamed;
    }

    public void OpenEvent(BoothEvent ev)
    {
        CurrentEvent = ev;
        Settings.CurrentEventFolder = ev.Folder;
        Customization = CustomizationStore.Load(ev.Folder);
        EnsureActiveTemplates();
        SaveSettings();
        _log.Information("Événement actif : {Event}", ev.Name);
        EventChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- Impression

    public byte[]? DriverSettingsFor(string profile)
    {
        if (Options.PrintToPdf) return null;
        if (!Settings.Print.DriverSettings.TryGetValue(profile, out var base64) || string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Au démarrage : choisit la DNP si aucune imprimante n'est réglée, puis prépare ses réglages « 10x15 »
    /// et « Bandelettes » s'ils n'existent pas encore. Rien n'est modifié si tu as déjà fait tes réglages.
    /// </summary>
    private void AutoConfigurePrinter()
    {
        if (Options.PrintToPdf) return;
        var print = Settings.Print;
        try
        {
            if (string.IsNullOrWhiteSpace(print.PrinterName))
            {
                foreach (var name in Printer.GetInstalledPrinters())
                {
                    if (!DriverProfileBuilder.LooksLikeDnp(name, Printer.GetQueueInfo(name).DriverName)) continue;
                    print.PrinterName = name;
                    _log.Information("Imprimante DNP détectée et choisie : {Printer}", name);
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(print.PrinterName)) return;
            var queue = Printer.GetQueueInfo(print.PrinterName);
            if (!queue.Exists || !DriverProfileBuilder.LooksLikeDnp(print.PrinterName, queue.DriverName)) return;

            if (print.DriverSettings.ContainsKey(PrintProfiles.Postcard) || print.DriverSettings.ContainsKey(PrintProfiles.Strips))
            {
                // Réglages automatiques d'une version précédente : le bloc privé du pilote DNP n'était pas rempli,
                // si bien que la coupe 2 pouces n'était pas faite. On refait seulement ceux-là ; les réglages faits
                // dans la fenêtre du pilote (bloc rempli par elle) ne sont pas touchés.
                var outdated = print.DriverSettings
                    .Where(pair => DecodeDevMode(pair.Value) is { } bytes && DnpPrivateSettings.NeedsSync(bytes))
                    .Select(pair => pair.Key)
                    .ToList();
                if (outdated.Count == 0) return;
                var fresh = DriverProfileBuilder.CreateDnpProfiles(print.PrinterName).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var name in outdated.Where(fresh.ContainsKey))
                {
                    print.DriverSettings[name] = Convert.ToBase64String(fresh[name].DevMode);
                }
                SaveSettings();
                _log.Information("Réglages DNP d'une version précédente refaits pour que la coupe 2 pouces soit transmise : {Profiles}", string.Join(", ", outdated));
                return;
            }
            ApplyDnpProfiles();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Configuration automatique de l'imprimante impossible");
        }
    }

    private static byte[]? DecodeDevMode(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Crée (ou recrée) les réglages DNP « 10x15 » (coupe standard) et « Bandelettes » (coupe 2 pouces).</summary>
    public IReadOnlyList<string> ApplyDnpProfiles()
    {
        var printer = Settings.Print.PrinterName;
        if (string.IsNullOrWhiteSpace(printer)) throw new PrinterException("Aucune imprimante choisie.");
        var profiles = DriverProfileBuilder.CreateDnpProfiles(printer);
        if (profiles.Count == 0)
        {
            throw new PrinterException("Ce pilote ne propose pas la coupe 2 pouces : faites les réglages à la main avec « Réglages du pilote… ».");
        }
        foreach (var profile in profiles) Settings.Print.DriverSettings[profile.Name] = Convert.ToBase64String(profile.DevMode);
        SaveSettings();
        var lines = profiles.Select(p => $"{p.Name} : {p.Description}").ToList();
        _log.Information("Réglages DNP automatiques pour {Printer} : {Profiles}", printer, string.Join(" ; ", lines));
        return lines;
    }

    /// <summary>Imprime un montage d'événement (session de l'invité ou réimpression depuis l'admin).</summary>
    public async Task PrintCompositeAsync(BoothEvent ev, string sessionId, string compositePath, string profile, int copies)
    {
        var printerName = EffectivePrinterName;
        if (string.IsNullOrWhiteSpace(printerName)) throw new PrinterException("Aucune imprimante choisie (admin > Impression).");

        string? pdf = null;
        if (Options.PrintToPdf)
        {
            Directory.CreateDirectory(ev.DemoPrintsFolder);
            pdf = Path.Combine(ev.DemoPrintsFolder, $"{sessionId}_{DateTime.Now:HHmmss}.pdf");
        }

        var request = new PrintJobRequest(compositePath, printerName, DriverSettingsFor(profile), copies, $"LibreBooth {sessionId}", pdf, FitWholeImage: Options.PrintToPdf);
        await Printer.PrintAsync(request).ConfigureAwait(true);

        Events.UpdateStats(ev, s => s.Prints += copies);
        Events.UpdateSession(ev, sessionId, r => r.Prints += copies);
        Settings.Print.PrintedSinceMediaChange += copies;
        SaveSettings();
        PrinterMonitor.RefreshSoon();
        _log.Information("Impression : {Copies} × {File} sur {Printer}", copies, Path.GetFileName(compositePath), printerName);
    }

    // ---------------------------------------------------------------- Réglages

    public void SaveSettings()
    {
        try
        {
            SettingsStore.Save(Settings);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Impossible d'enregistrer la configuration");
        }
    }

    /// <summary>Applique les réglages modifiés dans l'admin.</summary>
    public void ApplySettingsAfterAdmin()
    {
        Settings.Normalize();
        EnsureActiveTemplates();
        SaveSettings();
        SwitchCameraIfNeeded();
        if (!Options.IsCaptureRun)
        {
            try
            {
                StartupRegistration.Set(Settings.LaunchAtStartup);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Réglage du lancement au démarrage impossible");
            }
        }
        PrinterMonitor.RefreshSoon(TimeSpan.Zero);
        PregeneratePreviewsInBackground();
    }

    // ---------------------------------------------------------------- Mises à jour

    /// <summary>Dossier du téléchargement des mises à jour (vidé au lancement suivant, et par la désinstallation).</summary>
    public static string UpdateDownloadFolder => Path.Combine(Path.GetTempPath(), "LibreBooth-mise-a-jour");

    /// <summary>Copie installée par le programme d'installation : elle peut se mettre à jour elle-même.</summary>
    public static bool IsInstalledCopy => InstalledCopyForCapture ?? File.Exists(Path.Combine(AppContext.BaseDirectory, "fichiers-installes.txt"));

    /// <summary>Captures de vérification : montre l'admin d'une copie installée (true) ou d'une copie décompressée (false).</summary>
    internal static bool? InstalledCopyForCapture { get; set; }

    /// <summary>Captures de vérification : une nouvelle version fictive, comme si GitHub l'avait annoncée.</summary>
    internal void ShowUpdateForCapture(UpdateInfo? update)
    {
        AvailableUpdate = update;
        LastUpdateCheck = DateTime.Now;
        UpdateCheckError = null;
        UpdateStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public static Version CurrentVersion => UpdateChecker.TryParseVersion(App.Version, out var version) ? version : new Version(0, 0, 0);

    /// <summary>Nouvelle version trouvée sur GitHub (null : à jour, ou pas encore cherché).</summary>
    public UpdateInfo? AvailableUpdate { get; private set; }
    public DateTime? LastUpdateCheck { get; private set; }
    public string? UpdateCheckError { get; private set; }
    public bool IsCheckingForUpdates { get; private set; }

    /// <summary>Résultat de la recherche changé (toujours sur le fil de l'interface).</summary>
    public event EventHandler? UpdateStatusChanged;

    private Uri UpdateSource => Options.UpdateSource ?? UpdateChecker.LatestReleaseUri;

    /// <summary>Recherche au lancement, quelques secondes après, si l'option est cochée (jamais pendant les vérifications).</summary>
    private async Task CheckForUpdatesAtStartupAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        // Programme d'installation d'une mise à jour précédente : il a fini son travail.
        try
        {
            if (!_downloadingUpdate && Directory.Exists(UpdateDownloadFolder)) Directory.Delete(UpdateDownloadFolder, recursive: true);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Ancien téléchargement de mise à jour gardé");
        }
        await CheckForUpdatesAsync();
    }

    /// <summary>Demande à GitHub la dernière version publiée. Sans Internet, rien n'est affiché au photobooth.</summary>
    public async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdates) return;
        IsCheckingForUpdates = true;
        UpdateStatusChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) throw new HttpRequestException("pas de connexion réseau");
            using var http = UpdateChecker.CreateHttpClient(App.Version, TimeSpan.FromSeconds(20));
            var latest = await UpdateChecker.GetLatestAsync(http, UpdateSource, CancellationToken.None);
            AvailableUpdate = latest != null && UpdateChecker.IsNewer(latest, CurrentVersion) ? latest : null;
            UpdateCheckError = null;
            LastUpdateCheck = DateTime.Now;
            _log.Information("Mises à jour : {Result}", AvailableUpdate != null
                ? $"version {AvailableUpdate.Version.ToString(3)} disponible"
                : $"LibreBooth {App.Version} est à jour (dernière publiée : {latest?.Version.ToString(3) ?? "aucune"})");
        }
        catch (Exception ex)
        {
            UpdateCheckError = ex is HttpRequestException or TaskCanceledException
                ? "GitHub n'a pas pu être joint (pas de connexion Internet ?)."
                : "Réponse de GitHub illisible.";
            _log.Information("Recherche de mise à jour impossible : {Message}", ex.Message);
        }
        finally
        {
            IsCheckingForUpdates = false;
            UpdateStatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Télécharge le programme d'installation de la nouvelle version et contrôle son empreinte.</summary>
    public async Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<double> progress, CancellationToken cancellation)
    {
        var folder = UpdateDownloadFolder;
        _downloadingUpdate = true;
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            using var http = UpdateChecker.CreateHttpClient(App.Version, TimeSpan.FromMinutes(30));
            var installer = await UpdateDownloader.DownloadAsync(http, update, folder, progress, cancellation);
            _log.Information("Mise à jour {Version} téléchargée et contrôlée : {File}", update.Version.ToString(3), installer);
            return installer;
        }
        finally
        {
            _downloadingUpdate = false;
        }
    }

    private bool _downloadingUpdate;

    /// <summary>
    /// Lance la mise à jour (Windows demande l'autorisation). Le programme d'installation attend que LibreBooth soit
    /// fermé, installe la nouvelle version dans le même dossier, puis propose de relancer LibreBooth.
    /// </summary>
    /// <exception cref="System.ComponentModel.Win32Exception">Autorisation refusée (code 1223).</exception>
    public void LaunchUpdateInstaller(string installer)
    {
        var folder = AppContext.BaseDirectory.TrimEnd('\\');
        // Essai (fausse version publiée sur ce PC) : installation d'essai, sans droits d'administrateur.
        var test = Options.UpdateSource != null;
        var arguments = $"/mise-a-jour /dossier \"{folder}\"" + (test ? " /test" : "");
        using var process = Process.Start(new ProcessStartInfo(installer, arguments)
        {
            UseShellExecute = true,
            Verb = test ? "" : "runas",
            WorkingDirectory = Path.GetDirectoryName(installer)!,
        });
        _log.Information("Programme de mise à jour lancé : {Installer} {Arguments}", installer, arguments);
    }

    public void Dispose()
    {
        PrinterMonitor.Dispose();
        try
        {
            Camera.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Arrêt de l'appareil photo");
        }
        SaveSettings();
    }
}
