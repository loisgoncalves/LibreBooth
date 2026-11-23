namespace LibreBooth.App.Hardware.Canon;

/// <summary>Traduction en français des codes d'erreur du SDK Canon.</summary>
internal static class EdsdkErrors
{
    private static readonly Dictionary<uint, string> Messages = new()
    {
        [0x00000002] = "erreur interne du pilote Canon",
        [0x00000003] = "mémoire insuffisante",
        [0x00000007] = "fonction non prise en charge par cet appareil",
        [0x00000080] = "appareil introuvable",
        [0x00000081] = "appareil occupé",
        [0x00000082] = "appareil non valide",
        [0x00000083] = "arrêt d'urgence de l'appareil",
        [0x00000084] = "mémoire de l'appareil pleine",
        [0x00000085] = "erreur interne de l'appareil",
        [0x00000086] = "paramètre refusé par l'appareil",
        [0x00000087] = "pas de carte mémoire",
        [0x00000088] = "erreur de carte mémoire",
        [0x0000008A] = "la molette de mode a été tournée",
        [0x0000008B] = "appareil non installé",
        [0x0000008C] = "appareil en mode veille prolongée",
        [0x0000008D] = "le déclencheur n'a pas été relâché",
        [0x000000C0] = "l'appareil est déjà utilisé par un autre logiciel (EOS Utility ?)",
        [0x000000C1] = "appareil déconnecté",
        [0x000000C2] = "appareil incompatible",
        [0x000000C4] = "erreur du bus USB",
        [0x000000D0] = "verrouillage USB impossible",
        [0x000000E0] = "erreur Windows inconnue (WIA)",
        [0x000000E1] = "erreur Windows interne (WIA)",
        [0x000000E2] = "le pilote Windows de l'appareil n'a pas démarré",
        [0x000000E4] = "appareil non lancé",
        [0x000000F4] = "délai dépassé",
        [0x00002003] = "session non ouverte",
        [0x00002007] = "transfert incomplet",
        [0x00002019] = "appareil occupé",
        [0x0000201E] = "session déjà ouverte",
        [0x00002021] = "cet appareil n'est pas pris en charge par cette version du pilote Canon",
        [0x0000A005] = "opération refusée par l'appareil",
        [0x0000A006] = "le cache de l'objectif est fermé",
        [0x0000A101] = "batterie faible",
        [0x0000A102] = "image pas encore prête",
        [0x00008D01] = "mise au point impossible",
        [0x00008D03] = "miroir relevé",
        [0x00008D04] = "nettoyage du capteur en cours",
        [0x00008D06] = "pas de carte mémoire",
        [0x00008D07] = "erreur de carte mémoire",
        [0x00008D08] = "carte mémoire protégée en écriture",
        [0x00008D0A] = "flash en cours de recharge",
        [0x00008D0B] = "pas d'objectif",
    };

    public static string Describe(uint error) =>
        Messages.TryGetValue(error, out var message) ? $"{message} (0x{error:X})" : $"erreur Canon 0x{error:X}";

    public static string ForSession(uint error) => error switch
    {
        0x000000C0 or 0x00000081 or 0x00002019 =>
            "L'appareil photo est occupé par un autre logiciel : fermez EOS Utility et l'application Photos de Windows.",
        0x000000C1 or 0x000000C4 => "La connexion USB avec l'appareil photo a été coupée : vérifiez le câble.",
        0x00002021 => "Ce modèle d'appareil n'est pas reconnu par le pilote Canon installé.",
        _ => "Connexion à l'appareil photo impossible : " + Describe(error),
    };

    public static string ForCapture(uint error) => error switch
    {
        0x00008D01 => "Mise au point impossible : passez l'objectif en mise au point manuelle (MF).",
        0x00008D06 or 0x00008D07 or 0x00008D08 => "Problème de carte mémoire : vérifiez la carte SD ou réglez « Déclencher sans carte ».",
        0x0000A101 => "Batterie de l'appareil trop faible.",
        0x0000A006 or 0x00008D0B => "Vérifiez l'objectif de l'appareil photo.",
        0x000000C1 or 0x000000C4 => "La connexion USB avec l'appareil photo a été coupée.",
        _ => "L'appareil n'a pas pu prendre la photo : " + Describe(error),
    };
}
