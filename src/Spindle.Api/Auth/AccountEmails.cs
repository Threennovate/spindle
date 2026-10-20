using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Spindle.Api.Common;
using Spindle.Api.Email;
using Spindle.Infrastructure.Email;
using Spindle.Infrastructure.Identity;

namespace Spindle.Api.Auth;

/// <summary>Emails about the account itself: confirming the address, and resetting a forgotten password.</summary>
internal sealed class AccountEmails(
    UserManager<AppUser> users,
    IEmailSender emailSender,
    IOptions<AppOptions> app,
    ILogger<AccountEmails> logger)
{
    public async Task<bool> SendConfirmationAsync(AppUser user, CancellationToken cancellationToken)
    {
        var token = Encode(await users.GenerateEmailConfirmationTokenAsync(user));
        var link = app.Value.Link($"/confirm-email?user={user.Id}&token={token}");
        return await SendAsync(EmailTemplates.ConfirmEmail(user.Email!, user.FullName, link), cancellationToken);
    }

    public async Task<bool> SendPasswordResetAsync(AppUser user, CancellationToken cancellationToken)
    {
        var token = Encode(await users.GeneratePasswordResetTokenAsync(user));
        var link = app.Value.Link($"/reset-password?email={Uri.EscapeDataString(user.Email!)}&token={token}");
        return await SendAsync(EmailTemplates.PasswordReset(user.Email!, link), cancellationToken);
    }

    /// <summary>Reads a token back from a link, or returns null if it was mangled.</summary>
    public static string? Decode(string token)
    {
        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // Identity's tokens contain characters that do not survive a URL, so links carry them Base64url-encoded.
    private static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    private async Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await emailSender.SendAsync(message, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not send the email \"{Subject}\"", message.Subject);
            return false;
        }
    }
}
