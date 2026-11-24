namespace LibreBooth.Core.Config;

public enum CameraKind
{
    /// <summary>Reflex/hybride Canon EOS branché en USB (pilote choisi par <see cref="CameraSettings.Driver"/>).</summary>
    Canon,
    /// <summary>Fausse caméra pour tester sans appareil.</summary>
    Demo,
}

/// <summary>Comment LibreBooth pilote le Canon EOS (depuis la 3.1.0).</summary>
public enum CameraDriver
{
    /// <summary>Pilote intégré à Windows : rien à installer (par défaut).</summary>
    Windows,
    /// <summary>SDK Canon EDSDK, à télécharger chez Canon : solution de secours si Windows ne sait pas piloter l'appareil.</summary>
    CanonSdk,
}

/// <summary>Apparence des fenêtres de l'admin (le photobooth des invités garde les couleurs de l'événement).</summary>
public enum AdminAppearance
{
    Light,
    Dark,
}

/// <summary>Tous les réglages de LibreBooth, enregistrés dans config.json.</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public string AdminPin { get; set; } = "1234";
    public string? CurrentEventFolder { get; set; }
    public bool LaunchAtStartup { get; set; }
    /// <summary>Au lancement, demande à GitHub s'il existe une nouvelle version (proposée ensuite dans l'admin).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>
    /// Propose aussi les pré-versions (versions d'essai publiées sur GitHub), pour les essayer sur un booth. Depuis la
    /// 3.1.0 ; une version plus ancienne ignore ce réglage.
    /// </summary>
    public bool IncludePrereleases { get; set; }
    /// <summary>Admin en clair ou en sombre (depuis la 3.0.0 ; une version plus ancienne ignore ce réglage).</summary>
    public AdminAppearance AdminAppearance { get; set; } = AdminAppearance.Light;
    public CameraSettings Camera { get; set; } = new();
    public PrintSettings Print { get; set; } = new();
    public ScreenSettings Screens { get; set; } = new();
    // L'envoi par e-mail (versions 1.0 à 1.6) a été retiré en 2.0.0 : la partie « Email » d'un ancien config.json
    // est ignorée, puis effacée au prochain enregistrement (avec l'identifiant SMTP qu'elle pouvait contenir).

    /// <summary>Remet dans des bornes raisonnables les valeurs saisies à la main ou abîmées.</summary>
    public AppSettings Normalize()
    {
        Camera ??= new();
        Print ??= new();
        Screens ??= new();
        if (string.IsNullOrWhiteSpace(AdminPin) || AdminPin.Length < 4 || !AdminPin.All(char.IsDigit)) AdminPin = "1234";
        if (!Enum.IsDefined(AdminAppearance)) AdminAppearance = AdminAppearance.Light;

        if (!Enum.IsDefined(Camera.Kind)) Camera.Kind = CameraKind.Canon;
        if (!Enum.IsDefined(Camera.Driver)) Camera.Driver = CameraDriver.Windows;
        Camera.Rotation = Camera.Rotation switch { 90 or 180 or 270 => Camera.Rotation, _ => 0 };
        Camera.CaptureTimeoutSeconds = Math.Clamp(Camera.CaptureTimeoutSeconds, 4, 60);

        Print.DriverSettings ??= new();
        Print.AutoPrintCopies = Math.Clamp(Print.AutoPrintCopies, 1, 10);
        Print.EventPrintLimit = Math.Max(0, Print.EventPrintLimit);
        Print.LowMediaWarning = Math.Clamp(Print.LowMediaWarning, 0, 500);
        Print.MediaCapacity = Math.Clamp(Print.MediaCapacity, 1, 5000);
        Print.PrintedSinceMediaChange = Math.Max(0, Print.PrintedSinceMediaChange);

        Screens.CountdownSeconds = Math.Clamp(Screens.CountdownSeconds, 1, 10);
        Screens.PreviewMilliseconds = Math.Clamp(Screens.PreviewMilliseconds, 300, 5000);
        Screens.ChooseTimeoutSeconds = Math.Clamp(Screens.ChooseTimeoutSeconds, 10, 300);
        Screens.ReviewTimeoutSeconds = Math.Clamp(Screens.ReviewTimeoutSeconds, 10, 600);
        Screens.ThanksSeconds = Math.Clamp(Screens.ThanksSeconds, 2, 60);
        Screens.ErrorSeconds = Math.Clamp(Screens.ErrorSeconds, 3, 120);
        Screens.RetakeLimit = Math.Clamp(Screens.RetakeLimit, 0, ScreenSettings.MaxRetakes);
        if (string.IsNullOrWhiteSpace(Screens.StartKey)) Screens.StartKey = "Space";
        return this;
    }
}

