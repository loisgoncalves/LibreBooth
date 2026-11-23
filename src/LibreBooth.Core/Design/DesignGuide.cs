using LibreBooth.Core.Layouts;
using SkiaSharp;

namespace LibreBooth.Core.Design;

/// <summary>
/// Gabarit à ouvrir dans Canva ou Photoshop pour dessiner son propre fond : taille exacte, zones photo,
/// emplacement des textes et du logo, marge de sécurité.
/// </summary>
public static class DesignGuide
{
    private static readonly SKColor PhotoFill = new(0xDC, 0xEB, 0xFF);
    private static readonly SKColor PhotoStroke = new(0x2F, 0x6F, 0xD6);
    private static readonly SKColor TextStroke = new(0x6B, 0x6B, 0x6B);
    private static readonly SKColor LogoStroke = new(0xE0, 0x8A, 0x00);
    private static readonly SKColor SafeStroke = new(0xD6, 0x33, 0x33);

    public static SKImage Render(LayoutDefinition layout)
    {
        var design = new EventDesign { Decor = DecorStyle.None };
        var withoutLogo = DesignLayout.Compute(layout, design, DecorStyle.None, hasLogo: false);
        var withLogo = DesignLayout.Compute(layout, design, DecorStyle.None, hasLogo: true);
        var bookmark = DesignStyles.IsBookmark(layout.DesignStyle);

        var info = new SKImageInfo(layout.Width, layout.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Mémoire insuffisante pour le gabarit.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        var small = bookmark ? 21f : 28f;

        // Zones des textes de LibreBooth (on peut les désactiver quand le fond contient déjà les textes),
        // dessinées avec la place du logo pour que rien ne se chevauche.
        using (var dashed = Dashed(TextStroke, 3))
        {
            var textAreas = withLogo.Stacked
                ? new[] { withLogo.TitleArea }
                : new[] { withLogo.TitleArea, withLogo.DetailsArea };
            foreach (var area in textAreas)
            {
                canvas.DrawRect(area, dashed);
                Label(canvas, area == withLogo.TitleArea ? "Textes : titre" + (withLogo.Stacked ? ", date, phrase" : "") : "Textes : date, phrase",
                    new SKPoint(area.MidX, area.MidY + small * 0.35f), small, TextStroke, bold: true);
            }
        }

        if (withLogo.LogoArea is { } logo)
        {
            using var dashed = Dashed(LogoStroke, 3);
            canvas.DrawRect(logo, dashed);
            Label(canvas, "Logo (facultatif)", new SKPoint(logo.MidX, logo.MidY + small * 0.35f), small, LogoStroke, bold: true);
        }

        using (var fill = new SKPaint { Color = PhotoFill, IsAntialias = true })
        using (var stroke = new SKPaint { Color = PhotoStroke, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4 })
        {
            for (var i = 0; i < withoutLogo.Photos.Count; i++)
            {
                var photo = withoutLogo.Photos[i];
                canvas.DrawRect(photo, fill);
                canvas.DrawRect(photo, stroke);
                var big = Math.Min(photo.Height * 0.16f, bookmark ? 46f : 90f);
                Label(canvas, $"PHOTO {i + 1}", new SKPoint(photo.MidX, photo.MidY - big * 0.1f), big, PhotoStroke, bold: true);
                Label(canvas, $"{(int)photo.Width} × {(int)photo.Height} px", new SKPoint(photo.MidX, photo.MidY + big * 0.75f), small, PhotoStroke, bold: false);
            }
        }

        // Explications en bas des zones photo (une ligne par photo sur le marque-page).
        var notes = bookmark
            ? new[] { "Marque-page 600 × 1800 px, 300 dpi", "Votre fond passe sous les photos", "Imprimé 2 fois, coupé au milieu" }
            : new[] { "Gabarit 10x15 paysage – 1800 × 1200 px – 300 dpi", "Votre fond (PNG ou JPG) passe sous les photos : les zones bleues seront recouvertes." };
        if (bookmark)
        {
            for (var i = 0; i < notes.Length && i < withoutLogo.Photos.Count; i++)
            {
                var photo = withoutLogo.Photos[i];
                Label(canvas, notes[i], new SKPoint(photo.MidX, photo.Bottom - small * 0.9f), small * 0.9f, PhotoStroke, bold: false);
            }
        }
        else if (withoutLogo.Photos.Count > 0)
        {
            var first = withoutLogo.Photos[0];
            var y = first.Bottom - small * 1.25f * notes.Length;
            foreach (var note in notes)
            {
                Label(canvas, note, new SKPoint(first.MidX, y), small, PhotoStroke, bold: false);
                y += small * 1.25f;
            }
        }

        // Marge de sécurité : le bord du papier peut être rogné de 1 à 2 mm.
        using (var safe = Dashed(SafeStroke, 3))
        {
            canvas.DrawRect(withoutLogo.Safe, safe);
        }
        Label(canvas, "Marge de sécurité 3 mm", new SKPoint(withoutLogo.Canvas.MidX, DesignLayout.SafeMargin - 9), bookmark ? 17f : 21f, SafeStroke, bold: false);

        canvas.Flush();
        return surface.Snapshot();
    }

    public static void WritePng(LayoutDefinition layout, string path)
    {
        using var image = Render(layout);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        Util.AtomicFile.WriteAllBytes(path, data.ToArray());
    }

    private static SKPaint Dashed(SKColor color, float width) => new()
    {
        Color = color,
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = width,
        PathEffect = SKPathEffect.CreateDash(new[] { 16f, 10f }, 0),
    };

    private static void Label(SKCanvas canvas, string text, SKPoint center, float size, SKColor color, bool bold)
    {
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", bold ? SKFontStyle.Bold : SKFontStyle.Normal), size);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, center.X, center.Y, SKTextAlign.Center, font, paint);
    }
}
