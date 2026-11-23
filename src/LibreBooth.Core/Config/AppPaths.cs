namespace LibreBooth.Core.Config;

/// <summary>Emplacements des données de LibreBooth (configuration, journaux, modèles, événements).</summary>
public sealed class AppPaths
{
    private AppPaths(string dataRoot)
    {
        DataRoot = dataRoot;
        ConfigFile = Path.Combine(dataRoot, "config.json");
        LogsFolder = Path.Combine(dataRoot, "logs");
        TemplatesFolder = Path.Combine(dataRoot, "Modeles");
        EventsFolder = Path.Combine(dataRoot, "Evenements");
        DriversFolder = Path.Combine(dataRoot, "Pilotes");
    }

    public string DataRoot { get; }
    public string ConfigFile { get; }
    public string LogsFolder { get; }
    public string TemplatesFolder { get; }
    public string EventsFolder { get; }
    /// <summary>
    /// Fichiers des fabricants (SDK Canon, API d'état DNP), non livrés avec la version publique : on les copie ici,
    /// sans droits d'administrateur, et LibreBooth les trouve au lancement suivant.
    /// </summary>
    public string DriversFolder { get; }

    /// <summary>
    /// Dossiers essayés, dans l'ordre, quand aucun dossier n'est imposé (--data) :
    /// 1. Documents\LibreBooth s'il contient déjà des réglages (installations existantes) ;
    /// 2. C:\Users\&lt;nom&gt;\LibreBooth : le dossier par défaut ;
    /// 3. C:\Users\&lt;nom&gt;\AppData\Local\LibreBooth en dernier recours.
    /// Documents n'est plus le choix par défaut : la protection « Accès contrôlé aux dossiers » de Windows y bloque
    /// les logiciels qu'elle ne connaît pas, et il est souvent synchronisé par OneDrive (les photos partiraient dans le cloud).
    /// </summary>
    public static IEnumerable<string> DefaultCandidates(bool demo = false)
    {
        var name = demo ? "LibreBooth-demo" : "LibreBooth";
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documents) && File.Exists(Path.Combine(documents, name, "config.json")))
        {
            yield return Path.Combine(documents, name);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) yield return Path.Combine(profile, name);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, name);
    }

    /// <param name="dataRoot">Dossier imposé (option --data) ; sinon le premier dossier utilisable de <see cref="DefaultCandidates"/>.</param>
    public static AppPaths Create(string? dataRoot = null, bool demo = false)
    {
        if (!string.IsNullOrWhiteSpace(dataRoot)) return Prepare(Path.GetFullPath(dataRoot));

        Exception? lastError = null;
        foreach (var candidate in DefaultCandidates(demo))
        {
            try
            {
                return Prepare(Path.GetFullPath(candidate));
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw new IOException("Aucun dossier utilisable pour les données de LibreBooth (dernière erreur : " + lastError?.Message + ")", lastError);
    }

    /// <summary>Crée les dossiers et vérifie qu'on peut vraiment y écrire (un dossier protégé peut exister mais refuser l'écriture).</summary>
    private static AppPaths Prepare(string root)
    {
        var paths = new AppPaths(root);
        Directory.CreateDirectory(paths.DataRoot);
        Directory.CreateDirectory(paths.LogsFolder);
        Directory.CreateDirectory(paths.TemplatesFolder);
        Directory.CreateDirectory(paths.EventsFolder);
        Directory.CreateDirectory(paths.DriversFolder);
        var probe = Path.Combine(root, ".essai-ecriture-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
        return paths;
    }
}
