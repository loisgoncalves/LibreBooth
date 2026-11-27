namespace LibreBooth.Core.Admin;

/// <summary>Taille d'une fenêtre, en pixels indépendants de la résolution (NaN : taille automatique).</summary>
public readonly record struct WindowSize(double Width, double Height, double MinWidth, double MinHeight);

/// <summary>
/// Fenêtres de l'admin sur un petit écran (ou avec un texte agrandi dans Windows), depuis la 3.2.2 : une fenêtre plus
/// grande que la zone utile de l'écran (sans la barre des tâches) y est ramenée, tailles minimales comprises. Rien
/// n'est coupé : son contenu défile.
/// </summary>
public static class WindowFit
{
    /// <summary>Marge laissée autour de la fenêtre.</summary>
    public const double Margin = 40;

    public static WindowSize Fit(WindowSize window, double areaWidth, double areaHeight)
    {
        var maxWidth = Math.Max(320, areaWidth - Margin);
        var maxHeight = Math.Max(240, areaHeight - Margin);
        return new WindowSize(
            double.IsNaN(window.Width) ? window.Width : Math.Min(window.Width, maxWidth),
            double.IsNaN(window.Height) ? window.Height : Math.Min(window.Height, maxHeight),
            Math.Min(window.MinWidth, maxWidth),
            Math.Min(window.MinHeight, maxHeight));
    }
}
