using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LibreBooth.App.Infrastructure;
using LibreBooth.Core.Design;
using LibreBooth.Core.Events;
using LibreBooth.Core.Util;
using Serilog;

namespace LibreBooth.App.Admin;

/// <summary>
/// Choix de l'événement, au lancement de LibreBooth (après le code PIN) et depuis l'admin (« Changer d'événement… ») :
/// ouvrir un événement existant, ou en créer un avec son nom, sa date et, au choix, la personnalisation d'un autre.
/// </summary>
public partial class EventChooserWindow : Window
{
    private const int MaxThumbnails = 40;

    private readonly ILogger _log = Log.ForContext<EventChooserWindow>();
    private readonly EventStore _events;
    private readonly string? _currentFolder;
    private readonly bool _atStartup;
    private readonly ObservableCollection<EventItem> _items = new();

    /// <param name="currentFolder">Événement ouvert en dernier (présélectionné), ou null.</param>
    /// <param name="atStartup">Au lancement : « Quitter LibreBooth » au lieu d'« Annuler ».</param>
    public EventChooserWindow(EventStore events, string? currentFolder, bool atStartup)
    {
        InitializeComponent();
        _events = events;
        _currentFolder = currentFolder;
        _atStartup = atStartup;
        EventsList.ItemsSource = _items;
        NewDatePicker.SelectedDate = DateTime.Today;
        FolderHelpText.Text = $"Photos rangées dans {events.EventsFolder}, un dossier par événement : Photos, Montages et Photos refusées.";

        if (atStartup)
        {
            ShowInTaskbar = true;
            CancelButton.Content = "Quitter LibreBooth";
        }
        else
        {
            Title = "LibreBooth – Changer d'événement";
            HeaderTitle.Text = "Changer d'événement";
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            CancelButton.Content = "Annuler";
        }

        Loaded += (_, _) =>
        {
            LoadEvents(loadThumbnailsNow: false);
            if (_items.Count == 0) NewNameBox.Focus();
            else EventsList.Focus();
        };
    }

    /// <summary>L'événement ouvert ou créé ; null si l'opérateur a annulé (ou quitté LibreBooth au lancement).</summary>
    public BoothEvent? Chosen { get; private set; }

    /// <summary>Remplissage sans afficher la fenêtre (captures de vérification).</summary>
    internal void LoadForCapture() => LoadEvents(loadThumbnailsNow: true);

    private void LoadEvents(bool loadThumbnailsNow)
    {
        _items.Clear();
        IReadOnlyList<BoothEvent> all;
        try
        {
            all = _events.ListAll();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Liste des événements illisible");
            all = Array.Empty<BoothEvent>();
        }

        foreach (var ev in all)
        {
            var isCurrent = EventStore.SamePath(ev.Folder, _currentFolder);
            _items.Add(new EventItem(ev, isCurrent ? (_atStartup ? "Dernier ouvert" : "En cours") : null));
        }
        EventsList.SelectedItem = _items.FirstOrDefault(i => i.Badge != null) ?? _items.FirstOrDefault();
        if (EventsList.SelectedItem != null) EventsList.ScrollIntoView(EventsList.SelectedItem);
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = _items.Count > 0;

        var sources = new List<CopySource> { new("Neuve : le thème de LibreBooth", null) };
        sources.AddRange(all.Select(ev => new CopySource($"Comme « {ev.Name} » ({TextUtil.FormatDate(ev.Date)})", ev)));
        CopyCombo.ItemsSource = sources;
        CopyCombo.SelectedIndex = 0;

        // Vignette : le dernier montage de chaque événement, préparé en arrière-plan.
        var withThumbnails = _items.Take(MaxThumbnails).ToList();
        if (loadThumbnailsNow)
        {
            foreach (var item in withThumbnails) item.Thumbnail = LoadThumbnail(item.Event);
            return;
        }
        _ = Task.Run(() =>
        {
            foreach (var item in withThumbnails)
            {
                var image = LoadThumbnail(item.Event);
                if (image != null) Dispatcher.BeginInvoke(() => item.Thumbnail = image);
            }
        });
    }

