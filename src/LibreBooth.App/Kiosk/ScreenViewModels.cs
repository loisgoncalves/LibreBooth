using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreBooth.Core.Design;
using LibreBooth.Core.Events;
using LibreBooth.Core.Layouts;

namespace LibreBooth.App.Kiosk;

/// <summary>Données de la session en cours (une série de photos).</summary>
public sealed class SessionData
{
    public SessionData(string id, Template template, BoothEvent ev, DesignContext design)
    {
        Id = id;
        Template = template;
        Event = ev;
        Design = design;
    }

    public string Id { get; }
    public Template Template { get; }
    public BoothEvent Event { get; }
    /// <summary>Apparence de l'événement au début de la session (thème, textes, fond, logo).</summary>
    public DesignContext Design { get; }
    public List<string> Photos { get; } = new();
    public string? CompositePath { get; set; }
    public int PrintedCopies { get; set; }
    /// <summary>Nombre de séries déjà refusées par l'invité (bouton « Recommencer ») avant celle-ci.</summary>
    public int RetakeCount { get; init; }
}

public partial class AttractViewModel : ObservableObject
{
    private readonly Action _start;

    public AttractViewModel(Action start)
    {
        _start = start;
    }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _startText = "";

    /// <summary>Image de fond de l'accueil (propre à l'événement) ; sans image, la couleur de fond du thème.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackground))]
    private ImageSource? _background;

    public bool HasBackground => Background != null;

    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private string _notReadyTitle = "Le photobooth se prépare…";
    [ObservableProperty] private string _notReadyMessage = "";
    [ObservableProperty] private string? _operatorWarning;

    [RelayCommand]
    private void Start() => _start();
}

public sealed class TemplateCard
{
    public TemplateCard(Template template, ImageSource? preview)
    {
        Template = template;
        Preview = preview;
        Name = template.Name;
        Description = template.ShotCount == 1 ? "1 photo" : $"{template.ShotCount} photos";
        if (template.Layout.DuplicateStrip) Description += " · " + ReviewViewModel.PiecesName(template.Layout);
    }

    public Template Template { get; }
    public ImageSource? Preview { get; }
    public string Name { get; }
    public string Description { get; }
}

public partial class ChooseTemplateViewModel : ObservableObject
{
    private readonly Action<Template> _choose;
    private readonly Action _back;

    public ChooseTemplateViewModel(Action<Template> choose, Action back)
    {
        _choose = choose;
        _back = back;
    }

    public ObservableCollection<TemplateCard> Cards { get; } = new();

    [RelayCommand]
    private void Choose(TemplateCard? card)
    {
        if (card != null) _choose(card.Template);
    }

    [RelayCommand]
    private void Back() => _back();
}

public partial class ShootingViewModel : ObservableObject
{
    [ObservableProperty] private ImageSource? _liveImage;
    [ObservableProperty] private string? _countdownText;
    [ObservableProperty] private bool _isCountdownVisible;
    [ObservableProperty] private string _shotLabel = "";
    [ObservableProperty] private ImageSource? _previewImage;
    [ObservableProperty] private bool _isPreviewVisible;
    [ObservableProperty] private bool _isWaitingForCamera = true;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private double _mirrorScaleX = -1;
    [ObservableProperty] private double _rotationAngle;

    public event EventHandler? FlashRequested;

    public void Flash() => FlashRequested?.Invoke(this, EventArgs.Empty);

    public void Reset()
    {
        LiveImage = null;
        CountdownText = null;
        IsCountdownVisible = false;
        ShotLabel = "";
        PreviewImage = null;
        IsPreviewVisible = false;
        IsWaitingForCamera = true;
        StatusText = null;
    }
}

public partial class ProcessingViewModel : ObservableObject
{
    [ObservableProperty] private string _message = "Votre photo se prépare…";
}

public partial class ReviewViewModel : ObservableObject
{
    private readonly KioskViewModel _kiosk;

