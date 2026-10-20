namespace Spindle.Infrastructure.Identity;

/// <summary>
/// One link in a device session's chain of refresh tokens. Only the SHA-256 hash of the token is stored.
/// </summary>
/// <remarks>
/// Every refresh rotates the token: the presented one is revoked and points to its replacement.
/// All tokens issued for one sign-in share a <see cref="FamilyId"/>, so the whole session can be revoked at once.
/// </remarks>
public sealed class RefreshToken
{
    public const int TokenHashLength = 32;
    public const int DeviceNameMaxLength = 100;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid UserId { get; init; }

    public required Guid FamilyId { get; init; }

    public required byte[] TokenHash { get; init; }

    public string? DeviceName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? ReplacedByTokenId { get; set; }

    /// <summary>Concurrency token, so two parallel refreshes of the same token cannot both succeed.</summary>
    public uint Version { get; private set; }
}