    private static ImageSource? LoadThumbnail(BoothEvent ev)
    {
        try
        {
            if (!Directory.Exists(ev.CompositesFolder)) return null;
            var latest = Directory.EnumerateFiles(ev.CompositesFolder, "*.jpg")
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .FirstOrDefault();
            return latest == null ? null : ImageLoader.FromFile(latest, decodePixelWidth: 230);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- Créer

    private void NewNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Create();
    }

    private void Create_Click(object sender, RoutedEventArgs e) => Create();

    private void Create()
    {
        NewErrorText.Text = "";
        var name = NewNameBox.Text.Trim();
        if (name.Length == 0)
        {
            NewErrorText.Text = "Tapez d'abord le nom de l'événement.";
            NewNameBox.Focus();
            return;
        }
        var date = NewDatePicker.SelectedDate ?? DateTime.Today;

        var same = _events.FindSame(name, date);
        if (same != null)
        {
            var answer = MessageBox.Show(this,
                $"L'événement « {same.Name} » du {TextUtil.FormatDate(same.Date)} existe déjà.\n\n"
                + "Oui : ouvrir cet événement\nNon : créer quand même un deuxième événement",
                "LibreBooth", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                Choose(same);
                return;
            }
        }

        BoothEvent created;
        try
        {
            created = _events.Create(name, date);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Création de l'événement impossible");
            NewErrorText.Text = "Création impossible : " + ex.Message;
            return;
        }

        if (CopyCombo.SelectedItem is CopySource { Event: { } source })
        {
            try
            {
                CustomizationStore.CopyFrom(source.Folder, created.Folder);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Personnalisation de {Source} non reprise", source.Name);
                MessageBox.Show(this,
                    $"L'événement est créé, mais la personnalisation de « {source.Name} » n'a pas pu être reprise :\n{ex.Message}\n\n"
                    + "Vous pourrez la reprendre dans l'onglet Personnalisation.",
                    "LibreBooth", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        Choose(created);
    }

    // ---------------------------------------------------------------- Ouvrir

    private void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void EventsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControlFromSource(source) && EventsList.SelectedItem != null) OpenSelected();
    }

    /// <summary>Double-clic sur un événement (et pas sur la barre de défilement).</summary>
    private bool ItemsControlFromSource(DependencyObject source)
    {
        for (var current = source; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.ListBoxItem) return true;
            if (ReferenceEquals(current, EventsList)) return false;
        }
        return false;
    }

    private void EventsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OpenSelected();
    }

    private void OpenSelected()
    {
        OpenErrorText.Text = "";
        if (EventsList.SelectedItem is not EventItem item)
        {
            OpenErrorText.Text = "Choisissez d'abord un événement dans la liste.";
            return;
        }
        if (!Directory.Exists(item.Event.Folder))
        {
            OpenErrorText.Text = "Le dossier de cet événement n'existe plus.";
            LoadEvents(loadThumbnailsNow: false);
            return;
        }
        Choose(item.Event);
    }

    private void Choose(BoothEvent ev)
    {
        Chosen = ev;
        _log.Information("Événement choisi : {Event} ({Folder})", ev.Name, ev.Folder);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // ---------------------------------------------------------------- Lignes de la liste

    private sealed record CopySource(string Label, BoothEvent? Event);

    private sealed class EventItem : INotifyPropertyChanged
    {
        private ImageSource? _thumbnail;

        public EventItem(BoothEvent ev, string? badge)
        {
            Event = ev;
            Badge = badge;
            var stats = ev.Info.Stats;
            StatsText = stats.Sessions == 0 && stats.Prints == 0
                ? "Aucune photo pour l'instant"
                : $"{Plural(stats.Sessions, "session")} · {Plural(stats.Photos, "photo")} · {Plural(stats.Prints, "impression")}";
        }

        public BoothEvent Event { get; }
        public string Name => Event.Name;
        public string DateText => Capitalize(TextUtil.FormatLongDate(Event.Date));
        public string StatsText { get; }
        public string? Badge { get; }
        public Visibility BadgeVisibility => Badge == null ? Visibility.Collapsed : Visibility.Visible;

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                _thumbnail = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static string Plural(int count, string word) => $"{count} {word}{(count > 1 ? "s" : "")}";

        private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];
    }
}
