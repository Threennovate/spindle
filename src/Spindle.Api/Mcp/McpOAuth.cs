using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Spindle.Api.Common;

namespace Spindle.Api.Mcp;

/// <summary>
/// The names and addresses of Spindle's MCP server and of the OAuth server that lets agents in. Both live at the app's
/// public address (<see cref="AppOptions.BaseUrl"/>): the issuer is that address, the MCP server is <c>/mcp</c> under it.
/// </summary>
internal static class McpOAuth
{
    /// <summary>The authentication scheme of MCP requests, whose tokens are for the MCP server alone.</summary>
    public const string Scheme = "Mcp";

    public const string Policy = "Mcp";

    public const string CorsPolicy = "OAuth";

    public const string RegistrationRateLimit = "oauth-registration";

    /// <summary>Reading entries, projects, reports, proposals and synced agent activity.</summary>
    public const string ReadScope = "read";

    /// <summary>Describing entries, the timer, and proposing entries and changes for the person to confirm.</summary>
    public const string WriteScope = "write";

    public static readonly string[] Scopes = [ReadScope, WriteScope];

    public const string McpPath = "/mcp";

    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    public static string Issuer(AppOptions app) => app.BaseUrl.TrimEnd('/');

    /// <summary>The MCP server's canonical URL, the audience of its tokens.</summary>
    public static string Resource(AppOptions app) => Issuer(app) + McpPath;

    public static string ResourceMetadataUrl(AppOptions app) => Issuer(app) + "/.well-known/oauth-protected-resource" + McpPath;

    /// <summary>The scopes asked for that Spindle knows, in a fixed order; reading always comes with any of them.</summary>
    public static string[] Parse(string? scope)
    {
        var asked = (scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return asked.Contains(WriteScope) ? [ReadScope, WriteScope] : asked.Contains(ReadScope) ? [ReadScope] : [];
    }

    public static bool CanWrite(string scope) => scope.Split(' ').Contains(WriteScope);

    /// <summary>A random value safe in URLs: codes, client IDs and secrets.</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    /// <summary>Whether the PKCE verifier matches the challenge (S256 only).</summary>
    public static bool VerifierMatches(string verifier, string challenge) =>
        verifier.Length is >= 43 and <= 128
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))),
            Encoding.ASCII.GetBytes(challenge));

    /// <summary>The name people know an agent by, from what its client calls itself, for the list of devices and proposals.</summary>
    public static string DisplayName(string name)
    {
        var trimmed = name.Trim();
        var lower = trimmed.ToLowerInvariant();
        var known = lower switch
        {
            _ when lower.StartsWith("claude code", StringComparison.Ordinal) || lower.StartsWith("claude-code", StringComparison.Ordinal) => "Claude Code",
            _ when lower.StartsWith("codex", StringComparison.Ordinal) => "Codex",
            "claude" or "claude.ai" => "Claude",
            _ => null,
        };
        var display = known ?? (trimmed.Length == 0 ? "An agent" : trimmed);
        return display.Length > Infrastructure.Identity.OAuthClient.NameMaxLength ? display[..Infrastructure.Identity.OAuthClient.NameMaxLength] : display;
    }
}
