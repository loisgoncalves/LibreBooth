namespace LibreBooth.Core.Design;

/// <summary>Thème prêt à l'emploi : une apparence complète que l'opérateur peut ensuite retoucher.</summary>
public sealed class ThemePreset
{
    public ThemePreset(string id, string name, string category, string description, EventDesign look)
    {
        Id = id;
        Name = name;
        Category = category;
        Description = description;
        Look = look;
        Look.ThemeId = id;
    }

    public string Id { get; }
    public string Name { get; }
    public string Category { get; }
    public string Description { get; }
    public EventDesign Look { get; }

    public void ApplyTo(EventDesign design) => design.CopyLookFrom(Look);

    public override string ToString() => Name;
}

/// <summary>Thèmes fournis avec LibreBooth (polices livrées avec Windows 10 et 11).</summary>
public static class ThemeCatalog
{
    public const string DefaultThemeId = "mariage-ivoire";

    public static IReadOnlyList<ThemePreset> All { get; } = new[]
    {
        new ThemePreset("mariage-ivoire", "Ivoire et or", "Mariage", "Élégant : fond ivoire, filets dorés, écriture calligraphiée.", new EventDesign
        {
            Decor = DecorStyle.Lines,
            BackgroundColor = "#FBF6EE",
            AccentColor = "#B08D57",
            Accent2Color = "#D9C7A3",
            TitleColor = "#3B3127",
            TextColor = "#8A7354",
            TitleFont = "Gabriola",
            TitleWeight = 400,
            TitleScale = 110,
            TextFont = "Palatino Linotype",
            TextItalic = true,
        }),
        new ThemePreset("mariage-champetre", "Champêtre", "Mariage", "Naturel : blanc cassé, feuillage vert sauge, écriture manuscrite.", new EventDesign
        {
            Decor = DecorStyle.Leaves,
            BackgroundColor = "#FBFAF5",
            AccentColor = "#7F946C",
            Accent2Color = "#B9C7A8",
            TitleColor = "#42523A",
            TextColor = "#6F7D65",
            TitleFont = "Segoe Script",
            TitleWeight = 400,
            TitleScale = 85,
            TextFont = "Constantia",
            TextItalic = true,
            PhotoCornerRadius = 18,
            PhotoShadow = true,
        }),
        new ThemePreset("fete-confettis", "Confettis", "Anniversaire / fête", "Festif : fond blanc, confettis de couleur, police ronde et grasse.", new EventDesign
        {
            Decor = DecorStyle.Confetti,
            BackgroundColor = "#FFFFFF",
            AccentColor = "#FF5D73",
            Accent2Color = "#3EC5E0",
            TitleColor = "#2E2A5C",
            TextColor = "#FF5D73",
            TitleFont = "Segoe UI",
            TitleWeight = 900,
            TitleScale = 90,
            TextFont = "Segoe UI",
            TextWeight = 600,
            TextItalic = false,
            PhotoCornerRadius = 28,
            PhotoShadow = true,
        }),
        new ThemePreset("fete-neon", "Néon", "Anniversaire / fête", "Soirée : fond nuit, cadres lumineux rose et bleu.", new EventDesign
        {
            Decor = DecorStyle.Neon,
            BackgroundColor = "#1A0F3D",
            AccentColor = "#FF3EA5",
            Accent2Color = "#2EE6F6",
            TitleColor = "#FFFFFF",
            TextColor = "#7EF3FB",
            TitleFont = "Bahnschrift",
            TitleWeight = 700,
            TitleScale = 95,
            TitleUppercase = true,
            TextFont = "Bahnschrift",
            TextWeight = 400,
            TextItalic = false,
            TextUppercase = true,
            PhotoCornerRadius = 20,
        }),
        new ThemePreset("entreprise-noir", "Noir et or", "Entreprise / soirée", "Chic : fond noir, ligne dorée, majuscules espacées. Logo conseillé.", new EventDesign
        {
            Decor = DecorStyle.Minimal,
            BackgroundColor = "#16171B",
            AccentColor = "#C8A96A",
            Accent2Color = "#5A5B60",
            TitleColor = "#FFFFFF",
            TextColor = "#C8A96A",
            TitleFont = "Bahnschrift",
            TitleWeight = 300,
            TitleUppercase = true,
            TextFont = "Bahnschrift",
            TextWeight = 400,
            TextItalic = false,
            TextUppercase = true,
        }),
        new ThemePreset("entreprise-blanc", "Blanc et couleur", "Entreprise / soirée", "Sobre : fond blanc, bandeau à la couleur de l'entreprise. Logo conseillé.", new EventDesign
        {
            Decor = DecorStyle.Band,
            BackgroundColor = "#FFFFFF",
            AccentColor = "#1D4E89",
            Accent2Color = "#6FA8DC",
            TitleColor = "#FFFFFF",
            TextColor = "#D5E3F3",
            TitleFont = "Segoe UI",
            TitleWeight = 600,
            TextFont = "Segoe UI",
            TextWeight = 400,
            TextItalic = false,
        }),
    };

    public static ThemePreset? Find(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    public static ThemePreset Default => Find(DefaultThemeId)!;

    public static EventDesign CreateDefaultDesign() => CreateDesign(DefaultThemeId);

    public static EventDesign CreateDesign(string themeId)
    {
        var design = new EventDesign();
        (Find(themeId) ?? Default).ApplyTo(design);
        return design;
    }

    /// <summary>Noms lisibles des décors, pour l'admin.</summary>
    public static string DecorName(DecorStyle decor) => decor switch
    {
        DecorStyle.Lines => "Filets et ornement",
        DecorStyle.Leaves => "Feuillage",
        DecorStyle.Confetti => "Confettis",
        DecorStyle.Neon => "Néon",
        DecorStyle.Minimal => "Ligne fine",
        DecorStyle.Band => "Bandeau de couleur",
        _ => "Aucun décor",
    };

    /// <summary>Polices conseillées, toutes présentes dans Windows 10 et 11.</summary>
    public static IReadOnlyList<string> RecommendedFonts { get; } = new[]
    {
        "Gabriola", "Segoe Script", "Segoe Print", "Ink Free", "Palatino Linotype", "Georgia", "Constantia",
        "Cambria", "Sitka Display", "Bahnschrift", "Segoe UI", "Candara", "Corbel", "Calibri", "Trebuchet MS", "Impact",
    };
}
