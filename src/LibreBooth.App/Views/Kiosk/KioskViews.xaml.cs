using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using LibreBooth.App.Kiosk;

namespace LibreBooth.App.Views.Kiosk;

public partial class AttractView : UserControl
{
    public AttractView() => InitializeComponent();
}

public partial class ChooseTemplateView : UserControl
{
    public ChooseTemplateView() => InitializeComponent();
}

public partial class ShootingView : UserControl
{
    private ShootingViewModel? _viewModel;

    public ShootingView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_viewModel != null) _viewModel.FlashRequested -= OnFlashRequested;
            _viewModel = e.NewValue as ShootingViewModel;
            if (_viewModel != null) _viewModel.FlashRequested += OnFlashRequested;
        };
        Unloaded += (_, _) =>
        {
            if (_viewModel != null) _viewModel.FlashRequested -= OnFlashRequested;
            _viewModel = null;
        };
        Loaded += (_, _) =>
        {
            if (_viewModel == null && DataContext is ShootingViewModel vm)
            {
                _viewModel = vm;
                vm.FlashRequested += OnFlashRequested;
            }
        };
    }

    private void OnFlashRequested(object? sender, EventArgs e)
    {
        var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(450)) { EasingFunction = new QuadraticEase() };
        FlashOverlay.BeginAnimation(OpacityProperty, animation);
    }
}

public partial class ProcessingView : UserControl
{
    public ProcessingView() => InitializeComponent();
}

public partial class ReviewView : UserControl
{
    public ReviewView() => InitializeComponent();
}

public partial class ThanksView : UserControl
{
    public ThanksView() => InitializeComponent();
}

public partial class ErrorView : UserControl
{
    public ErrorView() => InitializeComponent();
}
