using System.Security.Cryptography;
using System.Text;

namespace Spindle.Activity;

/// <summary>
/// The repositories a person told Spindle to ignore, as the Mac's <c>IgnoredRepositories</c>: none of their prompts and
/// runs show anywhere, nor are they uploaded, though their prompts still end the attention before them, as a folder
/// outside git's do. The list syncs between the person's devices as hashes alone (<see cref="Hash"/>), so the server
/// never learns which they are.
/// </summary>
public sealed record IgnoredRepositories
{
    public IgnoredRepositories(string account, IEnumerable<string> hashes)
    {
        Account = account.ToLowerInvariant();
        Hashes = hashes.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Nothing ignored.</summary>
    public static IgnoredRepositories None { get; } = new("", []);

    /// <summary>The person's account ID, which keys the hashes.</summary>
    public string Account { get; }

    public IReadOnlySet<string> Hashes { get; }

    public bool IsEmpty => Hashes.Count == 0;

    public bool Contains(AgentRepository? repository) => repository is not null && Contains(repository.Identity);

    public bool Contains(string identity) => Hashes.Count > 0 && Hashes.Contains(Hash(identity, Account));

    /// <summary>This repository's hash for this person.</summary>
    public string HashOf(string identity) => Hash(identity, Account);

    /// <summary>
    /// HMAC-SHA256 with the account ID (lower case, with hyphens) as the key, over the repository's identity, in
    /// lower-case hex: the same on the Mac, Windows and the server (tests/vectors/ignored-repositories.json), and
    /// different for another person.
    /// </summary>
    public static string Hash(string identity, string account) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(account.ToLowerInvariant()), Encoding.UTF8.GetBytes(identity)));

    public bool Equals(IgnoredRepositories? other) => other is not null && Account == other.Account && Hashes.SetEquals(other.Hashes);

    public override int GetHashCode() => HashCode.Combine(Account, Hashes.Count);
}
