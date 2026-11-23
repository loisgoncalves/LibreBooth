using SkiaSharp;

namespace LibreBooth.Core.Layouts;

/// <summary>Images de démonstration (fausse caméra, aperçus des modèles) : dégradés et silhouettes stylisées.</summary>
public static class DemoImageFactory
{
    private static readonly SKColor[] Palette =
    {
        new(0xFF, 0x6B, 0x6B), new(0x4E, 0xCD, 0xC4), new(0xFF, 0xD9, 0x3D), new(0x6C, 0x5C, 0xE7),
        new(0x00, 0xB8, 0x94), new(0xFD, 0x79, 0xA8), new(0x09, 0x84, 0xE3), new(0xE1, 0x70, 0x55),
    };

    public static byte[] RenderJpeg(int width, int height, int index, string? label, int quality = 88, double phase = 0)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
                            ?? throw new InvalidOperationException("Mémoire insuffisante.");
        Draw(surface.Canvas, width, height, index, label, phase);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    /// <summary>Écrit des photos de démonstration dans un dossier et renvoie leurs chemins.</summary>
    public static IReadOnlyList<string> WriteSamplePhotos(string folder, int count, int width = 1500, int height = 1000)
    {
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(folder, $"exemple-{i + 1}.jpg");
            Util.AtomicFile.WriteAllBytes(path, RenderJpeg(width, height, i, $"Photo {i + 1}"));
            paths.Add(path);
        }
        return paths;
    }

    public static void Draw(SKCanvas canvas, int width, int height, int index, string? label, double phase)
    {
        var first = Palette[Math.Abs(index) % Palette.Length];
        var second = Palette[(Math.Abs(index) + 3) % Palette.Length];
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), new[] { first, second }, SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(0, 0, width, height, paint);
        }

        var random = new Random(1000 + index);
        using (var bubble = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(46) })
        {
            for (var i = 0; i < 7; i++)
            {
                var radius = (float)(height * (0.05 + random.NextDouble() * 0.15));
                var x = (float)(width * ((random.NextDouble() + phase * (0.03 + i * 0.01)) % 1.0));
                var y = (float)(height * ((random.NextDouble() + phase * 0.02) % 1.0));
                canvas.DrawCircle(x, y, radius, bubble);
            }
        }

        using (var person = new SKPaint { IsAntialias = true, Color = new SKColor(20, 20, 30, 150) })
        {
            var count = 2 + Math.Abs(index) % 3;
            for (var i = 0; i < count; i++)
            {
                var centerX = width * (i + 1f) / (count + 1f) + (float)(Math.Sin(phase * 2 + i) * width * 0.01);
                var headRadius = height * 0.085f;
                var headY = height * 0.45f;
                canvas.DrawCircle(centerX, headY, headRadius, person);
                var body = new SKRoundRect(SKRect.Create(centerX - headRadius * 1.6f, headY + headRadius * 1.25f, headRadius * 3.2f, height), headRadius * 1.2f);
                canvas.DrawRoundRect(body, person);
            }
        }

        if (!string.IsNullOrEmpty(label))
        {
            using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), height * 0.075f);
            using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 110) };
            using var text = new SKPaint { IsAntialias = true, Color = SKColors.White };
            canvas.DrawText(label, width / 2f + 3, height * 0.16f + 3, SKTextAlign.Center, font, shadow);
            canvas.DrawText(label, width / 2f, height * 0.16f, SKTextAlign.Center, font, text);
        }
    }
}
