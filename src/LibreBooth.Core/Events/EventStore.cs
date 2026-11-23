using System.Globalization;
using LibreBooth.Core.Util;
using Serilog;

namespace LibreBooth.Core.Events;

public sealed class EventStats
{
    public int Sessions { get; set; }
    public int Photos { get; set; }
    public int Prints { get; set; }
    public int EmailsQueued { get; set; }
    public int EmailsSent { get; set; }
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

/// <summary>Une session terminée (une série de photos et son montage), enregistrée à côté du montage.</summary>
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
    public int Emails { get; set; }
}

/// <summary>Un événement ouvert : un dossier avec les photos, les montages et la file d'e-mails.</summary>
public sealed class BoothEvent
{
    internal BoothEvent(string folder, EventInfo info)
    {
        Folder = folder;
        Info = info;
    }

    public string Folder { get; }
    public EventInfo Info { get; }
    public string Name => Info.Name;
    public DateTime Date => Info.Date;

    public string OriginalsFolder => Path.Combine(Folder, "originaux");
    public string CompositesFolder => Path.Combine(Folder, "montages");
    public string OutboxFolder => Path.Combine(Folder, "outbox");
    public string SentFolder => Path.Combine(Folder, "envoyes");
    public string FailedFolder => Path.Combine(Folder, "echecs");
    /// <summary>Séries refusées par les invités (bouton « Recommencer ») : photos et montage gardés à part.</summary>
    public string RefusedFolder => Path.Combine(Folder, "refusees");
    public string DemoPrintsFolder => Path.Combine(Folder, "impressions-demo");
    internal string InfoFile => Path.Combine(Folder, "event.json");

    public override string ToString() => $"{Name} ({TextUtil.FormatDate(Date)})";
}

/// <summary>Création, ouverture et statistiques des événements (un dossier par événement).</summary>
public sealed class EventStore
{
    /// <summary>Nom provisoire, à remplacer dans l'admin (il apparaît sur les impressions via {evenement}).</summary>
    public const string DefaultEventName = "Mon événement";

    private readonly ILogger _log = Serilog.Log.ForContext<EventStore>();
    private readonly Dictionary<string, BoothEvent> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public EventStore(string eventsFolder)
    {
        EventsFolder = Path.GetFullPath(eventsFolder);
        Directory.CreateDirectory(EventsFolder);
    }

    public string EventsFolder { get; }

    public BoothEvent Create(string? name, DateTime date)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? DefaultEventName : name.Trim();
        lock (_lock)
        {
            var baseName = $"{date:yyyy-MM-dd}_{TextUtil.Slug(cleanName)}";
            var folder = Path.Combine(EventsFolder, baseName);
            for (var i = 2; Directory.Exists(folder); i++) folder = Path.Combine(EventsFolder, $"{baseName}-{i}");

            var ev = new BoothEvent(folder, new EventInfo { Name = cleanName, Date = date.Date, CreatedAt = DateTime.Now });
            EnsureFolders(ev);
            SaveInfo(ev);
            _cache[folder] = ev;
            _log.Information("Événement créé : {Name} → {Folder}", cleanName, folder);
            return ev;
        }
    }

    public BoothEvent? Open(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        lock (_lock)
        {
            var full = Path.GetFullPath(folder);
            if (_cache.TryGetValue(full, out var cached)) return cached;

            EventInfo? info = null;
            var file = Path.Combine(full, "event.json");
            try
            {
                if (File.Exists(file)) info = JsonDefaults.Read<EventInfo>(file);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "event.json illisible dans {Folder} : informations reconstruites", full);
            }

            info ??= new EventInfo { Name = Path.GetFileName(full), Date = Directory.GetCreationTime(full).Date };
            info.Stats ??= new EventStats();
            var ev = new BoothEvent(full, info);
            EnsureFolders(ev);
            _cache[full] = ev;
            return ev;
        }
    }

    public IReadOnlyList<BoothEvent> ListAll()
    {
        var list = new List<BoothEvent>();
        foreach (var dir in Directory.GetDirectories(EventsFolder))
        {
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

    public void Rename(BoothEvent ev, string name, DateTime date)
    {
        lock (_lock)
        {
            ev.Info.Name = string.IsNullOrWhiteSpace(name) ? ev.Info.Name : name.Trim();
            ev.Info.Date = date.Date;
            SaveInfo(ev);
        }
    }

    public void SaveSession(BoothEvent ev, SessionRecord record)
    {
        lock (_lock)
        {
            JsonDefaults.Write(Path.Combine(ev.CompositesFolder, record.Id + ".json"), record);
        }
    }

    public void UpdateSession(BoothEvent ev, string sessionId, Action<SessionRecord> update)
    {
        lock (_lock)
        {
            var record = LoadSession(ev, sessionId);
            if (record == null) return;
            update(record);
            JsonDefaults.Write(Path.Combine(ev.CompositesFolder, record.Id + ".json"), record);
        }
    }

    /// <summary>
    /// Range une série que l'invité a refusée (bouton « Recommencer ») dans le dossier « refusees » de l'événement :
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

            var record = Path.Combine(ev.CompositesFolder, sessionId + ".json");
            var counted = File.Exists(record);
            AtomicFile.TryDelete(record);
            if (counted) ev.Info.Stats.Sessions = Math.Max(0, ev.Info.Stats.Sessions - 1);
            ev.Info.Stats.Retakes++;
            SaveInfo(ev);
        }
    }

    public SessionRecord? LoadSession(BoothEvent ev, string sessionId)
    {
        var file = Path.Combine(ev.CompositesFolder, sessionId + ".json");
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
        if (!Directory.Exists(ev.CompositesFolder)) return Array.Empty<SessionRecord>();
        return Directory.GetFiles(ev.CompositesFolder, "*.json")
            .Where(f => !Path.GetFileName(f).StartsWith('.'))
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Take(max)
            .Select(f => LoadSession(ev, Path.GetFileNameWithoutExtension(f)))
            .Where(r => r != null)
            .Select(r => r!)
            .ToList();
    }

    public static string NewSessionId() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

    private static void EnsureFolders(BoothEvent ev)
    {
        Directory.CreateDirectory(ev.Folder);
        Directory.CreateDirectory(ev.OriginalsFolder);
        Directory.CreateDirectory(ev.CompositesFolder);
        Directory.CreateDirectory(ev.OutboxFolder);
        Directory.CreateDirectory(ev.SentFolder);
    }

    private static void SaveInfo(BoothEvent ev) => JsonDefaults.Write(ev.InfoFile, ev.Info);
}
