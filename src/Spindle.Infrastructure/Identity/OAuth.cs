namespace Spindle.Infrastructure.Identity;

/// <summary>
/// An MCP client that registered itself (OAuth dynamic client registration, RFC 7591), such as Claude Code or Codex.
/// Clients that name themselves by a metadata document URL instead are not kept: their document is read when needed.
/// Registration needs no account, so a row says nothing about anyone; only consent ties a client to a person.
/// </summary>
public sealed class OAuthClient
{
    public const int IdLength = 43;
    public const int NameMaxLength = 100;
    public const int RedirectUriMaxLength = 2000;
    public const int MaxRedirectUris = 10;

    public required string Id { get; init; }

    /// <summary>What the client calls itself, shown on the consent page and as the device's name under Account.</summary>
    public required string Name { get; init; }

    /// <summary>Where codes may be sent; an authorization request must name one of them exactly.</summary>
    public required string[] RedirectUris { get; init; }

    /// <summary>
    /// The SHA-256 hash of the client's secret, for a client that asked to authenticate with one at the token endpoint;
    /// null for public clients, which prove themselves with PKCE alone.
    /// </summary>
    public byte[]? SecretHash { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// A code given to an MCP client after the person allowed it in, exchanged once for its first tokens. Only its
/// SHA-256 hash is kept, for a few minutes; taking it deletes it.
/// </summary>
public sealed class OAuthAuthorizationCode
{
    public required byte[] CodeHash { get; init; }

    public required Guid UserId { get; init; }

    public required Guid WorkspaceId { get; init; }

    /// <summary>The client's ID: a registered client's, or its metadata document's URL.</summary>
    public required string ClientId { get; init; }

    public required string ClientName { get; init; }

    public required string RedirectUri { get; init; }

    /// <summary>The PKCE challenge (S256) the token request's verifier must match.</summary>
    public required string CodeChallenge { get; init; }

    /// <summary>The scopes the person allowed, separated by spaces.</summary>
    public required string Scope { get; init; }

    /// <summary>The MCP server the tokens are for (RFC 8707).</summary>
    public required string Resource { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// An agent the person connected over MCP: one device session (<see cref="SessionId"/>, the refresh token family),
/// so it is listed under Account with the apps and browsers and signed out like them. It reaches one workspace, and
/// may change things there only with the write scope. Refresh tokens of these sessions only renew MCP access tokens,
/// never the apps' own.
/// </summary>
public sealed class AgentConnection
{
    public required Guid SessionId { get; init; }

    public required Guid UserId { get; init; }

    public required Guid WorkspaceId { get; init; }

    public required string ClientId { get; init; }

    public required string Scope { get; init; }

    public required string Resource { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
