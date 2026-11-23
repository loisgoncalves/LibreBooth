namespace LibreBooth.Core.Util;

/// <summary>
/// Molette et pavé tactile : un cran de molette classique vaut 120 et fait défiler 3 lignes (réglage Windows) ;
/// un pavé tactile envoie une rafale de petits mouvements (10, 20…) qui doivent s'additionner, pas compter chacun
/// pour un cran entier.
/// </summary>
public static class WheelSteps
{
    public const double NotchDelta = 120;

    /// <summary>Lignes à faire défiler pour ce mouvement (positif vers le haut) ; le reste est gardé pour le suivant.</summary>
    public static int TakeLines(ref double pending, int delta, int linesPerNotch)
    {
        pending += delta;
        var unit = NotchDelta / Math.Max(1, linesPerNotch);
        var lines = (int)(pending / unit);
        pending -= lines * unit;
        return lines;
    }
}
