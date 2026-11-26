using LibreBooth.Core.Config;

namespace LibreBooth.Core.Hardware;

/// <summary>
/// Le papier vérifié par les invités (depuis la 3.2.0). Le compteur de tirages peut se tromper : compteur de
/// LibreBooth quand celui de la DNP est illisible, rouleau changé sans le signaler. Quand il est à 0, l'impression part
/// quand même, puis le photobooth demande à l'invité si sa photo est sortie :
/// <list type="bullet">
/// <item>« Oui » : les impressions continuent, sans autre question jusqu'au prochain rouleau ;</item>
/// <item>« Non » : sa photo est réimprimée une fois ; si rien ne sort encore, le rouleau est déclaré vide :
/// impression suspendue, photos toujours prises et enregistrées, jusqu'à « J'ai installé un nouveau rouleau ».</item>
/// </list>
/// </summary>
public static class PaperCheck
{
    /// <summary>Temps d'un premier tirage sur une DS620 (impression et coupe), avec une marge.</summary>
    public static readonly TimeSpan FirstPrint = TimeSpan.FromSeconds(20);
    /// <summary>Temps de chaque exemplaire suivant.</summary>
    public static readonly TimeSpan NextPrint = TimeSpan.FromSeconds(12);
    /// <summary>Sans réponse dans ce délai, la question disparaît : elle sera posée de nouveau à la prochaine impression.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(60);
    /// <summary>Durée d'affichage du message « Plus de papier » donné à l'invité.</summary>
    public static readonly TimeSpan EmptyNoticeDuration = TimeSpan.FromSeconds(15);

    /// <summary>Attente avant de demander si la photo est sortie : le temps que tous les exemplaires s'impriment.</summary>
    public static TimeSpan QuestionDelay(int copies) => FirstPrint + NextPrint * (Math.Max(1, copies) - 1);

    /// <summary>
    /// Faut-il demander à l'invité si sa photo est sortie ? Oui quand le compteur était à 0 au moment d'imprimer et que
    /// personne n'a encore répondu pour ce rouleau. Compteur inconnu : pas de question.
    /// </summary>
    public static bool ShouldAsk(int? remainingBefore, PaperReport report) =>
        report == PaperReport.None && remainingBefore is <= 0;

    /// <summary>Rouleau déclaré vide : le bouton « Imprimer » est grisé (les photos continuent).</summary>
    public static bool BlocksPrinting(PaperReport report) => report == PaperReport.Empty;

    /// <summary>
    /// Le compteur est remonté au-dessus de 0 (nouveau rouleau lu par la DNP, capacité du rouleau changée) : les
    /// réponses des invités ne valent plus.
    /// </summary>
    public static bool ShouldForget(int? remaining, PaperReport report) => report != PaperReport.None && remaining is > 0;

    /// <summary>Nom des travaux d'impression de LibreBooth dans la file Windows (annulés quand le rouleau est vide).</summary>
    public const string JobNamePrefix = "LibreBooth";

    public static bool IsLibreBoothJob(string? documentName) =>
        documentName != null && documentName.StartsWith(JobNamePrefix, StringComparison.OrdinalIgnoreCase);
}
