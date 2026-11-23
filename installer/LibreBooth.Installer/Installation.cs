using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace LibreBooth.Installer;

internal sealed class InstallOptions
{
    public string InstallDir { get; set; } = Installation.DefaultInstallDir;
    public bool InstallDriver { get; set; } = true;
    public bool DesktopShortcut { get; set; } = true;

    /// <summary>Essai sans droits d'administrateur : ni registre, ni pilote ; raccourcis dans un sous-dossier.</summary>
    public bool TestMode { get; set; }
}

/// <summary>
/// Installation et désinstallation de LibreBooth. Seuls les fichiers installés (listés dans fichiers-installes.txt)
/// sont supprimés : les photos, événements et réglages (C:\Users\&lt;nom&gt;\LibreBooth) ne sont jamais touchés.
/// </summary>
internal static class Installation
{
    public const string ExeName = "LibreBooth.exe";
    public const string UninstallerName = "Desinstaller-LibreBooth.exe";
    public const string ManifestName = "fichiers-installes.txt";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LibreBooth";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DriverKey = @"SYSTEM\CurrentControlSet\Control\Print\Environments\Windows x64\Drivers\Version-3\DP-DS620";
    private const string AppShortcut = "LibreBooth.lnk";
    private const string DiagnosticShortcut = "Diagnostic du matériel LibreBooth.lnk";
    private const string UninstallShortcut = "Désinstaller LibreBooth.lnk";
    private const string VerifyShortcut = "Vérifier l'installation de LibreBooth.lnk";
    /// <summary>Auteur de LibreBooth, affiché comme éditeur dans Windows (Paramètres › Applications).</summary>
    public const string Author = "Loïs Gonçalves";

    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreBooth");

    public static string LogFile => Path.Combine(Path.GetTempPath(), "LibreBooth-installation.log");

