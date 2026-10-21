using System.Security.Cryptography;
using System.Text;

namespace Spindle.Domain.AgentActivity;

/// <summary>
/// A repository the person told Spindle to ignore on all their devices, known only by a hash of its identity
/// (<see cref="HashOf"/>): the server never learns which repositories are private. The apps leave its prompts and runs
/// out of everything, and upload none of it; its prompts still end the attention before them, as a folder outside git does.
/// </summary>
public sealed class IgnoredRepository
{
    public const int HashLength = 64;

    /// <summary>Lower-case hex of an HMAC-SHA256.</summary>
    public const string HashPattern = "^[0-9a-f]{64}$";

    /// <summary>The most repositories one person can ignore.</summary>
    public const int MaxPerUser = 1000;

    public Guid UserId { get; set; }

    public string Hash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// HMAC-SHA256 with the person's ID (lower case, with hyphens) as the key, over the repository's identity, in
    /// lower-case hex: the same on the Mac, Windows and the server (<c>tests/vectors/ignored-repositories.json</c>), and
    /// different for another person.
    /// </summary>
    public static string HashOf(Guid userId, string identity) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(userId.ToString("D")), Encoding.UTF8.GetBytes(identity)));
}