public sealed class CameraSettings
{
    public CameraKind Kind { get; set; } = CameraKind.Canon;
    /// <summary>
    /// Pilote du Canon : Windows par défaut, SDK Canon en secours (depuis la 3.1.0 ; une version plus ancienne ignore
    /// ce réglage et utilise toujours le SDK).
    /// </summary>
    public CameraDriver Driver { get; set; } = CameraDriver.Windows;
    /// <summary>Live view affiché comme un miroir (plus naturel pour les invités).</summary>
    public bool MirrorLiveView { get; set; } = true;
    /// <summary>Photos finales inversées comme un miroir (désactivé par défaut : le texte reste lisible).</summary>
    public bool MirrorPhotos { get; set; }
    /// <summary>Rotation de l'appareil sur son support : 0, 90, 180 ou 270 degrés.</summary>
    public int Rotation { get; set; }
    /// <summary>Faire l'autofocus avant chaque photo (déconseillé : mise au point manuelle plus fiable).</summary>
    public bool UseAutofocus { get; set; }
    // « Copie sur la carte SD » (SaveToCardToo) est retirée en 3.1.0 : les photos vont au PC seulement, et ce
    // réglage, encore présent dans un ancien config.json, est ignoré à la lecture.
    public int CaptureTimeoutSeconds { get; set; } = 10;
}

public sealed class PrintSettings
{
    public bool Enabled { get; set; } = true;
    public string? PrinterName { get; set; }
    // Le nombre maximal d'exemplaires choisi par l'invité se règle pour chaque événement (personnalisation.json).
    public bool AutoPrint { get; set; }
    /// <summary>Exemplaires imprimés automatiquement (sans dépasser le maximum de l'événement).</summary>
    public int AutoPrintCopies { get; set; } = 1;
    /// <summary>Limite d'impressions pour tout l'événement (0 = illimité).</summary>
    public int EventPrintLimit { get; set; }
    /// <summary>Lire le compteur et les erreurs de la DNP via cspstat.dll.</summary>
    public bool UseDnpStatus { get; set; } = true;
    public int LowMediaWarning { get; set; } = 30;
    /// <summary>Compteur logiciel (si le statut DNP est indisponible) : capacité du rouleau.</summary>
    public int MediaCapacity { get; set; } = 700;
    public int PrintedSinceMediaChange { get; set; }
    /// <summary>Réglages du pilote Windows (DEVMODE en base64) par profil d'impression.</summary>
    public Dictionary<string, string> DriverSettings { get; set; } = new();
}

public sealed class ScreenSettings
{
    public const int MaxRetakes = 5;

    public string Title { get; set; } = "Souriez !";
    public string StartText { get; set; } = "Touchez l'écran pour commencer";
    public string ThanksText { get; set; } = "Merci !";
    public string ThanksPrintText { get; set; } = "Votre photo arrive dans quelques secondes.";
    /// <summary>
    /// Ancienne image d'accueil commune à tous les événements (versions 1.0 à 1.2), utilisée seulement si
    /// l'événement n'a pas la sienne. Depuis la 1.3.0, l'image d'accueil se règle par événement.
    /// </summary>
    public string? AttractBackground { get; set; }
    // AccentColor et BackgroundColor (versions 1.0 à 1.2) ne servent plus : depuis la 1.3.0, les couleurs des écrans
    // viennent de la personnalisation de l'événement. Elles restent lues pour ne pas casser un ancien config.json.
    public string AccentColor { get; set; } = "#FFC83D";
    public string BackgroundColor { get; set; } = "#14161C";
    /// <summary>Nombre de fois où l'invité peut refaire toute sa série depuis l'écran de revue (0 = pas de bouton).</summary>
    public int RetakeLimit { get; set; } = 1;
    public int CountdownSeconds { get; set; } = 3;
    /// <summary>Durée d'affichage de chaque photo juste après la prise de vue.</summary>
    public int PreviewMilliseconds { get; set; } = 1500;
    public int ChooseTimeoutSeconds { get; set; } = 30;
    public int ReviewTimeoutSeconds { get; set; } = 45;
    public int ThanksSeconds { get; set; } = 6;
    public int ErrorSeconds { get; set; } = 10;
    /// <summary>Pointeur de la souris caché tant qu'elle ne bouge pas (il réapparaît dès qu'on la bouge).</summary>
    public bool HideCursor { get; set; } = true;
    public bool Topmost { get; set; } = true;
    /// <summary>Touche qui lance une session (bouton d'arcade USB qui imite un clavier).</summary>
    public string StartKey { get; set; } = "Space";
}
