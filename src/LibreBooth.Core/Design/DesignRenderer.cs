using LibreBooth.Core.Layouts;
using SkiaSharp;

namespace LibreBooth.Core.Design;

/// <summary>
/// Dessine un modèle personnalisable : fond (couleur, décor ou PNG de l'opérateur), photos avec coins arrondis,
/// bordure et ombre, puis textes et logo.
/// </summary>
public static class DesignRenderer
{
    private static readonly SKSamplingOptions HighQuality = new(SKCubicResampler.Mitchell);

    public static void Render(SKCanvas canvas, LayoutDefinition layout, IReadOnlyList<string> photoPaths, CompositionContext context, DesignContext designContext)
    {
        var design = designContext.Design;
        var bookmark = DesignStyles.IsBookmark(layout.DesignStyle);
        var backgroundPath = designContext.BackgroundPathFor(layout.DesignStyle);
        var custom = backgroundPath != null;
        var decor = custom ? DecorStyle.None : design.Decor;
        using var logo = LoadImage(designContext.LogoPath);
        var areas = DesignLayout.Compute(layout, design, decor, logo != null);

        // Les textes sont placés en premier : les décors s'organisent autour (ornements, confettis).
        var texts = !custom || design.TextsOverBackground
            ? TextBlock.Build(design, decor, areas, context, bookmark)
            : TextBlock.Empty;

        canvas.Clear(ColorUtil.Parse(design.BackgroundColor, SKColors.White));
        if (custom)
        {
            using var background = LayoutCompositor.LoadOriented(backgroundPath!, layout.Width, layout.Height, 0);
            if (background != null) LayoutCompositor.DrawCover(canvas, background, areas.Canvas, 0, false, false);
        }
        else
        {
            Decorations.DrawBackground(canvas, decor, design, areas, texts, bookmark);
        }

        var blackAndWhite = design.BlackAndWhite || layout.BlackAndWhite;
        for (var i = 0; i < layout.Zones.Count; i++)
        {
            DrawPhoto(canvas, i < photoPaths.Count ? photoPaths[i] : null, layout.Zones[i], design, blackAndWhite, context);
        }

        if (!custom) Decorations.DrawForeground(canvas, decor, design, areas, texts, bookmark);
        texts.Draw(canvas, design, decor);
        if (logo != null && areas.LogoArea is { } logoArea) DrawContained(canvas, logo, logoArea);
    }

    private static void DrawPhoto(SKCanvas canvas, string? path, PhotoZone zone, EventDesign design, bool blackAndWhite, CompositionContext context)
    {
        var rect = SKRect.Create(zone.X, zone.Y, zone.Width, zone.Height);
        var radius = Math.Min(design.PhotoCornerRadius, Math.Min(rect.Width, rect.Height) / 2f);
        var border = design.PhotoBorder;
        var outer = DesignLayout.Inflate(rect, border);
        var outerRadius = radius > 0 ? radius + border : 0;

        canvas.Save();
        if (Math.Abs(zone.Rotation) > 0.01f) canvas.RotateDegrees(zone.Rotation, rect.MidX, rect.MidY);

        if (design.PhotoShadow)
        {
            using var shadow = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(0, 0, 0, 85),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12),
            };
            var shadowRect = outer;
            shadowRect.Offset(0, 10);
            canvas.DrawRoundRect(shadowRect, outerRadius, outerRadius, shadow);
        }
        if (border > 0)
        {
            using var paint = new SKPaint { IsAntialias = true, Color = ColorUtil.Parse(design.PhotoBorderColor, SKColors.White) };
            canvas.DrawRoundRect(outer, outerRadius, outerRadius, paint);
        }

        using var photo = path != null ? LayoutCompositor.LoadOriented(path, zone.Width, zone.Height, context.PhotoRotation) : null;
        if (photo != null)
        {
            canvas.Save();
            if (radius > 0) canvas.ClipRoundRect(new SKRoundRect(rect, radius), SKClipOperation.Intersect, antialias: true);
            LayoutCompositor.DrawCover(canvas, photo, rect, 0, blackAndWhite, context.MirrorPhotos);
            canvas.Restore();
        }
        canvas.Restore();
    }

    private static SKBitmap? LoadImage(string? path)
    {
        if (path == null) return null;
        try
        {
            return SKBitmap.Decode(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Image entière dans la zone, sans déformation, centrée.</summary>
    private static void DrawContained(SKCanvas canvas, SKBitmap bitmap, SKRect area)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0) return;
        var scale = Math.Min(area.Width / bitmap.Width, area.Height / bitmap.Height);
        var width = bitmap.Width * scale;
        var height = bitmap.Height * scale;
        using var image = SKImage.FromBitmap(bitmap);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(image, SKRect.Create(area.MidX - width / 2, area.MidY - height / 2, width, height), HighQuality, paint);
    }
}
