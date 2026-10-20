using Spindle.Api.Auth;
using Spindle.Infrastructure.Identity;

namespace Spindle.Tests.Auth;

public sealed class RefreshTokenPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(30);

    [Fact]
    public void Active_token_is_rotated()
    {
        var decision = RefreshTokenPolicy.Evaluate(Token(), replacement: null, Now, GracePeriod);

        Assert.Equal(RefreshTokenDecision.Rotate, decision);
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var decision = RefreshTokenPolicy.Evaluate(Token(expiresAt: Now), replacement: null, Now, GracePeriod);

        Assert.Equal(RefreshTokenDecision.Reject, decision);
    }

    [Fact]
    public void Retry_within_grace_period_rotates_again_when_replacement_is_unused()
    {
        var replacement = Token();
        var presented = Token(revokedAt: Now.AddSeconds(-5), replacedBy: replacement.Id);

        var decision = RefreshTokenPolicy.Evaluate(presented, replacement, Now, GracePeriod);

        Assert.Equal(RefreshTokenDecision.RetryRotation, decision);
    }

    [Fact]
    public void Reuse_after_grace_period_revokes_session()
    {
        var replacement = Token();
        var presented = Token(revokedAt: Now - GracePeriod, replacedBy: replacement.Id);

        var decision = RefreshTokenPolicy.Evaluate(presented, replacement, Now, GracePeriod);

        Assert.Equal(RefreshTokenDecision.RevokeSession, decision);
    }

    [Fact]
    public void Reuse_after_replacement_was_used_revokes_session()
    {
        var replacement = Token(revokedAt: Now.AddSeconds(-1), replacedBy: Guid.NewGuid());
        var presented = Token(revokedAt: Now.AddSeconds(-5), replacedBy: replacement.Id);

        var decision = RefreshTokenPolicy.Evaluate(presented, replacement, Now, GracePeriod);

        Assert.Equal(RefreshTokenDecision.RevokeSession, decision);
    }

    [Fact]
    public void Reuse_of_signed_out_token_revokes_session()
    {
        var presented = Token(revokedAt: Now.AddSeconds(-5));

        var decision = RefreshTokenPolicy.Evaluate(presented, replacement: null, Now, GracePeriod);

        Assert.Equal(RefreshTokenDecision.RevokeSession, decision);
    }

    private static RefreshToken Token(DateTimeOffset? expiresAt = null, DateTimeOffset? revokedAt = null, Guid? replacedBy = null) => new()
    {
        UserId = Guid.NewGuid(),
        FamilyId = Guid.NewGuid(),
        TokenHash = new byte[RefreshToken.TokenHashLength],
        CreatedAt = Now.AddDays(-1),
        ExpiresAt = expiresAt ?? Now.AddDays(30),
        RevokedAt = revokedAt,
        ReplacedByTokenId = replacedBy,
    };
}
