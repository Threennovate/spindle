using Spindle.Domain.Common;
using Spindle.Domain.Workspaces;

namespace Spindle.Domain.Invitations;

public enum InvitationStatus
{
    Pending,
    Accepted,
    Revoked,
    Expired,
}

/// <summary>
/// An invitation to join a workspace, sent by email. Only the SHA-256 hash of the token in the link is stored, so
/// the database alone is not enough to accept an invitation.
/// </summary>
public sealed class Invitation : Entity
{
    public const int EmailMaxLength = 256;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    private Invitation()
    {
        Email = string.Empty;
        TokenHash = [];
    }

    public Guid OrganizationId { get; private set; }

    public Guid WorkspaceId { get; private set; }

    /// <summary>Lower case, so the address matches however it is typed.</summary>
    public string Email { get; private set; }

    public WorkspaceRole Role { get; private set; }

    public Guid InvitedByUserId { get; private set; }

    public byte[] TokenHash { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public Guid? AcceptedByUserId { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public static Invitation Create(
        Guid organizationId,
        Guid workspaceId,
        string email,
        WorkspaceRole role,
        Guid invitedByUserId,
        byte[] tokenHash,
        DateTimeOffset now) => new()
    {
        OrganizationId = Guard.Id(organizationId),
        WorkspaceId = Guard.Id(workspaceId),
        Email = NormalizeEmail(email),
        Role = role,
        InvitedByUserId = Guard.Id(invitedByUserId),
        TokenHash = tokenHash,
        ExpiresAt = now + Lifetime,
    };

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public InvitationStatus StatusAt(DateTimeOffset now) =>
        AcceptedAt is not null ? InvitationStatus.Accepted
        : RevokedAt is not null ? InvitationStatus.Revoked
        : ExpiresAt <= now ? InvitationStatus.Expired
        : InvitationStatus.Pending;

    /// <summary>Sends the invitation again with a new link; the old link stops working.</summary>
    public void Renew(byte[] tokenHash, WorkspaceRole role, DateTimeOffset now)
    {
        if (AcceptedAt is not null || RevokedAt is not null)
        {
            throw new DomainValidationException("invitation", "Only a pending invitation can be sent again.");
        }

        TokenHash = tokenHash;
        Role = role;
        ExpiresAt = now + Lifetime;
    }

    public void Accept(Guid userId, DateTimeOffset now)
    {
        if (StatusAt(now) != InvitationStatus.Pending)
        {
            throw new DomainValidationException("invitation", "This invitation can no longer be accepted.");
        }

        AcceptedAt = now;
        AcceptedByUserId = Guard.Id(userId);
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
