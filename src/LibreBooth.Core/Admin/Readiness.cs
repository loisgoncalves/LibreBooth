using System.Globalization;
using LibreBooth.Core.Config;
using LibreBooth.Core.Hardware;

namespace LibreBooth.Core.Admin;

/// <summary>Gravité d'un point de la liste « Prêt pour l'événement ? ».</summary>
public enum ReadinessLevel
{
    /// <summary>Tout va bien (pastille verte).</summary>
    Ok,
    /// <summary>Simple information : ne compte pas dans les points à vérifier (pastille bleue).</summary>
    Info,
    /// <summary>À vérifier, sans empêcher le photobooth de tourner (pastille orange).</summary>
    Warning,
    /// <summary>À corriger avant l'événement (pastille rouge).</summary>
    Error,
}

/// <summary>Ce que vérifie chaque point ; l'admin en déduit la page qui permet de le régler.</summary>
public enum ReadinessArea
{
    Camera,
    Printer,
    Paper,
    Formats,
    PrintLimit,
    Disk,
    Update,
}

public sealed record ReadinessItem(ReadinessArea Area, ReadinessLevel Level, string Title, string Detail)
{
    /// <summary>Compte dans « N points à vérifier ».</summary>
    public bool NeedsAttention => Level is ReadinessLevel.Warning or ReadinessLevel.Error;
}

/// <summary>État du booth au moment du calcul, en valeurs simples (l'admin les lit toutes les secondes).</summary>
public sealed record ReadinessInput
{
    /// <summary>LibreBooth lancé en mode démonstration (--demo) : appareil et imprimante d'essai.</summary>
    public bool DemoMode { get; init; }
    public CameraKind CameraKind { get; init; } = CameraKind.Canon;
    /// <summary>EDSDK.dll ou EdsImage.dll introuvable (appareil Canon choisi).</summary>
    public bool CanonSdkMissing { get; init; }
    public CameraState CameraState { get; init; } = CameraState.Disconnected;
    public string CameraName { get; init; } = "";
    public string CameraMessage { get; init; } = "";
    /// <summary>Batterie en %, -1 sur secteur, null si inconnue.</summary>
    public int? BatteryLevel { get; init; }

    public bool PrintEnabled { get; init; } = true;
    /// <summary>Impressions enregistrées en PDF (essais) au lieu d'être imprimées.</summary>
    public bool PrintToPdf { get; init; }
    public string? PrinterName { get; init; }
    /// <summary>L'imprimante a déjà été interrogée depuis le lancement.</summary>
    public bool PrinterChecked { get; init; }
    public bool PrinterCanPrint { get; init; }
    public PrinterCondition PrinterCondition { get; init; } = PrinterCondition.Unknown;
    public string PrinterMessage { get; init; } = "";
    public int? RemainingPrints { get; init; }
    public bool LowMedia { get; init; }

    /// <summary>Noms des formats proposés aux invités pour cet événement.</summary>
    public IReadOnlyList<string> Formats { get; init; } = Array.Empty<string>();
    public int EventPrints { get; init; }
    /// <summary>Limite d'impressions de l'événement (0 = illimité).</summary>
    public int EventPrintLimit { get; init; }

    /// <summary>Espace libre sur le disque des photos (null si inconnu).</summary>
    public long? FreeDiskBytes { get; init; }
    /// <summary>Nom du disque des photos (« C: »).</summary>
    public string? DiskName { get; init; }

    /// <summary>Version publiée plus récente que celle-ci, si la recherche l'a trouvée.</summary>
    public string? AvailableUpdate { get; init; }
}

/// <summary>
/// La liste « Prêt pour l'événement ? » de l'accueil de l'admin : un point par élément du booth (appareil, imprimante,
/// papier, formats, limite d'impressions, disque, mise à jour), chacun avec sa gravité et une phrase d'explication.
/// </summary>
public static class Readiness
{
    /// <summary>Batterie de l'appareil signalée en dessous de ce niveau (%).</summary>
    public const int LowBattery = 20;
    /// <summary>Limite d'impressions signalée à partir de cette part (90 %).</summary>
    public const double PrintLimitWarningShare = 0.9;

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public static IReadOnlyList<ReadinessItem> Evaluate(ReadinessInput input)
    {
        var items = new List<ReadinessItem> { Camera(input) };
        items.AddRange(PrinterAndPaper(input));
        items.Add(Formats(input));
        if (PrintLimit(input) is { } limit) items.Add(limit);
        if (Disk(input) is { } disk) items.Add(disk);
        if (!string.IsNullOrWhiteSpace(input.AvailableUpdate))
        {
            items.Add(new ReadinessItem(ReadinessArea.Update, ReadinessLevel.Info, "Mise à jour",
                $"La version {input.AvailableUpdate} est disponible."));
        }
        return items;
    }

