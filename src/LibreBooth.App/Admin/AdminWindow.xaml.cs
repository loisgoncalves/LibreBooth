using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibreBooth.App.Hardware;
using LibreBooth.App.Hardware.Printing;
using LibreBooth.App.Infrastructure;
using LibreBooth.App.Kiosk;
using LibreBooth.App.Services;
using LibreBooth.Core.Config;
using LibreBooth.Core.Events;
using LibreBooth.Core.Hardware;
using LibreBooth.Core.Layouts;
using LibreBooth.Core.Updates;
using LibreBooth.Core.Util;
using Microsoft.Win32;
using Serilog;

namespace LibreBooth.App.Admin;

/// <summary>Administration de LibreBooth (protégée par le code PIN).</summary>
public partial class AdminWindow : Window
{
    private readonly ILogger _log = Log.ForContext<AdminWindow>();
    private readonly AppServices _services;
    private readonly KioskViewModel _kiosk;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<TemplateItem> _templates = new();
    private readonly ObservableCollection<ProfileItem> _profiles = new();
    private readonly ObservableCollection<GalleryItem> _gallery = new();
    private LiveViewDecoder? _liveDecoder;
    private bool _loading;
    private int _tick;

    public AdminWindow(AppServices services, KioskViewModel kiosk)
    {
        InitializeComponent();
        _services = services;
        _kiosk = kiosk;
        HeaderVersionText.Text = $"Administration · version {App.Version}";
        RetakeCombo.ItemsSource = Enumerable.Range(0, ScreenSettings.MaxRetakes + 1).ToList();

        // Fenêtre normale (pas de plein écran), à la taille de l'écran s'il est plus petit.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width - 40));
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 40));

        // Les listes sont remplies AVANT de brancher les réglages, pour que les sélections soient bien reprises.
        TemplatesList.ItemsSource = _templates;
        ProfilesList.ItemsSource = _profiles;
        GalleryList.ItemsSource = _gallery;
        RotationCombo.ItemsSource = new[] { 0, 90, 180, 270 };
        var keys = new List<string> { "Space", "Enter", "B", "F5", "PageDown" };
        if (!keys.Contains(services.Settings.Screens.StartKey)) keys.Add(services.Settings.Screens.StartKey);
        StartKeyCombo.ItemsSource = keys;
        RootGrid.DataContext = services.Settings;
        CustomizationTab.Initialize(services);

        Loaded += (_, _) =>
        {
            LoadAll();
            _refreshTimer.Start();
        };
        _refreshTimer.Tick += (_, _) => RefreshLiveStatus();
        Closing += OnClosing;
        services.UpdateStatusChanged += OnUpdateStatusChanged;
    }

    /// <summary>« Quitter LibreBooth » (bouton, onglet Système, ou croix de la fenêtre confirmée).</summary>
    public bool QuitRequested { get; private set; }

    /// <summary>« Démarrer le photobooth ».</summary>
    public bool StartRequested { get; private set; }

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
            var answer = MessageBox.Show(this,
                "Quitter LibreBooth ?\n\nPour lancer le photobooth, cliquez plutôt sur « Démarrer le photobooth ».",
                "LibreBooth", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            QuitRequested = true;
        }
        OnClosingAdmin();
    }

    /// <summary>Chargement des onglets sans afficher la fenêtre (captures de vérification).</summary>
    internal void LoadForCapture() => LoadAll();

    private void LoadAll()
    {
        LoadEventTab();
        CustomizationTab.LoadFromEvent();
        LoadCameraTab();
        LoadPrintTab();
        LoadTemplatesTab();
        LoadLogTab();
        LoadSystemTab();
        RefreshLiveStatus();
    }

    private void OnClosingAdmin()
    {
        _services.UpdateStatusChanged -= OnUpdateStatusChanged;
        _updateDownload?.Cancel();
        CustomizationTab.Flush();
        StopLiveTest();
        _refreshTimer.Stop();
        _services.Settings.Normalize();
        _services.SaveSettings();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        StartRequested = true;
        Close();
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "LibreBooth", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ================================================================ Rafraîchissement

    private void RefreshLiveStatus()
    {
        var camera = _services.Camera;
        CameraStatusText.Text = $"{camera.Name} — {StateLabel(camera.State)} : {camera.StatusMessage}";
        BatteryText.Text = camera.BatteryLevel switch
        {
            null => "Batterie : inconnue",
            -1 => "Alimentation : secteur",
            var level => $"Batterie : {level} %",
        };
        UpdatePrinterTexts();

        if (_tick++ % 3 == 0) UpdateStatsText();

        FooterText.Text = $"Événement : {_services.CurrentEvent.Name} · Appareil : {StateLabel(camera.State)} · Imprimante : {_services.PrinterMonitor.Current.Message}";
    }

    private static string StateLabel(CameraState state) => state switch
    {
        CameraState.Ready => "prêt",
        CameraState.Connecting => "connexion",
        CameraState.Disconnected => "non connecté",
        _ => "erreur",
    };

    // ================================================================ Événement

    private void LoadEventTab()
    {
        var ev = _services.CurrentEvent;
        EventNameText.Text = ev.Name;
        EventDateText.Text = "Date : " + TextUtil.FormatLongDate(ev.Date);
        EventFolderText.Text = $"Dossier : {ev.Folder}\nDedans : « {BoothEvent.PhotosFolderName} » (photos de l'appareil), "
                               + $"« {BoothEvent.CompositesFolderName} » (photos imprimées) et « {BoothEvent.RefusedFolderName} » (séries refaites avec « Recommencer »).";
        RenameNameBox.Text = ev.Name;
        RenameDatePicker.SelectedDate = ev.Date;
        UpdateStatsText();
        LoadGallery();
    }

    private void UpdateStatsText()
    {
        var stats = _services.CurrentEvent.Info.Stats;
        StatsText.Text = $"{stats.Sessions} session(s) · {stats.Photos} photo(s) · {stats.Prints} impression(s)"
                         + (stats.Retakes > 0 ? $" · {stats.Retakes} série(s) recommencée(s)" : "");
    }

    private void LoadGallery()
    {
        _gallery.Clear();
        var ev = _services.CurrentEvent;
        foreach (var record in _services.Events.ListSessions(ev, 60))
        {
            var path = Path.Combine(ev.CompositesFolder, record.Composite);
            if (!File.Exists(path)) continue;
            _gallery.Add(new GalleryItem(record, path, ImageLoader.FromFile(path, decodePixelWidth: 230),
                $"{record.CreatedAt:HH:mm} · {record.TemplateName} · {record.Prints} impr."));
        }
        GalleryStatusText.Text = _gallery.Count == 0 ? "Aucune photo pour l'instant." : $"{_gallery.Count} montage(s) affiché(s).";
    }

    private void OpenEventFolder_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(_services.CurrentEvent.Folder);

    private void RenameEvent_Click(object sender, RoutedEventArgs e)
    {
        CustomizationTab.Flush();
        var ev = _services.CurrentEvent;
        var folderFollowed = _services.RenameCurrentEvent(RenameNameBox.Text, RenameDatePicker.SelectedDate ?? ev.Date);
        _kiosk.RefreshFromSettings();
        LoadEventTab();
        CustomizationTab.LoadFromEvent();
        if (!folderFollowed)
        {
            ShowError("L'événement est bien renommé, mais son dossier garde l'ancien nom : un fichier de ce dossier est sans doute "
                      + "ouvert dans un autre programme. Fermez-le, puis cliquez de nouveau sur « Enregistrer ».");
        }
    }

    /// <summary>Même fenêtre qu'au lancement : ouvrir un autre événement ou en créer un.</summary>
    private void ChangeEvent_Click(object sender, RoutedEventArgs e)
    {
        CustomizationTab.Flush();
        var chooser = new EventChooserWindow(_services.Events, _services.CurrentEvent.Folder, atStartup: false) { Owner = this };
        if (chooser.ShowDialog() != true || chooser.Chosen is not { } ev) return;
        if (!EventStore.SamePath(ev.Folder, _services.CurrentEvent.Folder)) _services.OpenEvent(ev);
        LoadEventTab();
        CustomizationTab.LoadFromEvent();
        LoadTemplatesTab();
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
        if (missing.Count == 0)
        {
            var path = NativeDrivers.Find(NativeDrivers.CanonFiles[0])!;
            var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            CanonSdkText.Text = $"✓ SDK Canon présent (EDSDK {version}) : {Path.GetDirectoryName(path)}";
            CanonSdkText.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x7B, 0x34));
            CanonSdkHelp.Text = "Pour passer à une version plus récente du SDK, téléchargez-la chez Canon puis ajoutez-la avec le bouton 2.";
            ImportCanonSdkButton.Content = "2. Remplacer le SDK Canon…";
        }
        else
        {
            CanonSdkText.Text = "SDK Canon manquant : sans lui, LibreBooth ne peut pas piloter l'appareil photo.";
            CanonSdkText.Foreground = new SolidColorBrush(Color.FromRgb(0xA4, 0x26, 0x2C));
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
            MessageBox.Show(this, $"SDK Canon ajouté (EDSDK {version}).\n\nBranchez l'appareil photo en USB et allumez-le : "
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
            MessageBox.Show($"Ouvrez cette adresse dans votre navigateur :\n{url}", "LibreBooth", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var health = _services.PrinterMonitor.Current;
        PrinterStateText.Text = (health.CanPrint ? "✓ " : "⚠ ") + health.Message;
        PrinterStateText.Foreground = health.CanPrint ? Brushes.DarkGreen : Brushes.DarkRed;
        RemainingText.Text = health.RemainingPrints is int remaining
            ? $"Tirages restants : {remaining}" + (health.LowMedia ? " — bientôt terminé !" : "")
            : "Tirages restants : inconnu";
        PrinterDetailsText.Text = string.IsNullOrEmpty(health.Details) ? "" : $"{health.Details} (vérifié à {health.CheckedAt:HH:mm:ss})";
        MediaCounterText.Text = $"Impressions depuis le dernier changement de rouleau : {_services.Settings.Print.PrintedSinceMediaChange}";
    }

    // ================================================================ Modèles

    private void LoadTemplatesTab()
    {
        _templates.Clear();
        var context = _services.CurrentPreviewContext();
        foreach (var template in _services.Templates.LoadAll())
        {
            ImageSource? preview = null;
            try
            {
                preview = ImageLoader.FromFile(TemplatePreview.GetOrCreate(_services.Compositor, template, context), decodePixelHeight: 280);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Aperçu du modèle {Template}", template.Id);
            }

            var offered = _services.Customization.Templates.Contains(template.Id, StringComparer.OrdinalIgnoreCase);
            _templates.Add(new TemplateItem(template, preview, offered));
        }
    }

    /// <summary>Après un changement dans la liste des modèles : l'onglet Personnalisation suit.</summary>
    private void AfterTemplatesChanged()
    {
        _services.EnsureActiveTemplates();
        LoadTemplatesTab();
        LoadProfiles();
        CustomizationTab.LoadFromEvent();
    }

    private TemplateItem? SelectedTemplate()
    {
        if (TemplatesList.SelectedItem is TemplateItem item) return item;
        ShowError("Choisissez d'abord un modèle dans la liste.");
        return null;
    }

    private void ImportTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choisir un cadre PNG", Filter = "Images PNG (*.png)|*.png" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var template = _services.Templates.ImportFrame(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName));
            var message = template.ShotCount == 0
                ? "Aucune zone transparente trouvée : ajoutez les zones photo à la main."
                : $"{template.ShotCount} zone(s) photo détectée(s) automatiquement : vérifiez-les puis enregistrez.";
            new TemplateEditorWindow(_services, template, message) { Owner = this }.ShowDialog();
            AfterTemplatesChanged();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Import du cadre impossible");
            ShowError("Import du cadre impossible : " + ex.Message);
        }
    }

    private void EditTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate() is not { } item) return;
        new TemplateEditorWindow(_services, item.Template) { Owner = this }.ShowDialog();
        AfterTemplatesChanged();
    }

    private void DuplicateTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate() is not { } item) return;
        _services.Templates.Duplicate(item.Template);
        AfterTemplatesChanged();
    }

    private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate() is not { } item) return;
        var answer = MessageBox.Show(this, $"Supprimer le modèle « {item.Name} » ?\nIl sera déplacé dans le dossier Modeles\\_corbeille.",
            "LibreBooth", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _services.Templates.Delete(item.Template);
        AfterTemplatesChanged();
    }

    private void OpenTemplatesFolder_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(_services.Templates.Root);

    // ================================================================ Journal et système

    private void LoadLogTab()
    {
        LogText.Text = Logging.ReadTail();
        LogText.ScrollToEnd();
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e) => LoadLogTab();

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(_services.Paths.LogsFolder);

    private void LoadSystemTab()
    {
        VersionText.Text = $"LibreBooth {App.Version}";
        ModeText.Text = _services.Options.Demo ? "Mode démo (lancé avec --demo)" : "Mode normal";
        DataFolderText.Text = "Données : " + _services.Paths.DataRoot;
        ShowUpdateStatus();
    }

    // ================================================================ Mises à jour

    private CancellationTokenSource? _updateDownload;

    private void OnUpdateStatusChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ShowUpdateStatus);

    /// <summary>Bandeau en haut de l'admin et partie « Mises à jour » de l'onglet Système.</summary>
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

    private void ShowUpdate_Click(object sender, RoutedEventArgs e)
    {
        AdminTabs.SelectedItem = SystemTab;
        Dispatcher.BeginInvoke(() => UpdatesGroup.BringIntoView(), DispatcherPriority.Loaded);
    }

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
        var answer = MessageBox.Show(this,
            $"Installer LibreBooth {version} ?\n\n"
            + $"LibreBooth va télécharger la mise à jour ({SizeText(update.Installer.Size)}) et contrôler son empreinte, puis se fermer pour l'installer. "
            + "Windows va demander l'autorisation d'installer.\n\n"
            + "Vos photos, événements, réglages et pilotes sont gardés. À la fin, LibreBooth peut se rouvrir tout seul.",
            "LibreBooth", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        CustomizationTab.Flush();
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
        CustomizationTab.Flush();
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
        var answer = MessageBox.Show(this, "Quitter LibreBooth ?", "LibreBooth", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        QuitRequested = true;
        Close();
    }
}

public sealed class TemplateItem
{
    public TemplateItem(Template template, ImageSource? preview, bool offered)
    {
        Template = template;
        Preview = preview;
        var layout = template.Layout;
        Details = $"{template.ShotCount} photo(s) · {layout.OutputWidth} × {layout.OutputHeight} px · profil d'impression « {layout.PrintProfile} »"
                  + (layout.IsCustomizable ? " · personnalisable" : " · cadre PNG")
                  + (layout.DuplicateStrip ? " · imprimé deux fois puis coupé" : "")
                  + (layout.BlackAndWhite ? " · noir et blanc" : "")
                  + (offered ? " · proposé pour cet événement" : "");
    }

    public Template Template { get; }
    public ImageSource? Preview { get; }
    public string Name => Template.Name;
    public string Details { get; }
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

public sealed record GalleryItem(SessionRecord Record, string CompositePath, ImageSource? Thumbnail, string Label);
