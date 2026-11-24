using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LibreBooth.App.Infrastructure;

namespace LibreBooth.App.Admin;

/// <summary>
/// Message ou confirmation de l'admin, en clair comme en sombre, avec de grands boutons « Oui », « Non », « OK »,
/// « Annuler » (Show, comme MessageBox.Show), ou nommés selon l'action (Choose, Ask). Échap ou la croix répondent « Non » ou « Annuler ».
/// Les erreurs de démarrage (App.xaml.cs) gardent la boîte de Windows : elles s'affichent avant tout chargement.
/// </summary>
public partial class AdminDialog : Window
{
    private MessageBoxResult _result;
    private readonly MessageBoxResult _closeResult;
    private Button? _defaultButton;

    private AdminDialog(string message, string caption, MessageBoxImage image, IReadOnlyList<(string Label, MessageBoxResult Result)> choices,
        MessageBoxResult defaultResult, MessageBoxResult closeResult)
    {
        InitializeComponent();
        AdminTheme.Attach(this);
        Title = caption;
        MessageText.Text = message;
        ShowIcon(image);

        // Action risquée (supprimer, écraser, démarrer malgré un problème) : le bouton principal est rouge.
        var risky = (image is MessageBoxImage.Warning or MessageBoxImage.Error) && choices.Count > 1;
        _closeResult = closeResult;
        _result = _closeResult;
        if (defaultResult == MessageBoxResult.None || choices.All(c => c.Result != defaultResult)) defaultResult = choices[0].Result;

        for (var i = 0; i < choices.Count; i++)
        {
            var (label, result) = choices[i];
            var button = new Button { Content = label, MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
            if (i == 0)
            {
                button.SetResourceReference(StyleProperty, risky ? "DangerButton" : "AccentButton");
            }
            button.IsDefault = result == defaultResult;
            button.IsCancel = result == _closeResult;
            button.Click += (_, _) =>
            {
                _result = result;
                Close();
            };
            if (button.IsDefault) _defaultButton = button;
            ButtonsPanel.Children.Add(button);
        }

        Loaded += (_, _) => (_defaultButton ?? ButtonsPanel.Children.OfType<Button>().First()).Focus();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Comme MessageBox.Show, aux couleurs de l'admin.</summary>
    /// <param name="owner">Fenêtre devant laquelle s'ouvrir (ignorée si elle n'est pas affichée).</param>
    public static MessageBoxResult Show(Window? owner, string message, string caption = "LibreBooth",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var choices = buttons switch
        {
            MessageBoxButton.OKCancel => new[] { ("OK", MessageBoxResult.OK), ("Annuler", MessageBoxResult.Cancel) },
            MessageBoxButton.YesNo => new[] { ("Oui", MessageBoxResult.Yes), ("Non", MessageBoxResult.No) },
            MessageBoxButton.YesNoCancel => new[] { ("Oui", MessageBoxResult.Yes), ("Non", MessageBoxResult.No), ("Annuler", MessageBoxResult.Cancel) },
            _ => new[] { ("OK", MessageBoxResult.OK) },
        };
        var closeResult = buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.OK => MessageBoxResult.OK,
            _ => MessageBoxResult.Cancel,
        };
        return Open(owner, new AdminDialog(message, caption, image, choices, defaultResult, closeResult));
    }

    /// <summary>
    /// Question dont chaque réponse a son propre texte de bouton (« Ouvrir l'existant », « Créer un deuxième »…).
    /// Le premier choix est le bouton principal ; Échap et la croix répondent <paramref name="closeResult"/>.
    /// </summary>
    public static MessageBoxResult Choose(Window? owner, string message, MessageBoxImage image, MessageBoxResult defaultResult,
        MessageBoxResult closeResult, params (string Label, MessageBoxResult Result)[] choices) =>
        Open(owner, new AdminDialog(message, "LibreBooth", image, choices, defaultResult, closeResult));

    /// <summary>Confirmation à deux boutons nommés (« Démarrer quand même », « Annuler ») : vrai si l'opérateur confirme.</summary>
    public static bool Ask(Window? owner, string message, string confirmLabel, string cancelLabel = "Annuler",
        MessageBoxImage image = MessageBoxImage.Question, bool defaultConfirm = true) =>
        Choose(owner, message, image, defaultConfirm ? MessageBoxResult.OK : MessageBoxResult.Cancel, MessageBoxResult.Cancel,
            (confirmLabel, MessageBoxResult.OK), (cancelLabel, MessageBoxResult.Cancel)) == MessageBoxResult.OK;

    /// <summary>Pour les captures : une confirmation telle qu'Ask l'ouvrirait, sans l'afficher.</summary>
    internal static AdminDialog CreateForCapture(string message, string confirmLabel, string cancelLabel, MessageBoxImage image, bool defaultConfirm) =>
        new(message, "LibreBooth", image, new[] { (confirmLabel, MessageBoxResult.OK), (cancelLabel, MessageBoxResult.Cancel) },
            defaultConfirm ? MessageBoxResult.OK : MessageBoxResult.Cancel, MessageBoxResult.Cancel);

    private static MessageBoxResult Open(Window? owner, AdminDialog dialog)
    {
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.Topmost = owner.Topmost;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
        }
        dialog.ShowDialog();
        return dialog._result;
    }

    private void ShowIcon(MessageBoxImage image)
    {
        var (glyph, brush) = image switch
        {
            MessageBoxImage.Error => ("\uE783", "Admin.Error"),
            MessageBoxImage.Warning => ("\uE7BA", "Admin.Warning"),
            MessageBoxImage.Question => ("\uE897", "Admin.Info"),
            MessageBoxImage.Information => ("\uE946", "Admin.Info"),
            _ => (null, null),
        };
        if (glyph == null) return;
        IconText.Text = glyph;
        IconText.SetResourceReference(TextBlock.ForegroundProperty, brush!);
        IconText.Visibility = Visibility.Visible;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+C copie le message, comme dans la boîte de Windows.
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control) return;
        try
        {
            Clipboard.SetText($"{Title}{Environment.NewLine}{MessageText.Text}");
        }
        catch (Exception)
        {
            // Presse-papiers occupé : sans importance.
        }
        e.Handled = true;
    }
}
