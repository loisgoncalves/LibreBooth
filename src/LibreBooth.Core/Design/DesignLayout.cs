using LibreBooth.Core.Layouts;
using SkiaSharp;

namespace LibreBooth.Core.Design;

/// <summary>Fichiers et réglages de personnalisation utilisés pendant un montage.</summary>
public sealed record DesignContext(EventDesign Design, string? AssetsFolder)
{
    public static DesignContext CreateDefault() => new(ThemeCatalog.CreateDefaultDesign(), null);

    public string? ResolveFile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var path = Path.IsPathRooted(name) ? name : AssetsFolder == null ? null : Path.Combine(AssetsFolder, name);
        return path != null && File.Exists(path) ? path : null;
    }

    public string? BackgroundPathFor(string? style) => ResolveFile(Design.BackgroundFileFor(style));

    public string? LogoPath => ResolveFile(Design.LogoFile);
}

/// <summary>Emplacements calculés d'un modèle personnalisable : photos, textes, logo, bandeaux.</summary>
public sealed class DesignAreas
{
    public required SKRect Canvas { get; init; }
    /// <summary>Zone de sécurité : 3 mm à l'intérieur des bords (le bord peut être rogné à l'impression).</summary>
    public required SKRect Safe { get; init; }
    public required IReadOnlyList<SKRect> Photos { get; init; }
    public required SKRect TitleArea { get; init; }
    public required SKRect DetailsArea { get; init; }
    /// <summary>Titre et détails (date, phrase) écrits ensemble dans la même zone (carte 10x15).</summary>
    public bool Stacked { get; init; }
    public SKRect? LogoArea { get; init; }
    /// <summary>Bandeaux de couleur du décor « Bandeau ».</summary>
    public IReadOnlyList<SKRect> Bands { get; init; } = Array.Empty<SKRect>();
}

/// <summary>Calcule où vont les textes et le logo à partir des zones photo du modèle.</summary>
public static class DesignLayout
{
    /// <summary>3 mm à 300 dpi.</summary>
    public const float SafeMargin = 36;

    /// <param name="decor">Décor réellement dessiné (aucun quand l'opérateur fournit son propre fond).</param>
    public static DesignAreas Compute(LayoutDefinition layout, EventDesign design, DecorStyle decor, bool hasLogo)
    {
        var canvas = SKRect.Create(0, 0, layout.Width, layout.Height);
        var safe = Inset(canvas, SafeMargin);
        var photos = layout.Zones.Select(z => SKRect.Create(z.X, z.Y, z.Width, z.Height)).ToList();
        // Espace laissé autour des photos pour la bordure, l'ombre et les cadres néon.
        var clearance = design.PhotoBorder + (design.PhotoShadow ? 12f : 0f) + (decor == DecorStyle.Neon ? 26f : 0f);
        return DesignStyles.IsBookmark(layout.DesignStyle)
            ? Bookmark(canvas, safe, photos, decor, hasLogo, clearance)
            : Postcard(canvas, safe, photos, decor, hasLogo, clearance);
    }

    /// <summary>10x15 paysage : textes (et logo à droite) dans la bande sous la photo.</summary>
    private static DesignAreas Postcard(SKRect canvas, SKRect safe, List<SKRect> photos, DecorStyle decor, bool hasLogo, float clearance)
    {
        var photosBottom = photos.Count > 0 ? photos.Max(p => p.Bottom) : canvas.Height * 0.72f;
        var bands = new List<SKRect>();
        var top = photosBottom + 22 + clearance;
        if (decor == DecorStyle.Band)
        {
            var band = new SKRect(canvas.Left, photosBottom + 30 + clearance, canvas.Right, canvas.Bottom);
            bands.Add(band);
            top = band.Top + 14;
        }

        // Le bas des textes reste à 44 px (3,7 mm) du bord : au-dessus des filets du décor et de la zone rognée.
        var bottom = canvas.Bottom - 44;
        top = Math.Min(top, bottom - 80);
        var block = new SKRect(safe.Left + 20, top, safe.Right - 20, bottom);
        SKRect? logo = null;
        if (hasLogo)
        {
            var width = Math.Min(block.Height * 2.4f, block.Width * 0.22f);
            logo = new SKRect(block.Right - width, block.Top + 8, block.Right, block.Bottom - 4);
            // Place réservée des deux côtés : le texte reste centré sur la carte.
            block = new SKRect(block.Left + width + 24, block.Top, block.Right - width - 24, block.Bottom);
        }

        return new DesignAreas
        {
            Canvas = canvas,
            Safe = safe,
            Photos = photos,
            TitleArea = block,
            DetailsArea = block,
            Stacked = true,
            LogoArea = logo,
            Bands = bands,
        };
    }

    /// <summary>Marque-page : titre au-dessus des photos, date, phrase et logo en dessous.</summary>
    private static DesignAreas Bookmark(SKRect canvas, SKRect safe, List<SKRect> photos, DecorStyle decor, bool hasLogo, float clearance)
    {
        // Sur le marque-page, un seul cadre néon (plus serré) entoure chaque photo.
        clearance = Math.Min(clearance, 12f + clearance / 3f);
        var firstTop = photos.Count > 0 ? photos.Min(p => p.Top) : canvas.Height * 0.2f;
        var lastBottom = photos.Count > 0 ? photos.Max(p => p.Bottom) : canvas.Height * 0.8f;
        var bands = new List<SKRect>();
        if (decor == DecorStyle.Band)
        {
            bands.Add(new SKRect(canvas.Left, canvas.Top, canvas.Right, Math.Max(canvas.Top, firstTop - 26 - clearance)));
            bands.Add(new SKRect(canvas.Left, Math.Min(canvas.Bottom, lastBottom + 26 + clearance), canvas.Right, canvas.Bottom));
        }

        // Le haut garde la place d'un petit ornement (façon œillet de marque-page).
        var ornament = decor is DecorStyle.Lines or DecorStyle.Neon or DecorStyle.Leaves ? 64f : 18f;
        var title = new SKRect(safe.Left + 8, safe.Top + ornament, safe.Right - 8, firstTop - 24 - clearance);
        var details = new SKRect(safe.Left + 8, lastBottom + 30 + clearance, safe.Right - 8, safe.Bottom - 20);

        SKRect? logo = null;
        if (hasLogo && details.Height > 120)
        {
            var height = Math.Min(details.Height * 0.42f, 170f);
            var width = Math.Min(details.Width * 0.8f, height * 2.6f);
            logo = SKRect.Create(details.MidX - width / 2, details.Bottom - height, width, height);
            details = new SKRect(details.Left, details.Top, details.Right, logo.Value.Top - 18);
        }

        var stacked = title.Height < 70;
        return new DesignAreas
        {
            Canvas = canvas,
            Safe = safe,
            Photos = photos,
            TitleArea = stacked ? details : title,
            DetailsArea = details,
            Stacked = stacked,
            LogoArea = logo,
            Bands = bands,
        };
    }

    internal static SKRect Inset(SKRect rect, float amount) =>
        new(rect.Left + amount, rect.Top + amount, rect.Right - amount, rect.Bottom - amount);

    internal static SKRect Inflate(SKRect rect, float amount) => Inset(rect, -amount);
}
