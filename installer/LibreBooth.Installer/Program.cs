using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Windows.Forms;

namespace LibreBooth.Installer;

/// <summary>
/// LibreBooth-Installation.exe : installe LibreBooth et le pilote DNP. Copié dans le dossier de LibreBooth sous le nom
/// Desinstaller-LibreBooth.exe, le même programme (sans le contenu) sert à désinstaller.
/// Options : /desinstaller, /silencieux (sans fenêtre), /test (essai sans droits d'administrateur), /dossier &lt;chemin&gt;,
/// /mise-a-jour (lancé par l'admin de LibreBooth : attend sa fermeture, puis remplace la version installée).
/// Désinstallation : /tout-supprimer (réglages, modèles, personnalisations, pilotes ajoutés, journaux),
/// /avec-photos (en plus, les photos de tous les événements), /donnees &lt;dossier&gt; (dossier de données à la place
/// de ceux du profil Windows, pour les essais). Essais de la fenêtre : /apercu &lt;image.png&gt;, et /ecran 1024x600 pour
/// simuler un petit écran.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var uninstall = HasFlag(args, "/desinstaller");
        var update = HasFlag(args, "/mise-a-jour");
        var silent = HasFlag(args, "/silencieux");
        var test = HasFlag(args, "/test");
        var folder = ValueOf(args, "/dossier");
        var preview = ValueOf(args, "/apercu");
        var dataFolder = ValueOf(args, "/donnees");
        var withPhotos = HasFlag(args, "/avec-photos");
        // Essai sur un petit écran simulé (avec /apercu ou /test) : /ecran 1024x600.
        if ((preview != null || test) && ParseSize(ValueOf(args, "/ecran")) is { } screen) FormParts.ScreenForTests = screen;
        var requested = new UninstallChoices { RemoveAppData = withPhotos || HasFlag(args, "/tout-supprimer"), RemovePhotos = withPhotos };

        try
        {
            if (preview != null)
            {
                return SavePreview(preview, uninstall
                    ? new UninstallForm(UserData.Summarize(UserData.FindRoots(dataFolder)), requested)
                    : new InstallerForm(null, new InstallOptions { InstallDriver = false }));
            }
            // Essai du téléchargement du pilote DNP (empreinte et décompression), sans rien installer.
            var driverTest = ValueOf(args, "/test-pilote-dnp");
            if (driverTest != null) return TestDriverDownload(driverTest);
            if (!test && !IsAdministrator()) return RestartAsAdministrator(args);
            var executable = Process.GetCurrentProcess().MainModule!.FileName;
            return uninstall ? RunUninstall(executable, silent, test, requested, dataFolder) : RunInstall(executable, silent, test, folder, update);
        }
        catch (Exception ex)
        {
            Installation.Log("Erreur : " + ex);
            if (!silent) MessageBox.Show(ex.Message, "LibreBooth", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <param name="update">
    /// Mise à jour lancée par l'admin de LibreBooth (/mise-a-jour) : dans le dossier de la copie qui l'a lancée (/dossier),
    /// sans toucher aux pilotes ni aux raccourcis, après la fermeture de LibreBooth.
    /// </param>
    private static int RunInstall(string executable, bool silent, bool test, string? folder, bool update)
    {
        using var payload = Payload.Open(executable);
        if (payload == null)
        {
            throw new InvalidOperationException(
                "Ce programme ne contient pas LibreBooth. Pour installer, lancez LibreBooth-Installation.exe ; pour désinstaller, utilisez « Désinstaller LibreBooth ».");
        }

        // Dossier : celui demandé, sinon celui de l'installation inscrite dans Windows (jamais pendant un essai), sinon le dossier habituel.
        var dir = Installation.NormalizeInstallDir(folder ?? (test ? null : Installation.RegisteredInstallDir()) ?? Installation.DefaultInstallDir);
        var options = new InstallOptions
        {
            InstallDir = dir,
            InstallDriver = !update && !test && payload.HasDriver && !Installation.IsDriverInstalled(),
            DownloadDriver = !update && !test && !payload.HasDriver && !Installation.IsDriverInstalled(),
            DesktopShortcut = !update || Installation.HasDesktopShortcut(test, dir),
            TestMode = test,
            Update = update,
        };

        if (!silent)
        {
            // Une version plus récente est déjà là : on prévient avant de revenir en arrière. Pas depuis l'admin
            // (/mise-a-jour) : le retour à la version stable y est déjà confirmé.
            var installed = Installation.InstalledVersion(dir);
            if (!update && Installation.IsOlder(Installation.Version, installed))
            {
                var answer = MessageBox.Show(
                    $"Une version plus récente de LibreBooth ({installed}) est déjà installée sur ce PC.\n\n"
                    + $"Installer quand même la version {Installation.Version}, plus ancienne ? Vos photos, événements et réglages sont gardés.",
                    "LibreBooth", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return 2;
            }
            Application.Run(new InstallerForm(payload, options));
            return 0;
        }

        if (update) Installation.WaitForLibreBoothToClose(TimeSpan.FromSeconds(60));
        if (Installation.IsLibreBoothRunning()) throw new InvalidOperationException("LibreBooth est ouvert : fermez-le avant d'installer.");
        Installation.Install(payload, options, (_, message) => Installation.Log(message));
        return 0;
    }

    /// <returns>0 : tout est fait ; 2 : annulé ; 3 : LibreBooth est désinstallé, mais des données n'ont pas pu être supprimées.</returns>
    private static int RunUninstall(string executable, bool silent, bool test, UninstallChoices choices, string? dataFolder)
    {
        var dir = Path.GetDirectoryName(executable)!;
        if (!File.Exists(Path.Combine(dir, Installation.ManifestName)))
        {
            throw new InvalidOperationException("Aucune installation de LibreBooth n'a été trouvée dans " + dir + ".");
        }

        var roots = UserData.FindRoots(dataFolder);
        if (!silent)
        {
            using var form = new UninstallForm(UserData.Summarize(roots), choices);
            if (form.ShowDialog() != DialogResult.OK) return 2;
            choices = form.Choices;
        }

        while (Installation.IsLibreBoothRunning())
        {
            if (silent) throw new InvalidOperationException("LibreBooth est ouvert : fermez-le avant de désinstaller.");
            var retry = MessageBox.Show(
                "LibreBooth est ouvert. Fermez-le (admin › Système › Quitter LibreBooth), puis cliquez sur Réessayer.",
                "LibreBooth", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
            if (retry != DialogResult.Retry) return 2;
        }

        Installation.Uninstall(dir, test);
        Installation.Log($"Données : {(roots.Count == 0 ? "aucun dossier trouvé" : string.Join(" ; ", roots))} "
                         + $"(fichiers de LibreBooth supprimés : {choices.RemoveAppData}, photos supprimées : {choices.RemovePhotos})");
        var failures = new List<string>();
        // Les photos d'abord : le dossier Evenements part entier dans la Corbeille (un seul élément à restaurer).
        if (choices.RemovePhotos) failures.AddRange(UserData.RemoveEvents(roots, interactive: !silent));
        // Les dossiers temporaires de Windows ne sont nettoyés que pour les vraies données (pas pendant un essai).
        if (choices.RemoveAppData) failures.AddRange(UserData.RemoveAppData(roots, interactive: !silent, includeTemp: dataFolder == null));

        if (!silent)
        {
            MessageBox.Show(FinalMessage(roots, choices, failures), "LibreBooth", MessageBoxButtons.OK,
                failures.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        // En tout dernier : le programme de désinstallation est effacé juste après sa fermeture (avec le journal
        // d'installation si tout ce qui concerne LibreBooth devait partir).
        Installation.DeleteUninstallerAfterExit(dir, deleteLog: choices.RemoveAppData && failures.Count == 0 && !test);
        return failures.Count == 0 ? 0 : 3;
    }

    private static string FinalMessage(List<string> roots, UninstallChoices choices, List<string> failures)
    {
        var message = "LibreBooth est désinstallé.";
        var where = roots.Count == 0 ? "le dossier LibreBooth de votre profil Windows" : string.Join("\n", roots);
        if (choices.RemovePhotos)
        {
            message += "\n\nLes photos des événements, les réglages, les modèles, les personnalisations, les pilotes ajoutés et les journaux sont dans la Corbeille de Windows.";
        }
        else if (choices.RemoveAppData)
        {
            message += "\n\nLes réglages, les modèles, les personnalisations, les pilotes ajoutés et les journaux sont dans la Corbeille de Windows. "
                       + "Les photos des événements sont toujours dans :\n" + string.Join("\n", roots.Select(r => Path.Combine(r, UserData.EventsFolderName)));
        }
        else
        {
            message += "\n\nVos photos et réglages sont toujours dans :\n" + where;
        }
        if (failures.Count > 0)
        {
            message += "\n\nCes éléments n'ont pas pu être supprimés (encore ouverts dans un autre programme ?) :\n"
                       + string.Join("\n", failures.Take(6)) + (failures.Count > 6 ? $"\n… et {failures.Count - 6} autres" : "");
        }
        return message;
    }

    /// <summary>Image d'une fenêtre (vérification de la mise en page), sans rien installer ni désinstaller.</summary>
    private static int SavePreview(string png, Form form)
    {
        using var _ = form;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(png, ImageFormat.Png);
        form.Close();
        return 0;
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Essai du téléchargement du pilote DNP : empreinte et décompression, sans rien installer.</summary>
    private static int TestDriverDownload(string folder)
    {
        try
        {
            var package = Installation.DownloadDriverPackage(folder);
            var ok = File.Exists(Path.Combine(package, "dpinst64.exe"));
            Installation.Log($"Essai du pilote DNP : {(ok ? "téléchargé, vérifié et décompressé" : "dpinst64.exe absent")} dans {package}");
            return ok ? 0 : 2;
        }
        catch (Exception ex)
        {
            Installation.Log("Essai du pilote DNP : échec – " + ex.Message);
            return 1;
        }
    }

    /// <summary>Relance le programme avec les droits d'administrateur (Windows demande la permission).</summary>
    private static int RestartAsAdministrator(string[] args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule!.FileName)
            {
                Arguments = string.Join(" ", args.Select(a => a.IndexOf(' ') >= 0 ? $"\"{a}\"" : a)),
                UseShellExecute = true,
                Verb = "runas",
            });
            return 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show("L'installation a besoin de l'accord de Windows (droits d'administrateur) pour copier LibreBooth et installer le pilote de l'imprimante.",
                "LibreBooth", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 2;
        }
    }

    /// <summary>« 1024x600 » → 1024 × 600 pixels.</summary>
    private static Size? ParseSize(string? text)
    {
        var parts = text?.Split('x', 'X');
        return parts is { Length: 2 } && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height)
               && width >= 200 && height >= 200
            ? new Size(width, height)
            : null;
    }

    private static bool HasFlag(string[] args, string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? ValueOf(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
