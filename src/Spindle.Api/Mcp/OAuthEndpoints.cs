using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Spindle.Api.Auth;
using Spindle.Api.Common;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Mcp;

/// <summary>An agent asking to be let in, as the authorization request named it; the consent page passes it on.</summary>
public sealed record OAuthAuthorizationRequest
{
    [MaxLength(50)]
    public string? ResponseType { get; init; }

    [MaxLength(OAuthClient.RedirectUriMaxLength)]
    public string? ClientId { get; init; }

    [MaxLength(OAuthClient.RedirectUriMaxLength)]
    public string? RedirectUri { get; init; }

    [MaxLength(200)]
    public string? CodeChallenge { get; init; }

    [MaxLength(20)]
    public string? CodeChallengeMethod { get; init; }

    [MaxLength(500)]
    public string? Scope { get; init; }

    [MaxLength(1000)]
    public string? State { get; init; }

    [MaxLength(OAuthClient.RedirectUriMaxLength)]
    public string? Resource { get; init; }
}

/// <summary>The person's answer on the consent page.</summary>
public sealed record OAuthConsentRequest
{
    [Required]
    public OAuthAuthorizationRequest Request { get; init; } = new();

    /// <summary>False when the person said no; the agent is told it was denied.</summary>
    public bool Approve { get; init; }

    /// <summary>The workspace the agent may reach.</summary>
    public Guid? WorkspaceId { get; init; }

    /// <summary>Whether the agent may also describe entries, run the timer and propose entries and changes.</summary>
    public bool AllowWrite { get; init; }
}

/// <summary>Who asks to be let in, for the consent page.</summary>
/// <param name="RedirectHost">Where the person's answer goes; for a local agent, <c>localhost</c>.</param>
/// <param name="ClientHost">The site that vouches for the client's name, for a client named by a metadata document.</param>
/// <param name="AsksToWrite">Whether the agent asked for more than reading.</param>
public sealed record OAuthAuthorizationPreview(string ClientName, string RedirectHost, bool LocalRedirect, string? ClientHost, bool AsksToWrite);

/// <summary>Where the browser goes next: back to the agent, with a code or an error.</summary>
public sealed record OAuthRedirectResponse(string RedirectUrl);

