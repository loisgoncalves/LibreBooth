using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibreBooth.App.Hardware;
using LibreBooth.App.Hardware.Printing;
using LibreBooth.App.Infrastructure;
using LibreBooth.App.Kiosk;
using LibreBooth.App.Services;
using LibreBooth.Core.Admin;
using LibreBooth.Core.Config;
using LibreBooth.Core.Events;
using LibreBooth.Core.Hardware;
using LibreBooth.Core.Layouts;
using LibreBooth.Core.Updates;
using LibreBooth.Core.Util;
using Microsoft.Win32;
using Serilog;

namespace LibreBooth.App.Admin;

/// <summary>Les pages de l'admin, dans l'ordre du menu de gauche.</summary>
public enum AdminPage
{
    Home,
    Event,
    Customization,
    Camera,
    Print,
    Screens,
    System,
}

/// <summary>
/// Administration de LibreBooth (protégée par le code PIN) : un menu à gauche, une page par sujet, et en bas l'état
/// de l'appareil, de l'imprimante et du papier, avec « Démarrer le photobooth ».
/// </summary>
public partial class AdminWindow : Window
{
    /// <summary>En dessous de cette largeur, le menu de gauche se réduit à ses icônes.</summary>
    private const double CompactBelow = 1150;

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private readonly ILogger _log = Log.ForContext<AdminWindow>();
    private readonly AppServices _services;
    private readonly KioskViewModel _kiosk;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<ProfileItem> _profiles = new();
    private readonly ObservableCollection<GalleryItem> _gallery = new();
    private readonly ObservableCollection<ReadinessRow> _readiness = new();
    private IReadOnlyList<ReadinessItem> _readinessItems = Array.Empty<ReadinessItem>();
    private IReadOnlyList<string> _formatNames = Array.Empty<string>();
    private long? _freeDiskBytes;
    private string? _diskName;
    private bool _canonSdkMissing;
    private LiveViewDecoder? _liveDecoder;
    private bool _loading;
    private int _tick;

