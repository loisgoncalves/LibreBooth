using System.Security.Cryptography;
using System.Text;
using LibreBooth.Core.Design;
using LibreBooth.Core.Util;
using SkiaSharp;

namespace LibreBooth.Core.Layouts;

/// <summary>Événement pour lequel on prépare l'aperçu d'un modèle personnalisable (son nom, sa date, son thème).</summary>
/// <param name="CacheFolder">Dossier où ranger les aperçus (dans la personnalisation de l'événement).</param>
public sealed record PreviewContext(string EventName, DateTime EventDate, DesignContext Design, string CacheFolder);

/// <summary>Aperçus des modèles, montés avec des photos de démonstration.</summary>
public static class TemplatePreview
{
    public const string SampleEventName = "Marie & Paul";

    private static readonly object SampleLock = new();
    private static string? _sampleFolder;

    /// <summary>
    /// Renvoie le chemin d'un aperçu à jour, en le créant si besoin. Un modèle personnalisable a un aperçu par
    /// personnalisation d'événement (le nom du fichier change dès qu'un réglage, le nom ou la date changent).
    /// </summary>
    public static string GetOrCreate(LayoutCompositor compositor, Template template, PreviewContext? context = null, int maxSide = 900)
    {
        if (template.Layout.IsCustomizable && context != null) return GetOrCreateForEvent(compositor, template, context, maxSide);

        // Aperçu d'une version précédente (« apercu.jpg ») : il peut avoir été fait avec d'autres photos d'exemple.
        AtomicFile.TryDelete(template.LegacyPreviewPath);
        var preview = template.PreviewPath;
        var newest = new[] { template.LayoutFile, template.FramePath, template.BackgroundPath }
            .Where(p => p != null && File.Exists(p))
            .Select(p => File.GetLastWriteTimeUtc(p!))
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        if (File.Exists(preview) && File.GetLastWriteTimeUtc(preview) >= newest) return preview;

        using var image = ComposeSample(compositor, template);
        LayoutCompositor.WriteScaled(image, preview, maxSide);
        return preview;
    }

    private static string GetOrCreateForEvent(LayoutCompositor compositor, Template template, PreviewContext context, int maxSide)
    {
        var key = CacheKey(template, context, maxSide);
        var cacheFolder = context.CacheFolder;
        // Événement renommé pendant la préparation (son dossier a changé de nom) : on ne recrée pas l'ancien dossier.
        var eventData = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(cacheFolder).TrimEnd(Path.DirectorySeparatorChar)));
        if (!Directory.Exists(cacheFolder) && eventData != null && !Directory.Exists(eventData))
        {
            cacheFolder = Path.Combine(Path.GetTempPath(), "LibreBooth-apercus");
        }
        var preview = Path.Combine(cacheFolder, $"{template.Id}-{key}.jpg");
        if (File.Exists(preview)) return preview;

        using (var image = ComposeSample(compositor, template, context.EventName, context.Design, context.EventDate))
        {
            LayoutCompositor.WriteScaled(image, preview, maxSide);
        }

        // Les anciens aperçus de ce modèle ne servent plus.
        foreach (var old in Directory.GetFiles(cacheFolder, template.Id + "-*.jpg"))
        {
            if (!string.Equals(old, preview, StringComparison.OrdinalIgnoreCase)) AtomicFile.TryDelete(old);
        }
        return preview;
    }

    private static string CacheKey(Template template, PreviewContext context, int maxSide)
    {
        var text = new StringBuilder()
            .Append(System.Text.Json.JsonSerializer.Serialize(template.Layout, JsonDefaults.Options))
            .Append(System.Text.Json.JsonSerializer.Serialize(context.Design.Design, JsonDefaults.Options))
            .Append(context.EventName).Append('|').Append(context.EventDate.ToString("yyyyMMdd")).Append('|').Append(maxSide);
        // « exemples-dessines » : les aperçus faits en 1.3.0 avec d'autres photos d'exemple sont refaits.
        text.Append("|exemples-dessines");
        foreach (var file in new[] { context.Design.BackgroundPathFor(template.Layout.DesignStyle), context.Design.LogoPath })
        {
            if (file == null) continue;
            var info = new FileInfo(file);
            text.Append('|').Append(info.Length).Append('-').Append(info.LastWriteTimeUtc.Ticks);
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>Montage complet (pleine résolution) avec des photos de démonstration : aperçu, page de test.</summary>
    public static SKImage ComposeSample(LayoutCompositor compositor, Template template, string? eventName = null, DesignContext? design = null, DateTime? eventDate = null)
    {
        var photos = SamplePhotos(Math.Max(1, template.ShotCount));
        return compositor.Compose(template, photos, new CompositionContext(eventName ?? SampleEventName, eventDate ?? DateTime.Today, Design: design));
    }

    /// <summary>Aperçu réduit en mémoire (JPEG), pour l'écran de personnalisation.</summary>
    public static byte[] RenderJpeg(LayoutCompositor compositor, Template template, string eventName, DateTime eventDate, DesignContext? design, int maxSide, int quality = 90)
    {
        using var image = ComposeSample(compositor, template, eventName, design, eventDate);
        return LayoutCompositor.EncodeScaled(image, maxSide, quality);
    }

    public static void WriteSample(LayoutCompositor compositor, Template template, string outputPath, string? eventName = null, DesignContext? design = null, DateTime? eventDate = null)
    {
        using var image = ComposeSample(compositor, template, eventName, design, eventDate);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        AtomicFile.WriteAllBytes(outputPath, data.ToArray());
    }

    /// <summary>Photos de démonstration, créées une fois dans le dossier temporaire puis réutilisées.</summary>
    public static IReadOnlyList<string> SamplePhotos(int count)
    {
        lock (SampleLock)
        {
            _sampleFolder ??= Path.Combine(Path.GetTempPath(), "librebooth-exemples");
            var paths = Enumerable.Range(1, count).Select(i => Path.Combine(_sampleFolder, $"exemple-{i}.jpg")).ToList();
            if (paths.All(File.Exists)) return paths;
            return DemoImageFactory.WriteSamplePhotos(_sampleFolder, count);
        }
    }
}
