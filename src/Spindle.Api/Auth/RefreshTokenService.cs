using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Auth;

/// <param name="FamilyId">The device session the token belongs to.</param>
internal sealed record IssuedRefreshToken(string Value, DateTimeOffset ExpiresAt, Guid UserId, Guid FamilyId);

internal sealed class RefreshTokenService(AppDbContext db, IOptions<AuthOptions> options, TimeProvider timeProvider)
{
    /// <summary>Starts a new device session.</summary>
    public async Task<IssuedRefreshToken> IssueAsync(Guid userId, string? deviceName, CancellationToken cancellationToken)
    {
        var (token, value) = Create(userId, Guid.CreateVersion7(), deviceName, timeProvider.GetUtcNow());
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);

        return new IssuedRefreshToken(value, token.ExpiresAt, userId, token.FamilyId);
    }

    /// <summary>The device session a refresh token belongs to, whatever its state, or null for a token never issued.</summary>
    public async Task<(Guid UserId, Guid FamilyId)?> SessionOfAsync(string value, CancellationToken cancellationToken)
    {
        var token = await FindAsync(value, cancellationToken);
        return token is null ? null : (token.UserId, token.FamilyId);
    }

    /// <summary>
    /// Exchanges a refresh token for a new one, or returns <see langword="null"/> if it must not be honoured. With
    /// <paramref name="revokeOnReuse"/> false, a token used again after the grace period is only refused, and the session
    /// lives on: agents run as several processes that share one sign-in, and one with an old token must not sign out
    /// the rest.
    /// </summary>
    public async Task<IssuedRefreshToken?> RotateAsync(string value, CancellationToken cancellationToken, bool revokeOnReuse = true)
    {
        var presented = await FindAsync(value, cancellationToken);
        if (presented is null)
        {
            return null;
        }

        var replacement = presented.ReplacedByTokenId is { } replacementId
            ? await db.RefreshTokens.SingleOrDefaultAsync(t => t.Id == replacementId, cancellationToken)
            : null;
        var now = timeProvider.GetUtcNow();

        switch (RefreshTokenPolicy.Evaluate(presented, replacement, now, options.Value.RefreshTokenReuseGracePeriod))
        {
            case RefreshTokenDecision.Rotate:
                return await ReplaceAsync(presented, unusedReplacement: null, now, cancellationToken);

            case RefreshTokenDecision.RetryRotation:
                return await ReplaceAsync(presented, replacement, now, cancellationToken);

            case RefreshTokenDecision.RevokeSession:
                // A token rotated long ago, or one of a session signed out: only an active session is revoked.
                if (revokeOnReuse)
                {
                    await RevokeFamilyAsync(presented.FamilyId, now, cancellationToken);
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>Whether the device session is still signed in.</summary>
    public Task<bool> IsActiveAsync(Guid familyId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return db.RefreshTokens.AnyAsync(t => t.FamilyId == familyId && t.RevokedAt == null && t.ExpiresAt > now, cancellationToken);
    }

    /// <summary>Signs a device out. Unknown tokens are ignored so callers learn nothing from the result.</summary>
    public async Task RevokeSessionAsync(string value, CancellationToken cancellationToken)
    {
        var token = await FindAsync(value, cancellationToken);
        if (token is not null)
        {
            await RevokeFamilyAsync(token.FamilyId, timeProvider.GetUtcNow(), cancellationToken);
        }
    }

    private async Task<IssuedRefreshToken?> ReplaceAsync(
        RefreshToken presented,
        RefreshToken? unusedReplacement,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (next, value) = Create(presented.UserId, presented.FamilyId, presented.DeviceName, now);
        db.RefreshTokens.Add(next);

        // On a retry the original rotation time is kept, so retries cannot extend the grace period.
        presented.RevokedAt ??= now;
        presented.ReplacedByTokenId = next.Id;

        if (unusedReplacement is not null)
        {
            unusedReplacement.RevokedAt = now;
            unusedReplacement.ReplacedByTokenId = next.Id;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A parallel request rotated the same token first.
            return null;
        }

        return new IssuedRefreshToken(value, next.ExpiresAt, presented.UserId, presented.FamilyId);
    }

    private Task<RefreshToken?> FindAsync(string value, CancellationToken cancellationToken)
    {
        var hash = Hash(value);
        return db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
    }

    private Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, now), cancellationToken);

    private (RefreshToken Token, string Value) Create(Guid userId, Guid familyId, string? deviceName, DateTimeOffset now)
    {
        var value = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var token = new RefreshToken
        {
            UserId = userId,
            FamilyId = familyId,
            TokenHash = Hash(value),
            DeviceName = deviceName,
            CreatedAt = now,
            ExpiresAt = now + options.Value.RefreshTokenLifetime,
        };

        return (token, value);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
