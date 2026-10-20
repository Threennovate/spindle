using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Spindle.Api.Auth;

internal sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

internal sealed class AccessTokenIssuer(IOptions<AuthOptions> options, TimeProvider timeProvider)
{
    /// <summary>The claim naming the device session the token belongs to: its refresh token family.</summary>
    public const string SessionClaim = "sid";

    private readonly JsonWebTokenHandler handler = new();

    /// <summary>The claim naming the workspace an agent's token reaches.</summary>
    public const string WorkspaceClaim = "ws";

    /// <summary>The OAuth scopes of an agent's token, separated by spaces.</summary>
    public const string ScopeClaim = "scope";

    public const string ClientClaim = "client_id";

    public AccessToken Issue(Guid userId, Guid sessionId) => Create(options.Value.Audience, new()
    {
        [JwtRegisteredClaimNames.Sub] = userId.ToString(),
        [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
        [SessionClaim] = sessionId.ToString(),
    });

    /// <summary>
    /// A token for an agent connected over MCP: for the MCP server only (its URL is the audience, RFC 8707), so the
    /// rest of the API refuses it, and for one workspace and the scopes the person allowed.
    /// </summary>
    public AccessToken IssueForAgent(Guid userId, Guid sessionId, Guid workspaceId, string scope, string clientId, string resource) => Create(resource, new()
    {
        [JwtRegisteredClaimNames.Sub] = userId.ToString(),
        [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
        [SessionClaim] = sessionId.ToString(),
        [WorkspaceClaim] = workspaceId.ToString(),
        [ScopeClaim] = scope,
        [ClientClaim] = clientId,
    });

    private AccessToken Create(string audience, Dictionary<string, object> claims)
    {
        var auth = options.Value;

        // JWT times have whole-second precision; keep the reported expiry identical to the token's.
        var now = DateTimeOffset.FromUnixTimeSeconds(timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var expiresAt = now + auth.AccessTokenLifetime;

        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = auth.Issuer,
            Audience = audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(auth.CreateSigningKey(), SecurityAlgorithms.HmacSha256),
        });

        return new AccessToken(token, expiresAt);
    }
}
