using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using LibreBooth.App.Infrastructure;

namespace LibreBooth.App.Admin;

/// <summary>Saisie du code PIN avant d'ouvrir l'admin (pavé tactile ou clavier), aux couleurs de l'admin.</summary>
public partial class PinWindow : Window
{
    private readonly string _expectedPin;
    private string _typed = "";

    /// <param name="title">Ce que le code protège (ouvrir l'admin, quitter le photobooth).</param>
    /// <param name="hint">Petite aide sous le titre (le code par défaut au lancement), ou null.</param>
    /// <param name="standalone">Sans fenêtre derrière (au lancement) : centrée sur l'écran et visible dans la barre des tâches.</param>
    public PinWindow(string expectedPin, string title = "Code administrateur", string? hint = null, bool standalone = false)
    {
        InitializeComponent();
        AdminTheme.Attach(this);
        _expectedPin = expectedPin;
        TitleText.Text = title;
        Title = "LibreBooth – " + title;
        if (!string.IsNullOrWhiteSpace(hint))
        {
            HintText.Text = hint;
            HintText.Visibility = Visibility.Visible;
        }
        if (standalone)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            BrandPanel.Visibility = Visibility.Visible;
        }
        foreach (var key in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "⌫", "0", "OK" })
        {
            var button = new Button { Content = key };
            button.SetResourceReference(StyleProperty, key == "OK" ? "PinOkKey" : "PinKey");
            AutomationProperties.SetName(button, key == "⌫" ? "Effacer" : key);
            button.Click += (_, _) => Press(key);
            Keypad.Children.Add(button);
        }
        PreviewKeyDown += OnKeyDown;
        UpdateDots();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is >= Key.D0 and <= Key.D9) Press(((int)(e.Key - Key.D0)).ToString());
        else if (e.Key is >= Key.NumPad0 and <= Key.NumPad9) Press(((int)(e.Key - Key.NumPad0)).ToString());
        else if (e.Key == Key.Back) Press("⌫");
        else if (e.Key == Key.Enter) Press("OK");
        else if (e.Key == Key.Escape) DialogResult = false;
        else return;
        e.Handled = true;
    }

    private void Press(string key)
    {
        ErrorText.Text = "";
        switch (key)
        {
            case "⌫":
                if (_typed.Length > 0) _typed = _typed[..^1];
                break;
            case "OK":
                Check();
                return;
            default:
                if (_typed.Length < 8) _typed += key;
                if (_typed.Length == _expectedPin.Length)
                {
                    UpdateDots();
                    Check();
                    return;
                }
                break;
        }
        UpdateDots();
    }

    private void Check()
    {
        if (_typed == _expectedPin)
        {
            DialogResult = true;
            return;
        }
        _typed = "";
        ErrorText.Text = "Code incorrect";
        UpdateDots();
    }

    private void UpdateDots() => DotsText.Text = _typed.Length == 0 ? "– – – –" : new string('●', _typed.Length);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
