using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibreBooth.App.Infrastructure;
using LibreBooth.App.Kiosk;
using LibreBooth.App.Services;
using LibreBooth.App.Views.Kiosk;
using LibreBooth.Core.Design;
using LibreBooth.Core.Events;
using LibreBooth.Core.Hardware;
using LibreBooth.Core.Layouts;
using Microsoft.Win32;
using Serilog;

namespace LibreBooth.App.Admin;

/// <summary>
/// Page « Personnalisation » : formats proposés, nombre d'exemplaires et apparence des modèles pour
/// l'événement en cours, avec un aperçu en direct, puis la liste des modèles. Chaque modification est enregistrée automatiquement.
/// </summary>
public partial class CustomizationPanel : UserControl
{
    private static readonly IReadOnlyList<Choice<int>> Weights = new[]
    {
        new Choice<int>(300, "Fin"), new Choice<int>(400, "Normal"), new Choice<int>(600, "Demi-gras"),
        new Choice<int>(700, "Gras"), new Choice<int>(900, "Très gras"),
    };

    private readonly ILogger _log = Log.ForContext<CustomizationPanel>();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly ObservableCollection<FormatItem> _formats = new();
    private readonly ObservableCollection<ThemeCard> _themes = new();
    private readonly ObservableCollection<BackgroundItem> _backgrounds = new();
    private readonly ObservableCollection<PreviewFormatItem> _previewFormats = new();
    private readonly ObservableCollection<string> _fonts = new();
    private AppServices? _services;
    private bool _loading;
    private int _previewVersion;
    private string _thumbnailsKey = "";
    private Template? _previewTemplate;
    /// <summary>L'aperçu montre l'écran d'accueil du photobooth plutôt qu'un format d'impression.</summary>
    private bool _previewAttract;

    public CustomizationPanel()
    {
        InitializeComponent();
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            _ = RenderPreviewAsync();
        };
        _saveTimer.Tick += (_, _) => Flush();

