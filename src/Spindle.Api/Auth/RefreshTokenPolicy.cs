using Spindle.Infrastructure.Identity;

namespace Spindle.Api.Auth;

internal enum RefreshTokenDecision
{
    /// <summary>Active token: replace it with a new one.</summary>
    Rotate,

    /// <summary>Just-rotated token presented again by a client that lost the response: replace the unused replacement.</summary>
    RetryRotation,

    /// <summary>Expired token: refuse it.</summary>
    Reject,

    /// <summary>A revoked token was reused, so it has leaked: revoke the whole session.</summary>
    RevokeSession,
}

internal static class RefreshTokenPolicy
{
    public static RefreshTokenDecision Evaluate(
        RefreshToken presented,
        RefreshToken? replacement,
        DateTimeOffset now,
        TimeSpan reuseGracePeriod)
    {
        if (presented.RevokedAt is null)
        {
            return presented.ExpiresAt > now ? RefreshTokenDecision.Rotate : RefreshTokenDecision.Reject;
        }

        var isRetryOfRecentRotation =
            replacement is { RevokedAt: null }
            && presented.ReplacedByTokenId == replacement.Id
            && now - presented.RevokedAt.Value < reuseGracePeriod;

        return isRetryOfRecentRotation ? RefreshTokenDecision.RetryRotation : RefreshTokenDecision.RevokeSession;
    }
}
