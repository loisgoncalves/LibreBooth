using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LibreBooth.Core.Design;

namespace LibreBooth.App.Infrastructure;

/// <summary>
/// Couleurs et police des écrans du photobooth, tirées de la personnalisation de l'événement (voir KioskPalette).
/// Les écrans les lisent en DynamicResource : elles changent dès qu'on les applique.
/// </summary>
internal static class Theme
{
    public static void Apply(Application app, KioskPalette palette)
    {
        var resources = CreateResources(palette);
        foreach (var key in resources.Keys) app.Resources[key] = resources[key];
    }

    /// <summary>Les ressources des écrans pour cette palette (aussi utilisées pour l'aperçu de l'accueil dans l'admin).</summary>
    public static ResourceDictionary CreateResources(KioskPalette palette)
    {
        var background = ParseColor(palette.Background, Color.FromRgb(0x14, 0x16, 0x1C));
        var foreground = ParseColor(palette.Foreground, Colors.White);
        var veil = palette.IsDark ? Colors.White : Colors.Black;

        return new ResourceDictionary
        {
            ["BackgroundBrush"] = Frozen(background),
            ["ForegroundBrush"] = Frozen(foreground),
            ["MutedTextBrush"] = Frozen(ParseColor(palette.Muted, foreground)),
            ["AccentBrush"] = Frozen(ParseColor(palette.Accent, foreground)),
            ["AccentForegroundBrush"] = Frozen(ParseColor(palette.AccentForeground, background)),
            ["AccentTextBrush"] = Frozen(ParseColor(palette.AccentText, foreground)),
            ["SurfaceBrush"] = Frozen(WithAlpha(veil, (byte)(palette.IsDark ? 0x14 : 0x0D))),
            ["SurfaceStrongBrush"] = Frozen(WithAlpha(veil, (byte)(palette.IsDark ? 0x26 : 0x12))),
            ["SurfaceBorderBrush"] = Frozen(WithAlpha(veil, (byte)(palette.IsDark ? 0x40 : 0x30))),
            ["KeyBrush"] = Frozen(Mix(background, veil, palette.IsDark ? 0.14 : 0.07)),
            ["OverlayBrush"] = Frozen(WithAlpha(background, 0xF0)),
            ["PlaceholderBrush"] = Frozen(WithAlpha(foreground, 0x66)),
            ["KioskTitleFont"] = new FontFamily($"{palette.TitleFont}, Segoe UI Variable Display, Segoe UI"),
            ["KioskTitleWeight"] = FontWeight.FromOpenTypeWeight(Math.Clamp(palette.TitleWeight, 1, 999)),
        };
    }

    public static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            return ColorConverter.ConvertFromString(value.Trim()) is Color color ? color : fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value == null || (value is string text && string.IsNullOrWhiteSpace(text)) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EmptyToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string text && text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
