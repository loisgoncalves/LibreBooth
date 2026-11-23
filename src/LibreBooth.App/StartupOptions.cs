namespace LibreBooth.App;

/// <summary>
/// Options de la ligne de commande :
/// --demo (fausse caméra + impression PDF), --fenetre (pas de plein écran), --data &lt;dossier&gt;,
/// --captures &lt;dossier&gt; (parcours automatique qui enregistre chaque écran en image, pour vérifier l'affichage).
/// </summary>
public sealed class StartupOptions
{
    public static StartupOptions Current { get; set; } = new();

    public bool Demo { get; private set; }
    public bool Windowed { get; private set; }
    public bool IsRestart { get; private set; }
    public string? DataRoot { get; private set; }
    public string? CapturesFolder { get; private set; }
    public bool IsCaptureRun => CapturesFolder != null;
    /// <summary>--captures avec --materiel : le parcours automatique utilise le vrai appareil photo.</summary>
    public bool RealHardware { get; private set; }
    /// <summary>Impressions redirigées vers des PDF (démo et parcours de vérification) : aucun papier consommé.</summary>
    public bool PrintToPdf => Demo || IsCaptureRun;
    /// <summary>--diagnostic [rapport.txt] : contrôle du matériel puis arrêt.</summary>
    public string? DiagnosticReport { get; private set; }
    public bool IsDiagnosticRun { get; private set; }
    /// <summary>--verifier : fenêtre de vérification de l'installation (dossiers, pilotes, imprimante, appareil), puis arrêt.</summary>
    public bool IsVerifyRun { get; private set; }
    /// <summary>--verifier rapport.txt : la vérification écrit son rapport dans ce fichier, sans fenêtre.</summary>
    public string? VerifyReport { get; private set; }
    /// <summary>--diagnostic --imprimer : imprime aussi une page de test 10x15 et un marque-page (2 tirages).</summary>
    public bool PrintTestPages { get; private set; }
    /// <summary>--diagnostic --imprimer --fichier : les données envoyées par le pilote sont écrites dans des fichiers, sans rien imprimer.</summary>
    public bool PrintToFiles { get; private set; }
    /// <summary>--diagnostic --marque-page N : imprime seulement le marque-page de test, en N pages dans une même tâche.</summary>
    public int BookmarkPages { get; private set; }

    public static StartupOptions Parse(string[] args)
    {
        var options = new StartupOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--demo":
                    options.Demo = true;
                    break;
                case "--fenetre":
                case "--window":
                    options.Windowed = true;
                    break;
                case "--restart":
                    options.IsRestart = true;
                    break;
                case "--data" when i + 1 < args.Length:
                    options.DataRoot = args[++i];
                    break;
                case "--captures" when i + 1 < args.Length:
                    options.CapturesFolder = Path.GetFullPath(args[++i]);
                    break;
                case "--materiel":
                    options.RealHardware = true;
                    break;
                case "--imprimer":
                    options.PrintTestPages = true;
                    break;
                case "--fichier":
                    options.PrintToFiles = true;
                    break;
                case "--marque-page" when i + 1 < args.Length && int.TryParse(args[i + 1], out var pages):
                    options.BookmarkPages = Math.Clamp(pages, 1, 4);
                    options.PrintTestPages = true;
                    i++;
                    break;
                case "--verifier":
                    options.IsVerifyRun = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        options.VerifyReport = Path.GetFullPath(args[++i]);
                    }
                    break;
                case "--diagnostic":
                    options.IsDiagnosticRun = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        options.DiagnosticReport = Path.GetFullPath(args[++i]);
                    }
                    break;
            }
        }

        // Le parcours de vérification utilise la caméra de démonstration, sauf avec --materiel.
        if (options.IsCaptureRun && !options.RealHardware) options.Demo = true;
        return options;
    }
}
