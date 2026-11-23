using LibreBooth.Core.Design;
using LibreBooth.Core.Util;
using Serilog;

namespace LibreBooth.Core.Layouts;

/// <summary>
/// Crée les deux modèles personnalisables de LibreBooth : le 10x15 une photo et le marque-page 3 photos.
/// Leur apparence (thème, couleurs, textes, fond) se règle pour chaque événement.
/// </summary>
public static class TemplateSeeder
{
    public const string PostcardId = "10x15-une-photo";
    public const string BookmarkId = "marque-page-3-photos";
    public static IReadOnlyList<string> DefaultTemplateIds { get; } = new[] { PostcardId, BookmarkId };

    /// <summary>Modèles de la première version, remplacés par les deux modèles personnalisables.</summary>
    private static readonly string[] LegacyIds = { "10x15-1-photo", "10x15-4-photos", "bandelette-3-photos" };
    private const string LegacyMarkerFile = ".modeles-initialises";
    private const string MarkerFile = ".modeles-v2";

    /// <summary>
    /// Ne fait rien si c'est déjà fait (on respecte les modèles que tu as supprimés). Lors de la mise à jour,
    /// les trois anciens modèles d'essai partent dans Modeles\_corbeille ; tes propres modèles ne bougent pas.
    /// </summary>
    public static bool SeedIfNeeded(TemplateLibrary library)
    {
        var marker = Path.Combine(library.Root, MarkerFile);
        if (File.Exists(marker)) return false;

        foreach (var id in LegacyIds)
        {
            var legacy = library.Find(id);
            if (legacy == null) continue;
            library.Delete(legacy);
            Log.ForContext(typeof(TemplateSeeder)).Information("Ancien modèle d'essai remplacé : {Id}", id);
        }

        if (library.Find(PostcardId) == null) WriteLayout(library.Root, PostcardId, CreatePostcard());
        if (library.Find(BookmarkId) == null) WriteLayout(library.Root, BookmarkId, CreateBookmark());
        File.WriteAllText(marker, DateTime.Now.ToString("O"));
        AtomicFile.TryDelete(Path.Combine(library.Root, LegacyMarkerFile));
        return true;
    }

    /// <summary>10x15 paysage (1800 × 1200 px à 300 dpi) : une grande photo, textes et logo dans la bande du bas.</summary>
    public static LayoutDefinition CreatePostcard() => new()
    {
        Name = "10x15 – une photo",
        Width = 1800,
        Height = 1200,
        FrameFile = null,
        DesignStyle = DesignStyles.Postcard,
        PrintProfile = PrintProfiles.Postcard,
        Zones = { new PhotoZone(66, 66, 1668, 920) },
    };

    /// <summary>
    /// Marque-page 5x15 (600 × 1800 px) : titre en haut, trois photos, date et phrase en bas.
    /// Il est imprimé deux fois côte à côte sur un 10x15 que la DNP coupe au milieu (coupe 2 pouces).
    /// </summary>
    public static LayoutDefinition CreateBookmark() => new()
    {
        Name = "Marque-page – 3 photos",
        Width = 600,
        Height = 1800,
        FrameFile = null,
        DesignStyle = DesignStyles.Bookmark,
        DuplicateStrip = true,
        PrintProfile = PrintProfiles.Strips,
        Zones =
        {
            new PhotoZone(48, 360, 504, 336),
            new PhotoZone(48, 722, 504, 336),
            new PhotoZone(48, 1084, 504, 336),
        },
    };

    private static void WriteLayout(string root, string id, LayoutDefinition layout)
    {
        var folder = Path.Combine(root, id);
        Directory.CreateDirectory(folder);
        JsonDefaults.Write(Path.Combine(folder, TemplateLibrary.LayoutFileName), layout);
    }
}
