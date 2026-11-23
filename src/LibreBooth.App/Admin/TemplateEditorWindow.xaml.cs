using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibreBooth.App.Infrastructure;
using LibreBooth.App.Services;
using LibreBooth.Core.Layouts;
using LibreBooth.Core.Util;
using Microsoft.Win32;
using Serilog;

namespace LibreBooth.App.Admin;

/// <summary>
/// Éditeur de modèle : on voit le cadre PNG, on déplace et redimensionne les zones photo à la souris ou au doigt,
/// on règle les textes, puis on vérifie avec un aperçu monté avec des photos de démonstration.
/// </summary>
public partial class TemplateEditorWindow : Window
{
    private readonly ILogger _log = Log.ForContext<TemplateEditorWindow>();
    private readonly AppServices _services;
    private readonly Template _template;
    private readonly LayoutDefinition _layout;
    private string? _pendingFramePath;
    private int _selectedZone = -1;
    private int _selectedText = -1;

    public TemplateEditorWindow(AppServices services, Template template, string? message = null)
    {
        InitializeComponent();
        _services = services;
        _template = template;
        _layout = JsonDefaults.Clone(template.Layout);

        InfoText.Text = message ?? "";
        if (_layout.IsCustomizable)
        {
            // Le décor, les textes et le fond viennent de l'onglet Personnalisation : ici, seules les photos se placent.
            TextsSection.Visibility = Visibility.Collapsed;
            FrameOptions.Visibility = Visibility.Collapsed;
            BwCheck.Visibility = Visibility.Collapsed;
            DetectZonesButton.Visibility = Visibility.Collapsed;
            if (string.IsNullOrEmpty(message))
            {
                InfoText.Text = "Modèle personnalisable : ici, vous réglez la place et la taille des photos. "
                                + "Couleurs, textes, fond et logo se règlent dans l'onglet Personnalisation.";
            }
        }
        NameBox.Text = _layout.Name;
        StripCheck.IsChecked = _layout.DuplicateStrip;
        BwCheck.IsChecked = _layout.BlackAndWhite;
        BackgroundColorBox.Text = _layout.BackgroundColor;
        ProfileCombo.ItemsSource = services.Templates.LoadAll().Select(t => t.Layout.PrintProfile)
            .Concat(new[] { PrintProfiles.Postcard, PrintProfiles.Strips })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ProfileCombo.Text = _layout.PrintProfile;
        TextAlignCombo.ItemsSource = new[] { "Gauche", "Centre", "Droite" };

        LoadFrame(template.FramePath);
        Redraw();
        if (_layout.Zones.Count > 0) SelectZone(0);
        if (_layout.Texts.Count > 0) SelectText(0);
    }

    // ---------------------------------------------------------------- Dessin du plan de travail

    private void LoadFrame(string? path)
    {
        FrameImage.Source = path != null ? ImageLoader.FromFile(path) : null;
    }

    private void Redraw()
    {
        CanvasHost.Width = _layout.Width;
        CanvasHost.Height = _layout.Height;
        var background = _layout.IsCustomizable ? _services.Customization.Design.BackgroundColor : _layout.BackgroundColor;
        BackgroundRect.Fill = new SolidColorBrush(Theme.ParseColor(background, Colors.White));
        SizeText.Text = $"Taille : {_layout.Width} × {_layout.Height} px" + DescribeSize();

        ZonesCanvas.Children.Clear();
        for (var i = 0; i < _layout.Zones.Count; i++) ZonesCanvas.Children.Add(CreateZoneVisual(i));
        foreach (var text in _layout.Texts) ZonesCanvas.Children.Add(CreateTextMarker(text));
        RefreshLists();
    }

    private string DescribeSize()
    {
        var ratio = (double)_layout.Height / _layout.Width;
        if (_layout.Width == 1800 && _layout.Height == 1200) return " (10x15 paysage à 300 dpi)";
        if (_layout.Width == 1200 && _layout.Height == 1800) return " (10x15 portrait à 300 dpi)";
        if (_layout.Width == 600 && _layout.Height == 1800) return " (bandelette 5x15 à 300 dpi)";
        if (ratio is > 2.9 and < 3.1) return " (proportions d'une bandelette 5x15)";
        if (Math.Abs(ratio - 1.5) < 0.05 || Math.Abs(ratio - 2.0 / 3.0) < 0.05) return " (proportions d'un 10x15)";
        return " – attention : proportions inhabituelles pour du 10x15";
    }

