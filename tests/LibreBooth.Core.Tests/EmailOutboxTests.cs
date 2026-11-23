using LibreBooth.Core.Events;
using LibreBooth.Core.Sharing;

namespace LibreBooth.Core.Tests;

public sealed class EmailOutboxTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed class FakeSender : IEmailSender
    {
        public List<EmailJob> Sent { get; } = new();
        public Func<EmailJob, Exception?> FailWith { get; set; } = _ => null;

        public Task SendAsync(EmailJob job, CancellationToken cancellationToken)
        {
            var error = FailWith(job);
            if (error != null) throw error;
            Sent.Add(job);
            return Task.CompletedTask;
        }
    }

    private (EventStore Store, BoothEvent Event, string Photo) CreateEvent()
    {
        var store = new EventStore(Path.Combine(_temp.Root, "Evenements"));
        var ev = store.Create("Mariage", new DateTime(2026, 6, 20));
        var photo = Path.Combine(ev.CompositesFolder, "montage.jpg");
        File.WriteAllBytes(photo, new byte[] { 1, 2, 3 });
        return (store, ev, photo);
    }

    [Fact]
    public async Task Sent_email_leaves_the_outbox_and_the_address_is_erased()
    {
        var (store, ev, photo) = CreateEvent();
        var sender = new FakeSender();
        var outbox = new EmailOutbox(store, sender);

        outbox.Enqueue(ev, "invite@example.com", new[] { photo });
        Assert.Single(Directory.GetFiles(ev.OutboxFolder, "*.json"));

        Assert.Equal(1, await outbox.ProcessDueAsync());

        Assert.Empty(Directory.GetFiles(ev.OutboxFolder, "*.json"));
        var record = Assert.Single(Directory.GetFiles(ev.SentFolder, "*.json"));
        Assert.DoesNotContain("invite@example.com", File.ReadAllText(record));
        Assert.Equal("invite@example.com", Assert.Single(sender.Sent).To);
        Assert.Equal(1, ev.Info.Stats.EmailsQueued);
        Assert.Equal(1, ev.Info.Stats.EmailsSent);
    }

    [Fact]
    public async Task Failed_email_is_retried_after_the_delay()
    {
        var (store, ev, photo) = CreateEvent();
        var now = new DateTime(2026, 6, 20, 20, 0, 0);
        var sender = new FakeSender { FailWith = _ => new IOException("Pas de réseau") };
        var outbox = new EmailOutbox(store, sender, clock: () => now);
        outbox.Enqueue(ev, "invite@example.com", new[] { photo });

        Assert.Equal(0, await outbox.ProcessDueAsync());
        var pending = Assert.Single(outbox.GetPending());
        Assert.Equal(1, pending.Job.Attempts);
        Assert.Equal(now.AddSeconds(30), pending.Job.NextAttemptAt);
        Assert.Equal("Pas de réseau", outbox.LastError);

        sender.FailWith = _ => null;
        now = now.AddSeconds(10);
        Assert.Equal(0, await outbox.ProcessDueAsync());
        Assert.Empty(sender.Sent);

        now = now.AddSeconds(25);
        Assert.Equal(1, await outbox.ProcessDueAsync());
        Assert.Single(sender.Sent);
        Assert.Null(outbox.LastError);
    }

    [Fact]
    public async Task Retry_now_ignores_the_waiting_delay()
    {
        var (store, ev, photo) = CreateEvent();
        var sender = new FakeSender { FailWith = _ => new IOException("Pas de réseau") };
        var outbox = new EmailOutbox(store, sender);
        outbox.Enqueue(ev, "invite@example.com", new[] { photo });
        await outbox.ProcessDueAsync();

        sender.FailWith = _ => null;
        outbox.TriggerNow();

        Assert.Equal(1, await outbox.ProcessDueAsync());
    }

    [Fact]
    public async Task Pending_emails_survive_a_restart()
    {
        var (store, ev, photo) = CreateEvent();
        new EmailOutbox(store, new FakeSender(), isEnabled: () => false).Enqueue(ev, "invite@example.com", new[] { photo });

        var restartedStore = new EventStore(store.EventsFolder);
        var sender = new FakeSender();
        var restarted = new EmailOutbox(restartedStore, sender);

        Assert.Equal(1, restarted.PendingCount);
        Assert.Equal(1, await restarted.ProcessDueAsync());
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Disabled_email_keeps_messages_waiting()
    {
        var (store, ev, photo) = CreateEvent();
        var sender = new FakeSender();
        var outbox = new EmailOutbox(store, sender, isEnabled: () => false);
        outbox.Enqueue(ev, "invite@example.com", new[] { photo });

        Assert.Equal(0, await outbox.ProcessDueAsync());
        Assert.Equal(1, outbox.PendingCount);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public void Retry_delays_grow_then_stay_at_ten_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), EmailOutbox.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(1), EmailOutbox.Backoff(2));
        Assert.Equal(TimeSpan.FromMinutes(2), EmailOutbox.Backoff(3));
        Assert.Equal(TimeSpan.FromMinutes(5), EmailOutbox.Backoff(4));
        Assert.Equal(TimeSpan.FromMinutes(10), EmailOutbox.Backoff(5));
        Assert.Equal(TimeSpan.FromMinutes(10), EmailOutbox.Backoff(50));
    }
}
