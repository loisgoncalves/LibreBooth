using LibreBooth.Core.Config;

namespace LibreBooth.Core.Design;

/// <summary>
/// Couleurs des fenêtres de l'admin, en clair ou en sombre (admin › Système › Apparence). Le photobooth des invités
/// garde les couleurs de l'événement (voir KioskPalette).
/// L'application range chaque couleur sous la clé « Admin.Nom » (Admin.Text, Admin.Card…). Les textes gardent au moins
/// 4,5:1 de contraste avec leurs fonds, et les bordures des champs au moins 3:1 (vérifié par les tests).
/// </summary>
public sealed record AdminPalette
{
    public required bool IsDark { get; init; }

    // ---------------------------------------------------------------- Surfaces
    /// <summary>Fond des fenêtres et des pages.</summary>
    public required string Window { get; init; }
    /// <summary>Menu de gauche.</summary>
    public required string Sidebar { get; init; }
    public required string SidebarHover { get; init; }
    /// <summary>Page ouverte dans le menu.</summary>
    public required string SidebarSelected { get; init; }
    /// <summary>Cartes qui regroupent les réglages.</summary>
    public required string Card { get; init; }
    public required string CardBorder { get; init; }
    /// <summary>Fond des champs, des listes déroulantes et des boutons ordinaires.</summary>
    public required string Control { get; init; }
    public required string ControlHover { get; init; }
    public required string ControlPressed { get; init; }
    /// <summary>Bordure des champs et des cases (au moins 3:1 sur les cartes et le fond).</summary>
    public required string ControlBorder { get; init; }
    /// <summary>Bordure discrète des boutons ordinaires (leur texte suffit à les reconnaître).</summary>
    public required string ButtonBorder { get; init; }
    public required string Divider { get; init; }
    public required string ScrollThumb { get; init; }
    /// <summary>Fond des aperçus d'images (montages, live view).</summary>
    public required string Preview { get; init; }

    // ---------------------------------------------------------------- Textes
    public required string Text { get; init; }
    /// <summary>Explications et textes secondaires.</summary>
    public required string TextMuted { get; init; }

    // ---------------------------------------------------------------- Accent (vert LibreBooth)
    /// <summary>Boutons principaux, interrupteurs activés, cases cochées.</summary>
    public required string Accent { get; init; }
    public required string AccentHover { get; init; }
    public required string AccentPressed { get; init; }
    /// <summary>Texte posé sur l'accent.</summary>
    public required string OnAccent { get; init; }
    /// <summary>Accent assez contrasté pour un texte ou une bordure sur les fonds.</summary>
    public required string AccentText { get; init; }
    /// <summary>Fond léger d'un choix sélectionné.</summary>
    public required string AccentSoft { get; init; }

    // ---------------------------------------------------------------- États (texte sur fond léger)
    public required string Success { get; init; }
    public required string SuccessBackground { get; init; }
    public required string Warning { get; init; }
    public required string WarningBackground { get; init; }
    public required string Error { get; init; }
    public required string ErrorBackground { get; init; }
    public required string Info { get; init; }
    public required string InfoBackground { get; init; }

    // ---------------------------------------------------------------- Pastilles pleines (icône blanche dessus)
    public required string SuccessFill { get; init; }
    public required string WarningFill { get; init; }
    public required string ErrorFill { get; init; }
    public required string InfoFill { get; init; }

    // ---------------------------------------------------------------- Bouton « Quitter LibreBooth », suppressions
    public required string Danger { get; init; }
    public required string DangerHover { get; init; }
    public required string DangerPressed { get; init; }
    public required string OnDanger { get; init; }

    public static AdminPalette Light { get; } = new()
    {
        IsDark = false,
        Window = "#F4F6F5",
        Sidebar = "#EAF0EC",
        SidebarHover = "#DFE8E2",
        SidebarSelected = "#D6E6DA",
        Card = "#FFFFFF",
        CardBorder = "#DCE3DE",
        Control = "#FFFFFF",
        ControlHover = "#EEF2EF",
        ControlPressed = "#E2E8E4",
        ControlBorder = "#858F88",
        ButtonBorder = "#C9D1CB",
        Divider = "#E3E8E4",
        ScrollThumb = "#B7C0B9",
        Preview = "#2B2B30",
        Text = "#1B1F1C",
        TextMuted = "#5B635D",
        Accent = "#1E7B34",
        AccentHover = "#196A2C",
        AccentPressed = "#145723",
        OnAccent = "#FFFFFF",
        AccentText = "#1E7B34",
        AccentSoft = "#E3F1E6",
        Success = "#1E7B34",
        SuccessBackground = "#E3F1E6",
        Warning = "#9A5B00",
        WarningBackground = "#FFF4E0",
        Error = "#B3261E",
        ErrorBackground = "#FDECEA",
        Info = "#1F5FA8",
        InfoBackground = "#E8F1FB",
        SuccessFill = "#1E7B34",
        WarningFill = "#C77700",
        ErrorFill = "#C42B1C",
        InfoFill = "#1F5FA8",
        Danger = "#A4262C",
        DangerHover = "#8E1F25",
        DangerPressed = "#761A1F",
        OnDanger = "#FFFFFF",
    };

    public static AdminPalette Dark { get; } = new()
    {
        IsDark = true,
        Window = "#1C1D1F",
        Sidebar = "#161718",
        SidebarHover = "#212326",
        SidebarSelected = "#24352A",
        Card = "#26282B",
        CardBorder = "#34373B",
        Control = "#2E3135",
        ControlHover = "#34383C",
        ControlPressed = "#3C4045",
        ControlBorder = "#6E737A",
        ButtonBorder = "#4A4E54",
        Divider = "#34373B",
        ScrollThumb = "#575C63",
        Preview = "#111214",
        Text = "#F1F3F2",
        TextMuted = "#AEB4B0",
        Accent = "#1E7B34",
        AccentHover = "#228739",
        AccentPressed = "#196A2C",
        OnAccent = "#FFFFFF",
        AccentText = "#5FD07E",
        AccentSoft = "#1F3A27",
        Success = "#5FD07E",
        SuccessBackground = "#1F3A27",
        Warning = "#F2B24C",
        WarningBackground = "#3D3020",
        Error = "#FF8A80",
        ErrorBackground = "#3F2424",
        Info = "#7FB5F0",
        InfoBackground = "#1E2D40",
        SuccessFill = "#1E7B34",
        WarningFill = "#C77700",
        ErrorFill = "#C42B1C",
        InfoFill = "#1F5FA8",
        Danger = "#A4262C",
        DangerHover = "#B92D34",
        DangerPressed = "#8E1F25",
        OnDanger = "#FFFFFF",
    };

    public static AdminPalette For(AdminAppearance appearance) => appearance == AdminAppearance.Dark ? Dark : Light;

    /// <summary>Chaque couleur (« #RRVVBB ») avec son nom (Window, Text…), pour les ressources de l'application.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Colors() =>
        typeof(AdminPalette).GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
            .Select(p => new KeyValuePair<string, string>(p.Name, (string)p.GetValue(this)!))
            .ToList();
}
