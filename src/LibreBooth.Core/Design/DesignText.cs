using System.Collections.Concurrent;
using System.Globalization;
using LibreBooth.Core.Layouts;
using LibreBooth.Core.Util;
using SkiaSharp;

namespace LibreBooth.Core.Design;

/// <summary>Polices de Windows utilisées par les modèles personnalisables.</summary>
public static class DesignFonts
{
    private static readonly ConcurrentDictionary<string, SKTypeface> Typefaces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<HashSet<string>> Installed = new(() =>
        new HashSet<string>(SKFontManager.Default.FontFamilies, StringComparer.OrdinalIgnoreCase));

    /// <summary>La police est-elle installée sur ce PC ? (sinon Windows la remplace par une police standard).</summary>
    public static bool IsInstalled(string? family) => !string.IsNullOrWhiteSpace(family) && Installed.Value.Contains(family.Trim());

    public static IReadOnlyCollection<string> InstalledFamilies => Installed.Value;

    internal static SKTypeface Get(string family, int weight, bool italic)
    {
        var key = $"{family}|{weight}|{italic}";
        return Typefaces.GetOrAdd(key, _ =>
            SKTypeface.FromFamilyName(
                family,
                new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright))
            ?? SKTypeface.Default);
    }
}

internal enum LineRole
{
    Title,
    Subtitle,
    Phrase,
}

/// <summary>Une ligne de texte placée sur le montage (centrée sur <see cref="CenterX"/>).</summary>
internal sealed class PlacedLine
{
    public required string Text { get; init; }
    public required LineRole Role { get; init; }
    public required SKTypeface Typeface { get; init; }
    public required SKColor Color { get; init; }
    public float Size { get; set; }
    /// <summary>Espacement ajouté entre les lettres, en pixels.</summary>
    public float Spacing { get; set; }
    public float Width { get; set; }
    /// <summary>Haut de l'encre par rapport à la ligne de base (négatif).</summary>
    public float InkTop { get; set; }
    public float InkBottom { get; set; }
    public float CenterX { get; set; }
    public float Baseline { get; set; }

    public float InkHeight => InkBottom - InkTop;
    public SKRect Ink => new(CenterX - Width / 2, Baseline + InkTop, CenterX + Width / 2, Baseline + InkBottom);

    public void Scale(float factor)
    {
        Size *= factor;
        Spacing *= factor;
        Width *= factor;
        InkTop *= factor;
        InkBottom *= factor;
    }
}

/// <summary>Mise en page des textes d'un modèle personnalisable : titre, date, phrase.</summary>
internal sealed class TextBlock
{
    /// <summary>Un titre trop long rétrécit jusqu'à 72 % de sa taille avant de passer sur deux lignes ; une phrase, 90 %.</summary>
    private const float TitleShrinkBeforeWrap = 0.72f;
    private const float PhraseShrinkBeforeWrap = 0.9f;

    private TextBlock(List<PlacedLine> lines) => Lines = lines;

    public static TextBlock Empty { get; } = new(new List<PlacedLine>());

    public IReadOnlyList<PlacedLine> Lines { get; }

    public IEnumerable<PlacedLine> TitleLines => Lines.Where(l => l.Role == LineRole.Title);
    public IEnumerable<PlacedLine> DetailLines => Lines.Where(l => l.Role != LineRole.Title);

