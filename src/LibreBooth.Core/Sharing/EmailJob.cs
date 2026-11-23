namespace LibreBooth.Core.Sharing;

/// <summary>Un e-mail en attente d'envoi (fichier JSON dans le dossier outbox de l'événement).</summary>
public sealed class EmailJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string To { get; set; } = "";
    public string EventName { get; set; } = "";
    public DateTime EventDate { get; set; } = DateTime.Today;
    public string EventFolder { get; set; } = "";
    public List<string> Attachments { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.MinValue;
    public string? LastError { get; set; }
}

/// <summary>Trace d'un envoi réussi, sans l'adresse de l'invité.</summary>
public sealed class SentEmailRecord
{
    public string Id { get; set; } = "";
    public DateTime SentAt { get; set; }
    public int Attempts { get; set; }
    public List<string> Attachments { get; set; } = new();
}

public sealed record PendingEmail(string FilePath, EmailJob Job);

public interface IEmailSender
{
    Task SendAsync(EmailJob job, CancellationToken cancellationToken);
}
