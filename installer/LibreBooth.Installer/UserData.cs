using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace LibreBooth.Installer;

/// <summary>Ce que contiennent les dossiers de données trouvés (pour la fenêtre de désinstallation).</summary>
internal sealed class UserDataSummary
{
    public List<string> Roots { get; } = new();
    public int Events { get; set; }
    public int Photos { get; set; }
    public long PhotoBytes { get; set; }
}

/// <summary>
/// Les données de LibreBooth dans le profil Windows (C:\Users\&lt;nom&gt;\LibreBooth, et LibreBooth-demo du mode démo).
/// La désinstallation ne les touche que si l'utilisateur coche les cases : d'abord les réglages, modèles,
/// personnalisations, pilotes ajoutés et journaux ; ensuite, à part, les photos des événements.
/// Rien n'est effacé d'un coup : tout part dans la Corbeille de Windows.
/// </summary>
internal static class UserData
{
    public const string EventsFolderName = "Evenements";
    private static readonly string[] AppFolders = { "logs", "Modeles", "Pilotes" };
    private static readonly string[] AppFilePatterns = { "config.json", "config.json.illisible-*", "diagnostic-*.txt", ".essai-ecriture-*" };
    /// <summary>Dans chaque événement : le dossier caché (2.0) et ce qui servait à LibreBooth dans les versions 1.x.</summary>
    private static readonly string[] EventAppFolders = { ".librebooth", "personnalisation", "outbox", "envoyes", "echecs" };
    private static readonly string[] AppMarkers = { "config.json", EventsFolderName, "Modeles", "Pilotes", "logs" };
    /// <summary>Dossiers de LibreBooth dans le dossier temporaire de Windows.</summary>
    public static readonly string[] TempFolders = { "LibreBooth-apercus", "LibreBooth-mise-a-jour" };