/// <summary>
/// The OAuth 2.1 authorization server that lets MCP clients in on the person's behalf, with the metadata the MCP
/// authorization spec asks for. Clients name themselves by a metadata document URL, or register (RFC 7591); the
/// person allows them on the web app's consent page, for one workspace, to read or also to write; codes need PKCE
/// (S256). Every agent let in is a device session of its own, listed under Account.
/// </summary>
internal static class OAuthEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // The protocol's own endpoints, for agents rather than for Spindle's apps, so not in the OpenAPI document.
        var wellKnown = app.MapGroup("/.well-known").ExcludeFromDescription().RequireCors(McpOAuth.CorsPolicy);
        wellKnown.MapGet("/oauth-protected-resource", ProtectedResource);
        wellKnown.MapGet("/oauth-protected-resource" + McpOAuth.McpPath, ProtectedResource);
        wellKnown.MapGet("/oauth-authorization-server", AuthorizationServer);

        var oauth = app.MapGroup("/oauth").ExcludeFromDescription();
        oauth.MapGet("/authorize", Authorize);
        oauth.MapPost("/register", RegisterAsync).RequireCors(McpOAuth.CorsPolicy).RequireRateLimiting(McpOAuth.RegistrationRateLimit);
        oauth.MapPost("/token", TokenAsync).RequireCors(McpOAuth.CorsPolicy).DisableAntiforgery();
        oauth.MapPost("/revoke", RevokeAsync).RequireCors(McpOAuth.CorsPolicy).DisableAntiforgery();

        // The consent page's calls, as the signed-in person.
        var consent = app.MapGroup("/api/oauth/authorizations").WithTags("Agents").RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        consent.MapPost("/preview", PreviewAsync).WithName("previewAgentAuthorization");
        consent.MapPost("", ConsentAsync).WithName("answerAgentAuthorization");

        return app;
    }

    private static IResult ProtectedResource(IOptions<AppOptions> app) => Write(new
    {
        resource = McpOAuth.Resource(app.Value),
        authorization_servers = new[] { McpOAuth.Issuer(app.Value) },
        scopes_supported = McpOAuth.Scopes,
        bearer_methods_supported = new[] { "header" },
        resource_name = "Spindle",
        resource_documentation = "https://github.com/Threennovate/spindle/blob/main/docs/connect-agents.md",
    });

    private static IResult AuthorizationServer(IOptions<AppOptions> app)
    {
        var issuer = McpOAuth.Issuer(app.Value);
        return Write(new
        {
            issuer,
            authorization_endpoint = issuer + "/oauth/authorize",
            token_endpoint = issuer + "/oauth/token",
            registration_endpoint = issuer + "/oauth/register",
            revocation_endpoint = issuer + "/oauth/revoke",
            scopes_supported = McpOAuth.Scopes,
            response_types_supported = new[] { "code" },
            response_modes_supported = new[] { "query" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none", "client_secret_post", "client_secret_basic" },
            revocation_endpoint_auth_methods_supported = new[] { "none", "client_secret_post", "client_secret_basic" },
            client_id_metadata_document_supported = true,
            authorization_response_iss_parameter_supported = true,
        });
    }

    /// <summary>
    /// The browser arrives here from the agent. The person answers on the web app's consent page, which signs them in
    /// first if need be, so everything in the request goes on there as it came.
    /// </summary>
    private static RedirectHttpResult Authorize(HttpRequest request, IOptions<AppOptions> app) =>
        TypedResults.Redirect(app.Value.Link("/connect" + request.QueryString.Value));

    // Registration (RFC 7591)

    private static async Task<IResult> RegisterAsync(HttpRequest request, AppDbContext db, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        JsonElement body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return Error("invalid_client_metadata", "The body must be a JSON object.");
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            return Error("invalid_client_metadata", "The body must be a JSON object.");
        }

        if (!body.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array
            || uris.GetArrayLength() is 0 or > OAuthClient.MaxRedirectUris)
        {
            return Error("invalid_redirect_uri", $"Give 1 to {OAuthClient.MaxRedirectUris} redirect_uris.");
        }

        var redirects = uris.EnumerateArray().Select(u => u.ValueKind == JsonValueKind.String ? u.GetString()! : "").ToArray();
        if (redirects.Any(u => u.Length > OAuthClient.RedirectUriMaxLength || !OAuthClients.IsAllowedRedirect(u)))
        {
            return Error("invalid_redirect_uri", "Redirect URIs must use HTTPS, or HTTP to localhost, without a fragment.");
        }

        var method = Text(body, "token_endpoint_auth_method") ?? "none";
        if (method is not ("none" or "client_secret_post" or "client_secret_basic"))
        {
            return Error("invalid_client_metadata", "token_endpoint_auth_method must be none, client_secret_post or client_secret_basic.");
        }

        if (body.TryGetProperty("grant_types", out var grants) && grants.ValueKind == JsonValueKind.Array
            && !grants.EnumerateArray().Any(g => g.ValueKind == JsonValueKind.String && g.GetString() == "authorization_code"))
        {
            return Error("invalid_client_metadata", "Spindle lets agents in with the authorization_code grant.");
        }

        var secret = method == "none" ? null : McpOAuth.NewToken();
        var client = new OAuthClient
        {
            Id = McpOAuth.NewToken(),
            Name = McpOAuth.DisplayName(Text(body, "client_name") ?? ""),
            RedirectUris = redirects,
            SecretHash = secret is null ? null : McpOAuth.Hash(secret),
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.OAuthClients.Add(client);
        await db.SaveChangesAsync(cancellationToken);

        return Write(new
        {
            client_id = client.Id,
            client_id_issued_at = client.CreatedAt.ToUnixTimeSeconds(),
            client_secret = secret,
            client_secret_expires_at = secret is null ? (long?)null : 0,
            client_name = client.Name,
            redirect_uris = client.RedirectUris,
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = method,
        }, StatusCodes.Status201Created);
    }

    // Consent

    /// <summary>A request checked: the client and where the answer goes, or why it can't be answered.</summary>
    private sealed record Checked(OAuthClientInfo? Client, string? Problem, string? ErrorRedirect, string Resource, string[] Scopes);

    private static async Task<Checked> CheckAsync(OAuthAuthorizationRequest request, OAuthClients clients, AppOptions app, CancellationToken cancellationToken)
    {
        var resource = McpOAuth.Resource(app);
        var client = await clients.FindAsync(request.ClientId, cancellationToken);
        if (client is null)
        {
            return new(null, "Spindle doesn't know the agent that sent you here. Start connecting it again from the agent.", null, resource, []);
        }

        if (request.RedirectUri is not { Length: > 0 } redirect || !OAuthClients.Matches(client, redirect))
        {
            return new(null, "The agent asked to be answered at an address it didn't register. Start connecting it again from the agent.", null, resource, []);
        }

        // From here on the agent is told what is wrong, at the address it registered.
        string? Fail(string error, string description) => ErrorUrl(redirect, error, description, request.State, app);
        if (request.ResponseType != "code")
        {
            return new(client, "The agent asked for something other than a code.", Fail("unsupported_response_type", "Only response_type=code is supported."), resource, []);
        }

        if (string.IsNullOrEmpty(request.CodeChallenge) || request.CodeChallengeMethod != "S256")
        {
            return new(client, "The agent didn't use PKCE.", Fail("invalid_request", "PKCE with code_challenge_method=S256 is required."), resource, []);
        }

        if (request.Resource is { Length: > 0 } asked && !SameResource(asked, resource))
        {
            return new(client, "The agent asked for access to another server.", Fail("invalid_target", $"The resource is {resource}."), resource, []);
        }

        var scopes = McpOAuth.Parse(request.Scope);
        return new(client, null, null, resource, scopes.Length == 0 ? McpOAuth.Scopes : scopes);
    }

    private static async Task<Results<Ok<OAuthAuthorizationPreview>, ProblemHttpResult>> PreviewAsync(
        OAuthAuthorizationRequest request,
        OAuthClients clients,
        IOptions<AppOptions> app,
        CancellationToken cancellationToken)
    {
        var check = await CheckAsync(request, clients, app.Value, cancellationToken);
        if (check.Problem is not null)
        {
            return Problem(check);
        }

        var redirect = new Uri(request.RedirectUri!);
        return TypedResults.Ok(new OAuthAuthorizationPreview(
            check.Client!.Name,
            redirect.Host,
            OAuthClients.IsLoopback(redirect.Host),
            check.Client.FromMetadataDocument ? new Uri(check.Client.Id).Host : null,
            check.Scopes.Contains(McpOAuth.WriteScope)));
    }

    /// <summary>Lets the agent in, for the workspace and scopes chosen, or tells it the person said no.</summary>
    private static async Task<Results<Ok<OAuthRedirectResponse>, ProblemHttpResult, ValidationProblem>> ConsentAsync(
        OAuthConsentRequest consent,
        ClaimsPrincipal principal,
        OAuthClients clients,
        AppDbContext db,
        IOptions<AppOptions> app,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var request = consent.Request;
        var check = await CheckAsync(request, clients, app.Value, cancellationToken);
        if (check.Problem is not null)
        {
            return Problem(check);
        }

        if (!consent.Approve)
        {
            return TypedResults.Ok(new OAuthRedirectResponse(ErrorUrl(request.RedirectUri!, "access_denied", "The person didn't allow it.", request.State, app.Value)));
        }

        var userId = principal.GetUserId();
        if (consent.WorkspaceId is not { } workspaceId
            || !await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId && db.Workspaces.Any(w => w.Id == workspaceId), cancellationToken))
        {
            return Problems.Validation("workspaceId", "Choose one of your workspaces.");
        }

        var code = McpOAuth.NewToken();
        var scope = consent.AllowWrite && check.Scopes.Contains(McpOAuth.WriteScope) ? McpOAuth.Scopes : [McpOAuth.ReadScope];
        var now = timeProvider.GetUtcNow();
        await db.OAuthAuthorizationCodes.Where(c => c.ExpiresAt < now).ExecuteDeleteAsync(cancellationToken);
        db.OAuthAuthorizationCodes.Add(new OAuthAuthorizationCode
        {
            CodeHash = McpOAuth.Hash(code),
            UserId = userId,
            WorkspaceId = workspaceId,
            ClientId = check.Client!.Id,
            ClientName = check.Client.Name,
            RedirectUri = request.RedirectUri!,
            CodeChallenge = request.CodeChallenge!,
            Scope = string.Join(' ', scope),
            Resource = check.Resource,
            ExpiresAt = now + McpOAuth.CodeLifetime,
        });
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new OAuthRedirectResponse(Url(request.RedirectUri!, new()
        {
            ["code"] = code,
            ["state"] = request.State,
            ["iss"] = McpOAuth.Issuer(app.Value),
        })));
    }

    private static ProblemHttpResult Problem(Checked check) => TypedResults.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: check.Problem,
        extensions: check.ErrorRedirect is null ? null : new Dictionary<string, object?> { ["redirectUrl"] = check.ErrorRedirect });

    // Tokens

    private static async Task<IResult> TokenAsync(
        HttpRequest request,
        OAuthClients clients,
        AppDbContext db,
        RefreshTokenService refreshTokens,
        AccessTokenIssuer accessTokens,
        IOptions<AppOptions> app,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Error("invalid_request", "Send the token request as application/x-www-form-urlencoded.");
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var (client, clientError) = await AuthenticateClientAsync(request, form, clients, cancellationToken);
        if (clientError is not null)
        {
            return clientError;
        }

        if (form["resource"].ToString() is { Length: > 0 } resource && !SameResource(resource, McpOAuth.Resource(app.Value)))
        {
            return Error("invalid_target", $"The resource is {McpOAuth.Resource(app.Value)}.");
        }

        return form["grant_type"].ToString() switch
        {
            "authorization_code" => await ExchangeCodeAsync(form, client!, db, refreshTokens, accessTokens, timeProvider, cancellationToken),
            "refresh_token" => await RefreshAsync(form, client!, db, refreshTokens, accessTokens, cancellationToken),
            _ => Error("unsupported_grant_type", "Use authorization_code or refresh_token."),
        };
    }

    private static async Task<IResult> ExchangeCodeAsync(
        IFormCollection form,
        OAuthClientInfo client,
        AppDbContext db,
        RefreshTokenService refreshTokens,
        AccessTokenIssuer accessTokens,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var hash = McpOAuth.Hash(form["code"].ToString());
        var code = await db.OAuthAuthorizationCodes.AsNoTracking().SingleOrDefaultAsync(c => c.CodeHash == hash, cancellationToken);
        // Taken once: whoever deletes it first may use it.
        if (code is null || await db.OAuthAuthorizationCodes.Where(c => c.CodeHash == hash).ExecuteDeleteAsync(cancellationToken) == 0)
        {
            return Error("invalid_grant", "The code is unknown or was used already.");
        }

        if (code.ExpiresAt < timeProvider.GetUtcNow()
            || code.ClientId != client.Id
            || code.RedirectUri != form["redirect_uri"].ToString()
            || !McpOAuth.VerifierMatches(form["code_verifier"].ToString(), code.CodeChallenge))
        {
            return Error("invalid_grant", "The code has expired, or doesn't match the client, redirect_uri or code_verifier.");
        }

        if (!await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == code.WorkspaceId && m.UserId == code.UserId, cancellationToken))
        {
            return Error("invalid_grant", "The person is no longer in that workspace.");
        }

        var issued = await refreshTokens.IssueAsync(code.UserId, code.ClientName, cancellationToken);
        db.AgentConnections.Add(new AgentConnection
        {
            SessionId = issued.FamilyId,
            UserId = code.UserId,
            WorkspaceId = code.WorkspaceId,
            ClientId = code.ClientId,
            Scope = code.Scope,
            Resource = code.Resource,
            CreatedAt = timeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken);

        return Tokens(accessTokens.IssueForAgent(code.UserId, issued.FamilyId, code.WorkspaceId, code.Scope, code.ClientId, code.Resource), issued, code.Scope);
    }

    private static async Task<IResult> RefreshAsync(
        IFormCollection form,
        OAuthClientInfo client,
        AppDbContext db,
        RefreshTokenService refreshTokens,
        AccessTokenIssuer accessTokens,
        CancellationToken cancellationToken)
    {
        var value = form["refresh_token"].ToString();
        var session = value.Length == 0 ? null : await refreshTokens.SessionOfAsync(value, cancellationToken);
        var connection = session is null ? null : await db.AgentConnections.AsNoTracking().SingleOrDefaultAsync(c => c.SessionId == session.Value.FamilyId, cancellationToken);
        // Only an agent's own session, never an app's: those renew at /api/auth.
        if (connection is null || connection.ClientId != client.Id)
        {
            return Error("invalid_grant", "The refresh token is unknown, or not this client's.");
        }

        var rotated = await refreshTokens.RotateAsync(value, cancellationToken, revokeOnReuse: false);
        if (rotated is null)
        {
            return Error("invalid_grant", "The refresh token has expired, was used already, or the agent was signed out.");
        }

        return Tokens(
            accessTokens.IssueForAgent(connection.UserId, connection.SessionId, connection.WorkspaceId, connection.Scope, connection.ClientId, connection.Resource),
            rotated,
            connection.Scope);
    }

    /// <summary>Signs the agent out (RFC 7009). Says yes whatever the token, so nothing is learned from the answer.</summary>
    private static async Task<IResult> RevokeAsync(
        HttpRequest request,
        OAuthClients clients,
        AppDbContext db,
        RefreshTokenService refreshTokens,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Error("invalid_request", "Send the request as application/x-www-form-urlencoded.");
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var (client, clientError) = await AuthenticateClientAsync(request, form, clients, cancellationToken);
        if (clientError is not null)
        {
            return clientError;
        }

        var value = form["token"].ToString();
        if (value.Length > 0 && await refreshTokens.SessionOfAsync(value, cancellationToken) is { } session
            && await db.AgentConnections.AnyAsync(c => c.SessionId == session.FamilyId && c.ClientId == client!.Id, cancellationToken))
        {
            await refreshTokens.RevokeSessionAsync(value, cancellationToken);
        }

        return TypedResults.Ok();
    }

    /// <summary>The client making a token request: by its ID for a public one, and its secret for one that has one.</summary>
    private static async Task<(OAuthClientInfo? Client, IResult? Error)> AuthenticateClientAsync(
        HttpRequest request,
        IFormCollection form,
        OAuthClients clients,
        CancellationToken cancellationToken)
    {
        var (id, secret) = (form["client_id"].ToString(), form["client_secret"].ToString());
        if (AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header) && header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter ?? "")).Split(':', 2);
                (id, secret) = (Uri.UnescapeDataString(pair[0]), pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "");
            }
            catch (FormatException)
            {
                return (null, Error("invalid_client", "The Authorization header is not valid Basic credentials.", StatusCodes.Status401Unauthorized));
            }
        }

        var client = await clients.FindAsync(id, cancellationToken);
        if (client is null)
        {
            // Claude re-registers when told invalid_client with a 401.
            return (null, Error("invalid_client", "Spindle doesn't know this client. Register again.", StatusCodes.Status401Unauthorized));
        }

        if (client.SecretHash is { } hash && !CryptographicOperations.FixedTimeEquals(hash, McpOAuth.Hash(secret)))
        {
            return (null, Error("invalid_client", "The client secret is wrong.", StatusCodes.Status401Unauthorized));
        }

        return (client, null);
    }

    private static IResult Tokens(AccessToken access, IssuedRefreshToken refresh, string scope) => Write(new
    {
        access_token = access.Value,
        token_type = "Bearer",
        expires_in = (long)Math.Max(0, (access.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds),
        refresh_token = refresh.Value,
        scope,
    });

    // Helpers

    /// <summary>The same resource, ignoring a trailing slash and the case of the scheme and host.</summary>
    private static bool SameResource(string asked, string resource) =>
        Uri.TryCreate(asked, UriKind.Absolute, out var a) && Uri.TryCreate(resource, UriKind.Absolute, out var b)
        && Uri.Compare(a, b, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
        || asked.TrimEnd('/') == resource;

    private static string ErrorUrl(string redirect, string error, string description, string? state, AppOptions app) =>
        Url(redirect, new() { ["error"] = error, ["error_description"] = description, ["state"] = state, ["iss"] = McpOAuth.Issuer(app) });

    private static string Url(string redirect, Dictionary<string, string?> parameters) =>
        QueryHelpers.AddQueryString(redirect, parameters.Where(p => p.Value is not null)!);

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static IResult Error(string error, string description, int status = StatusCodes.Status400BadRequest) =>
        Write(new { error, error_description = description }, status);

    /// <summary>Protocol JSON, never cached, as OAuth asks of anything that carries tokens.</summary>
    private static IResult Write(object value, int status = StatusCodes.Status200OK) => new NoStoreJson(value, status);

    private sealed class NoStoreJson(object value, int status) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = status;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            return context.Response.WriteAsJsonAsync(value, value.GetType(), Json);
        }
    }
}
