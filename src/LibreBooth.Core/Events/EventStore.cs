using System.Globalization;
using System.Text;
using LibreBooth.Core.Util;
using Serilog;

namespace LibreBooth.Core.Events;

public sealed class EventStats
{
    public int Sessions { get; set; }
    public int Photos { get; set; }
    public int Prints { get; set; }
    /// <summary>Séries refusées par les invités (bouton « Recommencer »).</summary>
    public int Retakes { get; set; }
}

public sealed class EventInfo
{
    public string Name { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Today;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public EventStats Stats { get; set; } = new();
}

/// <summary>Une session terminée (une série de photos et son montage), enregistrée dans le dossier caché de l'événement.</summary>
public sealed class SessionRecord
{
    public string Id { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string PrintProfile { get; set; } = "";
    public List<string> Photos { get; set; } = new();
    public string Composite { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int Prints { get; set; }
}

/// <summary>
/// Un événement : un dossier « 2026-11-23 Mariage Julie et Tom » avec ce que l'opérateur cherche (Photos, Montages,
/// Photos refusées) et un dossier caché (.librebooth) pour ce qui ne sert qu'à LibreBooth : informations et
/// statistiques, fiches des sessions, personnalisation.
/// </summary>
public sealed class BoothEvent
{
    public const string PhotosFolderName = "Photos";
    public const string CompositesFolderName = "Montages";
    public const string RefusedFolderName = "Photos refusées";
    public const string DemoPrintsFolderName = "Impressions (démo)";
    public const string DataFolderName = ".librebooth";
    internal const string InfoFileName = "event.json";
    internal const string SessionsFolderName = "sessions";

    internal BoothEvent(string folder, EventInfo info)
    {
        Folder = folder;
        Info = info;
    }

    /// <summary>Dossier de l'événement. Il suit le nom de l'événement : il change quand l'événement est renommé.</summary>
    public string Folder { get; internal set; }
    public EventInfo Info { get; }
    public string Name => Info.Name;
    public DateTime Date => Info.Date;

    /// <summary>Photos prises par l'appareil.</summary>
    public string OriginalsFolder => Path.Combine(Folder, PhotosFolderName);
    /// <summary>Montages (JPEG) imprimés ou à imprimer.</summary>
    public string CompositesFolder => Path.Combine(Folder, CompositesFolderName);
    /// <summary>Séries refusées par les invités (bouton « Recommencer ») : photos et montage gardés à part.</summary>
    public string RefusedFolder => Path.Combine(Folder, RefusedFolderName);
    /// <summary>Impressions du mode démo (fichiers PDF), créé seulement en mode démo.</summary>
    public string DemoPrintsFolder => Path.Combine(Folder, DemoPrintsFolderName);
    /// <summary>Dossier caché : informations, fiches des sessions et personnalisation.</summary>
    public string DataFolder => DataFolderFor(Folder);
    public string SessionsFolder => Path.Combine(DataFolder, SessionsFolderName);
    internal string InfoFile => Path.Combine(DataFolder, InfoFileName);

    public static string DataFolderFor(string eventFolder) => Path.Combine(eventFolder, DataFolderName);

    public override string ToString() => $"{Name} ({TextUtil.FormatDate(Date)})";
}

/// <summary>Création, ouverture, conversion et statistiques des événements (un dossier par événement).</summary>
public sealed partial class EventStore
{
    /// <summary>Nom donné quand aucun nom n'a été tapé (il apparaît sur les impressions via {evenement}).</summary>
    public const string DefaultEventName = "Mon événement";
    private const int MaxFolderNameLength = 60;

    private readonly ILogger _log = Serilog.Log.ForContext<EventStore>();
    private readonly Dictionary<string, BoothEvent> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public EventStore(string eventsFolder)
    {
        EventsFolder = Path.GetFullPath(eventsFolder);
        Directory.CreateDirectory(EventsFolder);
    }

    public string EventsFolder { get; }

    /// <summary>Nom du dossier d'un événement : « 2026-11-23 Mariage Julie et Tom ».</summary>
    public static string FolderNameFor(string? name, DateTime date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + SafeFolderText(name);

    /// <summary>Le nom tel quel, sans les caractères interdits dans un nom de dossier Windows.</summary>
    internal static string SafeFolderText(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        var lastWasSpace = false;
        foreach (var c in (name ?? "").Trim())
        {
            var replacement = c is '/' or '\\' or ':' ? '-' : invalid.Contains(c) || char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c;
            if (replacement == ' ')
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }
            sb.Append(replacement);
        }

        var text = sb.ToString().Trim().TrimEnd('.', ' ');
        if (text.Length > MaxFolderNameLength) text = text[..MaxFolderNameLength].TrimEnd('.', ' ');
        return text.Length == 0 ? DefaultEventName : text;
    }

    public BoothEvent Create(string? name, DateTime date)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? DefaultEventName : name.Trim();
        lock (_lock)
        {
            var folder = UniqueFolder(FolderNameFor(cleanName, date), except: null);
            var ev = new BoothEvent(folder, new EventInfo { Name = cleanName, Date = date.Date, CreatedAt = DateTime.Now });
            EnsureFolders(ev);
            SaveInfo(ev);
            _cache[folder] = ev;
            _log.Information("Événement créé : {Name} → {Folder}", cleanName, folder);
            return ev;
        }
    }

    /// <summary>Événement déjà créé avec ce nom et cette date, s'il y en a un.</summary>
    public BoothEvent? FindSame(string? name, DateTime date)
    {
        var wanted = (name ?? "").Trim();
        return ListAll().FirstOrDefault(e => e.Date == date.Date && string.Equals(e.Name.Trim(), wanted, StringComparison.CurrentCultureIgnoreCase));
    }

    public BoothEvent? Open(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        lock (_lock)
        {
            var full = Path.GetFullPath(folder);
            if (_cache.TryGetValue(full, out var cached) && Directory.Exists(cached.Folder)) return cached;
            if (!Directory.Exists(full)) return null;

            // Événement d'une version 1.x copié après le démarrage : converti au passage.
            if (NeedsUpgrade(full)) full = UpgradeLegacyEvent(full).To;

            EventInfo? info = null;
            var file = Path.Combine(BoothEvent.DataFolderFor(full), BoothEvent.InfoFileName);
            try
            {
                if (File.Exists(file)) info = JsonDefaults.Read<EventInfo>(file);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Informations de l'événement illisibles dans {Folder} : reconstruites", full);
            }

            info ??= InfoFromFolderName(full);
            info.Stats ??= new EventStats();
            var ev = new BoothEvent(full, info);
            EnsureFolders(ev);
            _cache[full] = ev;
            return ev;
        }
    }

    /// <summary>Les événements, du plus récent au plus ancien (date de l'événement, puis date de création).</summary>
    public IReadOnlyList<BoothEvent> ListAll()
    {
        var list = new List<BoothEvent>();
        foreach (var dir in Directory.GetDirectories(EventsFolder))
        {
            // Un dossier sans les informations ni les photos d'un événement n'est pas listé (dossier ajouté à la main…).
            var hasInfo = File.Exists(Path.Combine(BoothEvent.DataFolderFor(dir), BoothEvent.InfoFileName));
            if (!hasInfo && !Directory.Exists(Path.Combine(dir, BoothEvent.PhotosFolderName)) && !NeedsUpgrade(dir)) continue;
            var ev = Open(dir);
            if (ev != null) list.Add(ev);
        }
        return list.OrderByDescending(e => e.Date).ThenByDescending(e => e.Info.CreatedAt).ToList();
    }

    public void UpdateStats(BoothEvent ev, Action<EventStats> update)
    {
        lock (_lock)
        {
            update(ev.Info.Stats);
            SaveInfo(ev);
        }
    }

    public void UpdateStats(string eventFolder, Action<EventStats> update)
    {
        var ev = Open(eventFolder);
        if (ev != null) UpdateStats(ev, update);
    }

    /// <summary>
    /// Change le nom et la date de l'événement ; son dossier est renommé pour suivre (« 2026-11-23 Nouveau nom »).
    /// </summary>
    /// <returns>false si le dossier n'a pas pu être renommé (un fichier ouvert ailleurs) : il garde alors son ancien nom.</returns>
    public bool Rename(BoothEvent ev, string name, DateTime date)
    {
        lock (_lock)
        {
            ev.Info.Name = string.IsNullOrWhiteSpace(name) ? ev.Info.Name : name.Trim();
            ev.Info.Date = date.Date;
            SaveInfo(ev);
            return MoveToMatchingFolder(ev);
        }
    }

    public void SaveSession(BoothEvent ev, SessionRecord record)
    {
        lock (_lock)
        {
            JsonDefaults.Write(SessionFile(ev, record.Id), record);
        }
    }

    public void UpdateSession(BoothEvent ev, string sessionId, Action<SessionRecord> update)
    {
        lock (_lock)
        {
            var record = LoadSession(ev, sessionId);
            if (record == null) return;
            update(record);
            JsonDefaults.Write(SessionFile(ev, record.Id), record);
        }
    }

    /// <summary>
    /// Range une série que l'invité a refusée (bouton « Recommencer ») dans le dossier « Photos refusées » :
    /// ses photos et son montage sont gardés, mais elle sort de la galerie et ne compte plus comme une session.
    /// </summary>
    public void SetAsideRetakenSession(BoothEvent ev, string sessionId, IEnumerable<string> photos, string? composite)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(ev.RefusedFolder);
            foreach (var file in photos.Append(composite))
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) continue;
                try
                {
                    File.Move(file, Path.Combine(ev.RefusedFolder, Path.GetFileName(file)), overwrite: true);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Photo refusée laissée à sa place : {File}", file);
                }
            }