    /// <summary>« Tout est prêt », « 1 point à vérifier » ou « N points à vérifier ».</summary>
    public static string Summary(IReadOnlyList<ReadinessItem> items)
    {
        var count = items.Count(i => i.NeedsAttention);
        return count switch
        {
            0 => "Tout est prêt",
            1 => "1 point à vérifier",
            _ => $"{count} points à vérifier",
        };
    }

    /// <summary>Gravité de l'ensemble : la plus forte des points à vérifier (les informations ne comptent pas).</summary>
    public static ReadinessLevel Overall(IReadOnlyList<ReadinessItem> items)
    {
        if (items.Any(i => i.Level == ReadinessLevel.Error)) return ReadinessLevel.Error;
        return items.Any(i => i.Level == ReadinessLevel.Warning) ? ReadinessLevel.Warning : ReadinessLevel.Ok;
    }

    // ---------------------------------------------------------------- Appareil photo

    private static ReadinessItem Camera(ReadinessInput input)
    {
        const string title = "Appareil photo";
        if (input.DemoMode)
        {
            return new(ReadinessArea.Camera, ReadinessLevel.Warning, title,
                "Mode démonstration : les invités auront des images d'exemple, pas de vraies photos.");
        }
        if (input.CameraKind == CameraKind.Demo)
        {
            return new(ReadinessArea.Camera, ReadinessLevel.Warning, title,
                "Appareil de démonstration choisi : les invités auront des images d'exemple. Choisissez le Canon pour l'événement.");
        }
        if (input.CanonSdkMissing)
        {
            return new(ReadinessArea.Camera, ReadinessLevel.Error, title,
                "Le SDK Canon manque : ajoutez-le dans la page Appareil photo.");
        }

        var name = string.IsNullOrWhiteSpace(input.CameraName) ? "Appareil" : input.CameraName.Trim();
        switch (input.CameraState)
        {
            case CameraState.Ready:
                return input.BatteryLevel switch
                {
                    -1 => new(ReadinessArea.Camera, ReadinessLevel.Ok, title, $"{name} prêt, sur secteur."),
                    >= 0 and <= LowBattery => new(ReadinessArea.Camera, ReadinessLevel.Warning, title,
                        $"{name} prêt, mais batterie faible ({input.BatteryLevel} %) : branchez l'alimentation secteur ou changez la batterie."),
                    > LowBattery => new(ReadinessArea.Camera, ReadinessLevel.Ok, title, $"{name} prêt, batterie à {input.BatteryLevel} %."),
                    _ => new(ReadinessArea.Camera, ReadinessLevel.Ok, title, $"{name} prêt."),
                };
            case CameraState.Connecting:
                return new(ReadinessArea.Camera, ReadinessLevel.Info, title, "Connexion à l'appareil en cours…");
            case CameraState.Disconnected:
                return new(ReadinessArea.Camera, ReadinessLevel.Error, title,
                    "Aucun appareil connecté : branchez le Canon en USB et allumez-le.");
            default:
                var message = string.IsNullOrWhiteSpace(input.CameraMessage) ? "Erreur de l'appareil." : input.CameraMessage.Trim();
                return new(ReadinessArea.Camera, ReadinessLevel.Error, title, message);
        }
    }

    // ---------------------------------------------------------------- Imprimante et papier