    public static TextBlock Build(EventDesign design, DecorStyle decor, DesignAreas areas, CompositionContext context, bool bookmark)
    {
        string Prepare(string? raw, bool upper)
        {
            var text = TextUtil.ApplyVariables(raw, context.EventName, context.EventDate).Trim();
            return upper ? TextUtil.ToUpperFrench(text) : text;
        }

        var titleFace = DesignFonts.Get(design.TitleFont, design.TitleWeight, design.TitleItalic);
        var textFace = DesignFonts.Get(design.TextFont, design.TextWeight, design.TextItalic);
        var titleColor = ColorUtil.Parse(design.TitleColor, SKColors.Black);
        var textColor = ColorUtil.Parse(design.TextColor, SKColors.DimGray);
        var titleSize = (bookmark ? 84f : 100f) * design.TitleScale / 100f;
        var textSize = (bookmark ? 34f : 40f) * design.TextScale / 100f;
        var titleSpacing = design.TitleUppercase ? 0.14f : 0f;
        var textSpacing = design.TextUppercase ? 0.2f : 0f;

        var title = Prepare(design.Title, design.TitleUppercase);
        var subtitle = Prepare(design.Subtitle, design.TextUppercase);
        var phrase = Prepare(design.Phrase, design.TextUppercase);

        var all = new List<PlacedLine>();
        if (areas.Stacked)
        {
            var area = areas.TitleArea;
            var group = new List<PlacedLine>();
            group.AddRange(MakeLines(title, LineRole.Title, titleFace, titleSize, titleSpacing, titleColor, area.Width, bookmark ? 2 : 1));
            group.AddRange(MakeLines(subtitle, LineRole.Subtitle, textFace, textSize, textSpacing, textColor, area.Width, 1));
            group.AddRange(MakeLines(phrase, LineRole.Phrase, textFace, textSize * 0.9f, textSpacing, textColor, area.Width, bookmark ? 3 : 1));
            Stack(group, area, decor);
            all.AddRange(group);
        }
        else
        {
            var titleGroup = MakeLines(title, LineRole.Title, titleFace, titleSize, titleSpacing, titleColor, areas.TitleArea.Width, 2);
            Stack(titleGroup, areas.TitleArea, decor);
            all.AddRange(titleGroup);

            var details = new List<PlacedLine>();
            details.AddRange(MakeLines(subtitle, LineRole.Subtitle, textFace, textSize, textSpacing, textColor, areas.DetailsArea.Width, 1));
            details.AddRange(MakeLines(phrase, LineRole.Phrase, textFace, textSize * 0.9f, textSpacing, textColor, areas.DetailsArea.Width, 3));
            Stack(details, areas.DetailsArea, decor);
            all.AddRange(details);
        }
        return new TextBlock(all);
    }

