using System.Text.Json.Serialization;

namespace LibreBooth.Core.Design;

/// <summary>Styles de mise en page des modèles personnalisables.</summary>
public static class DesignStyles
{
    /// <summary>10x15 paysage, une photo, textes en bas.</summary>
    public const string Postcard = "carte-10x15";
    /// <summary>Marque-page 5x15, trois photos ; imprimé deux fois sur un 10x15 que la DNP coupe en deux.</summary>
    public const string Bookmark = "marque-page";

    public static bool IsBookmark(string? style) => string.Equals(style, Bookmark, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Décor dessiné par LibreBooth autour des photos.</summary>
public enum DecorStyle
{
    None,
    /// <summary>Filets fins autour de la carte et des photos, ornement losange.</summary>
    Lines,
    /// <summary>Brins de feuillage dans les coins.</summary>
    Leaves,
    /// <summary>Confettis de couleur dans les marges.</summary>
    Confetti,
    /// <summary>Cadres lumineux façon néon sur fond nuit.</summary>
    Neon,
    /// <summary>Une ligne fine sous les photos, typographie sobre.</summary>
    Minimal,
    /// <summary>Bandeau de couleur pleine derrière les textes.</summary>
    Band,
}

/// <summary>
/// L'apparence des modèles personnalisables pour un événement : couleurs, polices, textes, décor,
/// style des photos, fond PNG et logo. Les couleurs sont au format #RRVVBB.
/// </summary>
public sealed class EventDesign
{
    public const int MinScale = 50;
    public const int MaxScale = 160;

    /// <summary>Dernier thème appliqué (les réglages ont pu être modifiés depuis).</summary>
    public string ThemeId { get; set; } = ThemeCatalog.DefaultThemeId;
    public DecorStyle Decor { get; set; } = DecorStyle.Lines;

    public string BackgroundColor { get; set; } = "#FBF6EE";
    /// <summary>Couleur principale du décor (filets, feuillage, bandeau, néon…).</summary>
    public string AccentColor { get; set; } = "#B08D57";
    /// <summary>Seconde couleur du décor (néon, confettis).</summary>
    public string Accent2Color { get; set; } = "#D9C7A3";
    public string TitleColor { get; set; } = "#3B3127";
    public string TextColor { get; set; } = "#8A7354";

    public string TitleFont { get; set; } = "Gabriola";
    /// <summary>Graisse du titre : 300 fin, 400 normal, 600 demi-gras, 700 gras, 900 extra-gras.</summary>
    public int TitleWeight { get; set; } = 400;
    public bool TitleItalic { get; set; }
    /// <summary>Titre en majuscules espacées (style « entreprise »).</summary>
    public bool TitleUppercase { get; set; }
    public string TextFont { get; set; } = "Georgia";
    public int TextWeight { get; set; } = 400;
    public bool TextItalic { get; set; } = true;
    public bool TextUppercase { get; set; }
    /// <summary>Taille du titre en % de la taille prévue par le modèle (le texte rétrécit toujours s'il manque de place).</summary>
    public int TitleScale { get; set; } = 100;
    /// <summary>Taille de la date et de la phrase, en %.</summary>
    public int TextScale { get; set; } = 100;

    /// <summary>Texte principal ({evenement}, {date}, {date_longue} sont remplacés).</summary>
    public string Title { get; set; } = "{evenement}";
    public string Subtitle { get; set; } = "{date_longue}";
    /// <summary>Phrase libre facultative (remerciement, hashtag…).</summary>
    public string Phrase { get; set; } = "";

    public int PhotoCornerRadius { get; set; }
    /// <summary>Bordure autour de chaque photo, en pixels à 300 dpi (0 = aucune).</summary>
    public int PhotoBorder { get; set; }
    public string PhotoBorderColor { get; set; } = "#FFFFFF";
    public bool PhotoShadow { get; set; }
    public bool BlackAndWhite { get; set; }

    /// <summary>
    /// Mode de personnalisation : vrai = « Mes propres images » (les fonds importés sont utilisés), faux = « Thème
    /// LibreBooth » (les fonds importés sont gardés, mais pas utilisés). Vide dans une personnalisation d'avant la
    /// 1.4.0 : « Mes propres images » s'il y avait déjà un fond.
    /// </summary>
    public bool? OwnImages { get; set; }

    [JsonIgnore] public bool UsesOwnImages => OwnImages == true;

    /// <summary>Fond fourni par l'opérateur, par style de modèle (fichier du dossier de personnalisation).</summary>
    public Dictionary<string, string> Backgrounds { get; set; } = new();
    /// <summary>Avec un fond personnalisé : écrire quand même les textes de LibreBooth par-dessus.</summary>
    public bool TextsOverBackground { get; set; } = true;
    /// <summary>Logo (PNG transparent conseillé), fichier du dossier de personnalisation.</summary>
    public string? LogoFile { get; set; }

    /// <summary>Fond utilisé pour ce style : seulement en mode « Mes propres images ».</summary>
    public string? BackgroundFileFor(string? style) => UsesOwnImages ? StoredBackgroundFor(style) : null;

    /// <summary>Fond importé pour ce style, même quand le mode « Thème LibreBooth » ne l'utilise pas.</summary>
    public string? StoredBackgroundFor(string? style) =>
        style != null && Backgrounds.TryGetValue(style, out var file) && !string.IsNullOrWhiteSpace(file) ? file : null;

    public EventDesign Normalize()
    {
        Backgrounds ??= new();
        foreach (var key in Backgrounds.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).ToList()) Backgrounds.Remove(key);
        OwnImages ??= Backgrounds.Count > 0;
        ThemeId ??= ThemeCatalog.DefaultThemeId;
        Title ??= "";
        Subtitle ??= "";
        Phrase ??= "";
        BackgroundColor = ValidColor(BackgroundColor, "#FFFFFF");
        AccentColor = ValidColor(AccentColor, "#888888");
        Accent2Color = ValidColor(Accent2Color, AccentColor);
        TitleColor = ValidColor(TitleColor, "#222222");
        TextColor = ValidColor(TextColor, "#555555");
        PhotoBorderColor = ValidColor(PhotoBorderColor, "#FFFFFF");
        if (string.IsNullOrWhiteSpace(TitleFont)) TitleFont = "Georgia";
        if (string.IsNullOrWhiteSpace(TextFont)) TextFont = "Georgia";
        TitleWeight = Math.Clamp(TitleWeight, 100, 950);
        TextWeight = Math.Clamp(TextWeight, 100, 950);
        TitleScale = Math.Clamp(TitleScale, MinScale, MaxScale);
        TextScale = Math.Clamp(TextScale, MinScale, MaxScale);
        PhotoCornerRadius = Math.Clamp(PhotoCornerRadius, 0, 200);
        PhotoBorder = Math.Clamp(PhotoBorder, 0, 80);
        if (!Enum.IsDefined(Decor)) Decor = DecorStyle.None;
        return this;
    }

    /// <summary>Reprend l'apparence d'un thème (couleurs, polices, décor, photos) sans toucher aux textes, fonds et logo.</summary>
    public void CopyLookFrom(EventDesign look)
    {
        ThemeId = look.ThemeId;
        Decor = look.Decor;
        BackgroundColor = look.BackgroundColor;
        AccentColor = look.AccentColor;
        Accent2Color = look.Accent2Color;
        TitleColor = look.TitleColor;
        TextColor = look.TextColor;
        TitleFont = look.TitleFont;
        TitleWeight = look.TitleWeight;
        TitleItalic = look.TitleItalic;
        TitleUppercase = look.TitleUppercase;
        TextFont = look.TextFont;
        TextWeight = look.TextWeight;
        TextItalic = look.TextItalic;
        TextUppercase = look.TextUppercase;
        TitleScale = look.TitleScale;
        TextScale = look.TextScale;
        PhotoCornerRadius = look.PhotoCornerRadius;
        PhotoBorder = look.PhotoBorder;
        PhotoBorderColor = look.PhotoBorderColor;
        PhotoShadow = look.PhotoShadow;
        BlackAndWhite = look.BlackAndWhite;
    }

    private static string ValidColor(string? value, string fallback) =>
        !string.IsNullOrWhiteSpace(value) && SkiaSharp.SKColor.TryParse(value.Trim(), out _) ? value.Trim() : fallback;
}

/// <summary>
/// Réglages propres à un événement (fichier personnalisation.json du dossier de l'événement) :
/// formats proposés aux invités, nombre maximal d'exemplaires et apparence des modèles.
/// </summary>
public sealed class EventCustomization
{
    public const int MaxCopiesLimit = 10;

    public int Version { get; set; } = 1;
    /// <summary>Modèles proposés aux invités, dans l'ordre d'affichage (identifiants des dossiers de Modeles).</summary>
    public List<string> Templates { get; set; } = new();
    /// <summary>L'invité choisit de 1 à MaxCopies exemplaires par photo.</summary>
    public int MaxCopies { get; set; } = 2;
    public EventDesign Design { get; set; } = ThemeCatalog.CreateDefaultDesign();
    /// <summary>
    /// Image de fond de l'écran d'accueil du photobooth (fichier du dossier de personnalisation), facultative.
    /// Les couleurs des écrans, elles, viennent toujours de l'apparence des impressions.
    /// </summary>
    public string? AttractBackground { get; set; }

    [JsonIgnore] public bool IsNew { get; set; }

    public EventCustomization Normalize()
    {
        Templates ??= new();
        Templates = Templates.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        MaxCopies = Math.Clamp(MaxCopies, 1, MaxCopiesLimit);
        if (string.IsNullOrWhiteSpace(AttractBackground)) AttractBackground = null;
        Design ??= ThemeCatalog.CreateDefaultDesign();
        Design.Normalize();
        return this;
    }
}
