using LibreBooth.Core.Config;
using LibreBooth.Core.Util;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace LibreBooth.Core.Sharing;

/// <summary>Fabrique le message d'un invité à partir des réglages (objet, texte, pièces jointes).</summary>
public static class EmailComposer
{
    public static MimeMessage Build(EmailSettings settings, EmailJob job)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(settings.FromName) ? "LibreBooth" : settings.FromName, settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(job.To));
        message.Subject = TextUtil.ApplyVariables(settings.Subject, job.EventName, job.EventDate);

        var builder = new BodyBuilder
        {
            TextBody = TextUtil.ApplyVariables(settings.Body, job.EventName, job.EventDate),
        };
        foreach (var attachment in job.Attachments.Where(File.Exists))
        {
            builder.Attachments.Add(attachment);
        }
        message.Body = builder.ToMessageBody();
        return message;
    }
}

/// <summary>Envoi par un serveur SMTP (Gmail, Orange, Free, OVH, Brevo…) avec MailKit.</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly Func<EmailSettings> _settings;

    public SmtpEmailSender(Func<EmailSettings> settings)
    {
        _settings = settings;
    }

    public async Task SendAsync(EmailJob job, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var message = EmailComposer.Build(settings, job);
        await SendMessageAsync(settings, message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>E-mail de test envoyé depuis l'admin.</summary>
    public static Task SendTestAsync(EmailSettings settings, string to, CancellationToken cancellationToken = default)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(settings.FromName) ? "LibreBooth" : settings.FromName, settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(to.Trim()));
        message.Subject = "Test LibreBooth";
        message.Body = new TextPart("plain")
        {
            Text = "Bravo, l'envoi d'e-mails de LibreBooth fonctionne !\n\nEnvoyé le " + DateTime.Now.ToString("dd.MM.yyyy à HH:mm") + ".",
        };
        return SendMessageAsync(settings, message, cancellationToken);
    }

    private static async Task SendMessageAsync(EmailSettings settings, MimeMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpHost)) throw new InvalidOperationException("Serveur SMTP non renseigné.");
        if (string.IsNullOrWhiteSpace(settings.FromAddress)) throw new InvalidOperationException("Adresse d'expéditeur non renseignée.");

        using var client = new SmtpClient { Timeout = 30_000 };
        await client.ConnectAsync(settings.SmtpHost.Trim(), settings.SmtpPort, ToMailKit(settings.Security), cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            var password = PasswordProtector.Unprotect(settings.ProtectedPassword);
            await client.AuthenticateAsync(settings.Username.Trim(), password, cancellationToken).ConfigureAwait(false);
        }
        await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private static SecureSocketOptions ToMailKit(SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto,
    };
}
