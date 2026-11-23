using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LibreBooth.App.Controls;

/// <summary>Clavier tactile AZERTY pour saisir une adresse e-mail (minuscules, chiffres et symboles utiles).</summary>
public sealed class TouchKeyboard : StackPanel
{
    public static readonly DependencyProperty KeyCommandProperty =
        DependencyProperty.Register(nameof(KeyCommand), typeof(ICommand), typeof(TouchKeyboard));

    private static readonly string[][] Rows =
    {
        new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
        new[] { "a", "z", "e", "r", "t", "y", "u", "i", "o", "p" },
        new[] { "q", "s", "d", "f", "g", "h", "j", "k", "l", "m" },
        new[] { "w", "x", "c", "v", "b", "n", ".", "-", "_", "BACK" },
        new[] { "@", ".com", ".fr", "CLEAR" },
    };

    public TouchKeyboard()
    {
        Orientation = Orientation.Vertical;
        foreach (var row in Rows)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var key in row) panel.Children.Add(CreateKey(key));
            Children.Add(panel);
        }
    }

    public ICommand? KeyCommand
    {
        get => (ICommand?)GetValue(KeyCommandProperty);
        set => SetValue(KeyCommandProperty, value);
    }

    private Button CreateKey(string key)
    {
        var button = new Button
        {
            Content = key switch
            {
                "BACK" => "⌫",
                "CLEAR" => "Tout effacer",
                _ => key,
            },
            Tag = key,
        };
        button.SetResourceReference(StyleProperty, "KeyButton");
        if (key is "BACK") button.MinWidth = 150;
        if (key is "@" or ".com" or ".fr") button.MinWidth = 170;
        if (key is "CLEAR") button.MinWidth = 300;
        button.Click += (_, _) =>
        {
            var command = KeyCommand;
            if (command?.CanExecute(key) == true) command.Execute(key);
        };
        return button;
    }
}
