using LibreBooth.Core.Events;
using LibreBooth.Core.Util;
using Serilog;

namespace LibreBooth.Core.Sharing;

/// <summary>
/// File d'attente des e-mails, enregistrée sur disque : rien n'est perdu sans internet ni après un redémarrage.
/// Les envois ratés sont retentés de plus en plus espacés (30 s, 1 min, 2 min, 5 min puis toutes les 10 min).
/// </summary>
public sealed class EmailOutbox
{
    private readonly ILogger _log = Serilog.Log.ForContext<EmailOutbox>();
    private readonly EventStore _events;
    private readonly IEmailSender _sender;
    private readonly Func<bool> _isEnabled;
    private readonly Func<DateTime> _clock;
    private readonly SemaphoreSlim _processing = new(1, 1);
    private TaskCompletionSource _wake = NewSignal();
    private volatile bool _ignoreSchedule;

    public EmailOutbox(EventStore events, IEmailSender sender, Func<bool>? isEnabled = null, Func<DateTime>? clock = null)
    {
        _events = events;
        _sender = sender;
        _isEnabled = isEnabled ?? (() => true);
        _clock = clock ?? (() => DateTime.Now);
    }

    public int MaxAttempts { get; set; } = 200;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(20);
    public string? LastError { get; private set; }
    public DateTime? LastSentAt { get; private set; }

    public event EventHandler? Changed;

    public EmailJob Enqueue(BoothEvent ev, string to, IEnumerable<string> attachments)
    {
        var job = new EmailJob
        {
            To = to.Trim(),
            EventName = ev.Name,
            EventDate = ev.Date,
            EventFolder = ev.Folder,
            Attachments = attachments.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            CreatedAt = _clock(),
        };
        Directory.CreateDirectory(ev.OutboxFolder);
        JsonDefaults.Write(Path.Combine(ev.OutboxFolder, job.Id + ".json"), job);
        _events.UpdateStats(ev, s => s.EmailsQueued++);
        // On ne journalise jamais l'adresse de l'invité.
        _log.Information("E-mail mis en file d'attente ({Id}) pour {Event}", job.Id, ev.Name);
        Changed?.Invoke(this, EventArgs.Empty);
        TriggerNow(resetSchedule: false);
        return job;
    }

    /// <summary>Tous les e-mails en attente, de tous les événements (le plus ancien d'abord).</summary>
    public IReadOnlyList<PendingEmail> GetPending()
    {
        var list = new List<PendingEmail>();
        if (!Directory.Exists(_events.EventsFolder)) return list;
        foreach (var eventDir in Directory.GetDirectories(_events.EventsFolder))
        {
            var outbox = Path.Combine(eventDir, "outbox");
            if (!Directory.Exists(outbox)) continue;
            foreach (var file in Directory.GetFiles(outbox, "*.json"))
            {
                if (Path.GetFileName(file).StartsWith('.')) continue;
                try
                {
                    var job = JsonDefaults.Read<EmailJob>(file);
                    if (job != null) list.Add(new PendingEmail(file, job));
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "E-mail en attente illisible : {File}", file);
                }
            }
        }
        return list.OrderBy(p => p.Job.CreatedAt).ToList();
    }

    public int PendingCount => GetPending().Count;

    /// <summary>Envoie les e-mails dont l'heure est venue. Renvoie le nombre d'envois réussis.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        if (!_isEnabled()) return 0;
        if (!await _processing.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return 0;

        var sent = 0;
        var failed = false;
        try
        {
            var ignoreSchedule = _ignoreSchedule;
            _ignoreSchedule = false;
            var now = _clock();

            foreach (var pending in GetPending())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var job = pending.Job;
                if (!ignoreSchedule && job.NextAttemptAt > now) continue;

                try
                {
                    await _sender.SendAsync(job, cancellationToken).ConfigureAwait(false);
                    MarkSent(pending);
                    sent++;
                    LastError = null;
                    LastSentAt = _clock();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed = true;
                    job.Attempts++;
                    job.LastError = ex.Message;
                    job.NextAttemptAt = _clock() + Backoff(job.Attempts);
                    LastError = ex.Message;
                    if (job.Attempts >= MaxAttempts)
                    {
                        MoveToFailed(pending);
                    }
                    else
                    {
                        JsonDefaults.Write(pending.FilePath, job);
                    }
                    _log.Warning("Envoi d'e-mail impossible ({Id}, tentative {Attempt}) : {Error}", job.Id, job.Attempts, ex.Message);
                    // Sans doute pas de réseau : inutile d'essayer les suivants tout de suite.
                    break;
                }
            }
        }
        finally
        {
            _processing.Release();
        }

        if (sent > 0 || failed) Changed?.Invoke(this, EventArgs.Empty);
        return sent;
    }

    /// <summary>Boucle d'envoi en arrière-plan, jusqu'à l'arrêt du logiciel.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Erreur dans la file d'envoi des e-mails");
            }

            var signal = Volatile.Read(ref _wake);
            await Task.WhenAny(signal.Task, Task.Delay(PollInterval, cancellationToken)).ConfigureAwait(false);
            if (signal.Task.IsCompleted) Interlocked.CompareExchange(ref _wake, NewSignal(), signal);
        }
    }

    /// <summary>Relance l'envoi tout de suite (par exemple au retour du réseau).</summary>
    public void TriggerNow(bool resetSchedule = true)
    {
        if (resetSchedule) _ignoreSchedule = true;
        Volatile.Read(ref _wake).TrySetResult();
    }

    public static TimeSpan Backoff(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromSeconds(30),
        2 => TimeSpan.FromMinutes(1),
        3 => TimeSpan.FromMinutes(2),
        4 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(10),
    };

    private void MarkSent(PendingEmail pending)
    {
        var job = pending.Job;
        var eventFolder = Path.GetDirectoryName(Path.GetDirectoryName(pending.FilePath)!)!;
        var record = new SentEmailRecord
        {
            Id = job.Id,
            SentAt = _clock(),
            Attempts = job.Attempts + 1,
            Attachments = job.Attachments.Select(a => Path.GetFileName(a)).ToList(),
        };
        JsonDefaults.Write(Path.Combine(eventFolder, "envoyes", job.Id + ".json"), record);
        // L'adresse de l'invité disparaît avec ce fichier.
        File.Delete(pending.FilePath);
        _events.UpdateStats(eventFolder, s => s.EmailsSent++);
        _log.Information("E-mail envoyé ({Id})", job.Id);
    }

    private void MoveToFailed(PendingEmail pending)
    {
        var eventFolder = Path.GetDirectoryName(Path.GetDirectoryName(pending.FilePath)!)!;
        var failedFolder = Path.Combine(eventFolder, "echecs");
        Directory.CreateDirectory(failedFolder);
        File.Move(pending.FilePath, Path.Combine(failedFolder, Path.GetFileName(pending.FilePath)), overwrite: true);
        _log.Error("E-mail abandonné après {Attempts} tentatives ({Id})", pending.Job.Attempts, pending.Job.Id);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
