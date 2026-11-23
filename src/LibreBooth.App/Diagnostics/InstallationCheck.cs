using System.Diagnostics;
using LibreBooth.App.Hardware;
using LibreBooth.App.Hardware.Canon;
using LibreBooth.App.Hardware.Printing;
using LibreBooth.App.Services;
using LibreBooth.Core.Config;
using LibreBooth.Core.Design;
using LibreBooth.Core.Hardware;
using LibreBooth.Core.Layouts;
using LibreBooth.Core.Util;
using Microsoft.Win32;

namespace LibreBooth.App.Diagnostics;

public enum CheckStatus
{
    Ok,
    Info,
    Warning,
    Error,
}

/// <summary>Un point vérifié : ce qui a été trouvé et, en cas de problème, ce qu'il faut faire.</summary>
public sealed record CheckResult(string Category, string Title, CheckStatus Status, string Detail, string? Fix = null);

/// <summary>
/// Vérification de l'installation (LibreBooth.exe --verifier, programme d'installation, ou admin › Système) :
/// fichiers du programme, pilotes Canon et DNP, dossier des données, modèles, imprimante, appareil photo, polices.
/// Rien n'est modifié, rien n'est imprimé.
/// </summary>
public static class InstallationCheck
{
    public const string ProgramCategory = "Programme";
    public const string DataCategory = "Dossier des données";
    public const string PrinterCategory = "Imprimante DNP";
    public const string CameraCategory = "Appareil photo";
    public const string WindowsCategory = "Windows";

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LibreBooth";
    private const string DnpDriverKey = @"SYSTEM\CurrentControlSet\Control\Print\Environments\Windows x64\Drivers\Version-3\DP-DS620";
    private const string InstanceMutexName = "LibreBooth.Kiosk.Instance";

    private static readonly string[] ProgramFiles =
    {
        "LibreBooth.exe", "LibreBooth.dll", "LibreBooth.Core.dll", "SkiaSharp.dll", "libSkiaSharp.dll", "Serilog.dll",
        "CommunityToolkit.Mvvm.dll", "MailKit.dll", "MimeKit.dll", "PresentationFramework.dll", "wpfgfx_cor3.dll",
    };

    private static readonly string[] ThemeFonts = { "Gabriola", "Segoe Script", "Bahnschrift", "Georgia", "Segoe UI" };

    /// <param name="services">LibreBooth ouvert (admin) : l'état de l'appareil et de l'imprimante vient de lui. Sinon (null),
    /// l'appareil est recherché avec le pilote Canon et l'imprimante interrogée directement.</param>
    /// <param name="report">Appelé pour chaque point vérifié, au fil de l'eau.</param>
    public static async Task RunAsync(AppServices? services, AppPaths? paths, Action<CheckResult> report, CancellationToken token = default)
    {
        await Task.Yield();
        CheckProgram(report);
        token.ThrowIfCancellationRequested();

        var settings = CheckData(services, paths, report);
        token.ThrowIfCancellationRequested();

        await CheckPrinterAsync(services, settings, report, token);
        token.ThrowIfCancellationRequested();

        await CheckCameraAsync(services, report, token);
        token.ThrowIfCancellationRequested();

        CheckFonts(report);
    }

    // ---------------------------------------------------------------- Programme