    private static IEnumerable<ReadinessItem> PrinterAndPaper(ReadinessInput input)
    {
        const string title = "Imprimante";
        if (!input.PrintEnabled)
        {
            yield return new(ReadinessArea.Printer, ReadinessLevel.Info, title, "Impression désactivée : les invités repartent sans tirage.");
            yield break;
        }
        if (input.PrintToPdf)
        {
            yield return new(ReadinessArea.Printer, ReadinessLevel.Info, title, "Mode essai : les impressions sont enregistrées en PDF.");
            yield break;
        }
        if (string.IsNullOrWhiteSpace(input.PrinterName))
        {
            yield return new(ReadinessArea.Printer, ReadinessLevel.Error, title, "Aucune imprimante choisie : choisissez la DNP dans la page Impression.");
            yield break;
        }
        if (!input.PrinterChecked)
        {
            yield return new(ReadinessArea.Printer, ReadinessLevel.Info, title, "Vérification de l'imprimante…");
            yield break;
        }

        var message = string.IsNullOrWhiteSpace(input.PrinterMessage) ? "" : input.PrinterMessage.Trim();
        if (!input.PrinterCanPrint)
        {
            yield return new(ReadinessArea.Printer, ReadinessLevel.Error, title, message.Length > 0 ? message : $"{input.PrinterName} ne peut pas imprimer.");
        }
        else if (input.PrinterCondition == PrinterCondition.Warning)
        {
            yield return new(ReadinessArea.Printer, ReadinessLevel.Warning, title, message.Length > 0 ? message : $"{input.PrinterName} signale un problème.");
        }
        else
        {
            yield return new(ReadinessArea.Printer, ReadinessLevel.Ok, title, message.Length > 0 ? $"{input.PrinterName} : {message}" : $"{input.PrinterName} prête.");
        }

        const string paper = "Papier";
        if (input.RemainingPrints is not { } remaining)
        {
            yield return new(ReadinessArea.Paper, ReadinessLevel.Info, paper, "Nombre de tirages restants inconnu.");
        }
        else if (remaining <= 0)
        {
            yield return new(ReadinessArea.Paper, ReadinessLevel.Error, paper, "Plus aucun tirage restant : changez le rouleau.");
        }
        else if (input.LowMedia)
        {
            yield return new(ReadinessArea.Paper, ReadinessLevel.Warning, paper, $"{Prints(remaining)} : prévoyez un rouleau de rechange.");
        }
        else
        {
            yield return new(ReadinessArea.Paper, ReadinessLevel.Ok, paper, $"{Prints(remaining)}.");
        }
    }

    private static string Prints(int remaining) => remaining <= 1 ? $"{remaining} tirage restant" : $"{remaining} tirages restants";

    // ---------------------------------------------------------------- Formats, limite, disque

    private static ReadinessItem Formats(ReadinessInput input)
    {
        const string title = "Formats proposés";
        var formats = input.Formats.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList();
        return formats.Count == 0
            ? new(ReadinessArea.Formats, ReadinessLevel.Error, title,
                "Aucun format proposé aux invités : cochez-en au moins un dans la page Personnalisation.")
            : new(ReadinessArea.Formats, ReadinessLevel.Ok, title, string.Join(", ", formats) + ".");
    }

    private static ReadinessItem? PrintLimit(ReadinessInput input)
    {
        if (!input.PrintEnabled || input.EventPrintLimit <= 0) return null;
        const string title = "Limite d'impressions";
        var prints = Math.Max(0, input.EventPrints);
        var limit = input.EventPrintLimit;
        if (prints >= limit)
        {
            return new(ReadinessArea.PrintLimit, ReadinessLevel.Warning, title,
                $"Limite atteinte ({prints} / {limit}) : plus aucune impression pour cet événement.");
        }
        return prints >= limit * PrintLimitWarningShare
            ? new(ReadinessArea.PrintLimit, ReadinessLevel.Warning, title, $"{prints} / {limit} impressions : la limite est presque atteinte.")
            : new(ReadinessArea.PrintLimit, ReadinessLevel.Ok, title, $"{prints} / {limit} impressions.");
    }

    /// <summary>Les seuils de <see cref="DiskSpace"/> : rouge sous 300 Mo (plus de session), orange sous 2 Go.</summary>
    private static ReadinessItem? Disk(ReadinessInput input)
    {
        if (input.FreeDiskBytes is not { } free) return null;
        const string title = "Espace disque";
        var where = string.IsNullOrWhiteSpace(input.DiskName) ? "" : $" (disque {input.DiskName.Trim()})";
        if (!DiskSpace.AllowsSession(free))
        {
            return new(ReadinessArea.Disk, ReadinessLevel.Error, title,
                $"Plus que {Size(free)} libres{where} : le photobooth ne lance plus de session. Libérez de la place.");
        }
        return free < DiskSpace.StartMinimumBytes
            ? new(ReadinessArea.Disk, ReadinessLevel.Warning, title,
                $"{Size(free)} libres{where}, soit {DiskSpace.SessionsText(free)} : gardez au moins 2 Go pour un événement.")
            : new(ReadinessArea.Disk, ReadinessLevel.Ok, title, $"{Size(free)} libres{where}.");
    }

    /// <summary>« 350 Mo », « 1,4 Go », « 120 Go » (espace insécable : le nombre et l'unité restent sur la même ligne).</summary>
    public static string Size(long bytes)
    {
        const double mega = 1024 * 1024;
        const double giga = mega * 1024;
        if (bytes < giga) return $"{Math.Max(0, Math.Floor(bytes / mega)).ToString("0", French)} Mo";
        var gigabytes = bytes / giga;
        return gigabytes < 10
            ? $"{(Math.Floor(gigabytes * 10) / 10).ToString("0.#", French)} Go"
            : $"{Math.Floor(gigabytes).ToString("0", French)} Go";
    }
}
