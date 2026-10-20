namespace Spindle.Infrastructure.Identity;

/// <summary>Server-owned WebAuthn state. An opaque browser cookie selects it; consuming it deletes it atomically.</summary>
public sealed class PasskeyChallenge
{
    public string TokenHash { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public string? SecurityStamp { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