    private static void CheckProgram(Action<CheckResult> report)
    {
        var folder = AppContext.BaseDirectory;
        report(new CheckResult(ProgramCategory, $"LibreBooth {App.Version}", CheckStatus.Ok, $"Dossier du programme : {folder}"));

        var missing = ProgramFiles.Where(f => !File.Exists(Path.Combine(folder, f))).ToList();
        report(missing.Count == 0
            ? new CheckResult(ProgramCategory, "Fichiers du programme", CheckStatus.Ok, $"{ProgramFiles.Length} fichiers essentiels présents.")
            : new CheckResult(ProgramCategory, "Fichiers du programme", CheckStatus.Error, "Fichiers manquants : " + string.Join(", ", missing),
                "Relancez le programme d'installation de LibreBooth pour remettre les fichiers manquants."));

        report(DriverFile("Pilote de l'appareil Canon (SDK EDSDK)", NativeDrivers.CanonFiles, CheckStatus.Error,
            $"Téléchargez le SDK Canon EDSDK pour Windows (gratuit, sur inscription chez Canon : {CanonSdkImport.Page}), puis "
            + "ajoutez-le avec le bouton « Ajouter le SDK Canon… » (ici, ou dans l'admin › Appareil photo) : LibreBooth "
            + "en extrait lui-même les bons fichiers."));
        report(DriverFile("Lecture de l'état de la DNP", new[] { NativeDrivers.DnpStatusFile }, CheckStatus.Info,
            $"Facultatif : si vous avez cspstat.dll (API d'état des imprimantes DNP), copiez-la dans {NativeDrivers.UserFolder}. "
            + "Sans elle, LibreBooth compte lui-même ses tirages."));

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(UninstallKey);
            var version = key?.GetValue("DisplayVersion") as string;
            var location = key?.GetValue("InstallLocation") as string;
            if (key == null)
            {
                report(new CheckResult(ProgramCategory, "Installation dans Windows", CheckStatus.Info,
                    "LibreBooth est utilisé sans programme d'installation (copie du zip) : pas de raccourcis ni de désinstallation dans Windows."));
            }
            else
            {
                var samePlace = location != null && string.Equals(Path.GetFullPath(location).TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                report(new CheckResult(ProgramCategory, "Installation dans Windows",
                    samePlace ? CheckStatus.Ok : CheckStatus.Info,
                    $"Version {version ?? "?"} installée dans {location ?? "?"}"
                    + (samePlace ? "." : " (ce n'est pas la copie de LibreBooth lancée en ce moment).")));
                var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "LibreBooth.lnk");
                var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "LibreBooth", "LibreBooth.lnk");
                report(new CheckResult(ProgramCategory, "Raccourcis", File.Exists(startMenu) ? CheckStatus.Ok : CheckStatus.Warning,
                    $"Menu Démarrer : {(File.Exists(startMenu) ? "présent" : "absent")} · Bureau : {(File.Exists(desktop) ? "présent" : "absent")}",
                    File.Exists(startMenu) ? null : "Relancez le programme d'installation pour recréer les raccourcis."));
            }
        }
        catch (Exception ex)
        {
            report(new CheckResult(ProgramCategory, "Installation dans Windows", CheckStatus.Info, "Registre illisible : " + ex.Message));
        }
    }

    /// <summary>Fichiers d'un fabricant : à côté de LibreBooth.exe ou dans le dossier « Pilotes ».</summary>
    private static CheckResult DriverFile(string title, string[] files, CheckStatus statusIfMissing, string fix)
    {
        var details = new List<string>();
        var missing = new List<string>();
        foreach (var file in files)
        {
            var path = NativeDrivers.Find(file);
            if (path == null)
            {
                missing.Add(file);
                continue;
            }
            var where = string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(NativeDrivers.UserFolder), StringComparison.OrdinalIgnoreCase)
                ? " (dossier Pilotes)"
                : "";
            details.Add($"{file} {FileVersionInfo.GetVersionInfo(path).FileVersion}{where}".Trim());
        }
        return missing.Count == 0
            ? new CheckResult(ProgramCategory, title, CheckStatus.Ok, string.Join(" · ", details))
            : new CheckResult(ProgramCategory, title, statusIfMissing, "Manquant : " + string.Join(", ", missing), fix);
    }

    // ---------------------------------------------------------------- Dossier des données

    private static AppSettings? CheckData(AppServices? services, AppPaths? paths, Action<CheckResult> report)
    {
        try
        {
            paths ??= services?.Paths ?? AppPaths.Create();
        }
        catch (Exception ex)
        {
            report(new CheckResult(DataCategory, "Dossier des données", CheckStatus.Error, ex.Message,
                "Windows refuse l'écriture : vérifiez la protection « Accès contrôlé aux dossiers » de Sécurité Windows."));
            return null;
        }

        var root = paths.DataRoot;
        var probe = Path.Combine(root, $".verification-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "LibreBooth");
            File.Delete(probe);
            var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
            var synced = !string.IsNullOrEmpty(oneDrive) && root.StartsWith(oneDrive, StringComparison.OrdinalIgnoreCase);
            report(new CheckResult(DataCategory, "Dossier des données", synced ? CheckStatus.Warning : CheckStatus.Ok,
                $"{root} (lecture et écriture possibles)",
                synced ? "Ce dossier est synchronisé par OneDrive : toutes les photos partiraient dans le cloud." : null));
        }
        catch (Exception ex)
        {
            report(new CheckResult(DataCategory, "Dossier des données", CheckStatus.Error, $"{root} : écriture impossible ({ex.Message})",
                "Vérifiez que le disque n'est pas plein et que Sécurité Windows ne bloque pas LibreBooth."));
        }

        var missingFolders = new[] { paths.EventsFolder, paths.TemplatesFolder, paths.LogsFolder }.Where(f => !Directory.Exists(f)).ToList();
        report(missingFolders.Count == 0
            ? new CheckResult(DataCategory, "Sous-dossiers", CheckStatus.Ok, "Evenements, Modeles et logs sont en place.")
            : new CheckResult(DataCategory, "Sous-dossiers", CheckStatus.Warning, "Manquants : " + string.Join(", ", missingFolders.Select(Path.GetFileName)),
                "LibreBooth les recrée à son prochain lancement."));

        AppSettings? settings = services?.Settings;
        if (settings == null)
        {
            try
            {
                settings = File.Exists(paths.ConfigFile) ? JsonDefaults.Read<AppSettings>(paths.ConfigFile)?.Normalize() : new AppSettings().Normalize();
                report(new CheckResult(DataCategory, "Réglages", CheckStatus.Ok,
                    File.Exists(paths.ConfigFile) ? "config.json est lisible." : "Pas encore de réglages : ils seront créés au premier lancement."));
            }
            catch (Exception ex)
            {
                report(new CheckResult(DataCategory, "Réglages", CheckStatus.Warning, "config.json illisible : " + ex.Message,
                    "Au prochain lancement, LibreBooth le met de côté et repart des réglages par défaut."));
            }
        }

        try
        {
            var templates = new TemplateLibrary(paths.TemplatesFolder).LoadAll();
            var defaults = TemplateSeeder.DefaultTemplateIds.Where(id => templates.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))).Count();
            report(defaults == TemplateSeeder.DefaultTemplateIds.Count
                ? new CheckResult(DataCategory, "Modèles", CheckStatus.Ok, $"{templates.Count} modèle(s), dont le 10x15 et le marque-page de LibreBooth.")
                : new CheckResult(DataCategory, "Modèles", CheckStatus.Info, $"{templates.Count} modèle(s) : les modèles de LibreBooth seront créés au prochain lancement."));
        }
        catch (Exception ex)
        {
            report(new CheckResult(DataCategory, "Modèles", CheckStatus.Warning, "Lecture impossible : " + ex.Message));
        }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
            var status = freeGb < 0.3 ? CheckStatus.Error : freeGb < 2 ? CheckStatus.Warning : CheckStatus.Ok;
            report(new CheckResult(DataCategory, "Espace disque", status, $"{freeGb:0.0} Go libres sur {drive.Name}",
                status == CheckStatus.Ok ? null : "Libérez de la place : chaque photo prend 3 à 8 Mo."));
        }
        catch (Exception ex)
        {
            report(new CheckResult(DataCategory, "Espace disque", CheckStatus.Info, ex.Message));
        }
        return settings;
    }

    // ---------------------------------------------------------------- Imprimante DNP

    private static async Task CheckPrinterAsync(AppServices? services, AppSettings? settings, Action<CheckResult> report, CancellationToken token)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var driver = hklm.OpenSubKey(DnpDriverKey);
            report(driver != null
                ? new CheckResult(PrinterCategory, "Pilote Windows de la DS620", CheckStatus.Ok, "Installé.")
                : new CheckResult(PrinterCategory, "Pilote Windows de la DS620", CheckStatus.Error, "Non installé.",
                    "Installez le pilote Windows de la DS620 depuis la page Téléchargements de DNP (dnpphoto.eu), puis branchez l'imprimante en USB."));
        }
        catch (Exception ex)
        {
            report(new CheckResult(PrinterCategory, "Pilote Windows de la DS620", CheckStatus.Info, "Registre illisible : " + ex.Message));
        }

        var printers = new WindowsPrinterService();
        var dnpQueues = await Task.Run(() => printers.GetInstalledPrinters()
            .Select(name => (Name: name, Queue: printers.GetQueueInfo(name)))
            .Where(p => DriverProfileBuilder.LooksLikeDnp(p.Name, p.Queue.DriverName))
            .ToList(), token);
        report(dnpQueues.Count > 0
            ? new CheckResult(PrinterCategory, "Imprimante dans Windows", CheckStatus.Ok,
                string.Join(" · ", dnpQueues.Select(p => $"{p.Name} : {p.Queue.Message}")))
            : new CheckResult(PrinterCategory, "Imprimante dans Windows", CheckStatus.Error, "Aucune imprimante DNP n'est installée dans Windows.",
                "Branchez la DS620 en USB et allumez-la : Windows l'installe avec son pilote."));

        if (settings != null)
        {
            var chosen = settings.Print.PrinterName;
            if (!settings.Print.Enabled)
            {
                report(new CheckResult(PrinterCategory, "Imprimante choisie dans LibreBooth", CheckStatus.Info, "L'impression est désactivée (admin › Impression)."));
            }
            else if (string.IsNullOrWhiteSpace(chosen))
            {
                report(new CheckResult(PrinterCategory, "Imprimante choisie dans LibreBooth", CheckStatus.Warning, "Aucune imprimante choisie.",
                    "LibreBooth choisit la DS620 tout seul à son lancement si elle est branchée ; sinon : admin › Impression."));
            }
            else
            {
                var known = dnpQueues.Any(p => string.Equals(p.Name, chosen, StringComparison.OrdinalIgnoreCase))
                            || printers.GetInstalledPrinters().Contains(chosen, StringComparer.OrdinalIgnoreCase);
                report(new CheckResult(PrinterCategory, "Imprimante choisie dans LibreBooth", known ? CheckStatus.Ok : CheckStatus.Error,
                    chosen + (known ? "" : " : introuvable dans Windows"),
                    known ? null : "Admin › Impression : choisissez l'imprimante dans la liste."));
            }

            var profiles = new[] { PrintProfiles.Postcard, PrintProfiles.Strips }
                .Select(name => (Name: name, Bytes: Decode(settings.Print.DriverSettings.GetValueOrDefault(name))))
                .ToList();
            var missing = profiles.Where(p => p.Bytes == null).Select(p => p.Name).ToList();
            var outdated = profiles.Where(p => p.Bytes != null && DnpPrivateSettings.NeedsSync(p.Bytes)).Select(p => p.Name).ToList();
            report(missing.Count == 0 && outdated.Count == 0
                ? new CheckResult(PrinterCategory, "Réglages 10x15 et marque-page", CheckStatus.Ok, "Préparés, coupe 2 pouces du marque-page comprise.")
                : new CheckResult(PrinterCategory, "Réglages 10x15 et marque-page", CheckStatus.Warning,
                    missing.Count > 0 ? "Pas encore préparés : " + string.Join(", ", missing) : "La coupe 2 pouces n'est pas transmise : " + string.Join(", ", outdated),
                    "Admin › Impression › « Réglages automatiques DNP » (LibreBooth le fait aussi tout seul à son lancement)."));
        }

        PrinterStatusInfo status;
        if (services != null)
        {
            var health = services.PrinterMonitor.Current;
            status = new PrinterStatusInfo(health.Condition, health.Message, health.RemainingPrints, "", health.CheckedAt);
        }
        else
        {
            status = await new DnpStatusProvider().QueryAsync(token);
        }
        var result = status.Condition switch
        {
            PrinterCondition.Ready or PrinterCondition.Busy => CheckStatus.Ok,
            PrinterCondition.Warning => CheckStatus.Warning,
            PrinterCondition.Error => CheckStatus.Error,
            _ => CheckStatus.Warning,
        };
        report(new CheckResult(PrinterCategory, "État de la DS620", result,
            status.Message + (status.RemainingPrints is { } left ? $" · {left} tirages restants" : ""),
            result == CheckStatus.Ok ? null : "Vérifiez que la DS620 est allumée, branchée en USB, avec papier et ruban, capot fermé."));
    }

    private static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- Appareil photo

    private static async Task CheckCameraAsync(AppServices? services, Action<CheckResult> report, CancellationToken token)
    {
        if (services != null)
        {
            var camera = services.Camera;
            report(new CheckResult(CameraCategory, camera.Name, camera.State == CameraState.Ready ? CheckStatus.Ok : CheckStatus.Warning,
                camera.StatusMessage,
                camera.State == CameraState.Ready ? null : "Branchez l'appareil en USB (sans hub), allumez-le, Wi-Fi coupé, EOS Utility fermé."));
            return;
        }

        if (IsLibreBoothRunning())
        {
            report(new CheckResult(CameraCategory, "Appareil Canon", CheckStatus.Info,
                "LibreBooth est ouvert : il utilise déjà l'appareil. Pour le tester ici, fermez LibreBooth et relancez la vérification."));
            return;
        }

        using var canon = new CanonCamera(() => new CameraSettings());
        try
        {
            canon.Start();
            // Le pilote Canon cherche l'appareil toutes les 2 secondes : on lui laisse 12 secondes.
            var deadline = DateTime.Now.AddSeconds(12);
            while (DateTime.Now < deadline && canon.State != CameraState.Ready && canon.State != CameraState.Error)
            {
                await Task.Delay(250, token);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            report(new CheckResult(CameraCategory, "Pilote Canon", CheckStatus.Error, ex.Message,
                "Relancez le programme d'installation, puis vérifiez que l'appareil est branché."));
            return;
        }

        if (canon.State == CameraState.Ready)
        {
            var power = canon.BatteryLevel switch
            {
                null => "",
                -1 => " · alimentation secteur",
                var level => $" · batterie {level} %",
            };
            report(new CheckResult(CameraCategory, canon.Name, CheckStatus.Ok, "Détecté et prêt" + power + "."));
        }
        else
        {
            var sdkMissing = NativeDrivers.CanonFiles.Any(f => NativeDrivers.Find(f) == null);
            report(new CheckResult(CameraCategory, "Appareil Canon", CheckStatus.Warning, canon.StatusMessage,
                sdkMissing
                    ? "Ajoutez d'abord le SDK Canon (voir « Pilote de l'appareil Canon » plus haut)."
                    : "Branchez l'appareil en USB (sans hub) et allumez-le ; Wi-Fi de l'appareil coupé, EOS Utility fermé."));
        }
    }

    private static bool IsLibreBoothRunning()
    {
        try
        {
            using var mutex = Mutex.OpenExisting(InstanceMutexName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- Windows

    private static void CheckFonts(Action<CheckResult> report)
    {
        var missing = ThemeFonts.Where(f => !DesignFonts.IsInstalled(f)).ToList();
        report(missing.Count == 0
            ? new CheckResult(WindowsCategory, "Polices des thèmes", CheckStatus.Ok, string.Join(", ", ThemeFonts) + " : installées.")
            : new CheckResult(WindowsCategory, "Polices des thèmes", CheckStatus.Warning, "Absentes : " + string.Join(", ", missing),
                "Les thèmes qui les utilisent prennent une police standard à la place."));
    }

    /// <summary>Le rapport en texte (enregistré depuis la fenêtre de vérification).</summary>
    public static IEnumerable<string> ToReport(IEnumerable<CheckResult> results)
    {
        yield return $"Vérification de LibreBooth {App.Version} – {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
        yield return $"Windows {Environment.OSVersion.Version} · PC {Environment.MachineName}";
        foreach (var group in results.GroupBy(r => r.Category))
        {
            yield return "";
            yield return "== " + group.Key + " ==";
            foreach (var result in group)
            {
                var mark = result.Status switch
                {
                    CheckStatus.Ok => "[OK]",
                    CheckStatus.Info => "[i] ",
                    CheckStatus.Warning => "[!] ",
                    _ => "[X] ",
                };
                yield return $"{mark} {result.Title} : {result.Detail}";
                if (result.Fix != null) yield return "     → " + result.Fix;
            }
        }
    }
}
