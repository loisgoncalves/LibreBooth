namespace LibreBooth.Core.Hardware;

/// <summary>Résultat de la sonde DNP (LibreBooth.exe --dnp-probe), échangé en JSON.</summary>
public sealed class DnpProbeResult
{
    public bool Found { get; set; }
    /// <summary>Nombre d'imprimantes DNP annoncé par GetPrinterPortNum.</summary>
    public int PrinterCount { get; set; }
    public int Port { get; set; } = -1;
    public int Status { get; set; }
    /// <summary>Feuilles restantes sur le rouleau (GetMediaCounter).</summary>
    public int Remaining { get; set; } = -1;
    /// <summary>Demi-feuilles restantes (GetMediaCounterH) : des 10x15 quand le rouleau est en 15x20.</summary>
    public int RemainingHalf { get; set; } = -1;
    /// <summary>Contenance d'un rouleau neuf (GetInitialMediaCount).</summary>
    public int InitialCount { get; set; } = -1;
    /// <summary>Code du papier chargé (GetMedia), par exemple « 00310 » pour du 15x20.</summary>
    public string? Media { get; set; }
    public string? Error { get; set; }
    public List<string> Tried { get; set; } = new();
}

/// <summary>
/// Traduction des codes renvoyés par GetStatus de cspstat.dll (DNP).
/// Les groupes 0x0001xxxx (état), 0x0002xxxx (erreur réparable), 0x0004xxxx (erreur matérielle)
/// et 0x0008xxxx (erreur système) suivent la convention de l'API de statut DNP. Le code brut est
/// toujours affiché dans l'admin pour le diagnostic. Relevés sur une DS620 : 0x00010001 prête,
/// 0x00010002 impression en cours, 0x00010080 en veille, 0x00020001 capot ouvert.
/// </summary>
public static class DnpStatusCodes
{
    public const int GroupStatus = 0x00010000;
    public const int GroupSettingError = 0x00020000;
    public const int GroupHardwareError = 0x00040000;
    public const int GroupSystemError = 0x00080000;

    public static bool IsKnown(int status) =>
        status > 0 && (status & unchecked((int)0xFFF00000)) == 0 && (status & 0x000F0000) != 0;

    /// <summary>
    /// Papier chargé d'après le code de GetMedia (codes DNP : 300 = 6x4, 310 = 6x8…).
    /// Sur un rouleau 15x20 ou 15x23, chaque 10x15 occupe une demi-feuille.
    /// </summary>
    public static (string Name, bool TwoPrintsPerSheet) DescribeMedia(string? code)
    {
        if (!int.TryParse(code?.Trim(), out var value)) return ("inconnu", false);
        return value switch
        {
            200 => ("9x13 (5x3,5)", false),
            210 => ("13x13 (5x5)", false),
            300 => ("10x15 (6x4)", false),
            310 => ("15x20 (6x8)", true),
            400 => ("15x23 (6x9)", true),
            500 => ("20x25 (8x10)", false),
            510 => ("20x30 (8x12)", false),
            _ => ($"code {value}", false),
        };
    }

    /// <summary>Tirages 10x15 restants : demi-feuilles sur un rouleau 15x20, feuilles sinon.</summary>
    public static int? RemainingPostcards(DnpProbeResult result)
    {
        var (_, twoPerSheet) = DescribeMedia(result.Media);
        if (twoPerSheet && result.RemainingHalf >= 0) return result.RemainingHalf;
        if (twoPerSheet && result.Remaining >= 0) return result.Remaining * 2;
        return result.Remaining >= 0 ? result.Remaining : null;
    }

    public static (PrinterCondition Condition, string Message) Describe(int status)
    {
        if (!IsKnown(status)) return (PrinterCondition.Unknown, $"État inconnu (code 0x{status:X8})");
        var bits = status & 0xFFFF;

        if ((status & GroupSystemError) != 0)
            return (PrinterCondition.Error, "Erreur système de l'imprimante : éteignez-la puis rallumez-la.");
        if ((status & GroupHardwareError) != 0)
            return (PrinterCondition.Error, $"Erreur matérielle de l'imprimante (code 0x{status:X8}) : éteignez-la puis rallumez-la.");
        if ((status & GroupSettingError) != 0)
        {
            if ((bits & 0x0001) != 0) return (PrinterCondition.Error, "Le capot de l'imprimante est ouvert.");
            if ((bits & 0x0002) != 0) return (PrinterCondition.Error, "Bourrage papier.");
            if ((bits & 0x0004) != 0) return (PrinterCondition.Error, "Problème de ruban : vérifiez qu'il est bien installé.");
            if ((bits & 0x0008) != 0) return (PrinterCondition.Error, "Papier ou ruban non reconnu.");
            if ((bits & 0x0010) != 0) return (PrinterCondition.Error, "Erreur dans les données d'impression.");
            if ((bits & 0x0020) != 0) return (PrinterCondition.Error, "Bac à chutes plein ou absent.");
            return (PrinterCondition.Error, $"Erreur de l'imprimante (code 0x{status:X8}).");
        }

        if ((bits & 0x0008) != 0) return (PrinterCondition.Error, "Plus de papier : remplacez le rouleau.");
        if ((bits & 0x0010) != 0) return (PrinterCondition.Error, "Ruban terminé : remplacez le ruban.");
        if ((bits & 0x0002) != 0) return (PrinterCondition.Busy, "Impression en cours…");
        if ((bits & 0x0060) != 0) return (PrinterCondition.Busy, "L'imprimante refroidit…");
        if ((bits & 0x0004) != 0) return (PrinterCondition.Warning, "Imprimante en attente.");
        // 0x0080 : veille d'économie d'énergie (relevé sur une DS620 au repos) ; elle se réveille à l'impression.
        if ((bits & 0x0080) != 0) return (PrinterCondition.Ready, "Prête (en veille)");
        return (PrinterCondition.Ready, "Prête");
    }
}
