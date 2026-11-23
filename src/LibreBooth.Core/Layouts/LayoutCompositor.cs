using LibreBooth.Core.Design;
using LibreBooth.Core.Util;
using SkiaSharp;

namespace LibreBooth.Core.Layouts;

/// <summary>Informations utilisées pendant le montage.</summary>
/// <param name="PhotoRotation">Rotation forcée des photos (0 = suivre l'orientation enregistrée par l'appareil).</param>
/// <param name="Design">Personnalisation de l'événement, utilisée par les modèles personnalisables (thème par défaut sinon).</param>
public sealed record CompositionContext(string EventName, DateTime EventDate, bool MirrorPhotos = false, int PhotoRotation = 0, DesignContext? Design = null);

/// <summary>
/// Assemble les photos dans un modèle : fond, photos recadrées pour remplir leur zone, cadre PNG par-dessus, textes.
/// Les photos sont décodées directement à la taille utile, pour ménager la mémoire d'un programme 32 bits.
/// </summary>
public sealed class LayoutCompositor
{
    private static readonly SKSamplingOptions HighQuality = new(SKCubicResampler.Mitchell);
    private static readonly SKSamplingOptions Downscale = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private static readonly float[] GrayscaleMatrix =
    {
        0.2126f, 0.7152f, 0.0722f, 0, 0,
        0.2126f, 0.7152f, 0.0722f, 0, 0,
        0.2126f, 0.7152f, 0.0722f, 0, 0,
        0, 0, 0, 1, 0,
    };

    public SKImage Compose(Template template, IReadOnlyList<string> photoPaths, CompositionContext context)
    {
        var layout = template.Layout;
        var single = ComposeSingle(template, photoPaths, context);
        if (!layout.DuplicateStrip) return single;

        using (single)
        {
            var info = new SKImageInfo(layout.Width * 2, layout.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Mémoire insuffisante pour le montage.");
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            canvas.DrawImage(single, SKRect.Create(0, 0, layout.Width, layout.Height), HighQuality);
            canvas.DrawImage(single, SKRect.Create(layout.Width, 0, layout.Width, layout.Height), HighQuality);
            canvas.Flush();
            return surface.Snapshot();
        }
    }

    public void ComposeToFile(Template template, IReadOnlyList<string> photoPaths, CompositionContext context, string outputPath, int quality = 95)
    {
        using var image = Compose(template, photoPaths, context);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality)
                         ?? throw new InvalidOperationException("Impossible d'encoder le montage en JPEG.");
        AtomicFile.WriteAllBytes(outputPath, data.ToArray());
    }

    /// <summary>Écrit une version réduite d'une image (aperçus, galerie).</summary>
    public static void WriteScaled(SKImage image, string outputPath, int maxSide, int quality = 88) =>
        AtomicFile.WriteAllBytes(outputPath, EncodeScaled(image, maxSide, quality));

