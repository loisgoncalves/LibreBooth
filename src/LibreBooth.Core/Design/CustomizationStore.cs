using LibreBooth.Core.Events;
using LibreBooth.Core.Layouts;
using LibreBooth.Core.Util;
using Serilog;
using SkiaSharp;

namespace LibreBooth.Core.Design;

/// <summary>Résultat de l'import d'un fond : les proportions de l'image d'origine ont-elles obligé à la recadrer ?</summary>
public sealed record BackgroundImport(string FileName, int SourceWidth, int SourceHeight, bool Cropped);

/// <summary>
/// Personnalisation d'un événement, rangée dans son dossier caché :
/// Evenements\&lt;événement&gt;\.librebooth\personnalisation\ (personnalisation.json, fonds, logo, aperçus).
/// </summary>
public static class CustomizationStore
{
    public const string FolderName = "personnalisation";
    public const string FileName = "personnalisation.json";
    public const string LogoFileName = "logo.png";
    /// <summary>Taille des écrans du photobooth : l'image d'accueil y est mise exactement (recadrée au centre).</summary>
    public const int ScreenWidth = 1920;
    public const int ScreenHeight = 1080;
    private const string AttractFilePrefix = "accueil-";
    private const string PreviewsFolderName = "apercus";
    private const int MaxLogoSide = 1200;

    private static readonly ILogger Logger = Log.ForContext(typeof(CustomizationStore));

    public static string FolderFor(string eventFolder) => Path.Combine(BoothEvent.DataFolderFor(eventFolder), FolderName);

    public static string PreviewsFolderFor(string eventFolder) => Path.Combine(FolderFor(eventFolder), PreviewsFolderName);