    public AdminWindow(AppServices services, KioskViewModel kiosk)
    {
        InitializeComponent();
        AdminTheme.Attach(this);
        _services = services;
        _kiosk = kiosk;
        HeaderVersionText.Text = $"Version {App.Version}";
        RetakeCombo.ItemsSource = Enumerable.Range(0, ScreenSettings.MaxRetakes + 1).ToList();

        // Fenêtre normale (pas de plein écran), à la taille de l'écran s'il est plus petit.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width - 40));
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 40));

        // Les listes sont remplies AVANT de brancher les réglages, pour que les sélections soient bien reprises.
        ProfilesList.ItemsSource = _profiles;
        GalleryList.ItemsSource = _gallery;
        ReadinessList.ItemsSource = _readiness;
        RotationCombo.ItemsSource = new[] { 0, 90, 180, 270 };
        var keys = new List<string> { "Space", "Enter", "B", "F5", "PageDown" };
        if (!keys.Contains(services.Settings.Screens.StartKey)) keys.Add(services.Settings.Screens.StartKey);
        StartKeyCombo.ItemsSource = keys;
        RootGrid.DataContext = services.Settings;
        CustomizationView.Initialize(services);
        CustomizationView.TemplatesChanged += (_, _) => LoadProfiles();

        CameraChip.ToolTip = "Ouvrir la page Appareil photo";
        PrinterChip.ToolTip = "Ouvrir la page Impression";
        PaperChip.ToolTip = "Ouvrir la page Impression";
        ShowPage(AdminPage.Home);

        Loaded += (_, _) =>
        {
            LoadAll();
            _refreshTimer.Start();
        };
        SizeChanged += (_, _) => UpdateCompactMode();
        _refreshTimer.Tick += (_, _) => RefreshLiveStatus();
        Closing += OnClosing;
        services.UpdateStatusChanged += OnUpdateStatusChanged;
    }

    /// <summary>« Quitter LibreBooth » (bouton du bas, ou croix de la fenêtre confirmée).</summary>
    public bool QuitRequested { get; private set; }

    /// <summary>« Démarrer le photobooth ».</summary>
    public bool StartRequested { get; private set; }

    /// <summary>Page affichée.</summary>
    public AdminPage CurrentPage { get; private set; } = AdminPage.Home;

    private bool _closeWithoutPrompt;

    /// <summary>Ferme sans rien demander (captures de vérification).</summary>
    internal void CloseWithoutPrompt()
    {
        _closeWithoutPrompt = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // La croix de la fenêtre : l'admin est la fenêtre principale, la fermer veut dire quitter LibreBooth.
        if (!StartRequested && !QuitRequested && !_closeWithoutPrompt)
        {
            if (!AdminDialog.Ask(this,
                    "Quitter LibreBooth ?\n\nPour lancer le photobooth, cliquez plutôt sur « Démarrer le photobooth ».",
                    "Quitter LibreBooth", "Rester dans l'admin"))
            {
                e.Cancel = true;
                return;
            }
            QuitRequested = true;
        }
        OnClosingAdmin();
    }

    /// <summary>Chargement des pages sans afficher la fenêtre (captures de vérification).</summary>
    internal void LoadForCapture() => LoadAll();

    private void LoadAll()
    {
        LoadEventTab();
        CustomizationView.LoadFromEvent();
        LoadCameraTab();
        LoadPrintTab();
        LoadSystemTab();
        RefreshNow();
    }

    private void OnClosingAdmin()
    {
        _services.UpdateStatusChanged -= OnUpdateStatusChanged;
        _updateDownload?.Cancel();
        CustomizationView.Flush();
        StopLiveTest();
        _refreshTimer.Stop();
        _services.Settings.Normalize();
        _services.SaveSettings();
    }

    /// <summary>« Démarrer le photobooth » : avec moins de 2 Go libres, l'animateur est d'abord prévenu (il peut démarrer quand même).</summary>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_services.ProbePhotoDisk() is { } disk && disk.FreeBytes < DiskSpace.StartMinimumBytes)
        {
            _log.Warning("Démarrage du photobooth avec peu d'espace disque : {Free} libres sur {Drive}", Readiness.Size(disk.FreeBytes), disk.DriveName);
            if (!AdminDialog.Ask(this, LowDiskMessage(disk.FreeBytes, disk.DriveName), "Démarrer quand même", "Annuler",
                    MessageBoxImage.Warning, defaultConfirm: false))
            {
                _log.Information("Démarrage du photobooth annulé (espace disque)");
                RefreshNow();
                return;
            }
        }
        StartRequested = true;
        Close();
    }

    /// <summary>Le message d'avant le démarrage, quand le disque des photos a moins de 2 Go libres.</summary>
    internal static string LowDiskMessage(long freeBytes, string driveName)
    {
        var free = Readiness.Size(freeBytes);
        var situation = DiskSpace.AllowsSession(freeBytes)
            ? $"Peu de place pour les photos : {free} libres sur le disque {driveName}, soit {DiskSpace.SessionsText(freeBytes)}.\n\n"
              + "Pour un événement, gardez au moins 2 Go libres : sous 300 Mo, le photobooth met les sessions en pause."
            : $"Plus assez de place pour les photos : {free} libres sur le disque {driveName}.\n\n"
              + "Le photobooth ne lancera aucune session tant que vous n'aurez pas fait de la place.";
        return situation + "\n\nPour libérer de la place : videz la corbeille de Windows, ou déplacez d'anciens événements "
               + "(dossier Evenements des données de LibreBooth, page Système) sur une clé USB ou un disque externe.";
    }

    private void ShowError(string message) =>
        AdminDialog.Show(this, message, "LibreBooth", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ================================================================ Menu et pages

    private IEnumerable<(AdminPage Page, FrameworkElement Element)> PageElements() => new (AdminPage, FrameworkElement)[]
    {
        (AdminPage.Home, HomePage), (AdminPage.Event, EventPage), (AdminPage.Customization, CustomizationPage),
        (AdminPage.Camera, CameraPage), (AdminPage.Print, PrintPage), (AdminPage.Screens, ScreensPage), (AdminPage.System, SystemPage),
    };

    /// <summary>Affiche une page et la sélectionne dans le menu.</summary>
    internal void ShowPage(AdminPage page)
    {
        CurrentPage = page;
        foreach (var (value, element) in PageElements())
        {
            element.Visibility = value == page ? Visibility.Visible : Visibility.Collapsed;
        }
        var item = NavList.Items.OfType<ListBoxItem>().First(i => Equals(i.Tag, page));
        if (!ReferenceEquals(NavList.SelectedItem, item)) NavList.SelectedItem = item;
    }

    /// <summary>Affiche une page et fait défiler jusqu'à un élément.</summary>
    private void ShowPage(AdminPage page, FrameworkElement target)
    {
        ShowPage(page);
        Dispatcher.BeginInvoke(() => target.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is ListBoxItem { Tag: AdminPage page }) ShowPage(page);
        // Ctrl + clic désélectionne : le menu garde la page affichée.
        else if (NavList.SelectedItem == null) ShowPage(CurrentPage);
    }

    /// <summary>Fenêtre étroite : le menu ne garde que les icônes (le nom de la page s'affiche au survol).</summary>
    private void UpdateCompactMode()
    {
        var compact = ActualWidth > 0 && ActualWidth < CompactBelow;
        if (AdminUi.GetCompact(Sidebar) == compact) return;
        AdminUi.SetCompact(Sidebar, compact);
        SidebarColumn.Width = new GridLength(compact ? 72 : 248);
        BrandTexts.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SidebarEventCard.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShortcutCamera_Click(object sender, RoutedEventArgs e) => ShowPage(AdminPage.Camera, TestPhotoButton);

    private void ShortcutPrintTest_Click(object sender, RoutedEventArgs e) => ShowPage(AdminPage.Customization);

    private void ShortcutReprint_Click(object sender, RoutedEventArgs e) => ShowPage(AdminPage.Event, GalleryList);

    private void CameraChip_Click(object sender, RoutedEventArgs e) => ShowPage(AdminPage.Camera);

    private void PrinterChip_Click(object sender, RoutedEventArgs e) => ShowPage(AdminPage.Print);

    private void ReadinessAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ReadinessRow row) ShowPage(row.Page);
    }

    // ================================================================ Rafraîchissement (chaque seconde)

    /// <summary>Tout de suite, y compris ce qui n'est relu que toutes les 3 secondes (chiffres, formats, disque).</summary>
    private void RefreshNow()
    {
        _tick = 0;
        RefreshLiveStatus();
    }

    private void RefreshLiveStatus()
    {
        var camera = _services.Camera;
        // « Canon EOS 1300D : prêt » en vert ; le message de l'appareil seulement s'il apprend quelque chose
        // (quand l'appareil est prêt, il ne fait que répéter « prêt »).
        CameraStatusText.Text = $"{(camera.State == CameraState.Ready ? "✓ " : "")}{camera.Name} : {StateLabel(camera.State)}";
        CameraStatusText.SetResourceReference(TextBlock.ForegroundProperty, camera.State switch
        {
            CameraState.Ready => "Admin.Success",
            CameraState.Error => "Admin.Error",
            _ => "Admin.Warning",
        });
        CameraDetailText.Text = camera.State == CameraState.Ready ? "" : camera.StatusMessage ?? "";
        BatteryText.Text = camera.BatteryLevel switch
        {
            null => "Batterie : inconnue",
            -1 => "Alimentation : secteur",
            var level => $"Batterie : {level} %",
        };
        UpdatePrinterTexts();

        if (_tick++ % 3 == 0)
        {
            UpdateStatsText();
            RefreshSlowFacts();
        }
        UpdateReadiness();
    }

    private static string StateLabel(CameraState state) => state switch
    {
        CameraState.Ready => "prêt",
        CameraState.Connecting => "connexion",
        CameraState.Disconnected => "non connecté",
        _ => "erreur",
    };

    /// <summary>Formats proposés et espace disque : lus sur le disque, donc pas à chaque seconde.</summary>
    private void RefreshSlowFacts()
    {
        try
        {
            _formatNames = _services.ActiveTemplates().Select(t => t.Name).ToList();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Formats proposés illisibles");
        }

        var disk = _services.ProbePhotoDisk();
        _freeDiskBytes = disk?.FreeBytes;
        _diskName = disk?.DriveName;
    }

    private ReadinessInput BuildReadinessInput()
    {
        var settings = _services.Settings;
        var camera = _services.Camera;
        var monitor = _services.PrinterMonitor;
        var health = monitor.Current;
        return new ReadinessInput
        {
            DemoMode = _services.Options.Demo,
            CameraKind = settings.Camera.Kind,
            CanonSdkMissing = _canonSdkMissing && settings.Camera.Kind == CameraKind.Canon,
            CameraState = camera.State,
            CameraName = camera.Name,
            CameraMessage = camera.StatusMessage,
            BatteryLevel = camera.BatteryLevel,
            PrintEnabled = settings.Print.Enabled,
            PrintToPdf = _services.Options.PrintToPdf,
            PrinterName = _services.EffectivePrinterName,
            PrinterChecked = monitor.HasChecked,
            PrinterCanPrint = health.CanPrint,
            PrinterCondition = health.Condition,
            PrinterMessage = health.Message,
            RemainingPrints = health.RemainingPrints,
            LowMedia = health.LowMedia,
            Formats = _formatNames,
            EventPrints = _services.CurrentEvent.Info.Stats.Prints,
            EventPrintLimit = settings.Print.EventPrintLimit,
            FreeDiskBytes = _freeDiskBytes,
            DiskName = _diskName,
            AvailableUpdate = _services.AvailableUpdate?.Version.ToString(3),
        };
    }

    /// <summary>Liste « Prêt pour l'événement ? » de l'accueil et pastilles du bas.</summary>
    private void UpdateReadiness()
    {
        var items = Readiness.Evaluate(BuildReadinessInput());
        UpdateChips(items);
        if (items.SequenceEqual(_readinessItems)) return;

        // Mise à jour ligne par ligne : la liste ne clignote pas, et le bouton qui a le focus le garde.
        var sameAreas = items.Count == _readinessItems.Count && items.Select(i => i.Area).SequenceEqual(_readinessItems.Select(i => i.Area));
        if (!sameAreas) _readiness.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            var row = ReadinessRow.For(items[i]);
            if (!sameAreas) _readiness.Add(row);
            else if (items[i] != _readinessItems[i]) _readiness[i] = row;
        }
        _readinessItems = items;

        var overall = Readiness.Overall(items);
        ReadinessSummaryText.Text = Readiness.Summary(items);
        ReadinessIcon.Text = ReadinessRow.GlyphFor(overall);
        ReadinessIcon.SetResourceReference(TextBlock.ForegroundProperty, ReadinessRow.BrushFor(overall));
        AutomationProperties.SetName(ReadinessSummaryText, "Prêt pour l'événement ? " + ReadinessSummaryText.Text);
    }

    private void UpdateChips(IReadOnlyList<ReadinessItem> items)
    {
        var camera = items.FirstOrDefault(i => i.Area == ReadinessArea.Camera);
        var printer = items.FirstOrDefault(i => i.Area == ReadinessArea.Printer);
        var paper = items.FirstOrDefault(i => i.Area == ReadinessArea.Paper);
        SetChip(CameraChip, CameraChipDot, CameraChipText, camera?.Level ?? ReadinessLevel.Info, CameraChipLabel());
        SetChip(PrinterChip, PrinterChipDot, PrinterChipText, printer?.Level ?? ReadinessLevel.Info, PrinterChipLabel(printer));
        PaperChip.Visibility = paper == null ? Visibility.Collapsed : Visibility.Visible;
        if (paper != null) SetChip(PaperChip, PaperChipDot, PaperChipText, paper.Level, PaperChipLabel());
    }

    private static void SetChip(Button chip, FrameworkElement dot, TextBlock text, ReadinessLevel level, string label)
    {
        text.Text = label;
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, ReadinessRow.BrushFor(level));
        AutomationProperties.SetName(chip, label);
    }

    private string CameraChipLabel()
    {
        if (_services.Options.Demo || _services.Settings.Camera.Kind == CameraKind.Demo) return "Appareil de démonstration";
        if (_canonSdkMissing) return "SDK Canon manquant";
        var camera = _services.Camera;
        return camera.State switch
        {
            CameraState.Ready => camera.BatteryLevel is int battery and >= 0 ? $"Appareil prêt · {battery} %" : "Appareil prêt",
            CameraState.Connecting => "Appareil : connexion…",
            CameraState.Disconnected => "Appareil non connecté",
            _ => "Appareil en erreur",
        };
    }

    private string PrinterChipLabel(ReadinessItem? item)
    {
        if (!_services.Settings.Print.Enabled) return "Impression désactivée";
        if (_services.Options.PrintToPdf) return "Impression en PDF (démo)";
        if (string.IsNullOrWhiteSpace(_services.EffectivePrinterName)) return "Aucune imprimante";
        if (!_services.PrinterMonitor.HasChecked) return "Imprimante : vérification…";
        return item?.Level switch
        {
            ReadinessLevel.Ok => "Imprimante prête",
            ReadinessLevel.Warning => "Imprimante à vérifier",
            _ => "Imprimante en erreur",
        };
    }

    private string PaperChipLabel() => _services.PrinterMonitor.Current.RemainingPrints switch
    {
        null => "Papier : inconnu",
        <= 0 => "Papier épuisé",
        1 => "Papier : 1 tirage",
        var remaining => $"Papier : {remaining} tirages",
    };

    // ================================================================ Événement

    private void LoadEventTab()
    {
        var ev = _services.CurrentEvent;
        var date = Capitalize(TextUtil.FormatLongDate(ev.Date));
        EventNameText.Text = ev.Name;
        EventDateText.Text = "Date : " + TextUtil.FormatLongDate(ev.Date);
        EventFolderText.Text = $"Dossier : {ev.Folder}\nDedans : « {BoothEvent.PhotosFolderName} » (photos de l'appareil), "
                               + $"« {BoothEvent.CompositesFolderName} » (photos imprimées) et « {BoothEvent.RefusedFolderName} » (séries refaites avec « Recommencer »).";
        RenameNameBox.Text = ev.Name;
        RenameDatePicker.SelectedDate = ev.Date;

        HomeEventName.Text = ev.Name;
        HomeEventDate.Text = date;
        SidebarEventName.Text = ev.Name;
        SidebarEventDate.Text = date;
        SidebarEventCard.ToolTip = $"Événement ouvert : {ev.Name}\n{date}";
        CustomizationSubtitle.Text = $"Pour l'événement « {ev.Name} » : formats proposés, thème ou vos images, textes et modèles. "
                                     + "Tout est enregistré au fur et à mesure.";
        UpdateStatsText();
        LoadGallery();
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], French) + text[1..];

    private void UpdateStatsText()
    {
        var stats = _services.CurrentEvent.Info.Stats;
        StatsText.Text = $"{stats.Sessions} session(s) · {stats.Photos} photo(s) · {stats.Prints} impression(s)"
                         + (stats.Retakes > 0 ? $" · {stats.Retakes} série(s) recommencée(s)" : "");
        SetStat(HomeSessionsText, HomeSessionsLabel, stats.Sessions, "session", "sessions");
        SetStat(HomePhotosText, HomePhotosLabel, stats.Photos, "photo", "photos");
        SetStat(HomePrintsText, HomePrintsLabel, stats.Prints, "impression", "impressions");
    }

    private static void SetStat(TextBlock number, TextBlock label, int value, string singular, string plural)
    {
        number.Text = value.ToString("N0", French);
        label.Text = value >= 2 ? plural : singular;
    }

    private void LoadGallery()
    {
        _gallery.Clear();
        var ev = _services.CurrentEvent;
        foreach (var record in _services.Events.ListSessions(ev, 60))
        {
            var path = Path.Combine(ev.CompositesFolder, record.Composite);
            if (!File.Exists(path)) continue;
            var prints = record.Prints switch
            {
                0 => "Pas imprimée",
                1 => "1 impression",
                var n => $"{n} impressions",
            };
            _gallery.Add(new GalleryItem(record, path, ImageLoader.FromFile(path, decodePixelWidth: 230),
                $"{record.CreatedAt:HH:mm} · {record.TemplateName}\n{prints}"));
        }
        GalleryStatusText.Text = _gallery.Count == 0 ? "Aucune photo pour l'instant." : $"{_gallery.Count} montage(s) affiché(s).";
    }

    private void OpenEventFolder_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(_services.CurrentEvent.Folder);

    private void RenameEvent_Click(object sender, RoutedEventArgs e)
    {
        CustomizationView.Flush();
        var ev = _services.CurrentEvent;
        var folderFollowed = _services.RenameCurrentEvent(RenameNameBox.Text, RenameDatePicker.SelectedDate ?? ev.Date);
        _kiosk.RefreshFromSettings();
        LoadEventTab();
        CustomizationView.LoadFromEvent();
        RefreshNow();
        if (!folderFollowed)
        {
            ShowError("L'événement est bien renommé, mais son dossier garde l'ancien nom : un fichier de ce dossier est sans doute "
                      + "ouvert dans un autre programme. Fermez-le, puis cliquez de nouveau sur « Enregistrer ».");
        }
    }

    /// <summary>Même fenêtre qu'au lancement : ouvrir un autre événement ou en créer un.</summary>
    private void ChangeEvent_Click(object sender, RoutedEventArgs e)
    {
        CustomizationView.Flush();
        var chooser = new EventChooserWindow(_services.Events, _services.CurrentEvent.Folder, atStartup: false) { Owner = this };
        if (chooser.ShowDialog() != true || chooser.Chosen is not { } ev) return;
        if (!EventStore.SamePath(ev.Folder, _services.CurrentEvent.Folder)) _services.OpenEvent(ev);
        LoadEventTab();
        CustomizationView.LoadFromEvent();
        RefreshNow();
    }

    private void RefreshGallery_Click(object sender, RoutedEventArgs e) => LoadGallery();

    private async void Reprint_Click(object sender, RoutedEventArgs e)
    {
        if (GalleryList.SelectedItem is not GalleryItem item)
        {
            GalleryStatusText.Text = "Choisissez d'abord une photo dans la galerie.";
            return;
        }
        try
        {
            GalleryStatusText.Text = "Impression en cours…";
            await _services.PrintCompositeAsync(_services.CurrentEvent, item.Record.Id, item.CompositePath, item.Record.PrintProfile, 1);
            GalleryStatusText.Text = "Réimpression lancée.";
            UpdateStatsText();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Réimpression impossible");
            GalleryStatusText.Text = "Réimpression impossible : " + ex.Message;
        }
    }

    // ================================================================ Appareil photo

    private void LoadCameraTab()
    {
        _loading = true;
        CanonRadio.IsChecked = _services.Settings.Camera.Kind == CameraKind.Canon;
        DemoRadio.IsChecked = _services.Settings.Camera.Kind == CameraKind.Demo;
        _loading = false;
        CameraModeHelp.Text = _services.Options.Demo
            ? "LibreBooth a été lancé avec --demo : l'appareil de démonstration est utilisé quel que soit ce choix."
            : "Le changement est immédiat. L'appareil Canon se reconnecte tout seul s'il est débranché puis rebranché.";
        UpdateCanonSdkTexts();
    }

    /// <summary>Le SDK Canon ne peut pas être livré avec LibreBooth : on indique s'il est là, et comment l'ajouter.</summary>
    private void UpdateCanonSdkTexts()
    {
        var missing = NativeDrivers.CanonFiles.Where(f => NativeDrivers.Find(f) == null).ToList();
        _canonSdkMissing = missing.Count > 0;
        if (missing.Count == 0)
        {
            var path = NativeDrivers.Find(NativeDrivers.CanonFiles[0])!;
            var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            CanonSdkText.Text = $"✓ SDK Canon présent (EDSDK {version})";
            CanonSdkText.SetResourceReference(TextBlock.ForegroundProperty, "Admin.Success");
            CanonSdkHelp.Text = $"Dossier : {Path.GetDirectoryName(path)}\n"
                                + "Pour passer à une version plus récente du SDK, téléchargez-la chez Canon puis ajoutez-la avec le bouton 2.";
            ImportCanonSdkButton.Content = "2. Remplacer le SDK Canon…";
        }
        else
        {
            CanonSdkText.Text = "SDK Canon manquant : sans lui, LibreBooth ne peut pas piloter l'appareil photo.";
            CanonSdkText.SetResourceReference(TextBlock.ForegroundProperty, "Admin.Error");
            CanonSdkHelp.Text = "Il appartient à Canon et ne peut pas être fourni avec LibreBooth. 1. Ouvrez la page Canon, "
                                + "inscrivez-vous (gratuit) et téléchargez le SDK « EDSDK » pour Windows. 2. Indiquez le fichier "
                                + "zip téléchargé : LibreBooth en extrait lui-même les bons fichiers (version 32 bits).";
            ImportCanonSdkButton.Content = "2. Ajouter le SDK Canon…";
        }
    }

    private void OpenCanonPage_Click(object sender, RoutedEventArgs e) => OpenInBrowser(CanonSdkImport.Page);

    private async void ImportCanonSdk_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "SDK Canon EDSDK téléchargé (fichier zip)",
            Filter = "SDK Canon (*.zip ; EDSDK.dll)|*.zip;EDSDK.dll|Tous les fichiers|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dialog.ShowDialog(this) != true) return;

        ImportCanonSdkButton.IsEnabled = false;
        try
        {
            var source = dialog.FileName;
            var version = await Task.Run(() => _services.ImportCanonSdk(source));
            StopLiveTest();
            _services.RestartCamera();
            UpdateCanonSdkTexts();
            RefreshLiveStatus();
            AdminDialog.Show(this, $"SDK Canon ajouté (EDSDK {version}).\n\nBranchez l'appareil photo en USB et allumez-le : "
                                   + "LibreBooth s'y connecte tout seul.", "LibreBooth", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "SDK Canon non ajouté");
            ShowError("Ce fichier ne convient pas : " + ex.Message);
        }
        finally
        {
            ImportCanonSdkButton.IsEnabled = true;
        }
    }

    /// <summary>Page web dans le navigateur habituel.</summary>
    internal static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Page impossible à ouvrir : {Url}", url);
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
            AdminDialog.Show(owner, $"Ouvrez cette adresse dans votre navigateur :\n{url}", "LibreBooth",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void CameraKind_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        StopLiveTest();
        _services.Settings.Camera.Kind = DemoRadio.IsChecked == true ? CameraKind.Demo : CameraKind.Canon;
        _services.SwitchCameraIfNeeded();
        RefreshLiveStatus();
    }

    private async void LiveTest_Click(object sender, RoutedEventArgs e)
    {
        if (_liveDecoder != null)
        {
            StopLiveTest();
            return;
        }
        try
        {
            _liveDecoder = new LiveViewDecoder(_services.Camera, Dispatcher, image => CameraPreview.Source = image);
            _liveDecoder.Attach();
            await _services.Camera.StartLiveViewAsync();
            LiveTestButton.Content = "Arrêter le live view";
            AdminUi.SetGlyph(LiveTestButton, "\uE71A");
            CameraTestStatus.Text = "Live view en cours.";
        }
        catch (Exception ex)
        {
            StopLiveTest();
            CameraTestStatus.Text = "Live view impossible : " + ex.Message;
        }
    }

    private void StopLiveTest()
    {
        if (_liveDecoder == null) return;
        _liveDecoder.Dispose();
        _liveDecoder = null;
        LiveTestButton.Content = "Démarrer le live view";
        AdminUi.SetGlyph(LiveTestButton, "\uE768");
        _ = StopLiveViewSafelyAsync();
    }

    private async Task StopLiveViewSafelyAsync()
    {
        try
        {
            await _services.Camera.StopLiveViewAsync();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Arrêt du live view de test");
        }
    }

    private async void TestPhoto_Click(object sender, RoutedEventArgs e)
    {
        TestPhotoButton.IsEnabled = false;
        CameraTestStatus.Text = "Photo en cours…";
        try
        {
            var bytes = await _services.Camera.CaptureAsync(allowAutofocus: true, CancellationToken.None);
            var folder = Path.Combine(_services.Paths.DataRoot, "tests");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"test-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
            await File.WriteAllBytesAsync(path, bytes);
            StopLiveTest();
            CameraPreview.Source = ImageLoader.FromFileOriented(path, 760, _services.Settings.Camera.Rotation);
            CameraTestStatus.Text = $"Photo de test reçue ({bytes.Length / 1024} Ko) : {path}";
        }
        catch (Exception ex)
        {
            CameraTestStatus.Text = "Échec de la photo de test : " + ex.Message;
        }
        finally
        {
            TestPhotoButton.IsEnabled = true;
        }
    }

    // ================================================================ Impression

    private void LoadPrintTab()
    {
        LoadPrinters();
        LoadProfiles();
        PrinterModeHelp.Text = _services.Options.PrintToPdf
            ? "Mode démo : les impressions sont enregistrées en PDF dans le dossier « impressions-demo » de l'événement."
            : "";
        PrinterModeHelp.Visibility = PrinterModeHelp.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdatePrinterTexts();
    }

    private void LoadPrinters()
    {
        _loading = true;
        var printers = _services.Printer.GetInstalledPrinters().ToList();
        var current = _services.Settings.Print.PrinterName;
        if (!string.IsNullOrWhiteSpace(current) && !printers.Contains(current)) printers.Insert(0, current);
        PrinterCombo.ItemsSource = printers;
        PrinterCombo.SelectedItem = current;
        _loading = false;
    }

    private void PrinterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PrinterCombo.SelectedItem is not string printer) return;
        _services.Settings.Print.PrinterName = printer;
        _services.SaveSettings();
        LoadProfiles();
        _ = _services.PrinterMonitor.RefreshAsync();
    }

    private void RefreshPrinters_Click(object sender, RoutedEventArgs e) => LoadPrinters();

    private void LoadProfiles()
    {
        _profiles.Clear();
        var names = _services.Templates.LoadAll()
            .Select(t => t.Layout.PrintProfile)
            .Concat(new[] { PrintProfiles.Postcard, PrintProfiles.Strips })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names) _profiles.Add(new ProfileItem(name, DescribeProfile(name)));
    }

    private string DescribeProfile(string profile)
    {
        if (!_services.Settings.Print.DriverSettings.TryGetValue(profile, out var base64) || string.IsNullOrWhiteSpace(base64))
        {
            return "Réglages par défaut du pilote";
        }
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var device = DevModeHelper.DeviceName(bytes);
            var printer = _services.Settings.Print.PrinterName ?? "";
            if (!printer.StartsWith(device, StringComparison.OrdinalIgnoreCase))
            {
                return $"⚠ Réglages enregistrés pour une autre imprimante ({device}) : refaites-les";
            }
            var cutter = DriverProfileBuilder.DescribeCutter(printer, bytes);
            if (cutter == "CUT_2INCH" && DnpPrivateSettings.NeedsSync(bytes))
            {
                return "⚠ Coupe 2 pouces non transmise au pilote DNP : cliquez « Réglages automatiques DNP »";
            }
            return cutter switch
            {
                "CUT_2INCH" => "✓ Réglages enregistrés (coupe 2 pouces)",
                "CUT_STANDARD" => "✓ Réglages enregistrés (coupe standard)",
                _ => "✓ Réglages du pilote enregistrés",
            };
        }
        catch (FormatException)
        {
            return "Réglages illisibles : refaites-les";
        }
    }

    private void DriverSettings_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProfileItem profile) return;
        var printer = _services.Settings.Print.PrinterName;
        if (string.IsNullOrWhiteSpace(printer))
        {
            ShowError("Choisissez d'abord l'imprimante.");
            return;
        }
        try
        {
            byte[]? current = null;
            if (_services.Settings.Print.DriverSettings.TryGetValue(profile.Name, out var base64) && !string.IsNullOrWhiteSpace(base64))
            {
                current = Convert.FromBase64String(base64);
            }
            var result = DevModeHelper.ShowDriverDialog(new WindowInteropHelper(this).Handle, printer, current);
            if (result == null) return;
            _services.Settings.Print.DriverSettings[profile.Name] = Convert.ToBase64String(result);
            _services.SaveSettings();
            profile.StateText = DescribeProfile(profile.Name);
            ProfileStatusText.Text = $"Réglages « {profile.Name} » enregistrés.";
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Réglages du pilote impossibles");
            ShowError("Réglages du pilote impossibles : " + ex.Message);
        }
    }

    private void AutoDnp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var lines = _services.ApplyDnpProfiles();
            LoadProfiles();
            ProfileStatusText.Text = "Réglages automatiques enregistrés : " + string.Join(" ; ", lines) + ".";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Réglages automatiques DNP impossibles");
            ProfileStatusText.Text = "Réglages automatiques impossibles : " + ex.Message;
        }
    }

    private void ResetDriver_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProfileItem profile) return;
        _services.Settings.Print.DriverSettings.Remove(profile.Name);
        _services.SaveSettings();
        profile.StateText = DescribeProfile(profile.Name);
    }

    private async void TestPage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProfileItem profile) return;
        var printer = _services.EffectivePrinterName;
        if (string.IsNullOrWhiteSpace(printer))
        {
            ShowError("Choisissez d'abord l'imprimante.");
            return;
        }

        var templates = _services.Templates.LoadAll();
        var template = templates.FirstOrDefault(t => string.Equals(t.Layout.PrintProfile, profile.Name, StringComparison.OrdinalIgnoreCase))
                       ?? templates.FirstOrDefault();
        if (template == null)
        {
            ShowError("Aucun modèle disponible pour la page de test.");
            return;
        }

        try
        {
            ProfileStatusText.Text = $"Préparation de la page de test « {profile.Name} »…";
            var folder = Path.Combine(_services.Paths.DataRoot, "tests");
            Directory.CreateDirectory(folder);
            var image = Path.Combine(folder, $"page-test-{TextUtil.Slug(profile.Name)}.jpg");
            var design = _services.CurrentDesign();
            var ev = _services.CurrentEvent;
            await Task.Run(() => TemplatePreview.WriteSample(_services.Compositor, template, image, ev.Name, design, ev.Date));
            string? pdf = _services.Options.PrintToPdf ? Path.Combine(folder, $"page-test-{TextUtil.Slug(profile.Name)}.pdf") : null;
            await _services.Printer.PrintAsync(new PrintJobRequest(image, printer, _services.DriverSettingsFor(profile.Name), 1,
                "LibreBooth page de test", pdf, FitWholeImage: _services.Options.PrintToPdf));
            _services.Settings.Print.PrintedSinceMediaChange++;
            _services.SaveSettings();
            _services.PrinterMonitor.RefreshSoon();
            ProfileStatusText.Text = $"Page de test « {profile.Name} » envoyée à {printer} (modèle : {template.Name}).";
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Page de test impossible");
            ProfileStatusText.Text = "Page de test impossible : " + ex.Message;
        }
    }

    private async void RefreshPrinterState_Click(object sender, RoutedEventArgs e)
    {
        PrinterStateText.Text = "Vérification…";
        await _services.PrinterMonitor.RefreshAsync();
        UpdatePrinterTexts();
    }

    private void NewRoll_Click(object sender, RoutedEventArgs e)
    {
        _services.Settings.Print.PrintedSinceMediaChange = 0;
        _services.SaveSettings();
        _ = _services.PrinterMonitor.RefreshAsync();
        UpdatePrinterTexts();
    }

    private void UpdatePrinterTexts()
    {
        var monitor = _services.PrinterMonitor;
        var health = monitor.Current;
        string brush;
        if (!monitor.HasChecked)
        {
            PrinterStateText.Text = health.Message;
            brush = "Admin.TextMuted";
        }
        else
        {
            PrinterStateText.Text = (health.CanPrint ? "✓ " : "⚠ ") + health.Message;
            brush = health.CanPrint ? "Admin.Success" : _services.Settings.Print.Enabled ? "Admin.Error" : "Admin.TextMuted";
        }
        PrinterStateText.SetResourceReference(TextBlock.ForegroundProperty, brush);
        RemainingText.Text = health.RemainingPrints is int remaining
            ? $"Tirages restants : {remaining}" + (health.LowMedia ? " — bientôt terminé !" : "")
            : "Tirages restants : inconnu";
        PrinterDetailsText.Text = string.IsNullOrEmpty(health.Details) ? "" : $"{health.Details} (vérifié à {health.CheckedAt:HH:mm:ss})";
        MediaCounterText.Text = $"Impressions depuis le dernier changement de rouleau : {_services.Settings.Print.PrintedSinceMediaChange}";
    }

    // ================================================================ Système

    private void LoadLogTab()
    {
        LogText.Text = Logging.ReadTail();
        LogText.ScrollToEnd();
    }

    /// <summary>Le journal n'est lu qu'à l'ouverture de sa section.</summary>
    private void LogExpander_Expanded(object sender, RoutedEventArgs e) => LoadLogTab();

    private void RefreshLog_Click(object sender, RoutedEventArgs e) => LoadLogTab();

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(_services.Paths.LogsFolder);

    private void LoadSystemTab()
    {
        _loading = true;
        AppearanceLightRadio.IsChecked = _services.Settings.AdminAppearance == AdminAppearance.Light;
        AppearanceDarkRadio.IsChecked = _services.Settings.AdminAppearance == AdminAppearance.Dark;
        _loading = false;
        VersionText.Text = $"LibreBooth {App.Version}";
        ModeText.Text = _services.Options.Demo ? "Mode démo (lancé avec --demo)" : "Mode normal";
        DataFolderText.Text = "Données : " + _services.Paths.DataRoot;
        ShowUpdateStatus();
    }

    /// <summary>Clair ou sombre : toutes les fenêtres de l'admin changent tout de suite, barre de titre comprise.</summary>
    private void Appearance_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var appearance = AppearanceDarkRadio.IsChecked == true ? AdminAppearance.Dark : AdminAppearance.Light;
        if (_services.Settings.AdminAppearance == appearance && AdminTheme.Current == appearance) return;
        _services.Settings.AdminAppearance = appearance;
        _services.SaveSettings();
        AdminTheme.Apply(appearance);
    }

    // ================================================================ Mises à jour

    private CancellationTokenSource? _updateDownload;

    private void OnUpdateStatusChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ShowUpdateStatus);

    /// <summary>Bandeau en haut de l'admin et partie « Mises à jour » de la page Système.</summary>
    private void ShowUpdateStatus()
    {
        var update = _services.AvailableUpdate;
        var downloading = _updateDownload != null;
        UpdateBanner.Visibility = update != null && !downloading ? Visibility.Visible : Visibility.Collapsed;
        if (update != null) UpdateBannerText.Text = $"Nouvelle version de LibreBooth disponible : {update.Version.ToString(3)} (vous avez la {App.Version}).";
        CheckUpdatesButton.IsEnabled = !_services.IsCheckingForUpdates && !downloading;
        // Pendant le téléchargement, le texte suit la progression.
        if (downloading) return;

        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateNotesText.Visibility = Visibility.Collapsed;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        UpdatePageButton.Visibility = Visibility.Collapsed;
        if (_services.IsCheckingForUpdates)
        {
            UpdateStatusText.Text = "Recherche d'une nouvelle version…";
            return;
        }
        if (update != null)
        {
            var version = update.Version.ToString(3);
            UpdateStatusText.Text = $"Nouvelle version disponible : LibreBooth {version} (vous avez la {App.Version}).";
            UpdateNotesText.Text = update.Notes;
            UpdateNotesText.Visibility = update.Notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatePageButton.Visibility = Visibility.Visible;
            if (AppServices.IsInstalledCopy)
            {
                InstallUpdateButton.Content = $"Installer la mise à jour {version} ({SizeText(update.Installer.Size)})";
                InstallUpdateButton.IsEnabled = true;
                InstallUpdateButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateStatusText.Text += " Cette copie de LibreBooth n'a pas été installée avec le programme d'installation : "
                                         + "téléchargez la nouvelle version sur GitHub.";
            }
            return;
        }
        UpdateStatusText.Text = _services.UpdateCheckError != null
            ? "Recherche impossible : " + _services.UpdateCheckError
            : _services.LastUpdateCheck is { } when
                ? $"LibreBooth {App.Version} est à jour (vérifié {(when.Date == DateTime.Today ? $"aujourd'hui à {when:HH:mm}" : $"le {when:dd/MM/yyyy} à {when:HH:mm}")})."
                : "Pas encore de recherche depuis le lancement de LibreBooth.";
    }

    private static string SizeText(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0} Mo" : $"{Math.Max(1, bytes / 1024)} Ko";

    private void ShowUpdate_Click(object sender, RoutedEventArgs e) => ShowPage(AdminPage.System, UpdatesGroup);

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await _services.CheckForUpdatesAsync();

    private void UpdatePage_Click(object sender, RoutedEventArgs e) => Shell.OpenUrl(_services.AvailableUpdate?.PageUrl ?? UpdateChecker.ReleasesPage);

    /// <summary>
    /// Télécharge la nouvelle version, contrôle son empreinte, lance son programme d'installation (Windows demande
    /// l'autorisation), puis ferme LibreBooth pour qu'il puisse remplacer les fichiers.
    /// </summary>
    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_services.AvailableUpdate is not { } update || _updateDownload != null) return;
        var version = update.Version.ToString(3);
        if (!AdminDialog.Ask(this,
                $"Installer LibreBooth {version} ?\n\n"
                + $"LibreBooth va télécharger la mise à jour ({SizeText(update.Installer.Size)}) et contrôler son empreinte, puis se fermer pour l'installer. "
                + "Windows va demander l'autorisation d'installer.\n\n"
                + "Vos photos, événements, réglages et pilotes sont gardés. À la fin, LibreBooth peut se rouvrir tout seul.",
                "Installer", "Plus tard"))
        {
            return;
        }

        CustomizationView.Flush();
        _services.Settings.Normalize();
        _services.SaveSettings();
        _updateDownload = new CancellationTokenSource();
        ShowUpdateStatus();
        InstallUpdateButton.IsEnabled = false;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateStatusText.Text = $"Téléchargement de LibreBooth {version}…";
        string installer;
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress.Value = p;
                UpdateStatusText.Text = $"Téléchargement de LibreBooth {version}… {p * 100:0} %";
            });
            installer = await _services.DownloadUpdateAsync(update, progress, _updateDownload.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Mise à jour non téléchargée");
            EndUpdateDownload();
            if (ex is not OperationCanceledException || IsVisible)
            {
                ShowError("La mise à jour n'a pas pu être téléchargée :\n" + (ex is HttpRequestException or TaskCanceledException
                    ? "GitHub n'a pas pu être joint (connexion Internet coupée ?)."
                    : ex.Message));
            }
            return;
        }

        UpdateStatusText.Text = "Mise à jour téléchargée et contrôlée. Windows demande l'autorisation d'installer…";
        try
        {
            _services.LaunchUpdateInstaller(installer);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            EndUpdateDownload();
            ShowError("Mise à jour annulée : Windows n'a pas reçu l'autorisation d'installer. Vous pouvez réessayer.");
            return;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Programme de mise à jour impossible à lancer");
            EndUpdateDownload();
            ShowError("Le programme de mise à jour n'a pas pu démarrer :\n" + ex.Message);
            return;
        }

        // Le programme d'installation attend que LibreBooth soit fermé pour remplacer ses fichiers.
        _log.Information("Fermeture de LibreBooth pour installer la version {Version}", version);
        QuitRequested = true;
        Close();
    }

    private void EndUpdateDownload()
    {
        _updateDownload?.Dispose();
        _updateDownload = null;
        ShowUpdateStatus();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(_services.Paths.DataRoot);

    /// <summary>Vérification de l'installation, avec l'état de l'appareil et de l'imprimante vu par LibreBooth.</summary>
    private void Verify_Click(object sender, RoutedEventArgs e)
    {
        CustomizationView.Flush();
        _services.SaveSettings();
        new LibreBooth.App.Diagnostics.VerificationWindow(_services, _services.Paths) { Owner = this }.ShowDialog();
    }

    private void ChangePin_Click(object sender, RoutedEventArgs e)
    {
        var pin = NewPinBox.Password.Trim();
        if (pin.Length is < 4 or > 8 || !pin.All(char.IsDigit))
        {
            PinStatusText.Text = "Le code doit contenir 4 à 8 chiffres.";
            return;
        }
        if (pin != ConfirmPinBox.Password.Trim())
        {
            PinStatusText.Text = "Les deux codes ne sont pas identiques.";
            return;
        }
        _services.Settings.AdminPin = pin;
        _services.SaveSettings();
        NewPinBox.Clear();
        ConfirmPinBox.Clear();
        PinStatusText.Text = "Code modifié.";
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        if (!AdminDialog.Ask(this, "Quitter LibreBooth ?\n\nLe photobooth s'arrête : l'appareil photo et l'imprimante sont libérés.",
                "Quitter LibreBooth"))
        {
            return;
        }
        QuitRequested = true;
        Close();
    }
}

