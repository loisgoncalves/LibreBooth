using SkiaSharp;

namespace LibreBooth.Core.Tests;

internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "librebooth-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Sub(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Dossier temporaire : sans importance.
        }
    }
}

internal static class Images
{
    public static string WriteSolidJpeg(string path, int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(color);
        return WriteJpeg(bitmap, path);
    }

    /// <summary>Moitié gauche d'une couleur, moitié droite d'une autre.</summary>
    public static string WriteSplitJpeg(string path, int width, int height, SKColor left, SKColor right)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            using var leftPaint = new SKPaint { Color = left };
            using var rightPaint = new SKPaint { Color = right };
            canvas.DrawRect(0, 0, width / 2f, height, leftPaint);
            canvas.DrawRect(width / 2f, 0, width / 2f, height, rightPaint);
        }
        return WriteJpeg(bitmap, path);
    }

    /// <summary>PNG opaque avec des trous transparents.</summary>
    public static string WriteFramePng(string path, int width, int height, SKColor color, params SKRect[] holes)
    {
        using var bitmap = CreateFrame(width, height, color, holes);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    public static SKBitmap CreateFrame(int width, int height, SKColor color, params SKRect[] holes)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        using var clear = new SKPaint { BlendMode = SKBlendMode.Clear };
        foreach (var hole in holes) canvas.DrawRect(hole, clear);
        return bitmap;
    }

    public static void AssertColorNear(SKColor expected, SKColor actual, int tolerance = 40)
    {
        Assert.True(
            Math.Abs(expected.Red - actual.Red) <= tolerance &&
            Math.Abs(expected.Green - actual.Green) <= tolerance &&
            Math.Abs(expected.Blue - actual.Blue) <= tolerance,
            $"Couleur attendue {expected}, obtenue {actual}");
    }

    private static string WriteJpeg(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }
}
