using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Auth;

/// <summary>
/// How an account without a password confirms who it is before something that matters, such as adding a passkey or
/// deleting the account: by having signed in on this device in the last few minutes, with Google, Apple or a passkey.
/// A session taken long ago can't do it, and neither can one signed out since, e.g. a squatter's when the person
/// with the address took the account over: its access token still works for a few minutes, but not for this.
/// </summary>
internal static class RecentSignIn
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public const string Required = "Sign in again to confirm it's you, then try again within 10 minutes.";

    public static async Task<bool> IsRecentAsync(AppDbContext db, ClaimsPrincipal principal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (principal.GetSessionId() is not { } sessionId)
        {
            return false;
        }

        var userId = principal.GetUserId();
        var active = await db.RefreshTokens.AnyAsync(
            t => t.FamilyId == sessionId && t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now, cancellationToken);
        if (!active)
        {
            return false;
        }

        // The first token of the session, whatever became of it: every renewal adds one, so only that one says when.
        var started = await db.RefreshTokens
            .Where(t => t.FamilyId == sessionId && t.UserId == userId)
            .MinAsync(t => (DateTimeOffset?)t.CreatedAt, cancellationToken);
        return started is { } at && now - at <= Window;
    }
}
