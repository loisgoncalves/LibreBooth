using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibreBooth.App.Hardware.Printing;
using LibreBooth.App.Infrastructure;
using LibreBooth.App.Kiosk;
using LibreBooth.App.Services;
using LibreBooth.Core.Config;
using LibreBooth.Core.Events;
using LibreBooth.Core.Hardware;
using LibreBooth.Core.Layouts;
using LibreBooth.Core.Sharing;
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
        SecurityCombo.ItemsSource = Enum.GetValues<SmtpSecurity>();
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
        LoadEmailTab();
        LoadLogTab();
        LoadSystemTab();
        RefreshLiveStatus();
    }

    private void OnClosingAdmin()
    {
        CustomizationTab.Flush();
        SaveTypedPassword();
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

        if (_tick++ % 3 == 0)
        {
            var pending = _services.Outbox.PendingCount;
            OutboxStatusText.Text = pending == 0
                ? "File d'attente : aucun e-mail en attente."
                : $"File d'attente : {pending} e-mail(s) en attente."
                  + (_services.Outbox.LastError != null ? $" Dernière erreur : {_services.Outbox.LastError}" : "")
                  + (_services.Settings.Email.Enabled ? "" : " (l'envoi par e-mail est désactivé)");
            UpdateStatsText();
        }

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
        EventFolderText.Text = "Dossier : " + ev.Folder;
        RenameNameBox.Text = ev.Name;
        RenameDatePicker.SelectedDate = ev.Date;
        NewEventNameBox.Text = "";
        NewEventDatePicker.SelectedDate = DateTime.Today;
        var events = _services.Events.ListAll();
        EventsCombo.ItemsSource = events;
        EventsCombo.SelectedItem = events.FirstOrDefault(e => string.Equals(e.Folder, ev.Folder, StringComparison.OrdinalIgnoreCase));
        UpdateStatsText();
        LoadGallery();
    }

    private void UpdateStatsText()
    {
        var stats = _services.CurrentEvent.Info.Stats;
        StatsText.Text = $"{stats.Sessions} session(s) · {stats.Photos} photo(s) · {stats.Prints} impression(s) · "
                         + $"{stats.EmailsQueued} e-mail(s) demandé(s), {stats.EmailsSent} envoyé(s)"
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
        var ev = _services.CurrentEvent;
        _services.Events.Rename(ev, RenameNameBox.Text, RenameDatePicker.SelectedDate ?? ev.Date);
        _kiosk.RefreshFromSettings();
        LoadEventTab();
        CustomizationTab.LoadFromEvent();
    }

    private void CreateEvent_Click(object sender, RoutedEventArgs e)
    {
        CustomizationTab.Flush();
        var ev = _services.Events.Create(NewEventNameBox.Text, NewEventDatePicker.SelectedDate ?? DateTime.Today);
        _services.OpenEvent(ev);
        LoadEventTab();
        CustomizationTab.LoadFromEvent();
        LoadTemplatesTab();
    }

    private void ActivateEvent_Click(object sender, RoutedEventArgs e)
    {
        if (EventsCombo.SelectedItem is not BoothEvent ev) return;
        CustomizationTab.Flush();
        _services.OpenEvent(ev);
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

    private void ResendEmail_Click(object sender, RoutedEventArgs e)
    {
        if (GalleryList.SelectedItem is not GalleryItem item)
        {
            GalleryStatusText.Text = "Choisissez d'abord une photo dans la galerie.";
            return;
        }
        var address = ResendAddressBox.Text.Trim();
        if (!EmailValidator.IsValid(address))
        {
            GalleryStatusText.Text = "Adresse e-mail invalide.";
            return;
        }
        _services.Outbox.Enqueue(_services.CurrentEvent, address, new[] { item.CompositePath });
        ResendAddressBox.Text = "";
        GalleryStatusText.Text = _services.Settings.Email.Enabled
            ? "E-mail ajouté à la file d'attente."
            : "E-mail ajouté à la file d'attente : il partira quand l'envoi par e-mail sera activé.";
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

    // ================================================================ E-mail

    private void LoadEmailTab()
    {
        PasswordStateText.Text = string.IsNullOrEmpty(_services.Settings.Email.ProtectedPassword)
            ? "Aucun mot de passe enregistré."
            : "Un mot de passe est enregistré (chiffré). Tapez-en un nouveau pour le remplacer.";
    }

    private void SavePassword_Click(object sender, RoutedEventArgs e) => SaveTypedPassword();

    private void SaveTypedPassword()
    {
        if (string.IsNullOrEmpty(SmtpPasswordBox.Password)) return;
        _services.Settings.Email.ProtectedPassword = PasswordProtector.Protect(SmtpPasswordBox.Password);
        SmtpPasswordBox.Clear();
        _services.SaveSettings();
        PasswordStateText.Text = "Mot de passe enregistré (chiffré).";
    }

    private async void SendTestEmail_Click(object sender, RoutedEventArgs e)
    {
        SaveTypedPassword();
        var address = TestAddressBox.Text.Trim();
        if (!EmailValidator.IsValid(address))
        {
            TestEmailResult.Text = "Adresse de test invalide.";
            return;
        }
        TestEmailButton.IsEnabled = false;
        TestEmailResult.Text = "Envoi en cours…";
        try
        {
            await SmtpEmailSender.SendTestAsync(_services.Settings.Email, address);
            TestEmailResult.Text = "✓ E-mail de test envoyé : vérifiez la boîte de réception (et les courriers indésirables).";
            _services.Outbox.TriggerNow();
        }
        catch (Exception ex)
        {
            TestEmailResult.Text = "Échec de l'envoi : " + ex.Message;
        }
        finally
        {
            TestEmailButton.IsEnabled = true;
        }
    }

    private void RetryOutbox_Click(object sender, RoutedEventArgs e)
    {
        _services.Outbox.TriggerNow();
        OutboxStatusText.Text = "Nouvel essai d'envoi lancé…";
    }

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