    /// <summary>
    /// Dossiers de données, cherchés comme LibreBooth les cherche au lancement : C:\Users\&lt;nom&gt;\LibreBooth, puis
    /// Documents\LibreBooth et AppData\Local\LibreBooth s'ils contiennent des réglages ; de même pour LibreBooth-demo.
    /// </summary>
    /// <param name="overrideRoot">Dossier imposé (essais de la désinstallation), à la place de ceux du profil.</param>
    public static List<string> FindRoots(string? overrideRoot)
    {
        if (overrideRoot != null)
        {
            // Seulement un vrai dossier de données de LibreBooth (réglages ou événements), jamais un dossier quelconque.
            var full = Path.GetFullPath(overrideRoot).TrimEnd('\\');
            var isData = File.Exists(Path.Combine(full, "config.json")) || Directory.Exists(Path.Combine(full, EventsFolderName));
            return isData ? new List<string> { full } : new List<string>();
        }

        var roots = new List<string>();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var name in new[] { "LibreBooth", "LibreBooth-demo" })
        {
            AddRoot(roots, profile, name, requireSettings: false);
            AddRoot(roots, documents, name, requireSettings: true);
            AddRoot(roots, local, name, requireSettings: true);
        }
        return roots;
    }

    private static void AddRoot(List<string> roots, string parent, string name, bool requireSettings)
    {
        if (string.IsNullOrEmpty(parent)) return;
        var root = Path.Combine(parent, name);
        if (!Directory.Exists(root) || roots.Any(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase))) return;
        var looksLikeData = requireSettings
            ? File.Exists(Path.Combine(root, "config.json"))
            : AppMarkers.Any(m => File.Exists(Path.Combine(root, m)) || Directory.Exists(Path.Combine(root, m)));
        if (looksLikeData) roots.Add(root);
    }

    public static UserDataSummary Summarize(IEnumerable<string> roots)
    {
        var summary = new UserDataSummary();
        foreach (var root in roots)
        {
            summary.Roots.Add(root);
            var events = Path.Combine(root, EventsFolderName);
            if (!Directory.Exists(events)) continue;
            foreach (var ev in SafeDirectories(events))
            {
                summary.Events++;
                foreach (var file in PhotoFiles(ev))
                {
                    summary.Photos++;
                    try
                    {
                        summary.PhotoBytes += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // Fichier disparu entre-temps.
                    }
                }
            }
        }
        return summary;
    }

    /// <summary>Photos, montages et impressions d'essai d'un événement (sans ses fichiers internes).</summary>
    private static IEnumerable<string> PhotoFiles(string eventFolder)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(eventFolder, "*", SearchOption.AllDirectories).ToList();
        }
        catch
        {
            yield break;
        }
        foreach (var file in files)
        {
            var relative = file.Substring(eventFolder.Length).TrimStart('\\');
            var first = relative.Split('\\')[0];
            if (EventAppFolders.Contains(first, StringComparer.OrdinalIgnoreCase)) continue;
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is ".jpg" or ".jpeg" or ".png" or ".pdf") yield return file;
        }
    }

    /// <summary>Réglages, modèles, pilotes ajoutés, journaux et personnalisations des événements ; les photos restent.</summary>
    /// <param name="includeTemp">Nettoie aussi les dossiers de LibreBooth dans le dossier temporaire de Windows.</param>
    /// <returns>Ce qui n'a pas pu être mis à la Corbeille.</returns>
    public static List<string> RemoveAppData(IEnumerable<string> roots, bool interactive, bool includeTemp)
    {
        var failures = new List<string>();
        foreach (var root in roots)
        {
            foreach (var pattern in AppFilePatterns)
            {
                foreach (var file in SafeFiles(root, pattern)) Recycle(file, interactive, failures);
            }
            foreach (var name in AppFolders) Recycle(Path.Combine(root, name), interactive, failures);

            var events = Path.Combine(root, EventsFolderName);
            if (Directory.Exists(events))
            {
                foreach (var ev in SafeDirectories(events))
                {
                    foreach (var name in EventAppFolders) Recycle(Path.Combine(ev, name), interactive, failures);
                    Recycle(Path.Combine(ev, "event.json"), interactive, failures);
                    // Versions 1.x : les fiches des sessions étaient à côté des montages.
                    foreach (var json in SafeFiles(Path.Combine(ev, "montages"), "*.json")) Recycle(json, interactive, failures);
                    RemoveEmptyFolders(ev);
                }
                RemoveEmptyFolders(events);
            }
            RemoveEmptyFolders(root);
        }
        if (includeTemp)
        {
            // Aperçus préparés pendant le renommage d'un événement, et mises à jour téléchargées.
            foreach (var name in TempFolders) Recycle(Path.Combine(Path.GetTempPath(), name), interactive, failures);
        }
        return failures;
    }

    /// <summary>Les photos de tous les événements (dossier Evenements entier).</summary>
    /// <returns>Ce qui n'a pas pu être mis à la Corbeille.</returns>
    public static List<string> RemoveEvents(IEnumerable<string> roots, bool interactive)
    {
        var failures = new List<string>();
        foreach (var root in roots)
        {
            Recycle(Path.Combine(root, EventsFolderName), interactive, failures);
            RemoveEmptyFolders(root);
        }
        return failures;
    }

    public static string FormatSize(long bytes)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", culture) + " Go";
        if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0", culture) + " Mo";
        return Math.Max(1, bytes / 1024).ToString(culture) + " Ko";
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try
        {
            return Directory.GetDirectories(folder);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeFiles(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Supprime les dossiers vides (de l'intérieur vers l'extérieur), puis le dossier lui-même s'il est vide.</summary>
    private static void RemoveEmptyFolders(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var sub in SafeDirectories(folder)) RemoveEmptyFolders(sub);
        try
        {
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch
        {
            // Dossier encore utilisé : on le laisse.
        }
    }

    // ================================================================ Corbeille de Windows

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation64
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct ShFileOperation32
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation64(ref ShFileOperation64 operation);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation32(ref ShFileOperation32 operation);

    /// <summary>
    /// Met un fichier ou un dossier à la Corbeille. Si Windows ne peut pas l'y mettre (trop gros), il demande
    /// avant de supprimer définitivement (fenêtre de Windows) ; sans fenêtre, il supprime.
    /// </summary>
    private static void Recycle(string path, bool interactive, List<string> failures)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        var flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi);
        if (interactive) flags |= FofWantNukeWarning;
        int result;
        bool aborted;
        if (IntPtr.Size == 8)
        {
            var operation = new ShFileOperation64 { Func = FoDelete, From = path + "\0\0", Flags = flags };
            result = SHFileOperation64(ref operation);
            aborted = operation.AnyOperationsAborted != 0;
        }
        else
        {
            var operation = new ShFileOperation32 { Func = FoDelete, From = path + "\0\0", Flags = flags };
            result = SHFileOperation32(ref operation);
            aborted = operation.AnyOperationsAborted != 0;
        }

        var gone = !File.Exists(path) && !Directory.Exists(path);
        Installation.Log(gone ? $"Mis à la Corbeille : {path}" : $"Non supprimé ({result}{(aborted ? ", annulé" : "")}) : {path}");
        if (!gone) failures.Add(path);
    }
}