    public static string Version
    {
        get
        {
            var version = System.Windows.Forms.Application.ProductVersion;
            var plus = version.IndexOf('+');
            return plus > 0 ? version.Substring(0, plus) : version;
        }
    }

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // Journal facultatif.
        }
    }

    public static bool IsDriverInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DriverKey);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsLibreBoothRunning() => Process.GetProcessesByName("LibreBooth").Length > 0;

    /// <summary>Le dossier choisi reçoit toujours un sous-dossier dédié « LibreBooth » (jamais la racine d'un disque).</summary>
    public static string NormalizeInstallDir(string folder)
    {
        var full = Path.GetFullPath(folder.Trim()).TrimEnd('\\');
        if (!string.Equals(Path.GetFileName(full), "LibreBooth", StringComparison.OrdinalIgnoreCase)) full = Path.Combine(full, "LibreBooth");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (full.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("LibreBooth ne peut pas être installé dans le dossier de Windows.");
        }
        return full;
    }

    // ================================================================ Installation

    public static void Install(Payload payload, InstallOptions options, Action<int, string> progress)
    {
        var dir = options.InstallDir;
        Log($"Installation de LibreBooth {Version} dans {dir} (pilote : {options.InstallDriver}, bureau : {options.DesktopShortcut}, essai : {options.TestMode})");
        Directory.CreateDirectory(dir);

        progress(2, "Suppression de l'ancienne version…");
        RemoveListedFiles(dir);

        var entries = payload.Archive.Entries
            .Where(e => Normalize(e.FullName).StartsWith(Payload.AppFolder, StringComparison.Ordinal) && !Normalize(e.FullName).EndsWith("/", StringComparison.Ordinal))
            .ToList();
        if (entries.Count == 0) throw new InvalidDataException("Le contenu de l'installation est vide.");

        var installed = new List<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            var relative = Normalize(entries[i].FullName).Substring(Payload.AppFolder.Length);
            var destination = SafeCombine(dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var input = entries[i].Open())
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write))
            {
                input.CopyTo(output);
            }
            installed.Add(relative.Replace('/', '\\'));
            if (i % 10 == 0) progress(5 + 70 * i / entries.Count, $"Copie de LibreBooth… ({i + 1}/{entries.Count})");
        }

        progress(76, "Programme de désinstallation…");
        var uninstaller = Path.Combine(dir, UninstallerName);
        payload.CopyExecutableTo(uninstaller);
        File.WriteAllLines(Path.Combine(dir, ManifestName), installed, Encoding.UTF8);
        Log($"{installed.Count} fichiers copiés");

        progress(80, "Raccourcis…");
        var exe = Path.Combine(dir, ExeName);
        var startMenu = StartMenuFolder(options.TestMode, dir);
        Directory.CreateDirectory(startMenu);
        CreateShortcut(Path.Combine(startMenu, AppShortcut), exe, "", "Logiciel du photobooth");
        CreateShortcut(Path.Combine(startMenu, VerifyShortcut), exe, "--verifier", "Vérifie que tout est en place : dossiers, pilotes, imprimante, appareil photo");
        CreateShortcut(Path.Combine(startMenu, DiagnosticShortcut), exe, "--diagnostic", "Vérifie l'appareil photo et l'imprimante (LibreBooth doit être fermé)");
        CreateShortcut(Path.Combine(startMenu, UninstallShortcut), uninstaller, "/desinstaller", "Désinstaller LibreBooth (vos photos sont conservées)");
        var desktop = Path.Combine(DesktopFolder(options.TestMode, dir), AppShortcut);
        if (options.DesktopShortcut) CreateShortcut(desktop, exe, "", "Logiciel du photobooth");
        else TryDelete(desktop);

        if (!options.TestMode)
        {
            progress(84, "Inscription dans Windows…");
            RegisterUninstall(dir, uninstaller, exe, installed);
        }

        if (options.InstallDriver && !options.TestMode && payload.HasDriver)
        {
            progress(88, "Installation du pilote de l'imprimante DNP DS620…");
            InstallDriver(payload);
        }
        progress(100, "Installation terminée.");
        Log("Installation terminée");
    }

    private static void RegisterUninstall(string dir, string uninstaller, string exe, List<string> installed)
    {
        var size = installed.Select(f => SafeCombine(dir, f)).Where(File.Exists).Sum(f => new FileInfo(f).Length);
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey) ?? throw new InvalidOperationException("Registre inaccessible.");
        key.SetValue("DisplayName", "LibreBooth");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", Author);
        key.SetValue("InstallLocation", dir);
        key.SetValue("DisplayIcon", exe);
        key.SetValue("UninstallString", $"\"{uninstaller}\" /desinstaller");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>Installe le pilote DNP avec l'outil fourni par DNP (dpinst64), sans ses pages d'assistant.</summary>
    private static void InstallDriver(Payload payload)
    {
        var temp = Path.Combine(Path.GetTempPath(), "LibreBooth-pilote-DNP-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            foreach (var entry in payload.Archive.Entries)
            {
                var name = Normalize(entry.FullName);
                if (!name.StartsWith(Payload.DriverFolder, StringComparison.Ordinal) || name.EndsWith("/", StringComparison.Ordinal)) continue;
                var destination = SafeCombine(temp, name.Substring(Payload.DriverFolder.Length));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.Create, FileAccess.Write);
                input.CopyTo(output);
            }

            var installer = Path.Combine(temp, "dpinst64.exe");
            if (!File.Exists(installer)) throw new FileNotFoundException("Programme d'installation du pilote DNP introuvable.");
            using var process = Process.Start(new ProcessStartInfo(installer, "/SW")
            {
                WorkingDirectory = temp,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("Le programme d'installation du pilote DNP n'a pas démarré.");
            process.WaitForExit();

            // Code de retour de dpinst : 0xWWXXYYZZ (WW : 0x40 = échec, XX : paquets en échec,
            // YY : paquets copiés dans le magasin de pilotes, ZZ : paquets installés sur un appareil branché).
            var code = (uint)process.ExitCode;
            var failed = (code & 0x40000000) != 0 || ((code >> 16) & 0xFF) > 0;
            Log($"dpinst64 : code 0x{code:X8}");
            if (failed) throw new InvalidOperationException($"Le pilote DNP n'a pas pu être installé (code 0x{code:X8}).");
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch
            {
                // Dossier temporaire : Windows le nettoiera.
            }
        }
    }

    // ================================================================ Désinstallation

    public static void Uninstall(string dir, bool testMode)
    {
        Log($"Désinstallation de LibreBooth dans {dir} (essai : {testMode})");
        var startMenu = StartMenuFolder(testMode, dir);
        foreach (var name in new[] { AppShortcut, VerifyShortcut, DiagnosticShortcut, UninstallShortcut }) TryDelete(Path.Combine(startMenu, name));
        TryDeleteFolder(startMenu);
        TryDelete(Path.Combine(DesktopFolder(testMode, dir), AppShortcut));

        RemoveListedFiles(dir);
        RemoveEmptyFolders(dir);

        // Lancement au démarrage de Windows (réglé dans l'admin de LibreBooth), s'il pointe vers ce dossier.
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue("LibreBooth") is string value && value.IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                run.DeleteValue("LibreBooth", throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log("Lancement au démarrage non retiré : " + ex.Message);
        }

        if (!testMode)
        {
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                Log("Inscription Windows non retirée : " + ex.Message);
            }
        }

        // Le programme de désinstallation ne peut pas s'effacer lui-même : Windows le fait juste après sa fermeture.
        var self = Path.Combine(dir, UninstallerName);
        var command = $"/c ping 127.0.0.1 -n 3 > nul & del /f /q \"{self}\" & rmdir \"{dir}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", command) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        Log("Désinstallation terminée");
    }

    /// <summary>Supprime les fichiers listés par l'installation précédente (et rien d'autre).</summary>
    private static void RemoveListedFiles(string dir)
    {
        var manifest = Path.Combine(dir, ManifestName);
        if (!File.Exists(manifest)) return;
        foreach (var line in File.ReadAllLines(manifest))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string path;
            try
            {
                path = SafeCombine(dir, line.Trim());
            }
            catch (InvalidDataException)
            {
                continue;
            }
            TryDelete(path);
        }
        TryDelete(manifest);
    }

    private static void RemoveEmptyFolders(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var folder in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(f => f.Length))
        {
            TryDeleteFolder(folder);
        }
    }

    // ================================================================ Vérification

    /// <summary>Raccourci « Vérifier l'installation de LibreBooth » d'une installation (null s'il n'existe pas).</summary>
    public static string? VerifyShortcutPath(bool testMode, string dir)
    {
        var path = Path.Combine(StartMenuFolder(testMode, dir), VerifyShortcut);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Lance la vérification de LibreBooth par l'Explorateur (comme un double-clic sur le raccourci) : elle tourne
    /// avec les droits de la personne connectée, pas avec ceux de l'installation.
    /// </summary>
    public static bool LaunchVerification(bool testMode, string dir)
    {
        var shortcut = VerifyShortcutPath(testMode, dir);
        if (shortcut == null) return false;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{shortcut}\"") { UseShellExecute = true });
            Log("Vérification lancée : " + shortcut);
            return true;
        }
        catch (Exception ex)
        {
            Log("Vérification impossible à lancer : " + ex.Message);
            return false;
        }
    }

    // ================================================================ Outils

    private static string StartMenuFolder(bool testMode, string dir) => testMode
        ? Path.Combine(dir, "_raccourcis-test", "Menu Démarrer")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "LibreBooth");

    private static string DesktopFolder(bool testMode, string dir) => testMode
        ? Path.Combine(dir, "_raccourcis-test", "Bureau")
        : Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    private static void CreateShortcut(string path, string target, string arguments, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Création de raccourcis impossible sur ce PC.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.Arguments = arguments;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = description;
            link.IconLocation = target + ",0";
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string Normalize(string entryName) => entryName.Replace('\\', '/');

    /// <summary>Chemin dans le dossier donné, sans jamais pouvoir en sortir (« ..\ » refusé).</summary>
    private static string SafeCombine(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', '\\')));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Chemin refusé : " + relative);
        return full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log($"Suppression impossible de {path} : {ex.Message}");
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch
        {
            // Dossier encore utilisé : on le laisse.
        }
    }
}