    public void Draw(SKCanvas canvas, EventDesign design, DecorStyle decor)
    {
        foreach (var line in Lines)
        {
            using var font = CreateFont(line.Typeface, line.Size);
            if (decor == DecorStyle.Neon)
            {
                var glow = ColorUtil.Parse(line.Role == LineRole.Title ? design.AccentColor : design.Accent2Color, SKColors.HotPink);
                using var glowPaint = new SKPaint
                {
                    IsAntialias = true,
                    Color = glow.WithAlpha(230),
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(2f, line.Size * 0.09f)),
                };
                DrawLine(canvas, line, font, glowPaint);
                DrawLine(canvas, line, font, glowPaint);
            }
            using var paint = new SKPaint { IsAntialias = true, Color = line.Color };
            DrawLine(canvas, line, font, paint);
        }
    }

    private static SKFont CreateFont(SKTypeface typeface, float size) =>
        new(typeface, Math.Max(2f, size)) { Subpixel = true, Edging = SKFontEdging.Antialias };

    private static void DrawLine(SKCanvas canvas, PlacedLine line, SKFont font, SKPaint paint)
    {
        if (line.Spacing < 0.05f)
        {
            canvas.DrawText(line.Text, line.CenterX, line.Baseline, SKTextAlign.Center, font, paint);
            return;
        }

        var x = line.CenterX - line.Width / 2;
        foreach (var element in TextElements(line.Text))
        {
            canvas.DrawText(element, x, line.Baseline, SKTextAlign.Left, font, paint);
            x += font.MeasureText(element) + line.Spacing;
        }
    }

    /// <summary>Découpe le texte en lignes qui tiennent dans la largeur (le texte rétrécit si besoin).</summary>
    private static List<PlacedLine> MakeLines(string text, LineRole role, SKTypeface face, float size, float spacingEm, SKColor color, float maxWidth, int maxLines)
    {
        var result = new List<PlacedLine>();
        if (string.IsNullOrWhiteSpace(text) || maxWidth <= 10) return result;

        var parts = new List<string> { text };
        var width = Measure(text, face, size, spacingEm);
        var shrinkBeforeWrap = role == LineRole.Title ? TitleShrinkBeforeWrap : PhraseShrinkBeforeWrap;
        if (width > maxWidth / shrinkBeforeWrap && maxLines > 1 && text.Contains(' '))
        {
            // Plus d'une ligne seulement si le texte devait sinon trop rétrécir.
            var split = maxLines == 2 ? SplitBalanced(text, face, size, spacingEm) : WrapGreedy(text, face, size, spacingEm, maxWidth);
            var widest = split.Max(p => Measure(p, face, size, spacingEm));
            if (split.Count <= maxLines && widest < width * 0.85f) parts = split;
        }

        foreach (var part in parts)
        {
            var line = Place(part, role, face, size, spacingEm, color);
            if (line.Width > maxWidth) line.Scale(Math.Max(0.3f, maxWidth / line.Width));
            result.Add(line);
        }
        return result;
    }

    private static PlacedLine Place(string text, LineRole role, SKTypeface face, float size, float spacingEm, SKColor color)
    {
        using var font = CreateFont(face, size);
        font.MeasureText(text, out var bounds);
        var line = new PlacedLine
        {
            Text = text,
            Role = role,
            Typeface = face,
            Color = color,
            Size = size,
            Spacing = spacingEm * size,
            Width = Measure(text, face, size, spacingEm),
            InkTop = bounds.Height > 0 ? bounds.Top : -size * 0.7f,
            InkBottom = bounds.Height > 0 ? bounds.Bottom : 0,
        };
        return line;
    }

    private static float Measure(string text, SKTypeface face, float size, float spacingEm)
    {
        using var font = CreateFont(face, size);
        if (spacingEm < 0.005f) return font.MeasureText(text);
        var elements = TextElements(text).ToList();
        return elements.Sum(e => font.MeasureText(e)) + spacingEm * size * Math.Max(0, elements.Count - 1);
    }

    private static List<string> SplitBalanced(string text, SKTypeface face, float size, float spacingEm)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var best = new List<string> { text };
        var bestWidth = float.MaxValue;
        for (var i = 1; i < words.Length; i++)
        {
            var first = string.Join(' ', words[..i]);
            var second = string.Join(' ', words[i..]);
            var widest = Math.Max(Measure(first, face, size, spacingEm), Measure(second, face, size, spacingEm));
            if (widest < bestWidth)
            {
                bestWidth = widest;
                best = new List<string> { first, second };
            }
        }
        return best;
    }

    private static List<string> WrapGreedy(string text, SKTypeface face, float size, float spacingEm, float maxWidth)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && Measure(candidate, face, size, spacingEm) > maxWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    /// <summary>Empile les lignes, centrées dans la zone ; tout rétrécit ensemble si la hauteur manque.</summary>
    private static void Stack(List<PlacedLine> lines, SKRect area, DecorStyle decor)
    {
        if (lines.Count == 0) return;

        float Gap(int index)
        {
            if (index >= lines.Count - 1) return 0;
            var current = lines[index];
            var next = lines[index + 1];
            if (current.Role == LineRole.Title && next.Role == LineRole.Title) return current.Size * 0.12f;
            if (current.Role == LineRole.Title) return current.Size * 0.2f + (decor == DecorStyle.Minimal ? 34 : decor == DecorStyle.Lines ? 8 : 0);
            return Math.Max(current.Size, next.Size) * 0.45f;
        }

        var total = lines.Sum(l => l.InkHeight) + Enumerable.Range(0, lines.Count).Sum(Gap);
        if (total > area.Height && total > 0)
        {
            var factor = area.Height / total;
            foreach (var line in lines) line.Scale(factor);
            total = lines.Sum(l => l.InkHeight) + Enumerable.Range(0, lines.Count).Sum(Gap);
        }

        var y = area.MidY - total / 2;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            line.CenterX = area.MidX;
            line.Baseline = y - line.InkTop;
            y += line.InkHeight + Gap(i);
        }
    }

    private static IEnumerable<string> TextElements(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) yield return (string)enumerator.Current;
    }
}

internal static class ColorUtil
{
    public static SKColor Parse(string? value, SKColor fallback) =>
        !string.IsNullOrWhiteSpace(value) && SKColor.TryParse(value.Trim(), out var color) ? color : fallback;

    /// <summary>Mélange deux couleurs (t = 0 : la première, t = 1 : la seconde).</summary>
    public static SKColor Mix(SKColor a, SKColor b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)Math.Round(a.Red + (b.Red - a.Red) * t),
            (byte)Math.Round(a.Green + (b.Green - a.Green) * t),
            (byte)Math.Round(a.Blue + (b.Blue - a.Blue) * t),
            (byte)Math.Round(a.Alpha + (b.Alpha - a.Alpha) * t));
    }

    public static SKColor ShiftHue(SKColor color, float degrees)
    {
        color.ToHsv(out var h, out var s, out var v);
        return SKColor.FromHsv((h + degrees + 360f) % 360f, s, v);
    }

    /// <summary>Luminance approximative (0 noir, 1 blanc).</summary>
    public static float Luminance(SKColor color) => (0.2126f * color.Red + 0.7152f * color.Green + 0.0722f * color.Blue) / 255f;
}
