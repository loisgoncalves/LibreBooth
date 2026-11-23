using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LibreBooth.App.Kiosk;
using LibreBooth.App.Services;
using LibreBooth.Core.Session;

namespace LibreBooth.App;

/// <summary>Fenêtre plein écran du photobooth (le passage vers l'admin est géré par BoothShell).</summary>
public partial class MainWindow : Window
{
    /// <summary>Le pointeur de la souris disparaît après ce délai sans mouvement.</summary>
    private static readonly TimeSpan CursorDelay = TimeSpan.FromSeconds(4);

    private readonly AppServices _services;
    private readonly KioskViewModel _kiosk;
    private readonly BoothShell _shell;
    private readonly DispatcherTimer _adminPress = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _cursorTimer = new() { Interval = CursorDelay };
    private bool _dialogOpen;
    private bool _allowClose;
    private bool _cursorShown;
    private Point? _lastMousePosition;

    internal MainWindow(AppServices services, KioskViewModel kiosk, BoothShell shell)
    {
        InitializeComponent();
        _services = services;
        _kiosk = kiosk;
        _shell = shell;
        DataContext = kiosk;

        _adminPress.Tick += async (_, _) =>
        {
            _adminPress.Stop();
            await _shell.OpenAdminFromBoothAsync();
        };
        AdminCorner.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _adminPress.Start();
            e.Handled = true;
        };
        AdminCorner.PreviewMouseLeftButtonUp += (_, _) => _adminPress.Stop();
        AdminCorner.MouseLeave += (_, _) => _adminPress.Stop();
        AdminCorner.PreviewTouchDown += (_, e) =>
        {
            _adminPress.Start();
            e.Handled = true;
        };
        AdminCorner.PreviewTouchUp += (_, _) => _adminPress.Stop();

        // La souris marche comme le doigt (un clic = un appui) : son pointeur se montre quand elle bouge.
        _cursorTimer.Tick += (_, _) => HideCursor();
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseDown += (_, e) =>
        {
            _kiosk.NotifyActivity();
            if (e.StylusDevice == null) ShowCursor();
        };
        PreviewTouchDown += (_, _) =>
        {
            _kiosk.NotifyActivity();
            HideCursor();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                // Photobooth affiché : le pointeur se cache tout de suite (il revient dès que la souris bouge).
                _cursorShown = false;
                ApplyCursor();
                return;
            }
            // Photobooth caché (retour à l'admin) : le pointeur doit être visible partout.
            _adminPress.Stop();
            _cursorTimer.Stop();
            _cursorShown = false;
            Mouse.OverrideCursor = null;
        };
        Closing += (_, e) =>
        {
            // Pas de fermeture accidentelle (Alt+F4) : on quitte par le bouton du photobooth ou par l'admin.
            if (!_allowClose) e.Cancel = true;
        };
    }

    /// <summary>Affiche le photobooth en plein écran (au démarrage, ou en revenant de l'admin).</summary>
    public void ShowBooth()
    {
        ApplyWindowMode();
        Show();
        Activate();
    }

    /// <summary>Autorise la fermeture (on quitte LibreBooth).</summary>
    public void AllowClose() => _allowClose = true;

    /// <summary>Un code PIN ou un choix s'affiche par-dessus le photobooth : le pointeur reste visible.</summary>
    public void SetDialogOpen(bool open)
    {
        _dialogOpen = open;
        ApplyCursor();
    }

    private void ApplyWindowMode()
    {
        var screens = _services.Settings.Screens;
        if (_services.Options.Windowed)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            Width = 1280;
            Height = 720;
            Topmost = false;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Topmost = screens.Topmost;
        }
        ApplyCursor();
    }

    private async void LeaveButton_Click(object sender, RoutedEventArgs e) => await _shell.LeaveBoothAsync();

    // ---------------------------------------------------------------- Pointeur de la souris

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        // Un appui sur l'écran tactile produit aussi des mouvements de souris : on les ignore.
        if (e.StylusDevice != null) return;

        // WPF signale aussi des « mouvements » quand l'écran change sous un pointeur immobile : on ne compte que les vrais.
        var position = e.GetPosition(this);
        var previous = _lastMousePosition;
        _lastMousePosition = position;
        if (previous == null || (Math.Abs(previous.Value.X - position.X) < 3 && Math.Abs(previous.Value.Y - position.Y) < 3)) return;

        ShowCursor();
        _kiosk.NotifyActivity();
    }

    private void ShowCursor()
    {
        _cursorTimer.Stop();
        _cursorTimer.Start();
        if (_cursorShown) return;
        _cursorShown = true;
        ApplyCursor();
    }

    private void HideCursor()
    {
        _cursorTimer.Stop();
        if (!_cursorShown) return;
        _cursorShown = false;
        ApplyCursor();
    }

    /// <summary>
    /// Réglage « Masquer le pointeur » : il est caché tant que la souris ne bouge pas, et toujours visible
    /// sur le code PIN, en mode fenêtré et quand le photobooth est caché.
    /// </summary>
    private void ApplyCursor()
    {
        var autoHide = _services.Settings.Screens.HideCursor && !_services.Options.Windowed && !_dialogOpen && IsVisible;
        Mouse.OverrideCursor = autoHide && !_cursorShown ? Cursors.None : null;
    }

    // ---------------------------------------------------------------- Clavier

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _kiosk.NotifyActivity();
        if (e.Key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            await _shell.OpenAdminFromBoothAsync();
            return;
        }

        if (_kiosk.State == KioskState.Attract && IsStartKey(e.Key))
        {
            e.Handled = true;
            _kiosk.StartSession();
        }
    }

    private bool IsStartKey(Key key)
    {
        var configured = _services.Settings.Screens.StartKey;
        if (Enum.TryParse<Key>(configured, ignoreCase: true, out var startKey) && key == startKey) return true;
        return key == Key.Space && string.IsNullOrWhiteSpace(configured);
    }
}