        FormatsList.ItemsSource = _formats;
        ThemesList.ItemsSource = _themes;
        BackgroundsList.ItemsSource = _backgrounds;
        PreviewFormatsList.ItemsSource = _previewFormats;
        MaxCopiesCombo.ItemsSource = Enumerable.Range(1, EventCustomization.MaxCopiesLimit).ToList();
        TitleWeightCombo.ItemsSource = Weights;
        TextWeightCombo.ItemsSource = Weights;
        DecorCombo.ItemsSource = Enum.GetValues<DecorStyle>().Select(d => new Choice<DecorStyle>(d, ThemeCatalog.DecorName(d))).ToList();
        TitleFontCombo.ItemsSource = _fonts;
        TextFontCombo.ItemsSource = _fonts;
    }

    private AppServices Services => _services ?? throw new InvalidOperationException("Page de personnalisation non initialisée.");
    private EventCustomization Customization => Services.Customization;
    private EventDesign Design => Customization.Design;

    /// <summary>Un modèle a été importé, modifié, copié ou supprimé (l'admin met à jour les réglages du pilote par format).</summary>
    public event EventHandler? TemplatesChanged;

    public void Initialize(AppServices services)
    {
        _services = services;
        TemplatesView.Initialize(services);
        TemplatesView.TemplatesChanged += (_, _) =>
        {
            LoadFromEvent();
            TemplatesChanged?.Invoke(this, EventArgs.Empty);
        };
        _themes.Clear();
        foreach (var preset in ThemeCatalog.All) _themes.Add(new ThemeCard(preset));

        // Polices conseillées d'abord, puis toutes celles installées sur ce PC.
        _fonts.Clear();
        foreach (var font in ThemeCatalog.RecommendedFonts.Where(DesignFonts.IsInstalled)) _fonts.Add(font);
        foreach (var font in DesignFonts.InstalledFamilies.OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!_fonts.Contains(font, StringComparer.OrdinalIgnoreCase)) _fonts.Add(font);
        }
    }

    /// <summary>Recharge tout depuis l'événement en cours (ouverture de l'admin, changement d'événement, modèles modifiés).</summary>
    public void LoadFromEvent()
    {
        if (_services == null) return;
        _saveTimer.Stop();
        _loading = true;
        try
        {
            var ev = Services.CurrentEvent;
            var templates = Services.Templates.LoadAll();

            _formats.Clear();
            foreach (var template in templates)
            {
                var item = new FormatItem(template, Customization.Templates.Contains(template.Id, StringComparer.OrdinalIgnoreCase));
                item.CheckedChanged += OnFormatChecked;
                _formats.Add(item);
            }
            MaxCopiesCombo.SelectedItem = Customization.MaxCopies;

            var previous = _previewTemplate?.Id;
            _previewFormats.Clear();
            foreach (var template in templates.Where(t => t.Layout.IsCustomizable)) _previewFormats.Add(new PreviewFormatItem(template));
            var attract = new PreviewFormatItem(null, "Écran d'accueil");
            _previewFormats.Add(attract);
            var selected = _previewAttract
                ? attract
                : _previewFormats.FirstOrDefault(p => p.Template != null && p.Template.Id == previous)
                  ?? _previewFormats.FirstOrDefault(p => p.Template != null && Customization.Templates.Contains(p.Template.Id, StringComparer.OrdinalIgnoreCase))
                  ?? _previewFormats.First();
            selected.IsSelected = true;
            _previewAttract = selected.IsAttractScreen;
            _previewTemplate = selected.Template ?? _previewFormats.FirstOrDefault(p => p.Template != null)?.Template;

            LoadDesignControls();
            CopyEventCombo.ItemsSource = Services.Events.ListAll()
                .Where(e => !string.Equals(e.Folder, ev.Folder, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        finally
        {
            _loading = false;
        }

        RefreshBackgrounds();
        RefreshLogo();
        RefreshAttract();
        RefreshThemeHighlight();
        UpdateFontWarning();
        UpdatePreviewNote();
        SchedulePreview();
        _ = RefreshThumbnailsAsync();
        if (TemplatesExpander.IsExpanded) TemplatesView.Load();
    }

    /// <summary>La liste des modèles (avec un aperçu de chacun) n'est chargée qu'à l'ouverture de sa section.</summary>
    private void TemplatesExpander_Expanded(object sender, RoutedEventArgs e) => TemplatesView.Load();

    private void UpdatePreviewNote()
    {
        PreviewNoteText.Text = _previewAttract
            ? "Écran d'accueil du photobooth, avec les couleurs de l'événement. Les autres écrans prennent les mêmes couleurs."
            : _previewTemplate == null
                ? "Aucun modèle personnalisable : les cadres PNG importés gardent leur propre décor."
                : "Aperçu avec des photos d'exemple : les invités verront leurs propres photos.";
        PrintTestButton.IsEnabled = !_previewAttract && _previewTemplate != null;
    }

    /// <summary>Fait défiler les réglages (captures de vérification).</summary>
    internal void ScrollSettings(double offset)
    {
        SettingsScroll.ScrollToVerticalOffset(offset);
        SettingsScroll.UpdateLayout();
    }

    /// <summary>Enregistre tout de suite les modifications en attente (fermeture de l'admin).</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        if (_services == null) return;
        Services.SaveCustomization();
    }

    // ================================================================ Lecture et écriture des contrôles

    private void LoadDesignControls()
    {
        var d = Design;
        TitleBox.Text = d.Title;
        SubtitleBox.Text = d.Subtitle;
        PhraseBox.Text = d.Phrase;

        SelectFont(TitleFontCombo, d.TitleFont);
        TitleWeightCombo.SelectedValue = NearestWeight(d.TitleWeight);
        TitleItalicCheck.IsChecked = d.TitleItalic;
        TitleUpperCheck.IsChecked = d.TitleUppercase;
        TitleScaleSlider.Value = d.TitleScale;
        SelectFont(TextFontCombo, d.TextFont);
        TextWeightCombo.SelectedValue = NearestWeight(d.TextWeight);
        TextItalicCheck.IsChecked = d.TextItalic;
        TextUpperCheck.IsChecked = d.TextUppercase;
        TextScaleSlider.Value = d.TextScale;

        BackgroundColorBox.Text = d.BackgroundColor;
        TitleColorBox.Text = d.TitleColor;
        TextColorBox.Text = d.TextColor;
        AccentColorBox.Text = d.AccentColor;
        Accent2ColorBox.Text = d.Accent2Color;

        OwnModeRadio.IsChecked = d.UsesOwnImages;
        ThemeModeRadio.IsChecked = !d.UsesOwnImages;
        ApplyMode();

        DecorCombo.SelectedValue = d.Decor;
        CornerSlider.Value = d.PhotoCornerRadius;
        BorderSlider.Value = d.PhotoBorder;
        BorderColorBox.Text = d.PhotoBorderColor;
        ShadowCheck.IsChecked = d.PhotoShadow;
        BlackWhiteCheck.IsChecked = d.BlackAndWhite;
        TextsOverBackgroundCheck.IsChecked = d.TextsOverBackground;
        UpdateSliderTexts();
        UpdateSwatches();
    }

    private void ReadControls()
    {
        var d = Design;
        d.Title = TitleBox.Text;
        d.Subtitle = SubtitleBox.Text;
        d.Phrase = PhraseBox.Text;

        if (TitleFontCombo.SelectedItem is string titleFont) d.TitleFont = titleFont;
        if (TitleWeightCombo.SelectedValue is int titleWeight) d.TitleWeight = titleWeight;
        d.TitleItalic = TitleItalicCheck.IsChecked == true;
        d.TitleUppercase = TitleUpperCheck.IsChecked == true;
        d.TitleScale = (int)Math.Round(TitleScaleSlider.Value);
        if (TextFontCombo.SelectedItem is string textFont) d.TextFont = textFont;
        if (TextWeightCombo.SelectedValue is int textWeight) d.TextWeight = textWeight;
        d.TextItalic = TextItalicCheck.IsChecked == true;
        d.TextUppercase = TextUpperCheck.IsChecked == true;
        d.TextScale = (int)Math.Round(TextScaleSlider.Value);

        d.BackgroundColor = ReadColor(BackgroundColorBox, d.BackgroundColor);
        d.TitleColor = ReadColor(TitleColorBox, d.TitleColor);
        d.TextColor = ReadColor(TextColorBox, d.TextColor);
        d.AccentColor = ReadColor(AccentColorBox, d.AccentColor);
        d.Accent2Color = ReadColor(Accent2ColorBox, d.Accent2Color);

        if (DecorCombo.SelectedValue is DecorStyle decor) d.Decor = decor;
        d.PhotoCornerRadius = (int)Math.Round(CornerSlider.Value);
        d.PhotoBorder = (int)Math.Round(BorderSlider.Value);
        d.PhotoBorderColor = ReadColor(BorderColorBox, d.PhotoBorderColor);
        d.PhotoShadow = ShadowCheck.IsChecked == true;
        d.BlackAndWhite = BlackWhiteCheck.IsChecked == true;
        d.TextsOverBackground = TextsOverBackgroundCheck.IsChecked == true;

        if (MaxCopiesCombo.SelectedItem is int maxCopies) Customization.MaxCopies = maxCopies;
    }

    /// <summary>Couleur tapée (#RRVVBB) : refusée en rouge tant qu'elle n'est pas valide.</summary>
    private static string ReadColor(TextBox box, string current)
    {
        var text = box.Text.Trim();
        if (text.Length > 0 && !text.StartsWith('#')) text = "#" + text;
        var valid = text.Length == 7 && text.Skip(1).All(Uri.IsHexDigit);
        if (valid) box.ClearValue(Control.BorderBrushProperty);
        else box.SetResourceReference(Control.BorderBrushProperty, "Admin.Error");
        return valid ? text.ToUpperInvariant() : current;
    }

    // ================================================================ Mode : thème ou propres images

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _services == null) return;
        Design.OwnImages = OwnModeRadio.IsChecked == true;
        ApplyMode();
        RefreshBackgrounds();
        MarkDirty();
        StatusText.Text = Design.UsesOwnImages
            ? "Mode « Mes propres images » : vos fonds remplacent le thème sur les formats où vous en avez mis un."
            : "Mode « Thème LibreBooth » : vos fonds sont gardés, mais le thème est utilisé.";
    }

    /// <summary>
    /// « Mes propres images » : les emplacements de vos images passent en premier, les couleurs et polices en réglages
    /// avancés (repliés). « Thème LibreBooth » : les thèmes, puis le logo et l'image d'accueil.
    /// </summary>
    private void ApplyMode()
    {
        var own = OwnModeRadio.IsChecked == true;
        ThemeGroup.Visibility = own ? Visibility.Collapsed : Visibility.Visible;
        OwnBackgroundsPanel.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
        ImagesGroup.Header = own ? "Mes images" : "Logo et image d'accueil";
        AdvancedExpander.Header = own ? "Réglages avancés : couleurs, polices, décor" : "Couleurs, polices et décor";
        AdvancedExpander.IsExpanded = !own;
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _services == null) return;
        ReadControls();
        UpdateSwatches();
        UpdateFontWarning();
        MarkDirty();
    }

    private void SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Les curseurs changent déjà de valeur pendant la création de l'écran, avant que les textes existent.
        if (_services == null) return;
        UpdateSliderTexts();
        Changed(sender, e);
    }

    private void MarkDirty()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        SchedulePreview();
    }

    private void UpdateSliderTexts()
    {
        TitleScaleText.Text = $"{TitleScaleSlider.Value:0} %";
        TextScaleText.Text = $"{TextScaleSlider.Value:0} %";
        CornerText.Text = CornerSlider.Value < 1 ? "carrés" : $"{CornerSlider.Value:0} px";
        BorderText.Text = BorderSlider.Value < 1 ? "aucune" : $"{BorderSlider.Value:0} px";
    }

    private void UpdateSwatches()
    {
        foreach (var (swatch, box) in new[]
                 {
                     (BackgroundSwatch, BackgroundColorBox), (TitleSwatch, TitleColorBox), (TextSwatch, TextColorBox),
                     (AccentSwatch, AccentColorBox), (Accent2Swatch, Accent2ColorBox), (BorderSwatch, BorderColorBox),
                 })
        {
            swatch.Background = new SolidColorBrush(Theme.ParseColor(box.Text.Trim().StartsWith('#') ? box.Text.Trim() : "#" + box.Text.Trim(), Colors.Transparent));
        }
    }

    private void UpdateFontWarning()
    {
        var missing = new[] { Design.TitleFont, Design.TextFont }
            .Where(f => !DesignFonts.IsInstalled(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        FontWarningText.Text = missing.Count == 0
            ? ""
            : $"Police non installée sur ce PC : {string.Join(", ", missing)}. Windows la remplace par une police standard.";
    }

    private void SelectFont(ComboBox combo, string font)
    {
        var match = _fonts.FirstOrDefault(f => string.Equals(f, font, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            _fonts.Insert(0, font);
            match = font;
        }
        combo.SelectedItem = match;
    }

    private static int NearestWeight(int weight) => Weights.MinBy(w => Math.Abs(w.Value - weight))!.Value;

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TextBox box) return;
        var current = Theme.ParseColor(box.Text.Trim().StartsWith('#') ? box.Text.Trim() : "#" + box.Text.Trim(), Colors.White);
        var picked = ColorPicker.Pick(Window.GetWindow(this), current);
        if (picked is { } color) box.Text = ColorPicker.ToHex(color);
    }

    // ================================================================ Formats et thèmes

    private void OnFormatChecked(object? sender, bool isChecked)
    {
        if (_loading || sender is not FormatItem item) return;
        var ids = _formats.Where(f => f.IsChecked).Select(f => f.Template.Id).ToList();
        if (ids.Count == 0)
        {
            // Au moins un format : on recoche celui qu'on vient de décocher.
            Dispatcher.BeginInvoke(() => item.IsChecked = true);
            StatusText.Text = "Gardez au moins un format pour les invités.";
            return;
        }
        Customization.Templates = ids;
        StatusText.Text = "";
        MarkDirty();
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ThemeCard card) return;
        card.Preset.ApplyTo(Design);
        _loading = true;
        try
        {
            LoadDesignControls();
        }
        finally
        {
            _loading = false;
        }
        RefreshThemeHighlight();
        UpdateFontWarning();
        MarkDirty();
        StatusText.Text = $"Thème « {card.Name} » appliqué.";
    }

    private void RefreshThemeHighlight()
    {
        foreach (var card in _themes) card.IsCurrent = string.Equals(card.Preset.Id, Design.ThemeId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Vignettes des thèmes sur le 10x15, avec le nom de l'événement.</summary>
    private async Task RefreshThumbnailsAsync()
    {
        var template = Services.Templates.LoadAll().FirstOrDefault(t => t.Layout.IsCustomizable && !t.Layout.DuplicateStrip)
                       ?? Services.Templates.LoadAll().FirstOrDefault(t => t.Layout.IsCustomizable);
        if (template == null) return;
        var ev = Services.CurrentEvent;
        var key = $"{template.Id}|{ev.Name}|{ev.Date:yyyyMMdd}|{Design.Title}|{Design.Subtitle}";
        if (key == _thumbnailsKey) return;
        _thumbnailsKey = key;

        foreach (var card in _themes.ToList())
        {
            var design = ThemeCatalog.CreateDesign(card.Preset.Id);
            design.Title = Design.Title;
            design.Subtitle = Design.Subtitle;
            var context = new DesignContext(design, null);
            try
            {
                var bytes = await Task.Run(() => TemplatePreview.RenderJpeg(Services.Compositor, template, ev.Name, ev.Date, context, 360, 85));
                if (_thumbnailsKey != key) return;
                card.Thumbnail = ImageLoader.FromBytes(bytes);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Vignette du thème {Theme} impossible", card.Preset.Id);
            }
        }
    }

    // ================================================================ Aperçu

    private void PreviewFormat_Checked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PreviewFormatItem item) return;
        _previewAttract = item.IsAttractScreen;
        if (item.Template != null) _previewTemplate = item.Template;
        if (_loading) return;
        UpdatePreviewNote();
        SchedulePreview();
    }

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private async Task RenderPreviewAsync()
    {
        if (_services == null) return;
        var palette = Services.CurrentKioskPalette();
        KioskColorsText.Text = $"Couleurs des écrans : d'après {palette.Source}.";
        if (_previewAttract)
        {
            await RenderAttractPreviewAsync(palette);
            return;
        }

        var template = _previewTemplate;
        if (template == null)
        {
            PreviewImage.Source = null;
            PreviewBusyText.Visibility = Visibility.Collapsed;
            return;
        }

        var version = ++_previewVersion;
        var ev = Services.CurrentEvent;
        var design = Services.CurrentDesign();
        PreviewBusyText.Visibility = Visibility.Visible;
        try
        {
            var bytes = await Task.Run(() => TemplatePreview.RenderJpeg(Services.Compositor, template, ev.Name, ev.Date, design, 1400));
            if (version != _previewVersion) return;
            PreviewImage.Source = ImageLoader.FromBytes(bytes);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Aperçu de la personnalisation impossible");
            StatusText.Text = "Aperçu impossible : " + ex.Message;
        }
        finally
        {
            if (version == _previewVersion) PreviewBusyText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Aperçu de l'écran d'accueil : la vraie vue du photobooth, dessinée hors écran avec les couleurs de
    /// l'événement (ses propres ressources, sans toucher au photobooth qui attend derrière l'admin).
    /// </summary>
    private async Task RenderAttractPreviewAsync(KioskPalette palette)
    {
        var version = ++_previewVersion;
        PreviewBusyText.Visibility = Visibility.Visible;
        try
        {
            var model = new AttractViewModel(() => { })
            {
                Title = KioskViewModel.AttractTitleFor(Services),
                StartText = Services.Settings.Screens.StartText,
                Background = ImageLoader.FromFile(Services.CurrentAttractBackground(), decodePixelWidth: 1920),
                IsReady = true,
            };
            var host = new Grid { Width = 1920, Height = 1080, Resources = Theme.CreateResources(palette) };
            host.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
            host.SetResourceReference(TextElement.ForegroundProperty, "ForegroundBrush");
            host.SetResourceReference(TextElement.FontFamilyProperty, "KioskFont");
            host.Children.Add(new AttractView { DataContext = model });

            var size = new Size(1920, 1080);
            host.Measure(size);
            host.Arrange(new Rect(size));
            // Laisse les liaisons (textes, image, déclencheurs) se mettre en place avant de dessiner.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (version != _previewVersion) return;
            host.UpdateLayout();

            var bitmap = new RenderTargetBitmap(1280, 720, 64, 64, PixelFormats.Pbgra32);
            bitmap.Render(host);
            bitmap.Freeze();
            PreviewImage.Source = bitmap;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Aperçu de l'écran d'accueil impossible");
            StatusText.Text = "Aperçu de l'accueil impossible : " + ex.Message;
        }
        finally
        {
            if (version == _previewVersion) PreviewBusyText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Coche un seul choix d'aperçu : les autres sont décochés, même si la liste n'est pas encore affichée.</summary>
    private void SelectPreviewItem(PreviewFormatItem? item)
    {
        if (item == null) return;
        foreach (var other in _previewFormats) other.IsSelected = ReferenceEquals(other, item);
    }

    private void ShowAttractPreview()
    {
        SelectPreviewItem(_previewFormats.FirstOrDefault(p => p.IsAttractScreen));
        _previewAttract = true;
        UpdatePreviewNote();
        SchedulePreview();
    }

    private void ShowAttractPreview_Click(object sender, RoutedEventArgs e) => ShowAttractPreview();

    /// <summary>Aperçu de l'écran d'accueil (captures de vérification).</summary>
    internal void ShowAttractPreviewForCapture() => ShowAttractPreview();

    /// <summary>Aperçu du premier format, le 10x15 (captures de vérification).</summary>
    internal void ShowFormatPreviewForCapture()
    {
        var item = _previewFormats.FirstOrDefault(p => !p.IsAttractScreen);
        if (item == null) return;
        SelectPreviewItem(item);
        _previewAttract = false;
        _previewTemplate = item.Template;
        UpdatePreviewNote();
        SchedulePreview();
    }

    /// <summary>Ouvre la section des modèles et la fait défiler en haut des réglages (captures de vérification).</summary>
    internal void ShowTemplatesForCapture()
    {
        TemplatesExpander.IsExpanded = true;
        SettingsScroll.UpdateLayout();
        if (SettingsScroll.Content is not UIElement settings) return;
        var top = TemplatesExpander.TranslatePoint(new Point(0, 0), settings).Y;
        ScrollSettings(Math.Max(0, top - 8));
    }

    private async void PrintTest_Click(object sender, RoutedEventArgs e)
    {
        var template = _previewTemplate;
        if (template == null || _previewAttract) return;
        var printer = Services.EffectivePrinterName;
        if (string.IsNullOrWhiteSpace(printer))
        {
            ShowError("Choisissez d'abord l'imprimante (page Impression).");
            return;
        }
        var owner = Window.GetWindow(this);
        if (!AdminDialog.Ask(owner, $"Imprimer un essai « {template.Name} » avec des photos d'exemple ?\n\nCela utilise une feuille de papier.",
                "Imprimer l'essai"))
        {
            return;
        }

        Flush();
        PrintTestButton.IsEnabled = false;
        StatusText.Text = "Préparation de l'essai…";
        try
        {
            var ev = Services.CurrentEvent;
            var design = Services.CurrentDesign();
            var folder = Path.Combine(Services.Paths.DataRoot, "tests");
            Directory.CreateDirectory(folder);
            var image = Path.Combine(folder, $"essai-{template.Id}.jpg");
            await Task.Run(() => TemplatePreview.WriteSample(Services.Compositor, template, image, ev.Name, design, ev.Date));
            var pdf = Services.Options.PrintToPdf ? Path.Combine(folder, $"essai-{template.Id}.pdf") : null;
            await Services.Printer.PrintAsync(new PrintJobRequest(image, printer, Services.DriverSettingsFor(template.Layout.PrintProfile), 1,
                "LibreBooth essai de personnalisation", pdf, FitWholeImage: Services.Options.PrintToPdf));
            Services.Settings.Print.PrintedSinceMediaChange++;
            Services.SaveSettings();
            Services.PrinterMonitor.RefreshSoon();
            StatusText.Text = $"Essai envoyé à {printer}.";
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Essai d'impression de la personnalisation impossible");
            StatusText.Text = "Essai impossible : " + ex.Message;
        }
        finally
        {
            PrintTestButton.IsEnabled = true;
        }
    }

    // ================================================================ Fond personnalisé et gabarit

    private void RefreshBackgrounds()
    {
        _backgrounds.Clear();
        var templates = Services.Templates.LoadAll().Where(t => t.Layout.IsCustomizable);
        foreach (var group in templates.GroupBy(t => t.Layout.DesignStyle!, StringComparer.OrdinalIgnoreCase))
        {
            var template = group.First();
            var file = Design.StoredBackgroundFor(group.Key);
            var path = file == null ? null : Path.Combine(Services.CustomizationFolder, file);
            var has = path != null && File.Exists(path);
            var name = DesignStyles.IsBookmark(group.Key) ? "Fond du marque-page" : "Fond du 10x15";
            _backgrounds.Add(new BackgroundItem(template, group.Key,
                $"{name} ({template.Layout.Width} × {template.Layout.Height} px)",
                has ? "✓ Votre fond est utilisé pour ce format." : "Pas encore d'image : ce format garde la couleur et le décor du thème.",
                has,
                has ? ImageLoader.FromFile(path, decodePixelHeight: 168) : null));
        }
    }

    private void ExportGuide_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BackgroundItem item) return;
        var dialog = new SaveFileDialog
        {
            Title = "Enregistrer le gabarit",
            FileName = $"gabarit-{item.Style}.png",
            Filter = "Image PNG (*.png)|*.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            DesignGuide.WritePng(item.Template.Layout, dialog.FileName);
            StatusText.Text = "Gabarit enregistré : " + dialog.FileName;
        }
        catch (Exception ex)
        {
            ShowError("Gabarit impossible à enregistrer : " + ex.Message);
        }
    }

    private void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BackgroundItem item) return;
        var dialog = new OpenFileDialog { Title = "Choisir mon fond", Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            ReadControls();
            var layout = item.Template.Layout;
            var result = CustomizationStore.ImportBackground(Services.CurrentEvent.Folder, Customization, item.Style, dialog.FileName, layout.Width, layout.Height);
            var notes = new List<string> { "Fond importé." };
            if (result.Cropped)
            {
                notes.Add($"Votre image ({result.SourceWidth} × {result.SourceHeight}) n'a pas les proportions du format ({layout.Width} × {layout.Height}) : elle a été recadrée au centre.");
            }
            if (result.SourceWidth < layout.Width * 0.6 || result.SourceHeight < layout.Height * 0.6)
            {
                notes.Add("Elle est petite : elle risque d'être floue à l'impression.");
            }
            StatusText.Text = string.Join(" ", notes);
            RefreshBackgrounds();
            ShowPreviewOf(item.Template);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Import du fond impossible");
            ShowError("Fond impossible à importer : " + ex.Message);
        }
    }

    private void RemoveBackground_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BackgroundItem item) return;
        ReadControls();
        CustomizationStore.RemoveBackground(Services.CurrentEvent.Folder, Customization, item.Style);
        RefreshBackgrounds();
        StatusText.Text = "Fond retiré : le thème est de nouveau utilisé.";
        ShowPreviewOf(item.Template);
    }

    private void ShowPreviewOf(Template template)
    {
        SelectPreviewItem(_previewFormats.FirstOrDefault(p => p.Template?.Id == template.Id));
        _previewTemplate = template;
        _previewAttract = false;
        UpdatePreviewNote();
        SchedulePreview();
    }

    // ================================================================ Écran d'accueil du photobooth

    private void RefreshAttract()
    {
        var own = CustomizationStore.AttractBackgroundPath(Services.CurrentEvent.Folder, Customization);
        var current = Services.CurrentAttractBackground();
        AttractStateText.Text = own != null
            ? "✓ Image d'accueil propre à cet événement."
            : current != null
                ? "Image commune réglée avec une version précédente de LibreBooth (avant la 1.3.0)."
                : "Pas d'image : l'accueil prend la couleur de fond du thème.";
        RemoveAttractButton.IsEnabled = current != null;
        AttractThumb.Source = ImageLoader.FromFile(current, decodePixelWidth: 240);
    }

    private void ChooseAttract_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Image de l'écran d'accueil", Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            ReadControls();
            var result = CustomizationStore.ImportAttractBackground(Services.CurrentEvent.Folder, Customization, dialog.FileName);
            var notes = new List<string> { "Image d'accueil ajoutée." };
            if (result.Cropped)
            {
                notes.Add($"Votre image ({result.SourceWidth} × {result.SourceHeight}) n'a pas le format 16:9 de l'écran : ses bords sont coupés.");
            }
            if (result.SourceWidth < 1280 || result.SourceHeight < 720)
            {
                notes.Add("Elle est petite : elle risque d'être floue sur l'écran.");
            }
            StatusText.Text = string.Join(" ", notes);
            RefreshAttract();
            ShowAttractPreview();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Import de l'image d'accueil impossible");
            ShowError("Image impossible à importer : " + ex.Message);
        }
    }

    private void RemoveAttract_Click(object sender, RoutedEventArgs e)
    {
        ReadControls();
        CustomizationStore.RemoveAttractBackground(Services.CurrentEvent.Folder, Customization);
        if (!string.IsNullOrWhiteSpace(Services.Settings.Screens.AttractBackground))
        {
            // Image commune des versions précédentes : elle est abandonnée elle aussi.
            Services.Settings.Screens.AttractBackground = null;
            Services.SaveSettings();
        }
        RefreshAttract();
        StatusText.Text = "Image d'accueil retirée : l'accueil prend la couleur de fond du thème.";
        ShowAttractPreview();
    }

    // ================================================================ Logo

    private void RefreshLogo()
    {
        var file = Design.LogoFile;
        var path = file == null ? null : Path.Combine(Services.CustomizationFolder, file);
        var has = path != null && File.Exists(path);
        LogoStateText.Text = has ? "✓ Un logo est ajouté aux impressions." : "Pas de logo.";
        RemoveLogoButton.IsEnabled = has;
        LogoThumb.Source = has ? ImageLoader.FromFile(path, decodePixelHeight: 144) : null;
    }

    private void ChooseLogo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choisir un logo", Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            ReadControls();
            CustomizationStore.ImportLogo(Services.CurrentEvent.Folder, Customization, dialog.FileName);
            RefreshLogo();
            StatusText.Text = "Logo ajouté.";
            SchedulePreview();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Import du logo impossible");
            ShowError("Logo impossible à importer : " + ex.Message);
        }
    }

    private void RemoveLogo_Click(object sender, RoutedEventArgs e)
    {
        ReadControls();
        CustomizationStore.RemoveLogo(Services.CurrentEvent.Folder, Customization);
        RefreshLogo();
        StatusText.Text = "Logo retiré.";
        SchedulePreview();
    }

    // ================================================================ Copie depuis un autre événement

    private void CopyFromEvent_Click(object sender, RoutedEventArgs e)
    {
        if (CopyEventCombo.SelectedItem is not BoothEvent source)
        {
            StatusText.Text = "Choisissez d'abord l'événement à reprendre.";
            return;
        }
        if (!AdminDialog.Ask(Window.GetWindow(this),
                $"Remplacer la personnalisation de « {Services.CurrentEvent.Name} » par celle de « {source.Name} » ?\n\n"
                + "Formats, exemplaires, thème, textes, fond, logo et image d'accueil de cet événement seront remplacés.",
                "Remplacer", image: MessageBoxImage.Warning, defaultConfirm: false))
        {
            return;
        }
        try
        {
            _saveTimer.Stop();
            var copy = CustomizationStore.CopyFrom(source.Folder, Services.CurrentEvent.Folder);
            Services.ReplaceCustomization(copy);
            LoadFromEvent();
            StatusText.Text = $"Personnalisation reprise de « {source.Name} ».";
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Copie de la personnalisation impossible");
            ShowError("Copie impossible : " + ex.Message);
        }
    }

    private void ShowError(string message) =>
        AdminDialog.Show(Window.GetWindow(this), message, "LibreBooth", MessageBoxButton.OK, MessageBoxImage.Warning);
}

