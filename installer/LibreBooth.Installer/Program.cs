using System;
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
/// Options : /desinstaller, /silencieux (sans fenêtre), /test (essai sans droits d'administrateur), /dossier &lt;chemin&gt;.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var uninstall = HasFlag(args, "/desinstaller");
        var silent = HasFlag(args, "/silencieux");
        var test = HasFlag(args, "/test");
        var folder = ValueOf(args, "/dossier");
        var preview = ValueOf(args, "/apercu");

        try
        {
            if (preview != null) return SavePreview(preview);
            // Essai du téléchargement du pilote DNP (empreinte et décompression), sans rien installer.
            var driverTest = ValueOf(args, "/test-pilote-dnp");
            if (driverTest != null) return TestDriverDownload(driverTest);
            if (!test && !IsAdministrator()) return RestartAsAdministrator(args);
            var executable = Process.GetCurrentProcess().MainModule!.FileName;
            return uninstall ? RunUninstall(executable, silent, test) : RunInstall(executable, silent, test, folder);
        }
        catch (Exception ex)
        {
            Installation.Log("Erreur : " + ex);
            if (!silent) MessageBox.Show(ex.Message, "LibreBooth", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int RunInstall(string executable, bool silent, bool test, string? folder)
    {
        using var payload = Payload.Open(executable);
        if (payload == null)
        {
            throw new InvalidOperationException(
                "Ce programme ne contient pas LibreBooth. Pour installer, lancez LibreBooth-Installation.exe ; pour désinstaller, utilisez « Désinstaller LibreBooth ».");
        }

        var options = new InstallOptions
        {
            InstallDir = Installation.NormalizeInstallDir(folder ?? Installation.DefaultInstallDir),
            InstallDriver = !test && payload.HasDriver && !Installation.IsDriverInstalled(),
            DownloadDriver = !test && !payload.HasDriver && !Installation.IsDriverInstalled(),
            DesktopShortcut = true,
            TestMode = test,
        };

        if (!silent)
        {
            Application.Run(new InstallerForm(payload, options));
            return 0;
        }

        if (Installation.IsLibreBoothRunning()) throw new InvalidOperationException("LibreBooth est ouvert : fermez-le avant d'installer.");
        Installation.Install(payload, options, (_, message) => Installation.Log(message));
        return 0;
    }

    private static int RunUninstall(string executable, bool silent, bool test)
    {
        var dir = Path.GetDirectoryName(executable)!;
        if (!File.Exists(Path.Combine(dir, Installation.ManifestName)))
        {
            throw new InvalidOperationException("Aucune installation de LibreBooth n'a été trouvée dans " + dir + ".");
        }

        if (!silent)
        {
            var answer = MessageBox.Show(
                "Désinstaller LibreBooth de ce PC ?\n\nVos photos, vos événements et vos réglages (dossier LibreBooth de votre profil Windows) sont conservés. "
                + "Le pilote de l'imprimante DNP reste installé.",
                "LibreBooth", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return 2;
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
        if (!silent)
        {
            MessageBox.Show("LibreBooth est désinstallé. Vos photos et réglages sont toujours dans le dossier LibreBooth de votre profil Windows (C:\\Users\\<nom>\\LibreBooth).",
                "LibreBooth", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        return 0;
    }

    /// <summary>Image de la fenêtre d'installation (vérification de la mise en page), sans rien installer.</summary>
    private static int SavePreview(string png)
    {
        using var form = new InstallerForm(null, new InstallOptions { InstallDriver = false })
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-20000, -20000),
            ShowInTaskbar = false,
        };
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

    private static bool HasFlag(string[] args, string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? ValueOf(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
