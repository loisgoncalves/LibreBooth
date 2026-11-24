using System.Windows;
using System.Windows.Input;
using LibreBooth.App.Admin;
using LibreBooth.App.Infrastructure;
using LibreBooth.App.Kiosk;
using LibreBooth.App.Services;
using LibreBooth.Core.Config;
using LibreBooth.Core.Events;
using Serilog;

namespace LibreBooth.App;

/// <summary>
/// Les deux modes de LibreBooth et le passage de l'un à l'autre :
/// <list type="bullet">
/// <item>au lancement : le code PIN, le choix de l'événement (ouvrir ou créer), puis l'admin dans une fenêtre normale ;</item>
/// <item>le photobooth, en plein écran, n'apparaît qu'avec « Démarrer le photobooth » ;</item>
/// <item>depuis le photobooth, revenir à l'admin ou quitter LibreBooth demande le code PIN.</item>
/// </list>
/// </summary>
internal sealed class BoothShell
{
    private readonly ILogger _log = Log.ForContext<BoothShell>();
    private readonly AppServices _services;
    private readonly KioskViewModel _kiosk;
    private MainWindow? _boothWindow;
    private bool _busy;

    public BoothShell(AppServices services, KioskViewModel kiosk)
    {
        _services = services;
        _kiosk = kiosk;
    }

    /// <summary>
    /// Au lancement, avant de démarrer l'appareil et l'imprimante : le code PIN, puis le choix de l'événement (ouvrir un
    /// événement existant ou en créer un). Le choix est enregistré dans la configuration, où les services le reprennent.
    /// </summary>
    /// <returns>false si l'opérateur renonce : LibreBooth se ferme.</returns>
    public static bool AskPinAndEvent(AppPaths paths)
    {
        var store = new SettingsStore(paths.ConfigFile);
        var settings = store.Load();
        var hint = settings.AdminPin == "1234" ? "Code par défaut : 1234 (à changer dans la page Système de l'admin)" : null;
        var pin = new PinWindow(settings.AdminPin, "Code administrateur", hint, standalone: true);
        if (pin.ShowDialog() != true) return false;

        var events = new EventStore(paths.EventsFolder);
        AppServices.UpgradeOldEvents(events, settings);
        var chooser = new EventChooserWindow(events, settings.CurrentEventFolder, atStartup: true);
        if (chooser.ShowDialog() != true || chooser.Chosen is not { } chosen) return false;
        settings.CurrentEventFolder = chosen.Folder;
        store.Save(settings);
        return true;
    }

    /// <param name="restartAfterCrash">
    /// Relance automatique après un plantage : on repart directement sur le photobooth, les invités attendent
    /// (et l'admin n'est pas laissée ouverte sans code devant eux).
    /// </param>
    public async Task StartAsync(bool restartAfterCrash)
    {
        if (restartAfterCrash)
        {
            _log.Information("Relance après un plantage : photobooth démarré directement");
            ShowBooth();
            return;
        }

        // Le code PIN et l'événement ont été demandés juste avant (AskPinAndEvent).
        await _kiosk.PauseForAdminAsync();
        await RunAdminAsync();
    }

    /// <summary>Appui long dans le coin ou Ctrl+Maj+A sur le photobooth : code PIN, puis l'admin.</summary>
    public async Task OpenAdminFromBoothAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!AskPin(_boothWindow, "Code administrateur", null)) return;
            await SwitchToAdminAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Bouton « Quitter » du photobooth : code PIN, puis revenir à l'admin ou quitter LibreBooth.</summary>
    public async Task LeaveBoothAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!AskPin(_boothWindow, "Code pour quitter le photobooth", null)) return;
            var choice = new LeaveBoothWindow();
            if (_boothWindow is { IsVisible: true }) choice.Owner = _boothWindow;
            choice.ShowDialog();
            switch (choice.Choice)
            {
                case LeaveBoothChoice.Admin:
                    await SwitchToAdminAsync();
                    break;
                case LeaveBoothChoice.Quit:
                    _log.Information("Fermeture de LibreBooth demandée depuis le photobooth");
                    Quit();
                    break;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private bool AskPin(Window? owner, string title, string? hint)
    {
        _boothWindow?.SetDialogOpen(true);
        try
        {
            var pin = new PinWindow(_services.Settings.AdminPin, title, hint, standalone: owner is not { IsVisible: true });
            if (owner is { IsVisible: true }) pin.Owner = owner;
            return pin.ShowDialog() == true;
        }
        finally
        {
            _boothWindow?.SetDialogOpen(false);
        }
    }

    private async Task SwitchToAdminAsync()
    {
        _log.Information("Retour à l'admin depuis le photobooth");
        await _kiosk.PauseForAdminAsync();
        _boothWindow?.Hide();
        await RunAdminAsync();
    }

    private async Task RunAdminAsync()
    {
        Mouse.OverrideCursor = null;
        Log.Information("Ouverture de l'admin");
        AdminWindow admin;
        try
        {
            admin = new AdminWindow(_services, _kiosk);
            Application.Current.MainWindow = admin;
            admin.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Erreur dans l'admin : retour au photobooth");
            ShowBooth();
            return;
        }

        if (admin.QuitRequested)
        {
            _log.Information("Fermeture de LibreBooth demandée depuis l'admin");
            Quit();
            return;
        }

        _services.ApplySettingsAfterAdmin();
        Theme.Apply(Application.Current, _services.CurrentKioskPalette());
        await Task.Yield();
        ShowBooth();
    }

    private void ShowBooth()
    {
        _boothWindow ??= new MainWindow(_services, _kiosk, this);
        Application.Current.MainWindow = _boothWindow;
        _boothWindow.ShowBooth();
        _kiosk.ResumeAfterAdmin();
        _log.Information("Photobooth démarré");
    }

    private void Quit()
    {
        _boothWindow?.AllowClose();
        Application.Current.Shutdown();
    }
}
