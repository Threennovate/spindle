using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Spindle.Infrastructure.Email;

namespace Spindle.Tests.Integration;

/// <summary>Keeps sent emails in memory instead of sending them, and can pretend the mail server is down.</summary>
public sealed partial class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> sent = new();

    public bool Fail { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new IOException("The mail server is not reachable.");
        }

        sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public IReadOnlyList<EmailMessage> To(string email) => sent.Where(message => message.To == email).ToList();

    public EmailMessage LastTo(string email) => sent.Last(message => message.To == email);

    /// <summary>The token of the invitation link in an email.</summary>
    public static string InvitationToken(EmailMessage message) => InvitationLink().Match(message.TextBody).Groups["token"].Value;

    [GeneratedRegex(@"/invitations/(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex InvitationLink();
}
