using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LibreBooth.Core.Util;

namespace LibreBooth.App.Infrastructure;

/// <summary>
/// Défilement à la molette et au pavé tactile, pour toutes les fenêtres de LibreBooth.
/// <para>
/// Le pavé tactile d'un portable envoie une rafale de petits mouvements ; WPF faisait défiler de 3 lignes à chacun,
/// si bien que la page partait d'un coup. Ici, le défilement suit l'ampleur du geste : une ligne tous les 40 crans
/// (un cran de molette classique vaut 120, soit 3 lignes, comme avant). Quand une liste est déjà au bout, c'est la
/// page autour qui continue de défiler.
/// </para>
/// </summary>
internal static class WheelScrolling
{
    /// <summary>Après cette pause, un reste de petit geste est oublié (pas de saut au geste suivant).</summary>
    private const int PauseMilliseconds = 400;

    private static readonly ConditionalWeakTable<ScrollViewer, Pending> PendingByViewer = new();
    private static bool _registered;

    private sealed class Pending
    {
        public double Delta;
        public int Timestamp;
    }

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnMouseWheel));
    }

    private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer viewer) return;
        // Réglage Windows « faire défiler un écran à la fois » : on garde le comportement normal.
        var linesPerNotch = SystemParameters.WheelScrollLines;
        if (linesPerNotch <= 0) return;

        var canMove = e.Delta > 0 ? viewer.VerticalOffset > 0 : viewer.VerticalOffset < viewer.ScrollableHeight;
        if (viewer.ScrollableHeight <= 0 || !canMove)
        {
            // Liste au bout (ou sans défilement) : la page autour prend le relais.
            e.Handled = true;
            var parent = FindParentScrollViewer(viewer);
            parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = viewer });
            return;
        }

        var pending = PendingByViewer.GetOrCreateValue(viewer);
        if (Math.Sign(pending.Delta) != Math.Sign(e.Delta) || unchecked(e.Timestamp - pending.Timestamp) > PauseMilliseconds) pending.Delta = 0;
        pending.Timestamp = e.Timestamp;

        var lines = WheelSteps.TakeLines(ref pending.Delta, e.Delta, linesPerNotch);
        for (var i = 0; i < Math.Abs(lines); i++)
        {
            if (lines > 0) viewer.LineUp();
            else viewer.LineDown();
        }
        e.Handled = true;
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject start)
    {
        var current = GetParent(start);
        while (current != null)
        {
            if (current is ScrollViewer viewer) return viewer;
            current = GetParent(current);
        }
        return null;
    }

    private static DependencyObject? GetParent(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
}
