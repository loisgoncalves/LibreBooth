using System.Collections.Concurrent;
using SkiaSharp;

namespace LibreBooth.Core.Design;

/// <summary>
/// Couleurs et police des écrans du photobooth pour un événement. Elles viennent du thème d'impression,
/// ou du fond PNG (ou du cadre importé) quand l'opérateur en a fourni un : l'écran s'accorde aux tirages.
/// Les textes gardent toujours un contraste suffisant pour être lus de loin.
/// </summary>
/// <param name="Background">Fond des écrans.</param>
/// <param name="Foreground">Textes principaux.</param>
/// <param name="Muted">Textes secondaires.</param>
/// <param name="Accent">Boutons principaux, compte à rebours, cercle de l'accueil.</param>
/// <param name="AccentForeground">Texte posé sur la couleur d'accent.</param>
/// <param name="AccentText">Couleur d'accent assez contrastée pour écrire un texte sur le fond.</param>
/// <param name="IsDark">Fond sombre : voiles et bordures blancs ; sinon noirs.</param>
/// <param name="Source">D'où viennent les couleurs, pour l'admin.</param>
public sealed record KioskPalette(
    string Background,
    string Foreground,
    string Muted,
    string Accent,
    string AccentForeground,
    string AccentText,
    bool IsDark,
    string TitleFont,
    int TitleWeight,
    string Source)
{
    public const string DefaultFont = "Segoe UI Variable Display";

    /// <summary>Contraste minimal (norme WCAG) pour les textes principaux et pour les textes en couleur d'accent.</summary>
    public const double TextContrast = 4.5;
    /// <summary>Contraste minimal des textes secondaires et des boutons par rapport au fond.</summary>
    public const double MutedContrast = 3.0;
    public const double FillContrast = 1.5;

    private static readonly SKColor Ink = new(0x1B, 0x1B, 0x1F);
    private static readonly SKColor Paper = SKColors.White;
    private static readonly ConcurrentDictionary<string, IReadOnlyList<ImageColor>> ImageCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Couleurs de LibreBooth avant la 1.3.0 (fond nuit, accent jaune), si rien d'autre n'est disponible.</summary>
    public static KioskPalette Default { get; } = new("#14161C", "#FFFFFF", "#B8BCC8", "#FFC83D", "#14161C", "#FFC83D", true, DefaultFont, 700, "couleurs de LibreBooth");

    /// <param name="design">Apparence de l'événement.</param>
    /// <param name="imagePath">Fond PNG ou cadre importé dont on tire les couleurs (null : couleurs du thème).</param>
    /// <param name="sourceName">Nom de cette image pour l'admin (« votre fond 10x15 », « le cadre Mariage »).</param>
    public static KioskPalette Create(EventDesign design, string? imagePath = null, string? sourceName = null)
    {
        var titleFont = DesignFonts.IsInstalled(design.TitleFont) ? design.TitleFont : DefaultFont;
        var titleWeight = Math.Clamp(design.TitleWeight, 300, 900);
        var themeName = ThemeCatalog.Find(design.ThemeId)?.Name;

        var colors = imagePath != null ? ReadImageColors(imagePath) : null;
        if (colors is { Count: > 0 })
        {
            return FromImage(colors, design, titleFont, titleWeight, sourceName ?? "votre image");
        }
        return FromTheme(design, titleFont, titleWeight, themeName != null ? $"le thème « {themeName} »" : "les couleurs de l'événement");
    }

    // ---------------------------------------------------------------- Thème

    private static KioskPalette FromTheme(EventDesign design, string titleFont, int titleWeight, string source)
    {
        var background = ColorUtil.Parse(design.BackgroundColor, SKColors.White).WithAlpha(255);
        // Le titre du thème s'il se lit sur le fond. Sinon (titre blanc posé sur un bandeau de couleur, par exemple),
        // la couleur du thème la plus lisible sur le fond des écrans, ou à défaut noir ou blanc.
        var title = ColorUtil.Parse(design.TitleColor, Ink).WithAlpha(255);
        var foreground = Contrast(title, background) >= TextContrast
            ? title
            : new[] { design.TextColor, design.AccentColor, design.Accent2Color }
                  .Select(c => ColorUtil.Parse(c, Ink).WithAlpha(255))
                  .Where(c => Contrast(c, background) >= 7)
                  .OrderByDescending(c => Contrast(c, background))
                  .Select(c => (SKColor?)c)
                  .FirstOrDefault() ?? BestInk(background);
        var muted = Contrast(ColorUtil.Parse(design.TextColor, foreground), background) >= MutedContrast
            ? ColorUtil.Parse(design.TextColor, foreground).WithAlpha(255)
            : Soften(foreground, background);
        var accent = PickAccent(background, foreground,
            ColorUtil.Parse(design.AccentColor, foreground), ColorUtil.Parse(design.Accent2Color, foreground));
        return Build(background, foreground, muted, accent, titleFont, titleWeight, source);
    }

    // ---------------------------------------------------------------- Image (fond PNG ou cadre)

    private static KioskPalette FromImage(IReadOnlyList<ImageColor> colors, EventDesign design, string titleFont, int titleWeight, string source)
    {
        // Fond : la couleur qui couvre le plus de surface.
        var background = colors[0].Color;

        // Texte : une couleur bien présente de l'image si elle se lit, sinon noir ou blanc.
        var foreground = colors.Skip(1)
            .Where(c => c.Share >= 0.01 && Contrast(c.Color, background) >= 7)
            .Select(c => (SKColor?)c.Color)
            .FirstOrDefault() ?? BestInk(background);

        // Accent : la couleur vive la plus présente qui se détache du fond ; à défaut, celle du thème.
        var vivid = colors.Skip(1)
            .Where(c => c.Share >= 0.015 && Saturation(c.Color) >= 0.25 && Contrast(c.Color, background) >= FillContrast)
            .OrderByDescending(c => Saturation(c.Color) * 0.6 + Math.Min(Contrast(c.Color, background), 4) / 4 * 0.4 + Math.Min(c.Share, 0.2))
            .Select(c => (SKColor?)c.Color)
            .FirstOrDefault();
        var accent = vivid ?? PickAccent(background, foreground,
            ColorUtil.Parse(design.AccentColor, foreground), ColorUtil.Parse(design.Accent2Color, foreground));

        return Build(background, foreground, Soften(foreground, background), accent, titleFont, titleWeight, source);
    }

    /// <summary>Couleurs dominantes d'une image (pixels opaques seulement), de la plus présente à la moins présente.</summary>
    public static IReadOnlyList<ImageColor> ReadImageColors(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Array.Empty<ImageColor>();
            var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            return ImageCache.GetOrAdd(key, _ => AnalyzeImage(info.FullName));
        }
        catch (Exception)
        {
            return Array.Empty<ImageColor>();
        }
    }

    private static IReadOnlyList<ImageColor> AnalyzeImage(string path)
    {
        using var source = SKBitmap.Decode(path);
        if (source == null) return Array.Empty<ImageColor>();
        var scale = Math.Min(1f, 120f / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)(source.Width * scale));
        var height = Math.Max(1, (int)(source.Height * scale));
        using var small = source.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKFilterMode.Linear));
        if (small == null) return Array.Empty<ImageColor>();

        // 8 niveaux par composante : 512 « cases » de couleurs voisines, dont on garde la couleur moyenne.
        var sums = new long[512, 4];
        var opaque = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = small.GetPixel(x, y);
                if (pixel.Alpha < 200) continue;
                var bucket = (pixel.Red >> 5) << 6 | (pixel.Green >> 5) << 3 | pixel.Blue >> 5;
                sums[bucket, 0] += pixel.Red;
                sums[bucket, 1] += pixel.Green;
                sums[bucket, 2] += pixel.Blue;
                sums[bucket, 3]++;
                opaque++;
            }
        }

        // Un cadre presque entièrement transparent ne dit rien de ses couleurs.
        if (opaque < 50 || opaque < width * height * 0.05) return Array.Empty<ImageColor>();
        var colors = new List<ImageColor>();
        for (var bucket = 0; bucket < 512; bucket++)
        {
            var count = sums[bucket, 3];
            if (count == 0) continue;
            colors.Add(new ImageColor(
                new SKColor((byte)(sums[bucket, 0] / count), (byte)(sums[bucket, 1] / count), (byte)(sums[bucket, 2] / count)),
                (double)count / opaque));
        }
        return colors.OrderByDescending(c => c.Share).ToList();
    }

    // ---------------------------------------------------------------- Règles de lisibilité

    private static KioskPalette Build(SKColor background, SKColor foreground, SKColor muted, SKColor accent, string titleFont, int titleWeight, string source)
    {
        var accentText = Readable(accent, background, TextContrast);
        var accentForeground = Contrast(Paper, accent) >= Contrast(Ink, accent) ? Paper : Ink;
        return new KioskPalette(Hex(background), Hex(foreground), Hex(muted), Hex(accent), Hex(accentForeground), Hex(accentText),
            IsDarkColor(background), titleFont, titleWeight, source);
    }

    /// <summary>L'accent du thème s'il se détache du fond, sinon le second, sinon la couleur du texte.</summary>
    private static SKColor PickAccent(SKColor background, SKColor foreground, params SKColor[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var opaque = candidate.WithAlpha(255);
            if (Contrast(opaque, background) >= FillContrast) return opaque;
        }
        return foreground;
    }

    /// <summary>
    /// La couleur demandée si elle se lit sur le fond ; sinon la même, assombrie (fond clair) ou éclaircie
    /// (fond sombre) juste assez pour atteindre le contraste voulu.
    /// </summary>
    public static SKColor Readable(SKColor color, SKColor background, double minimum)
    {
        color = color.WithAlpha(255);
        if (Contrast(color, background) >= minimum) return color;
        var target = IsDarkColor(background) ? Paper : Ink;
        for (var step = 1; step <= 10; step++)
        {
            var mixed = ColorUtil.Mix(color, target, step / 10f);
            if (Contrast(mixed, background) >= minimum) return mixed;
        }
        return target;
    }

    /// <summary>Texte secondaire : le texte principal un peu fondu dans le fond, sans passer sous le contraste minimal.</summary>
    private static SKColor Soften(SKColor foreground, SKColor background)
    {
        for (var t = 0.4f; t > 0f; t -= 0.05f)
        {
            var mixed = ColorUtil.Mix(foreground, background, t);
            if (Contrast(mixed, background) >= MutedContrast + 0.5) return mixed;
        }
        return foreground;
    }

    private static SKColor BestInk(SKColor background) => Contrast(Paper, background) >= Contrast(Ink, background) ? Paper : Ink;

    /// <summary>Un fond est sombre quand un texte blanc s'y lit mieux qu'un texte noir.</summary>
    public static bool IsDarkColor(SKColor color) => RelativeLuminance(color) < 0.179;

    /// <summary>Rapport de contraste WCAG entre deux couleurs (1 à 21).</summary>
    public static double Contrast(SKColor a, SKColor b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static double Contrast(string a, string b) => Contrast(ColorUtil.Parse(a, SKColors.Black), ColorUtil.Parse(b, SKColors.White));

    private static double RelativeLuminance(SKColor color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.Red) + 0.7152 * Channel(color.Green) + 0.0722 * Channel(color.Blue);
    }

    private static double Saturation(SKColor color)
    {
        var max = Math.Max(color.Red, Math.Max(color.Green, color.Blue));
        var min = Math.Min(color.Red, Math.Min(color.Green, color.Blue));
        return max == 0 ? 0 : (max - min) / (double)max;
    }

    private static string Hex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
}

/// <summary>Une couleur d'image et la part de la surface (0 à 1) qu'elle occupe.</summary>
public sealed record ImageColor(SKColor Color, double Share);
