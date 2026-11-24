using System.Globalization;
using System.Security;

namespace LibreBooth.Core.Admin;

/// <summary>
/// Place libre sur le disque des photos, pour que la soirée ne s'arrête pas sur un disque plein :
/// 2 Go conseillés pour démarrer le photobooth (sinon « Démarrer le photobooth » avertit), et au moins 300 Mo
/// avant chaque session (sinon plus aucune session ne démarre et l'écran d'accueil prévient l'animateur).
/// </summary>
public static class DiskSpace
{
    /// <summary>Conseillé pour démarrer le photobooth (2 Go, environ 170 sessions avant la pause).</summary>
    public const long StartMinimumBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>En dessous, plus aucune session ne démarre (300 Mo : de quoi finir sans risque la session en cours).</summary>
    public const long SessionMinimumBytes = 300L * 1024 * 1024;

    /// <summary>Place prise par une session, en moyenne : les photos de l'appareil et le montage imprimé (10 Mo).</summary>
    public const long BytesPerSession = 10L * 1024 * 1024;

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Assez de place pour lancer une session de plus.</summary>
    public static bool AllowsSession(long freeBytes) => freeBytes >= SessionMinimumBytes;

    /// <summary>
    /// Sessions qui peuvent encore démarrer avant que le photobooth se mette en pause : une session démarre tant qu'il
    /// reste au moins 300 Mo, et chacune en prend environ 10.
    /// </summary>
    public static long EstimatedSessions(long freeBytes) =>
        AllowsSession(freeBytes) ? (freeBytes - SessionMinimumBytes) / BytesPerSession + 1 : 0;

    /// <summary>« environ 170 sessions », « 8 sessions », « aucune session » (arrondi à la dizaine à partir de 20).</summary>
    public static string SessionsText(long freeBytes)
    {
        var sessions = EstimatedSessions(freeBytes);
        if (sessions == 0) return "aucune session";
        if (sessions == 1) return "1 session";
        if (sessions < 20) return $"{sessions} sessions";
        return $"environ {(sessions / 10 * 10).ToString("N0", French)} sessions";
    }

    /// <summary>
    /// Place libre sur le disque d'un dossier, et le nom de ce disque (« C: ») ; null pour un dossier réseau, un disque
    /// absent ou illisible (on ne bloque alors rien).
    /// </summary>
    public static (long FreeBytes, string DriveName)? Probe(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.AvailableFreeSpace, root.TrimEnd('\\', '/')) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            return null;
        }
    }
}
