using System.Net;
using System.Net.Sockets;
using System.Text;
using LibreBooth.Core.Config;
using LibreBooth.Core.Sharing;
using MimeKit;

namespace LibreBooth.Core.Tests;

/// <summary>Envoi réel par MailKit vers un mini-serveur SMTP local (sans internet ni compte e-mail).</summary>
public sealed class SmtpSenderTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Guest_email_contains_subject_text_and_the_photo()
    {
        using var server = new FakeSmtpServer();
        var photo = Path.Combine(_temp.Root, "montage.jpg");
        File.WriteAllBytes(photo, DemoJpeg());
        var settings = new EmailSettings
        {
            SmtpHost = "127.0.0.1",
            SmtpPort = server.Port,
            Security = SmtpSecurity.None,
            FromAddress = "booth@example.com",
            FromName = "Mon photobooth",
            Subject = "Votre photo – {evenement}",
            Body = "Merci d'être venus à {evenement} le {date} !",
        };
        var sender = new SmtpEmailSender(() => settings);
        var job = new EmailJob
        {
            To = "invite@example.com",
            EventName = "Mariage de Marie",
            EventDate = new DateTime(2026, 6, 20),
            Attachments = { photo },
        };

        await sender.SendAsync(job, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        var raw = Assert.Single(server.Messages);
        var message = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(raw)));
        Assert.Equal("Votre photo – Mariage de Marie", message.Subject);
        Assert.Equal("invite@example.com", message.To.Mailboxes.Single().Address);
        Assert.Equal("booth@example.com", message.From.Mailboxes.Single().Address);
        Assert.Contains("Merci d'être venus à Mariage de Marie le 20.06.2026 !", message.TextBody);
        var attachment = Assert.Single(message.Attachments.OfType<MimePart>());
        Assert.Equal("montage.jpg", attachment.FileName);
    }

    [Fact]
    public async Task Missing_sender_address_is_reported_clearly()
    {
        var sender = new SmtpEmailSender(() => new EmailSettings { SmtpHost = "127.0.0.1", FromAddress = "" });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.SendAsync(new EmailJob { To = "invite@example.com" }, CancellationToken.None));

        Assert.Contains("expéditeur", error.Message);
    }

    private static byte[] DemoJpeg() => LibreBooth.Core.Layouts.DemoImageFactory.RenderJpeg(320, 200, 1, "Test");

    /// <summary>Serveur SMTP minimal : accepte tout et garde le contenu des messages reçus.</summary>
    private sealed class FakeSmtpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public FakeSmtpServer()
        {
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public List<string> Messages { get; } = new();

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch
                {
                    return;
                }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 localhost ESMTP test");
                while (await reader.ReadLineAsync() is { } line)
                {
                    var command = line.ToUpperInvariant();
                    if (command.StartsWith("EHLO", StringComparison.Ordinal))
                    {
                        await writer.WriteLineAsync("250-localhost");
                        await writer.WriteLineAsync("250 8BITMIME");
                    }
                    else if (command.StartsWith("HELO", StringComparison.Ordinal))
                    {
                        await writer.WriteLineAsync("250 localhost");
                    }
                    else if (command == "DATA")
                    {
                        await writer.WriteLineAsync("354 Fin des données avec <CR><LF>.<CR><LF>");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                        {
                            data.Append(dataLine.StartsWith("..", StringComparison.Ordinal) ? dataLine[1..] : dataLine).Append("\r\n");
                        }
                        lock (Messages) Messages.Add(data.ToString());
                        await writer.WriteLineAsync("250 OK");
                    }
                    else if (command == "QUIT")
                    {
                        await writer.WriteLineAsync("221 Au revoir");
                        return;
                    }
                    else
                    {
                        await writer.WriteLineAsync("250 OK");
                    }
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