    /// <summary>Lit la personnalisation ; sans fichier (événement neuf ou ancien), renvoie les réglages par défaut.</summary>
    public static EventCustomization Load(string eventFolder)
    {
        var file = Path.Combine(FolderFor(eventFolder), FileName);
        try
        {
            if (File.Exists(file))
            {
                var loaded = JsonDefaults.Read<EventCustomization>(file);
                if (loaded != null) return loaded.Normalize();
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Personnalisation illisible, réglages par défaut utilisés : {File}", file);
        }
        return new EventCustomization { Templates = TemplateSeeder.DefaultTemplateIds.ToList(), IsNew = true }.Normalize();
    }

    public static void Save(string eventFolder, EventCustomization customization)
    {
        customization.Normalize();
        JsonDefaults.Write(Path.Combine(FolderFor(eventFolder), FileName), customization);
        customization.IsNew = false;
    }

    /// <summary>
    /// Copie le fond choisi par l'opérateur, mis exactement à la taille du modèle (recadré au centre si ses
    /// proportions diffèrent). La transparence d'un PNG est conservée.
    /// </summary>
    public static BackgroundImport ImportBackground(string eventFolder, EventCustomization customization, string style, string sourcePath, int width, int height)
    {
        using var source = LayoutCompositor.LoadOriented(sourcePath, width, height, 0)
                           ?? throw new InvalidDataException("Cette image n'est pas lisible (PNG ou JPG attendu).");
        var (sourceWidth, sourceHeight) = ReadSize(sourcePath);
        var cropped = Math.Abs((double)sourceWidth / Math.Max(1, sourceHeight) - (double)width / height) > 0.02;

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Mémoire insuffisante.");
        surface.Canvas.Clear(SKColors.Transparent);
        LayoutCompositor.DrawCover(surface.Canvas, source, SKRect.Create(0, 0, width, height), 0, false, false);
        surface.Canvas.Flush();

        var fileName = $"fond-{TextUtil.Slug(style)}.png";
        using (var image = surface.Snapshot())
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        {
            AtomicFile.WriteAllBytes(Path.Combine(FolderFor(eventFolder), fileName), data.ToArray());
        }
        customization.Design.Backgrounds[style] = fileName;
        // Importer un fond, c'est vouloir l'utiliser : mode « Mes propres images ».
        customization.Design.OwnImages = true;
        Save(eventFolder, customization);
        Logger.Information("Fond personnalisé importé pour {Style} : {Source} ({Width}×{Height}{Cropped})",
            style, Path.GetFileName(sourcePath), sourceWidth, sourceHeight, cropped ? ", recadré" : "");
        return new BackgroundImport(fileName, sourceWidth, sourceHeight, cropped);
    }

    public static void RemoveBackground(string eventFolder, EventCustomization customization, string style)
    {
        if (customization.Design.Backgrounds.Remove(style, out var file))
        {
            AtomicFile.TryDelete(Path.Combine(FolderFor(eventFolder), file));
        }
        Save(eventFolder, customization);
    }

    /// <summary>Copie le logo (réduit à 1200 px au plus, transparence conservée).</summary>
    public static void ImportLogo(string eventFolder, EventCustomization customization, string sourcePath)
    {
        using var source = SKBitmap.Decode(sourcePath) ?? throw new InvalidDataException("Ce logo n'est pas lisible (PNG ou JPG attendu).");
        var scale = Math.Min(1f, (float)MaxLogoSide / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
                            ?? throw new InvalidOperationException("Mémoire insuffisante.");
        surface.Canvas.Clear(SKColors.Transparent);
        using (var image = SKImage.FromBitmap(source))
        {
            surface.Canvas.DrawImage(image, SKRect.Create(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        surface.Canvas.Flush();
        using (var result = surface.Snapshot())
        using (var data = result.Encode(SKEncodedImageFormat.Png, 100))
        {
            AtomicFile.WriteAllBytes(Path.Combine(FolderFor(eventFolder), LogoFileName), data.ToArray());
        }
        customization.Design.LogoFile = LogoFileName;
        Save(eventFolder, customization);
        Logger.Information("Logo importé : {Source} ({Width}×{Height})", Path.GetFileName(sourcePath), source.Width, source.Height);
    }

    public static void RemoveLogo(string eventFolder, EventCustomization customization)
    {
        if (!string.IsNullOrEmpty(customization.Design.LogoFile))
        {
            AtomicFile.TryDelete(Path.Combine(FolderFor(eventFolder), customization.Design.LogoFile));
        }
        customization.Design.LogoFile = null;
        Save(eventFolder, customization);
    }

    /// <summary>
    /// Copie l'image de l'écran d'accueil, mise à la taille de l'écran (1920 × 1080, recadrée au centre si ses
    /// proportions diffèrent). Chaque import a son propre nom de fichier : l'écran affiche bien la nouvelle image.
    /// </summary>
    public static BackgroundImport ImportAttractBackground(string eventFolder, EventCustomization customization, string sourcePath)
    {
        var (sourceWidth, sourceHeight) = ReadSize(sourcePath);
        using var source = LayoutCompositor.LoadOriented(sourcePath, ScreenWidth, ScreenHeight, 0);
        if (source == null || sourceWidth <= 0 || sourceHeight <= 0) throw new InvalidDataException("Cette image n'est pas lisible (PNG ou JPG attendu).");
        var cropped = Math.Abs((double)sourceWidth / sourceHeight - (double)ScreenWidth / ScreenHeight) > 0.02;

        using var surface = SKSurface.Create(new SKImageInfo(ScreenWidth, ScreenHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
                            ?? throw new InvalidOperationException("Mémoire insuffisante.");
        surface.Canvas.Clear(SKColors.Black);
        LayoutCompositor.DrawCover(surface.Canvas, source, SKRect.Create(0, 0, ScreenWidth, ScreenHeight), 0, false, false);
        surface.Canvas.Flush();

        var folder = FolderFor(eventFolder);
        Directory.CreateDirectory(folder);
        var fileName = $"{AttractFilePrefix}{DateTime.Now:yyyyMMdd-HHmmss}.jpg";
        using (var image = surface.Snapshot())
        using (var data = image.Encode(SKEncodedImageFormat.Jpeg, 90))
        {
            AtomicFile.WriteAllBytes(Path.Combine(folder, fileName), data.ToArray());
        }
        DeleteOtherAttractFiles(folder, fileName);
        customization.AttractBackground = fileName;
        Save(eventFolder, customization);
        Logger.Information("Image d'accueil importée : {Source} ({Width}×{Height}{Cropped})",
            Path.GetFileName(sourcePath), sourceWidth, sourceHeight, cropped ? ", recadrée" : "");
        return new BackgroundImport(fileName, sourceWidth, sourceHeight, cropped);
    }

    public static void RemoveAttractBackground(string eventFolder, EventCustomization customization)
    {
        customization.AttractBackground = null;
        DeleteOtherAttractFiles(FolderFor(eventFolder), keep: null);
        Save(eventFolder, customization);
    }

    /// <summary>Chemin de l'image d'accueil de l'événement, ou null s'il n'en a pas (ou si le fichier a disparu).</summary>
    public static string? AttractBackgroundPath(string eventFolder, EventCustomization customization)
    {
        if (string.IsNullOrWhiteSpace(customization.AttractBackground)) return null;
        var path = Path.Combine(FolderFor(eventFolder), customization.AttractBackground);
        return File.Exists(path) ? path : null;
    }

    private static void DeleteOtherAttractFiles(string folder, string? keep)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var old in Directory.GetFiles(folder, AttractFilePrefix + "*"))
        {
            if (!string.Equals(Path.GetFileName(old), keep, StringComparison.OrdinalIgnoreCase)) AtomicFile.TryDelete(old);
        }
    }

    /// <summary>Reprend toute la personnalisation d'un autre événement (réglages, fonds, logo et image d'accueil).</summary>
    public static EventCustomization CopyFrom(string sourceEventFolder, string targetEventFolder)
    {
        var source = Load(sourceEventFolder);
        var sourceFolder = FolderFor(sourceEventFolder);
        var targetFolder = FolderFor(targetEventFolder);
        Directory.CreateDirectory(targetFolder);
        DeleteOtherAttractFiles(targetFolder, source.AttractBackground);

        var files = source.Design.Backgrounds.Values.Append(source.Design.LogoFile).Append(source.AttractBackground)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var from = Path.Combine(sourceFolder, file);
            if (File.Exists(from)) File.Copy(from, Path.Combine(targetFolder, file), overwrite: true);
        }

        var copy = JsonDefaults.Clone(source);
        Save(targetEventFolder, copy);
        Logger.Information("Personnalisation copiée de {Source} vers {Target}", sourceEventFolder, targetEventFolder);
        return copy;
    }

    /// <summary>Taille de l'image telle qu'on la voit (orientation EXIF des photos d'appareil comprise).</summary>
    private static (int Width, int Height) ReadSize(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec == null) return (0, 0);
        var swap = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        return swap ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);
    }
}