/// <summary>Une ligne de « Prêt pour l'événement ? » : le point vérifié, et la page qui permet de le régler.</summary>
public sealed record ReadinessRow(ReadinessItem Item, AdminPage Page, string PageLabel)
{
    public ReadinessLevel Level => Item.Level;
    public string Title => Item.Title;
    public string Detail => Item.Detail;
    public string Glyph => GlyphFor(Item.Level);

    /// <summary>Bouton vers la page, seulement si le point n'est pas au vert (null : pas de bouton).</summary>
    public string? PageName => Item.Level == ReadinessLevel.Ok ? null : PageLabel;

    public string ActionName => $"{Item.Title} : ouvrir la page {PageLabel}";

    public static ReadinessRow For(ReadinessItem item) => item.Area switch
    {
        ReadinessArea.Camera => new(item, AdminPage.Camera, "Appareil photo"),
        ReadinessArea.Printer or ReadinessArea.Paper or ReadinessArea.PrintLimit => new(item, AdminPage.Print, "Impression"),
        ReadinessArea.Formats => new(item, AdminPage.Customization, "Personnalisation"),
        _ => new(item, AdminPage.System, "Système"),
    };

    public static string GlyphFor(ReadinessLevel level) => level switch
    {
        ReadinessLevel.Ok => "\uE930",
        ReadinessLevel.Info => "\uE946",
        ReadinessLevel.Warning => "\uE7BA",
        _ => "\uE783",
    };

    /// <summary>Clé du pinceau de l'admin (clair ou sombre) pour cette gravité.</summary>
    public static string BrushFor(ReadinessLevel level) => level switch
    {
        ReadinessLevel.Ok => "Admin.Success",
        ReadinessLevel.Info => "Admin.Info",
        ReadinessLevel.Warning => "Admin.Warning",
        _ => "Admin.Error",
    };
}

public sealed class ProfileItem : INotifyPropertyChanged
{
    private string _stateText;

    public ProfileItem(string name, string stateText)
    {
        Name = name;
        _stateText = stateText;
    }

    public string Name { get; }

    public string StateText
    {
        get => _stateText;
        set
        {
            _stateText = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record GalleryItem(SessionRecord Record, string CompositePath, ImageSource? Thumbnail, string Label)
{
    /// <summary>Ce que lit un lecteur d'écran pour la ligne de la galerie : l'heure, le format et les impressions.</summary>
    public override string ToString() => Label.Replace("\n", " · ");
}
