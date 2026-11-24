using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LibreBooth.App.Admin;
using LibreBooth.App.Hardware;
using LibreBooth.App.Infrastructure;
using LibreBooth.App.Services;
using LibreBooth.Core.Config;
using LibreBooth.Core.Hardware;
using Microsoft.Win32;
using Serilog;

namespace LibreBooth.App.Diagnostics;

/// <summary>
/// Fenêtre de vérification de l'installation, aux couleurs de l'admin : chaque point vérifié s'affiche au fil de l'eau
/// avec une icône (vert : bon, bleu : information, orange : à vérifier, rouge : à corriger) et ce qu'il faut faire.
/// </summary>
public partial class VerificationWindow : Window
{
    private readonly ILogger _log = Log.ForContext<VerificationWindow>();
    private readonly AppServices? _services;
    private readonly AppPaths? _paths;
    private readonly ObservableCollection<CheckResult> _results = new();
    private CancellationTokenSource? _run;

    /// <param name="services">LibreBooth ouvert (admin), ou null quand la vérification est lancée seule (--verifier).</param>
    public VerificationWindow(AppServices? services, AppPaths? paths)
    {
        InitializeComponent();
        AdminTheme.Attach(this);
        _services = services;
        _paths = paths;
        var view = CollectionViewSource.GetDefaultView(_results);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CheckResult.Category)));
        ResultsList.ItemsSource = view;
        LaunchButton.Visibility = services == null ? Visibility.Visible : Visibility.Collapsed;
        if (services != null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
        }

        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => _run?.Cancel();
    }

    /// <summary>Résultats de la dernière vérification (captures et tests).</summary>
    internal IReadOnlyList<CheckResult> Results => _results;

    internal Task RunForCaptureAsync() => RunAsync();

    private async Task RunAsync()
    {
        _run?.Cancel();
        var run = _run = new CancellationTokenSource();
        _results.Clear();
        CanonSdkBanner.Visibility = NativeDrivers.CanonFiles.All(f => NativeDrivers.Find(f) != null) ? Visibility.Collapsed : Visibility.Visible;
        BusyBar.Visibility = Visibility.Visible;
        RerunButton.IsEnabled = false;
        SummaryText.Text = "Vérification en cours…";
        SummaryText.SetResourceReference(TextBlock.ForegroundProperty, "Admin.TextMuted");
        try
        {
            await InstallationCheck.RunAsync(_services, _paths, result => _results.Add(result), run.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Vérification interrompue");
            _results.Add(new CheckResult("Vérification", "Vérification interrompue", CheckStatus.Error, ex.Message));
        }
        finally
        {
            if (_run == run)
            {
                BusyBar.Visibility = Visibility.Collapsed;
                RerunButton.IsEnabled = true;
            }
        }

        var errors = _results.Count(r => r.Status == CheckStatus.Error);
        var warnings = _results.Count(r => r.Status == CheckStatus.Warning);
        if (errors == 0 && warnings == 0)
        {
            SummaryText.Text = "✓ Tout est prêt : LibreBooth peut fonctionner.";
            SummaryText.SetResourceReference(TextBlock.ForegroundProperty, "Admin.Success");
        }
        else
        {
            SummaryText.Text = (errors > 0 ? $"{errors} problème(s) à corriger" : "Aucun problème bloquant")
                               + (warnings > 0 ? $" · {warnings} point(s) à vérifier" : "")
                               + " (voir « À faire »).";
            SummaryText.SetResourceReference(TextBlock.ForegroundProperty, errors > 0 ? "Admin.Error" : "Admin.Warning");
        }
        _log.Information("Vérification de l'installation : {Errors} problème(s), {Warnings} point(s) à vérifier", errors, warnings);
        foreach (var line in InstallationCheck.ToReport(_results)) _log.Information("[vérification] {Line}", line);
    }

    private async void Rerun_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private void OpenCanonPage_Click(object sender, RoutedEventArgs e) => AdminWindow.OpenInBrowser(CanonSdkImport.Page);

    /// <summary>Le zip du SDK téléchargé chez Canon : EDSDK.dll et EdsImage.dll (32 bits) vont dans le dossier « Pilotes ».</summary>
    private async void ImportCanonSdk_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "SDK Canon EDSDK téléchargé (fichier zip)",
            Filter = "SDK Canon (*.zip ; EDSDK.dll)|*.zip;EDSDK.dll|Tous les fichiers|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dialog.ShowDialog(this) != true) return;

        CanonSdkImportButton.IsEnabled = false;
        try
        {
            var source = dialog.FileName;
            string version;
            if (_services != null)
            {
                version = await Task.Run(() => _services.ImportCanonSdk(source));
                _services.RestartCamera();
            }
            else
            {
                version = await Task.Run(() => CanonSdkImport.Import(source, NativeDrivers.UserFolder));
                NativeDrivers.Refresh();
                _log.Information("SDK Canon ajouté dans {Folder} (EDSDK {Version})", NativeDrivers.UserFolder, version);
            }
            AdminDialog.Show(this, $"SDK Canon ajouté (EDSDK {version}). La vérification va être relancée.", "LibreBooth",
                MessageBoxButton.OK, MessageBoxImage.Information);
            await RunAsync();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "SDK Canon non ajouté");
            AdminDialog.Show(this, "Ce fichier ne convient pas : " + ex.Message, "LibreBooth", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            CanonSdkImportButton.IsEnabled = true;
        }
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Enregistrer le rapport de vérification",
            FileName = $"verification-librebooth-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Texte (*.txt)|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllLines(dialog.FileName, InstallationCheck.ToReport(_results));
        }
        catch (Exception ex)
        {
            AdminDialog.Show(this, "Rapport impossible à enregistrer : " + ex.Message, "LibreBooth", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            AdminDialog.Show(this, "LibreBooth n'a pas pu être lancé : " + ex.Message, "LibreBooth", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