    public ReviewViewModel(KioskViewModel kiosk)
    {
        _kiosk = kiosk;
    }

    [ObservableProperty] private ImageSource? _compositeImage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IncreaseCopiesCommand))]
    [NotifyCanExecuteChangedFor(nameof(DecreaseCopiesCommand))]
    [NotifyPropertyChangedFor(nameof(CopiesLabel))]
    [NotifyPropertyChangedFor(nameof(CopiesDetail))]
    private int _copies = 1;

    /// <summary>Un exemplaire donne deux morceaux une fois coupé (marque-pages ou bandelettes) ; null sinon.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopiesDetail))]
    private string? _pieces;

    public string CopiesLabel => Copies > 1 ? "exemplaires" : "exemplaire";

    /// <summary>« soit 4 marque-pages » : ce que l'invité va réellement recevoir.</summary>
    public string? CopiesDetail => Pieces == null ? null : $"soit {Copies * 2} {Pieces}";

    /// <summary>« marque-pages » ou « bandelettes » pour un modèle imprimé deux fois puis coupé.</summary>
    public static string PiecesNoun(LayoutDefinition layout) =>
        DesignStyles.IsBookmark(layout.DesignStyle) ? "marque-pages" : "bandelettes";

    public static string PiecesName(LayoutDefinition layout) => "2 " + PiecesNoun(layout);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IncreaseCopiesCommand))]
    private int _maxCopies = 2;

    [ObservableProperty] private bool _showPrintButton;
    [ObservableProperty] private bool _showCopiesSelector;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    private bool _canPrint;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    private bool _isPrinting;

    [ObservableProperty] private string? _printHint;
    [ObservableProperty] private string? _statusMessage;

    /// <summary>« Recommencer » : tant que l'invité n'a rien imprimé, dans la limite fixée dans l'admin.</summary>
    [ObservableProperty] private bool _showRetakeButton;

    [RelayCommand]
    private void Retake() => _kiosk.Retake();

    [RelayCommand(CanExecute = nameof(CanIncreaseCopies))]
    private void IncreaseCopies() => Copies++;

    private bool CanIncreaseCopies() => Copies < MaxCopies;

    [RelayCommand(CanExecute = nameof(CanDecreaseCopies))]
    private void DecreaseCopies() => Copies--;

    private bool CanDecreaseCopies() => Copies > 1;

    [RelayCommand(CanExecute = nameof(CanPrintNow))]
    private Task Print() => _kiosk.PrintFromReviewAsync(Copies);

    private bool CanPrintNow() => CanPrint && !IsPrinting;

    [RelayCommand]
    private void Finish() => _kiosk.Finish();
}

/// <summary>
/// Question posée par-dessus l'écran quand le compteur de papier est à 0 (depuis la 3.2.0) : « Votre photo est-elle
/// sortie ? », puis le nouvel essai et, si rien ne sort, le message « Plus de papier ».
/// </summary>
public partial class PaperQuestionViewModel : ObservableObject
{
    private readonly Action<bool> _answer;
    private readonly Action _close;

    public PaperQuestionViewModel(Action<bool> answer, Action close)
    {
        _answer = answer;
        _close = close;
    }

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _message;
    /// <summary>Boutons « Oui » et « Non ».</summary>
    [ObservableProperty] private bool _showAnswers;
    /// <summary>Bouton « OK » (message final).</summary>
    [ObservableProperty] private bool _showOk;
    /// <summary>Roue d'attente pendant le nouvel essai.</summary>
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private void Yes() => _answer(true);

    [RelayCommand]
    private void No() => _answer(false);

    [RelayCommand]
    private void Ok() => _close();
}

public partial class ThanksViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "Merci !";
    [ObservableProperty] private string? _message;
}

public partial class ErrorViewModel : ObservableObject
{
    private readonly Action _retry;

    public ErrorViewModel(Action retry)
    {
        _retry = retry;
    }

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private int _secondsLeft;

    [RelayCommand]
    private void Retry() => _retry();
}
