using System.Text.Json.Serialization;

namespace LibreBooth.Core.Layouts;

/// <summary>Noms des profils d'impression (un jeu de réglages du pilote DNP par profil).</summary>
public static class PrintProfiles
{
    public const string Postcard = "10x15";
    public const string Strips = "Bandelettes";
}

/// <summary>
/// Description d'un modèle de montage (fichier layout.json). Toutes les mesures sont en pixels à 300 dpi :
/// 1800 × 1200 pour un 10x15, 600 × 1800 pour une bandelette 5x15.
/// </summary>
public sealed class LayoutDefinition
{
    public string Name { get; set; } = "Nouveau modèle";
    public int Width { get; set; } = 1800;
    public int Height { get; set; } = 1200;
    /// <summary>Cadre PNG posé par-dessus les photos (zones photo transparentes).</summary>
    public string? FrameFile { get; set; } = "cadre.png";
    /// <summary>Image facultative placée sous les photos.</summary>
    public string? BackgroundFile { get; set; }
    public string BackgroundColor { get; set; } = "#FFFFFF";
    /// <summary>Bandelette 5x15 : elle est placée deux fois côte à côte sur le 10x15, que la DNP coupe au milieu.</summary>
    public bool DuplicateStrip { get; set; }
    public bool BlackAndWhite { get; set; }
    public string PrintProfile { get; set; } = PrintProfiles.Postcard;
    /// <summary>
    /// Modèle personnalisable (« carte-10x15 » ou « marque-page ») : le fond, le décor, les textes et le logo
    /// viennent de la personnalisation de l'événement. Vide pour un modèle classique (cadre PNG et textes fixes).
    /// </summary>
    public string? DesignStyle { get; set; }
    public List<PhotoZone> Zones { get; set; } = new();
    public List<TextField> Texts { get; set; } = new();

    [JsonIgnore] public int OutputWidth => DuplicateStrip ? Width * 2 : Width;
    [JsonIgnore] public int OutputHeight => Height;
    [JsonIgnore] public bool IsCustomizable => !string.IsNullOrWhiteSpace(DesignStyle);

    public LayoutDefinition Normalize()
    {
        Width = Math.Clamp(Width, 100, 12000);
        Height = Math.Clamp(Height, 100, 12000);
        Zones ??= new();
        Texts ??= new();
        if (string.IsNullOrWhiteSpace(BackgroundColor)) BackgroundColor = "#FFFFFF";
        if (string.IsNullOrWhiteSpace(PrintProfile)) PrintProfile = DuplicateStrip ? PrintProfiles.Strips : PrintProfiles.Postcard;
        foreach (var zone in Zones) zone.ClampTo(Width, Height);
        return this;
    }
}

/// <summary>Emplacement d'une photo dans le montage.</summary>
public sealed class PhotoZone
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Rotation en degrés (pour les cadres façon polaroïd penché).</summary>
    public float Rotation { get; set; }

    public PhotoZone() { }

    public PhotoZone(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public void ClampTo(int canvasWidth, int canvasHeight)
    {
        Width = Math.Clamp(Width, 10, canvasWidth);
        Height = Math.Clamp(Height, 10, canvasHeight);
        X = Math.Clamp(X, -Width / 2, canvasWidth - Width / 2);
        Y = Math.Clamp(Y, -Height / 2, canvasHeight - Height / 2);
    }

    public override string ToString() => $"{X}, {Y} – {Width} × {Height}";
}

public enum TextAlign
{
    Left,
    Center,
    Right,
}

/// <summary>Texte ajouté au montage ; {evenement} et {date} sont remplacés automatiquement.</summary>
public sealed class TextField
{
    public string Content { get; set; } = "{evenement}";
    /// <summary>Position horizontale : bord gauche, centre ou bord droit selon <see cref="Align"/>.</summary>
    public int X { get; set; }
    /// <summary>Ligne de base du texte.</summary>
    public int Y { get; set; }
    public float Size { get; set; } = 60;
    public string Color { get; set; } = "#222222";
    public string Font { get; set; } = "Georgia";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public TextAlign Align { get; set; } = TextAlign.Center;
    /// <summary>Largeur maximale : le texte est réduit s'il dépasse (0 = largeur du modèle moins les marges).</summary>
    public int MaxWidth { get; set; }
}
