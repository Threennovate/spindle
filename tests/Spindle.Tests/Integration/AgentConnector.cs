using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Spindle.Api.Mcp;

namespace Spindle.Tests.Integration;

/// <summary>Connects agents to Spindle's MCP server the way Claude Code and Codex do: OAuth with PKCE, then Streamable HTTP.</summary>
internal static class AgentConnector
{
    public const string BaseUrl = "https://track.example.com";
    public const string Redirect = "http://127.0.0.1:43123/callback";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A client on the test server at the app's public address, which follows no redirects.</summary>
    public static HttpClient Http(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(BaseUrl), AllowAutoRedirect = false });

    public sealed record Tokens(string AccessToken, string RefreshToken, string Scope, int ExpiresIn);

    public sealed record Registered(string ClientId, string? ClientSecret);

    public static async Task<Registered> RegisterAsync(HttpClient http, string name = "Claude Code (spindle)", string redirect = Redirect)
    {
        var response = await http.PostAsJsonAsync("/oauth/register", new { client_name = name, redirect_uris = new[] { redirect }, token_endpoint_auth_method = "none" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new(body.GetProperty("client_id").GetString()!, null);
    }

    public static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    public static OAuthAuthorizationRequest Request(string clientId, string challenge, string scope = "read write", string redirect = Redirect) => new()
    {
        ResponseType = "code",
        ClientId = clientId,
        RedirectUri = redirect,
        CodeChallenge = challenge,
        CodeChallengeMethod = "S256",
        Scope = scope,
        State = "xyz",
        Resource = BaseUrl + "/mcp",
    };

    /// <summary>The person allows the agent on the consent page; returns the query the agent gets back.</summary>
    public static async Task<Dictionary<string, string>> ConsentAsync(TestUser user, OAuthAuthorizationRequest request, bool allowWrite = true, bool approve = true, Guid? workspaceId = null)
    {
        var answer = await TestUser.ReadAsync<OAuthRedirectResponse>(await user.Client.PostAsJsonAsync(
            "/api/oauth/authorizations",
            new { request, approve, workspaceId = workspaceId ?? user.WorkspaceId, allowWrite },
            Ct));
        Assert.StartsWith(request.RedirectUri!, answer.RedirectUrl, StringComparison.Ordinal);
        return QueryHelpers.ParseQuery(new Uri(answer.RedirectUrl).Query).ToDictionary(p => p.Key, p => p.Value.ToString());
    }

    public static Task<HttpResponseMessage> TokenAsync(HttpClient http, Dictionary<string, string> form) =>
        http.PostAsync("/oauth/token", new FormUrlEncodedContent(form), Ct);

    public static async Task<Tokens> ReadTokensAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new(
            body.GetProperty("access_token").GetString()!,
            body.GetProperty("refresh_token").GetString()!,
            body.GetProperty("scope").GetString()!,
            body.GetProperty("expires_in").GetInt32());
    }

    /// <summary>Registers, is allowed in by the person and takes its first tokens.</summary>
    public static async Task<(Tokens Tokens, Registered Client)> SignInAsync(WebApplicationFactory<Program> factory, TestUser user, bool allowWrite = true)
    {
        using var http = Http(factory);
        var client = await RegisterAsync(http);
        var (verifier, challenge) = Pkce();
        var answer = await ConsentAsync(user, Request(client.ClientId, challenge), allowWrite);
        var tokens = await ReadTokensAsync(await TokenAsync(http, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = answer["code"],
            ["redirect_uri"] = Redirect,
            ["client_id"] = client.ClientId,
            ["code_verifier"] = verifier,
            ["resource"] = BaseUrl + "/mcp",
        }));
        return (tokens, client);
    }

    /// <summary>An MCP client signed in with a token, as an agent is after OAuth.</summary>
    public static async Task<McpClient> ClientAsync(WebApplicationFactory<Program> factory, string accessToken)
    {
        var http = Http(factory);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(BaseUrl + "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    /// <summary>An MCP client that signs in by itself through OAuth, the person allowing it on the consent page.</summary>
    public static async Task<McpClient> SignedInClientAsync(WebApplicationFactory<Program> factory, TestUser user, bool allowWrite = true)
    {
        var http = Http(factory);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(BaseUrl + "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            OAuth = new ClientOAuthOptions
            {
                RedirectUri = new Uri(Redirect),
                DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "Claude Code" },
                AuthorizationCallbackHandler = async (context, cancellationToken) =>
                {
                    // The browser: from the authorization endpoint to the consent page, where the person allows it.
                    var authorize = await http.GetAsync(context.AuthorizationUri, cancellationToken);
                    Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
                    var consent = authorize.Headers.Location!;
                    Assert.Equal("/connect", consent.AbsolutePath);
                    var query = QueryHelpers.ParseQuery(consent.Query);
                    var answer = await ConsentAsync(user, new OAuthAuthorizationRequest
                    {
                        ResponseType = query["response_type"],
                        ClientId = query["client_id"],
                        RedirectUri = query["redirect_uri"],
                        CodeChallenge = query["code_challenge"],
                        CodeChallengeMethod = query["code_challenge_method"],
                        Scope = query["scope"],
                        State = query["state"],
                        Resource = query["resource"],
                    }, allowWrite);
                    return new AuthorizationResult { Code = answer["code"], State = answer["state"], Iss = answer["iss"] };
                },
            },
        }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    /// <summary>Calls a tool and returns what it answered, failing the test if the tool failed.</summary>
    public static async Task<JsonElement> CallAsync(this McpClient client, string tool, object? arguments = null)
    {
        var result = await client.CallToolAsync(tool, Arguments(arguments), cancellationToken: Ct);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, $"{tool} failed: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Calls a tool that should fail and returns what it said.</summary>
    public static async Task<string> FailAsync(this McpClient client, string tool, object? arguments = null)
    {
        var result = await client.CallToolAsync(tool, Arguments(arguments), cancellationToken: Ct);
        Assert.True(result.IsError);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    private static Dictionary<string, object?> Arguments(object? arguments) => arguments is null
        ? []
        : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments))!;
}
