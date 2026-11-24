using System.Diagnostics;
using System.Reflection;
using System.Windows;
using LibreBooth.App.Diagnostics;
using LibreBooth.App.Infrastructure;
using LibreBooth.App.Kiosk;
using LibreBooth.App.Services;
using LibreBooth.Core.Config;
using Serilog;

namespace LibreBooth.App;

public partial class App : Application
{
    private const string InstanceMutexName = "LibreBooth.Kiosk.Instance";
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    public static AppServices? Services { get; private set; }

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = StartupOptions.Current;

        AppPaths paths;
        try
        {
            // Le mode démo travaille dans son propre dossier pour ne pas mélanger ses photos avec celles des événements.
            paths = AppPaths.Create(options.DataRoot, options.Demo);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Impossible de créer le dossier des données de LibreBooth :\n" + ex.Message, "LibreBooth", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        Logging.Configure(paths.LogsFolder);
        Log.Information("Démarrage de LibreBooth {Version} (démo : {Demo}, données : {Data})", Version, options.Demo, paths.DataRoot);
        RegisterGlobalErrorHandlers();
        WheelScrolling.Register();
        // Admin en clair ou en sombre (admin › Système › Apparence), avant la première fenêtre (code PIN, vérification…).
        AdminTheme.Apply(AdminTheme.ReadSetting(paths.ConfigFile));

        if (options.IsDiagnosticRun)
        {
            var report = options.DiagnosticReport ?? Path.Combine(paths.DataRoot, $"diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            _ = RunDiagnosticAsync(report, paths);
            return;
        }

        if (options.IsVerifyRun && options.VerifyReport != null)
        {
            _ = RunVerificationReportAsync(paths, options.VerifyReport);
            return;
        }

        if (options.IsVerifyRun)
        {
            // Vérification de l'installation, lancée seule (menu Démarrer, programme d'installation).
            var verification = new VerificationWindow(null, paths);
            MainWindow = verification;
            verification.Closed += (_, _) => Shutdown(0);
            verification.Show();
            return;
        }

        if (!options.IsCaptureRun && !AcquireSingleInstance(options))
        {
            MessageBox.Show("LibreBooth est déjà ouvert.", "LibreBooth", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // Lancement normal : le code PIN, puis le choix de l'événement (ouvrir ou créer), avant tout le reste.
        // Après un plantage, LibreBooth repart directement sur le photobooth avec le même événement.
        if (!options.IsCaptureRun && !options.IsRestart)
        {
            bool chosen;
            try
            {
                chosen = BoothShell.AskPinAndEvent(paths);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Choix de l'événement impossible");
                MessageBox.Show("LibreBooth n'a pas pu ouvrir la liste des événements :\n" + ex.Message + "\n\nDétails dans le journal : " + paths.LogsFolder,
                    "LibreBooth", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
            if (!chosen)
            {
                Log.Information("Code PIN ou événement non choisi au lancement : LibreBooth se ferme");
                Shutdown(0);
                return;
            }
        }

        try
        {
            Services = AppServices.Create(paths, options, Dispatcher);
            AdminTheme.Apply(Services.Settings.AdminAppearance);
            Theme.Apply(this, Services.CurrentKioskPalette());
            var kiosk = new KioskViewModel(Services);
            Services.Kiosk = kiosk;
            Services.Start();

            if (options.IsCaptureRun)
            {
                _ = RunCapturesAsync(Services, kiosk, options.CapturesFolder!);
                return;
            }

            PowerManagement.KeepDisplayOn();
            // Puis l'admin seule (fenêtre normale). Le photobooth plein écran n'apparaît qu'avec « Démarrer le photobooth ».
            // Après un plantage, LibreBooth repart directement sur le photobooth.
            var shell = new BoothShell(Services, kiosk);
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await shell.StartAsync(options.IsRestart);
                }
                catch (Exception ex)
                {
                    Log.Fatal(ex, "Démarrage de l'admin impossible");
                    MessageBox.Show("LibreBooth n'a pas pu démarrer :\n" + ex.Message, "LibreBooth", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(1);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Démarrage impossible");
            MessageBox.Show("LibreBooth n'a pas pu démarrer :\n" + ex.Message + "\n\nDétails dans le journal : " + paths.LogsFolder,
                "LibreBooth", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task RunCapturesAsync(AppServices services, KioskViewModel kiosk, string folder)
    {
        var exitCode = 0;
        try
        {
            await CaptureRunner.RunAsync(services, kiosk, folder);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Parcours de vérification interrompu");
            exitCode = 2;
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    /// <summary>Vérification de l'installation sans fenêtre : le rapport est écrit dans un fichier.</summary>
    private async Task RunVerificationReportAsync(AppPaths paths, string report)
    {
        var results = new List<CheckResult>();
        try
        {
            await InstallationCheck.RunAsync(null, paths, results.Add);
        }
        catch (Exception ex)
        {
            results.Add(new CheckResult("Vérification", "Vérification interrompue", CheckStatus.Error, ex.Message));
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllLinesAsync(report, InstallationCheck.ToReport(results));
            Shutdown(0);
        }
    }

    private async Task RunDiagnosticAsync(string report, AppPaths paths)
    {
        try
        {
            // Le diagnostic essaie le pilote de l'appareil choisi dans l'admin (celui de Windows par défaut).
            var camera = InstallationCheck.CameraSettingsFor(null, paths);
            await HardwareDiagnostic.RunAsync(report, camera, StartupOptions.Current.PrintTestPages, StartupOptions.Current.PrintToFiles,
                StartupOptions.Current.BookmarkPages);
            if (StartupOptions.Current.DiagnosticReport == null)
            {
                // Lancé à la main : on ouvre le rapport dans le Bloc-notes.
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{report}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Diagnostic impossible");
        }
        finally
        {
            Shutdown(0);
        }
    }

    private bool AcquireSingleInstance(StartupOptions options)
    {
        _instanceMutex = new Mutex(false, InstanceMutexName);
        try
        {
            // Après un redémarrage automatique, on attend que l'ancienne instance ait libéré la place.
            _ownsMutex = _instanceMutex.WaitOne(options.IsRestart ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }
        return _ownsMutex;
    }

    private void RegisterGlobalErrorHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Erreur inattendue dans l'interface");
            args.Handled = true;
            try
            {
                Services?.Kiosk?.RecoverFromCrash(args.Exception);
            }
            catch (Exception inner)
            {
                Log.Error(inner, "Reprise impossible après l'erreur");
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Erreur fatale : LibreBooth va redémarrer");
            Log.CloseAndFlush();
            RestartAfterCrash();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Tâche en erreur non observée");
            args.SetObserved();
        };
    }

    /// <summary>Relance automatique après une erreur fatale (sauf si le plantage survient dès le démarrage).</summary>
    private static void RestartAfterCrash()
    {
        var options = StartupOptions.Current;
        if (options.IsCaptureRun) return;
        try
        {
            var uptime = DateTime.Now - Process.GetCurrentProcess().StartTime;
            if (uptime < TimeSpan.FromSeconds(60) || Environment.ProcessPath == null) return;
            var arguments = Environment.GetCommandLineArgs().Skip(1).Where(a => a != "--restart").Append("--restart");
            Process.Start(new ProcessStartInfo(Environment.ProcessPath, string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)))
            {
                UseShellExecute = false,
            });
        }
        catch
        {
            // Rien de plus à faire.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Services?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Arrêt des services");
        }
        PowerManagement.Release();
        Log.Information("Arrêt de LibreBooth");
        Log.CloseAndFlush();
        if (_ownsMutex)
        {
            try
            {
                _instanceMutex?.ReleaseMutex();
            }
            catch
            {
                // Déjà libéré.
            }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