/// <summary>Choix d'une liste déroulante : une valeur et son libellé.</summary>
public sealed record Choice<T>(T Value, string Label);

public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class FormatItem : NotifyBase
{
    private bool _isChecked;

    public FormatItem(Template template, bool isChecked)
    {
        Template = template;
        _isChecked = isChecked;
        var layout = template.Layout;
        Details = layout.IsCustomizable
            ? DesignStyles.IsBookmark(layout.DesignStyle)
                ? $"{template.ShotCount} photos · 2 marque-pages par feuille, coupés par la DNP · personnalisable"
                : $"{template.ShotCount} photo{(template.ShotCount > 1 ? "s" : "")} · 10x15 · personnalisable"
            : $"{template.ShotCount} photo{(template.ShotCount > 1 ? "s" : "")} · cadre PNG importé (décor fixe)";
    }

    public Template Template { get; }
    public string Name => Template.Name;
    public string Details { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
            CheckedChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<bool>? CheckedChanged;
}

public sealed class ThemeCard : NotifyBase
{
    private ImageSource? _thumbnail;
    private bool _isCurrent;

    public ThemeCard(ThemePreset preset) => Preset = preset;

    public ThemePreset Preset { get; }
    public string Name => Preset.Name;
    public string Category => Preset.Category;
    public string Description => Preset.Description;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged();
        }
    }
}

public sealed record BackgroundItem(Template Template, string Style, string Name, string State, bool HasBackground, ImageSource? Thumbnail);

/// <summary>Un choix de l'aperçu : un format d'impression, ou l'écran d'accueil du photobooth (sans modèle).</summary>
public sealed class PreviewFormatItem : NotifyBase
{
    private bool _isSelected;

    public PreviewFormatItem(Template? template, string? name = null)
    {
        Template = template;
        Name = name ?? template?.Name ?? "";
    }

    public Template? Template { get; }
    public bool IsAttractScreen => Template == null;
    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }
}