    /// <summary>Version réduite d'une image, encodée en JPEG.</summary>
    public static byte[] EncodeScaled(SKImage image, int maxSide, int quality = 88)
    {
        var scale = Math.Min(1f, (float)maxSide / Math.Max(image.Width, image.Height));
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
                            ?? throw new InvalidOperationException("Mémoire insuffisante.");
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawImage(image, SKRect.Create(0, 0, width, height), Downscale);
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    private static SKImage ComposeSingle(Template template, IReadOnlyList<string> photoPaths, CompositionContext context)
    {
        var layout = template.Layout;
        var info = new SKImageInfo(layout.Width, layout.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Mémoire insuffisante pour le montage.");
        var canvas = surface.Canvas;

        if (layout.IsCustomizable)
        {
            DesignRenderer.Render(canvas, layout, photoPaths, context, context.Design ?? DesignContext.CreateDefault());
            canvas.Flush();
            return surface.Snapshot();
        }

        canvas.Clear(ParseColor(layout.BackgroundColor, SKColors.White));

        if (template.BackgroundPath is { } backgroundPath)
        {
            using var background = LoadOriented(backgroundPath, layout.Width, layout.Height, 0);
            if (background != null)
            {
                DrawCover(canvas, background, SKRect.Create(0, 0, layout.Width, layout.Height), 0, false, false);
            }
        }

        for (var i = 0; i < layout.Zones.Count && i < photoPaths.Count; i++)
        {
            var zone = layout.Zones[i];
            using var photo = LoadOriented(photoPaths[i], zone.Width, zone.Height, context.PhotoRotation);
            if (photo == null) continue;
            DrawCover(canvas, photo, SKRect.Create(zone.X, zone.Y, zone.Width, zone.Height), zone.Rotation, layout.BlackAndWhite, context.MirrorPhotos);
        }

        if (template.FramePath is { } framePath)
        {
            using var frame = SKBitmap.Decode(framePath);
            if (frame != null)
            {
                using var frameImage = SKImage.FromBitmap(frame);
                canvas.DrawImage(frameImage, SKRect.Create(0, 0, layout.Width, layout.Height), HighQuality);
            }
        }

        foreach (var text in layout.Texts)
        {
            DrawText(canvas, text, layout, context);
        }

        canvas.Flush();
        return surface.Snapshot();
    }

    /// <summary>
    /// Décode une photo à la taille utile et la remet à l'endroit (orientation EXIF, ou rotation forcée 90/180/270).
    /// </summary>
    public static SKBitmap? LoadOriented(string path, int targetWidth, int targetHeight, int forcedRotation)
    {
        if (!File.Exists(path)) return null;
        using var codec = SKCodec.Create(path);
        if (codec == null) return null;

        var origin = forcedRotation switch
        {
            90 => SKEncodedOrigin.RightTop,
            180 => SKEncodedOrigin.BottomRight,
            270 => SKEncodedOrigin.LeftBottom,
            _ => codec.EncodedOrigin,
        };

        var fullSize = codec.Info.Size;
        var swap = SwapsAxes(origin);
        var orientedWidth = swap ? fullSize.Height : fullSize.Width;
        var orientedHeight = swap ? fullSize.Width : fullSize.Height;
        var needed = Math.Max((float)Math.Max(1, targetWidth) / orientedWidth, (float)Math.Max(1, targetHeight) / orientedHeight);

        // Le décodeur JPEG sait réduire par 1/8, 2/8 … 8/8 : on prend la plus petite échelle qui couvre la zone.
        var scaled = fullSize;
        if (needed < 1f)
        {
            for (var eighths = 1; eighths <= 8; eighths++)
            {
                var candidate = codec.GetScaledDimensions(eighths / 8f);
                var candidateWidth = swap ? candidate.Height : candidate.Width;
                var candidateHeight = swap ? candidate.Width : candidate.Height;
                if (candidateWidth >= targetWidth && candidateHeight >= targetHeight)
                {
                    scaled = candidate;
                    break;
                }
            }
        }

        var info = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        SKBitmap? bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            bitmap = SKBitmap.Decode(path);
            if (bitmap == null) return null;
        }

        return ApplyOrigin(bitmap, origin);
    }

    private static bool SwapsAxes(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    private static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return source;

        var swap = SwapsAxes(origin);
        var width = swap ? source.Height : source.Width;
        var height = swap ? source.Width : source.Height;
        var result = new SKBitmap(new SKImageInfo(width, height, source.ColorType, source.AlphaType));
        using (var canvas = new SKCanvas(result))
        {
            switch (origin)
            {
                case SKEncodedOrigin.TopRight:
                    canvas.Translate(width, 0);
                    canvas.Scale(-1, 1);
                    break;
                case SKEncodedOrigin.BottomRight:
                    canvas.Translate(width, height);
                    canvas.RotateDegrees(180);
                    break;
                case SKEncodedOrigin.BottomLeft:
                    canvas.Translate(0, height);
                    canvas.Scale(1, -1);
                    break;
                case SKEncodedOrigin.LeftTop:
                    canvas.Scale(-1, 1);
                    canvas.RotateDegrees(90);
                    break;
                case SKEncodedOrigin.RightTop:
                    canvas.Translate(width, 0);
                    canvas.RotateDegrees(90);
                    break;
                case SKEncodedOrigin.RightBottom:
                    canvas.Translate(width, height);
                    canvas.Scale(1, -1);
                    canvas.RotateDegrees(90);
                    break;
                case SKEncodedOrigin.LeftBottom:
                    canvas.Translate(0, height);
                    canvas.RotateDegrees(-90);
                    break;
            }
            canvas.DrawBitmap(source, 0, 0);
        }
        source.Dispose();
        return result;
    }

    internal static void DrawCover(SKCanvas canvas, SKBitmap bitmap, SKRect dest, float rotation, bool blackAndWhite, bool mirror)
    {
        var scale = Math.Max(dest.Width / bitmap.Width, dest.Height / bitmap.Height);
        var sourceWidth = dest.Width / scale;
        var sourceHeight = dest.Height / scale;
        var source = SKRect.Create((bitmap.Width - sourceWidth) / 2f, (bitmap.Height - sourceHeight) / 2f, sourceWidth, sourceHeight);

        using var image = SKImage.FromBitmap(bitmap);
        using var paint = new SKPaint { IsAntialias = true };
        if (blackAndWhite) paint.ColorFilter = SKColorFilter.CreateColorMatrix(GrayscaleMatrix);

        canvas.Save();
        if (Math.Abs(rotation) > 0.01f) canvas.RotateDegrees(rotation, dest.MidX, dest.MidY);
        if (mirror) canvas.Scale(-1, 1, dest.MidX, dest.MidY);
        canvas.DrawImage(image, source, dest, HighQuality, paint);
        canvas.Restore();
    }

    private static void DrawText(SKCanvas canvas, TextField field, LayoutDefinition layout, CompositionContext context)
    {
        var text = TextUtil.ApplyVariables(field.Content, context.EventName, context.EventDate);
        if (string.IsNullOrWhiteSpace(text)) return;

        var style = new SKFontStyle(
            field.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            field.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var typeface = SKTypeface.FromFamilyName(string.IsNullOrWhiteSpace(field.Font) ? "Segoe UI" : field.Font, style);
        using var font = new SKFont(typeface, Math.Max(4, field.Size));

        var maxWidth = field.MaxWidth > 0 ? field.MaxWidth : Math.Max(50, layout.Width - 80);
        var measured = font.MeasureText(text);
        if (measured > maxWidth && measured > 0) font.Size = font.Size * maxWidth / measured;

        using var paint = new SKPaint { Color = ParseColor(field.Color, SKColors.Black), IsAntialias = true };
        var align = field.Align switch
        {
            TextAlign.Left => SKTextAlign.Left,
            TextAlign.Right => SKTextAlign.Right,
            _ => SKTextAlign.Center,
        };
        canvas.DrawText(text, field.X, field.Y, align, font, paint);
    }

    internal static SKColor ParseColor(string? value, SKColor fallback) =>
        !string.IsNullOrWhiteSpace(value) && SKColor.TryParse(value.Trim(), out var color) ? color : fallback;
}
