using System.Windows;
using System.Windows.Input;

namespace LibreBooth.App.Admin;

public enum LeaveBoothChoice
{
    Cancel,
    Admin,
    Quit,
}

/// <summary>Après le code PIN, depuis le photobooth : revenir à l'admin ou quitter LibreBooth.</summary>
public partial class LeaveBoothWindow : Window
{
    public LeaveBoothWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    public LeaveBoothChoice Choice { get; private set; } = LeaveBoothChoice.Cancel;

    private void Admin_Click(object sender, RoutedEventArgs e) => Finish(LeaveBoothChoice.Admin);

    private void Quit_Click(object sender, RoutedEventArgs e) => Finish(LeaveBoothChoice.Quit);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(LeaveBoothChoice.Cancel);

    private void Finish(LeaveBoothChoice choice)
    {
        Choice = choice;
        Close();
    }
}
