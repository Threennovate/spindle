using System.ComponentModel.DataAnnotations;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Spindle.Infrastructure.Email;

public sealed record EmailMessage(string To, string Subject, string TextBody, string HtmlBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    [Required]
    public string Host { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    /// <summary><c>Auto</c> uses TLS when the server offers it; <c>None</c> for a local catcher such as Mailpit.</summary>
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.Auto;

    public string? UserName { get; set; }

    public string? Password { get; set; }

    [Required]
    [EmailAddress]
    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "Spindle";

    /// <summary>Where replies go, when the sender address is not read by anyone (e.g. a no-reply address).</summary>
    [EmailAddress]
    public string? ReplyToAddress { get; set; }
}

/// <summary>Sends mail through the SMTP server in configuration, e.g. the host's mail server in production.</summary>
internal sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var smtp = options.Value;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        if (!string.IsNullOrEmpty(smtp.ReplyToAddress))
        {
            mime.ReplyTo.Add(MailboxAddress.Parse(smtp.ReplyToAddress));
        }

        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();

        // Mail is sent while the person waits (e.g. to sign up), so an unreachable server gives up after half a minute
        // instead of MailKit's two.
        using var client = new SmtpClient { Timeout = (int)TimeSpan.FromSeconds(30).TotalMilliseconds };
        await client.ConnectAsync(smtp.Host, smtp.Port, smtp.Security, cancellationToken);
        if (!string.IsNullOrEmpty(smtp.UserName))
        {
            await client.AuthenticateAsync(smtp.UserName, smtp.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