            var record = SessionFile(ev, sessionId);
            var counted = File.Exists(record);
            AtomicFile.TryDelete(record);
            if (counted) ev.Info.Stats.Sessions = Math.Max(0, ev.Info.Stats.Sessions - 1);
            ev.Info.Stats.Retakes++;
            SaveInfo(ev);
        }
    }

    public SessionRecord? LoadSession(BoothEvent ev, string sessionId)
    {
        var file = SessionFile(ev, sessionId);
        try
        {
            return File.Exists(file) ? JsonDefaults.Read<SessionRecord>(file) : null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Session illisible : {File}", file);
            return null;
        }
    }

    /// <summary>Les sessions les plus récentes d'abord.</summary>
    public IReadOnlyList<SessionRecord> ListSessions(BoothEvent ev, int max = int.MaxValue)
    {
        if (!Directory.Exists(ev.SessionsFolder)) return Array.Empty<SessionRecord>();
        return Directory.GetFiles(ev.SessionsFolder, "*.json")
            .Where(f => !Path.GetFileName(f).StartsWith('.'))
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Take(max)
            .Select(f => LoadSession(ev, Path.GetFileNameWithoutExtension(f)))
            .Where(r => r != null)
            .Select(r => r!)
            .ToList();
    }

    public static string NewSessionId() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

    private static string SessionFile(BoothEvent ev, string sessionId) => Path.Combine(ev.SessionsFolder, sessionId + ".json");

    /// <summary>Dossier libre pour ce nom : « … (2) », « … (3) » si un autre événement l'utilise déjà.</summary>
    private string UniqueFolder(string folderName, string? except)
    {
        var folder = Path.Combine(EventsFolder, folderName);
        for (var i = 2; IsTaken(folder, except); i++) folder = Path.Combine(EventsFolder, $"{folderName} ({i})");
        return folder;
    }

    private static bool IsTaken(string path, string? except) =>
        (Directory.Exists(path) || File.Exists(path)) && !SamePath(path, except);

    public static bool SamePath(string? a, string? b) =>
        a != null && b != null
        && string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private bool MoveToMatchingFolder(BoothEvent ev)
    {
        var target = UniqueFolder(FolderNameFor(ev.Info.Name, ev.Info.Date), except: ev.Folder);
        if (string.Equals(target, ev.Folder, StringComparison.Ordinal)) return true;
        var previous = ev.Folder;
        try
        {
            MoveDirectory(previous, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Le dossier de l'événement n'a pas pu être renommé : il garde son nom {Folder}", previous);
            return false;
        }
        _cache.Remove(previous);
        ev.Folder = target;
        _cache[target] = ev;
        _log.Information("Dossier de l'événement renommé : {From} → {To}", Path.GetFileName(previous), Path.GetFileName(target));
        return true;
    }

    /// <summary>Renomme un dossier, y compris quand seules les majuscules changent (« montages » → « Montages »).</summary>
    internal static void MoveDirectory(string from, string to)
    {
        if (!string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(from, to);
            return;
        }
        var temp = Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar) + ".renommage-" + Guid.NewGuid().ToString("N")[..6];
        Directory.Move(from, temp);
        try
        {
            Directory.Move(temp, to);
        }
        catch
        {
            Directory.Move(temp, from);
            throw;
        }
    }

    /// <summary>Nom et date repris du nom du dossier quand les informations de l'événement manquent.</summary>
    private static EventInfo InfoFromFolderName(string folder)
    {
        var name = Path.GetFileName(folder);
        if (name.Length > 11 && DateTime.TryParseExact(name[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                             && name[10] is ' ' or '_')
        {
            return new EventInfo { Name = name[11..].Trim(), Date = date, CreatedAt = Directory.GetCreationTime(folder) };
        }
        return new EventInfo { Name = name, Date = Directory.GetCreationTime(folder).Date, CreatedAt = Directory.GetCreationTime(folder) };
    }

    private static void EnsureFolders(BoothEvent ev)
    {
        Directory.CreateDirectory(ev.Folder);
        Directory.CreateDirectory(ev.OriginalsFolder);
        Directory.CreateDirectory(ev.CompositesFolder);
        CreateHiddenFolder(ev.DataFolder);
        Directory.CreateDirectory(ev.SessionsFolder);
    }

    /// <summary>Crée le dossier et le cache dans l'Explorateur Windows (il reste visible avec « Éléments masqués »).</summary>
    internal static void CreateHiddenFolder(string path)
    {
        var info = Directory.CreateDirectory(path);
        try
        {
            if (!info.Attributes.HasFlag(FileAttributes.Hidden)) info.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception)
        {
            // Dossier visible : sans conséquence.
        }
    }

    private static void SaveInfo(BoothEvent ev)
    {
        CreateHiddenFolder(ev.DataFolder);
        JsonDefaults.Write(ev.InfoFile, ev.Info);
    }
}