    private FrameworkElement CreateZoneVisual(int index)
    {
        var zone = _layout.Zones[index];
        var selected = index == _selectedZone;
        var stroke = Math.Max(4, Math.Min(_layout.Width, _layout.Height) / 220.0);
        var handle = Math.Max(28, Math.Min(_layout.Width, _layout.Height) / 22.0);

        var grid = new Grid { Width = zone.Width, Height = zone.Height, Tag = index };
        grid.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(selected ? (byte)120 : (byte)80, 51, 153, 255)),
            BorderBrush = selected ? Brushes.Orange : Brushes.DeepSkyBlue,
            BorderThickness = new Thickness(selected ? stroke * 1.6 : stroke),
        });
        grid.Children.Add(new TextBlock
        {
            Text = (index + 1).ToString(CultureInfo.InvariantCulture),
            FontSize = Math.Max(30, Math.Min(zone.Width, zone.Height) / 3.0),
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        });

        var move = new Thumb { Template = (ControlTemplate)FindResource("MoveThumbTemplate"), Cursor = Cursors.SizeAll };
        move.DragStarted += (_, _) => SelectZone(index, redraw: false);
        move.DragDelta += (_, e) =>
        {
            zone.X += (int)Math.Round(e.HorizontalChange);
            zone.Y += (int)Math.Round(e.VerticalChange);
            zone.ClampTo(_layout.Width, _layout.Height);
            PlaceZone(grid, zone);
        };
        move.DragCompleted += (_, _) => Redraw();
        grid.Children.Add(move);

        var resize = new Thumb
        {
            Template = (ControlTemplate)FindResource("ResizeThumbTemplate"),
            Width = handle,
            Height = handle,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
        };
        resize.DragStarted += (_, _) => SelectZone(index, redraw: false);
        resize.DragDelta += (_, e) =>
        {
            zone.Width = Math.Max(40, zone.Width + (int)Math.Round(e.HorizontalChange));
            zone.Height = Math.Max(40, zone.Height + (int)Math.Round(e.VerticalChange));
            zone.ClampTo(_layout.Width, _layout.Height);
            PlaceZone(grid, zone);
        };
        resize.DragCompleted += (_, _) => Redraw();
        grid.Children.Add(resize);

        PlaceZone(grid, zone);
        return grid;
    }

    private void PlaceZone(FrameworkElement visual, PhotoZone zone)
    {
        visual.Width = zone.Width;
        visual.Height = zone.Height;
        Canvas.SetLeft(visual, zone.X);
        Canvas.SetTop(visual, zone.Y);
        if (visual.Tag is int index && index == _selectedZone) FillZoneFields(zone);
    }

    private FrameworkElement CreateTextMarker(TextField field)
    {
        var sample = TextUtil.ApplyVariables(field.Content, TemplatePreview.SampleEventName, DateTime.Today);
        var block = new TextBlock
        {
            Text = sample,
            FontSize = Math.Max(4, field.Size),
            FontFamily = new FontFamily(string.IsNullOrWhiteSpace(field.Font) ? "Segoe UI" : field.Font),
            FontWeight = field.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = field.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = new SolidColorBrush(Theme.ParseColor(field.Color, Colors.Black)),
            IsHitTestVisible = false,
        };
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = block.DesiredSize.Width;
        var left = field.Align switch
        {
            TextAlign.Left => field.X,
            TextAlign.Right => field.X - width,
            _ => field.X - width / 2,
        };
        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, field.Y - field.Size * 0.92);
        return block;
    }

    private void RefreshLists()
    {
        var zoneIndex = _selectedZone;
        ZonesList.ItemsSource = _layout.Zones.Select((z, i) => $"Zone {i + 1} : {z}").ToList();
        ZonesList.SelectedIndex = zoneIndex < _layout.Zones.Count ? zoneIndex : -1;
        var textIndex = _selectedText;
        TextsList.ItemsSource = _layout.Texts.Select((t, i) => $"Texte {i + 1} : {t.Content}").ToList();
        TextsList.SelectedIndex = textIndex < _layout.Texts.Count ? textIndex : -1;
    }

    // ---------------------------------------------------------------- Zones

    private void SelectZone(int index, bool redraw = true)
    {
        _selectedZone = index;
        if (index >= 0 && index < _layout.Zones.Count) FillZoneFields(_layout.Zones[index]);
        if (redraw) Redraw();
    }

    private void FillZoneFields(PhotoZone zone)
    {
        ZoneXBox.Text = zone.X.ToString(CultureInfo.InvariantCulture);
        ZoneYBox.Text = zone.Y.ToString(CultureInfo.InvariantCulture);
        ZoneWBox.Text = zone.Width.ToString(CultureInfo.InvariantCulture);
        ZoneHBox.Text = zone.Height.ToString(CultureInfo.InvariantCulture);
    }

    private void ZonesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZonesList.SelectedIndex < 0 || ZonesList.SelectedIndex == _selectedZone) return;
        SelectZone(ZonesList.SelectedIndex);
    }

    private void ApplyZone_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedZone < 0 || _selectedZone >= _layout.Zones.Count) return;
        var zone = _layout.Zones[_selectedZone];
        if (!TryInt(ZoneXBox.Text, out var x) || !TryInt(ZoneYBox.Text, out var y) || !TryInt(ZoneWBox.Text, out var w) || !TryInt(ZoneHBox.Text, out var h))
        {
            StatusText.Text = "Valeurs de zone invalides (nombres entiers en pixels).";
            return;
        }
        zone.X = x;
        zone.Y = y;
        zone.Width = w;
        zone.Height = h;
        zone.ClampTo(_layout.Width, _layout.Height);
        Redraw();
    }

    private void DetectZones_Click(object sender, RoutedEventArgs e)
    {
        var frame = _pendingFramePath ?? _template.FramePath;
        if (frame == null)
        {
            StatusText.Text = "Pas de cadre PNG : choisissez-en un d'abord.";
            return;
        }
        if (_layout.Zones.Count > 0 &&
            MessageBox.Show(this, "Remplacer les zones actuelles par celles détectées dans le PNG ?", "LibreBooth",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            var zones = ZoneDetector.Detect(frame);
            var scaleX = (double)_layout.Width / Math.Max(1, PixelWidth(frame));
            var scaleY = (double)_layout.Height / Math.Max(1, PixelHeight(frame));
            _layout.Zones = zones.Select(z => new PhotoZone(
                (int)Math.Round(z.X * scaleX), (int)Math.Round(z.Y * scaleY),
                (int)Math.Round(z.Width * scaleX), (int)Math.Round(z.Height * scaleY))).ToList();
            _selectedZone = _layout.Zones.Count > 0 ? 0 : -1;
            StatusText.Text = $"{_layout.Zones.Count} zone(s) détectée(s).";
            Redraw();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Détection impossible : " + ex.Message;
        }
    }

    private void AddZone_Click(object sender, RoutedEventArgs e)
    {
        _layout.Zones.Add(new PhotoZone(_layout.Width / 4, _layout.Height / 4, _layout.Width / 2, _layout.Height / 2));
        SelectZone(_layout.Zones.Count - 1);
    }

    private void RemoveZone_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedZone < 0 || _selectedZone >= _layout.Zones.Count) return;
        _layout.Zones.RemoveAt(_selectedZone);
        SelectZone(Math.Min(_selectedZone, _layout.Zones.Count - 1));
    }

    private void SortZones_Click(object sender, RoutedEventArgs e)
    {
        _layout.Zones = ZoneDetector.SortReadingOrder(_layout.Zones);
        SelectZone(_layout.Zones.Count > 0 ? 0 : -1);
    }

    // ---------------------------------------------------------------- Textes

    private void SelectText(int index)
    {
        _selectedText = index;
        if (index < 0 || index >= _layout.Texts.Count) return;
        var text = _layout.Texts[index];
        TextContentBox.Text = text.Content;
        TextXBox.Text = text.X.ToString(CultureInfo.InvariantCulture);
        TextYBox.Text = text.Y.ToString(CultureInfo.InvariantCulture);
        TextSizeBox.Text = text.Size.ToString(CultureInfo.InvariantCulture);
        TextColorBox.Text = text.Color;
        TextFontBox.Text = text.Font;
        TextBoldCheck.IsChecked = text.Bold;
        TextItalicCheck.IsChecked = text.Italic;
        TextAlignCombo.SelectedIndex = (int)text.Align;
    }

    private void TextsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TextsList.SelectedIndex >= 0) SelectText(TextsList.SelectedIndex);
    }

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        _layout.Texts.Add(new TextField
        {
            Content = "{evenement}",
            X = _layout.Width / 2,
            Y = _layout.Height - 60,
            Size = Math.Max(30, _layout.Height / 25f),
            Color = "#222222",
        });
        _selectedText = _layout.Texts.Count - 1;
        Redraw();
        SelectText(_selectedText);
    }

    private void RemoveText_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedText < 0 || _selectedText >= _layout.Texts.Count) return;
        _layout.Texts.RemoveAt(_selectedText);
        _selectedText = Math.Min(_selectedText, _layout.Texts.Count - 1);
        Redraw();
        SelectText(_selectedText);
    }

    private void ApplyText_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedText < 0 || _selectedText >= _layout.Texts.Count) return;
        if (!TryInt(TextXBox.Text, out var x) || !TryInt(TextYBox.Text, out var y) ||
            !float.TryParse(TextSizeBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
        {
            StatusText.Text = "Valeurs de texte invalides.";
            return;
        }
        var text = _layout.Texts[_selectedText];
        text.Content = TextContentBox.Text;
        text.X = x;
        text.Y = y;
        text.Size = Math.Clamp(size, 4, 1000);
        text.Color = TextColorBox.Text.Trim();
        text.Font = TextFontBox.Text.Trim();
        text.Bold = TextBoldCheck.IsChecked == true;
        text.Italic = TextItalicCheck.IsChecked == true;
        text.Align = TextAlignCombo.SelectedIndex switch
        {
            0 => TextAlign.Left,
            2 => TextAlign.Right,
            _ => TextAlign.Center,
        };
        Redraw();
    }

    // ---------------------------------------------------------------- Cadre, options, aperçu, enregistrement

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (StripCheck.IsChecked == true && string.Equals(ProfileCombo.Text, PrintProfiles.Postcard, StringComparison.OrdinalIgnoreCase))
        {
            ProfileCombo.Text = PrintProfiles.Strips;
        }
    }

    private void BackgroundColor_LostFocus(object sender, RoutedEventArgs e)
    {
        _layout.BackgroundColor = BackgroundColorBox.Text.Trim();
        Redraw();
    }

    private void ChangeFrame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choisir un cadre PNG", Filter = "Images PNG (*.png)|*.png" };
        if (dialog.ShowDialog(this) != true) return;
        var width = PixelWidth(dialog.FileName);
        var height = PixelHeight(dialog.FileName);
        if (width <= 0 || height <= 0)
        {
            StatusText.Text = "Ce fichier n'est pas une image PNG lisible.";
            return;
        }
        if ((width != _layout.Width || height != _layout.Height) &&
            MessageBox.Show(this, $"Le nouveau cadre mesure {width} × {height} px. Adapter la taille du modèle ?", "LibreBooth",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _layout.Width = width;
            _layout.Height = height;
        }
        _pendingFramePath = dialog.FileName;
        LoadFrame(dialog.FileName);
        Redraw();
        StatusText.Text = "Nouveau cadre chargé : utilisez « Détecter les zones transparentes » si besoin.";
    }

    private void ApplyOptionsToLayout()
    {
        _layout.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? _layout.Name : NameBox.Text.Trim();
        _layout.DuplicateStrip = StripCheck.IsChecked == true;
        _layout.BlackAndWhite = BwCheck.IsChecked == true;
        _layout.PrintProfile = string.IsNullOrWhiteSpace(ProfileCombo.Text) ? PrintProfiles.Postcard : ProfileCombo.Text.Trim();
        _layout.BackgroundColor = string.IsNullOrWhiteSpace(BackgroundColorBox.Text) ? "#FFFFFF" : BackgroundColorBox.Text.Trim();
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        ApplyOptionsToLayout();
        var folder = Path.Combine(Path.GetTempPath(), "librebooth-editeur-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(folder);
            var layout = JsonDefaults.Clone(_layout);
            var frame = _pendingFramePath ?? _template.FramePath;
            if (frame != null)
            {
                File.Copy(frame, Path.Combine(folder, "cadre.png"), overwrite: true);
                layout.FrameFile = "cadre.png";
            }
            if (_template.BackgroundPath is { } background && layout.BackgroundFile != null)
            {
                File.Copy(background, Path.Combine(folder, layout.BackgroundFile), overwrite: true);
            }
            var output = Path.Combine(folder, "apercu.jpg");
            TemplatePreview.WriteSample(_services.Compositor, new Template(folder, layout), output, _services.CurrentEvent.Name,
                _services.CurrentDesign(), _services.CurrentEvent.Date);
            ShowImage(output, $"Aperçu – {layout.Name}");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Aperçu du modèle impossible");
            StatusText.Text = "Aperçu impossible : " + ex.Message;
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Dossier temporaire.
            }
        }
    }

    private void ShowImage(string path, string title)
    {
        var image = ImageLoader.FromFile(path, decodePixelHeight: 1000);
        var window = new Window
        {
            Title = title,
            Owner = this,
            Width = 1100,
            Height = 860,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x30)),
            Content = new Image { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(16) },
        };
        window.ShowDialog();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplyOptionsToLayout();
        if (_layout.Zones.Count == 0 &&
            MessageBox.Show(this, "Ce modèle n'a aucune zone photo : il ne pourra pas être utilisé. Enregistrer quand même ?", "LibreBooth",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            if (_pendingFramePath != null)
            {
                File.Copy(_pendingFramePath, Path.Combine(_template.Folder, "cadre.png"), overwrite: true);
                _layout.FrameFile = "cadre.png";
            }
            _template.Layout = _layout;
            _services.Templates.Save(_template);
            _log.Information("Modèle enregistré : {Template}", _template.Id);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Enregistrement du modèle impossible");
            StatusText.Text = "Enregistrement impossible : " + ex.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool TryInt(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static int PixelWidth(string path) => ReadSize(path).Width;

    private static int PixelHeight(string path) => ReadSize(path).Height;

    private static (int Width, int Height) ReadSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return (0, 0);
        }
    }
}
