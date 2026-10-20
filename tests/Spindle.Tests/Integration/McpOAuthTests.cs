using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Spindle.Api.Mcp;
using Spindle.Api.Me;
using Spindle.Api.Auth;
using static Spindle.Tests.Integration.AgentConnector;

namespace Spindle.Tests.Integration;

/// <summary>Agents sign in to Spindle's MCP server with OAuth: discovery, registration, consent, PKCE, sessions.</summary>
[Collection(PostgresCollection.Name)]
public sealed class McpOAuthTests(PostgresApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_without_a_token_is_told_where_to_sign_in()
    {
        using var http = Http(factory);

        var response = await http.PostAsync("/mcp", JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains($"resource_metadata=\"{BaseUrl}/.well-known/oauth-protected-resource/mcp\"", challenge, StringComparison.Ordinal);

        var resource = await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp", Ct);
        Assert.Equal(BaseUrl + "/mcp", resource.GetProperty("resource").GetString());
        Assert.Equal(BaseUrl, resource.GetProperty("authorization_servers")[0].GetString());

        var server = await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server", Ct);
        Assert.Equal(BaseUrl, server.GetProperty("issuer").GetString());
        Assert.Equal(["S256"], server.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()));
        Assert.True(server.GetProperty("client_id_metadata_document_supported").GetBoolean());
        Assert.Equal(BaseUrl + "/oauth/register", server.GetProperty("registration_endpoint").GetString());
    }

    [Fact]
    public async Task An_agent_the_person_allows_in_is_a_device_session_that_can_be_signed_out()
    {
        var user = await TestUser.RegisterAsync(factory);
        var (tokens, client) = await SignInAsync(factory, user);
        Assert.Equal("read write", tokens.Scope);

        // Listed under Account, with what it may reach.
        var sessions = await user.Client.GetFromJsonAsync<List<SessionResponse>>("/api/me/sessions", TestJson.Options, Ct);
        var agent = Assert.Single(sessions!, s => s.Agent is not null);
        Assert.Equal("Claude Code", agent.DeviceName);
        Assert.Equal((user.WorkspaceId, true), (agent.Agent!.WorkspaceId, agent.Agent.CanWrite));

        await using (var mcp = await ClientAsync(factory, tokens.AccessToken))
        {
            Assert.Contains((await mcp.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name), name => name == "propose_entries");
        }

        // The token is for the MCP server alone, and its refresh token renews nothing but MCP tokens.
        using var http = Http(factory);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken }, Ct)).StatusCode);

        var renewed = await ReadTokensAsync(await TokenAsync(http, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.RefreshToken, ["client_id"] = client.ClientId }));
        Assert.NotEqual(tokens.RefreshToken, renewed.RefreshToken);

        // Signed out under Account: its token stops working at once.
        Assert.Equal(HttpStatusCode.NoContent, (await user.Client.DeleteAsync($"/api/me/sessions/{agent.Id}", Ct)).StatusCode);
        using var signedOut = Http(factory);
        signedOut.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", renewed.AccessToken);
        var refused = await signedOut.PostAsync("/mcp", JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("invalid_token", refused.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_old_refresh_token_is_refused_without_signing_the_agent_out()
    {
        // Agents run as several processes sharing one sign-in; one holding an old token must not sign the rest out.
        var strict = factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:RefreshTokenReuseGracePeriod", "00:00:00"));
        var user = await TestUser.RegisterAsync(strict);
        var (tokens, client) = await SignInAsync(strict, user);
        using var http = Http(strict);
        Dictionary<string, string> Refresh(string token) => new() { ["grant_type"] = "refresh_token", ["refresh_token"] = token, ["client_id"] = client.ClientId };

        var renewed = await ReadTokensAsync(await TokenAsync(http, Refresh(tokens.RefreshToken)));
        var stale = await TokenAsync(http, Refresh(tokens.RefreshToken));

        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        var again = await ReadTokensAsync(await TokenAsync(http, Refresh(renewed.RefreshToken)));
        await using var mcp = await ClientAsync(strict, again.AccessToken);
        Assert.NotEmpty(await mcp.ListToolsAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task The_mcp_sdk_client_signs_in_through_the_consent_page()
    {
        var user = await TestUser.RegisterAsync(factory);

        await using var mcp = await SignedInClientAsync(factory, user, allowWrite: false);
        var timer = await mcp.CallAsync("get_timer");

        Assert.Equal(user.WorkspaceId, timer.GetProperty("workspace_id").GetGuid());
        Assert.Contains("propose_entries", mcp.ServerInstructions, StringComparison.Ordinal);
        var sessions = await user.Client.GetFromJsonAsync<List<SessionResponse>>("/api/me/sessions", TestJson.Options, Ct);
        Assert.False(Assert.Single(sessions!, s => s.Agent is not null).Agent!.CanWrite);
    }

    [Fact]
    public async Task Codes_are_used_once_and_only_with_their_verifier()
    {
        var user = await TestUser.RegisterAsync(factory);
        using var http = Http(factory);
        var client = await RegisterAsync(http);
        var (verifier, challenge) = Pkce();
        var answer = await ConsentAsync(user, Request(client.ClientId, challenge));
        Assert.Equal(("xyz", BaseUrl), (answer["state"], answer["iss"]));
        Dictionary<string, string> Exchange(string codeVerifier) => new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = answer["code"],
            ["redirect_uri"] = Redirect,
            ["client_id"] = client.ClientId,
            ["code_verifier"] = codeVerifier,
        };

        var wrong = await TokenAsync(http, Exchange(Pkce().Verifier));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("invalid_grant", (await wrong.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());

        // The failed attempt used it up, as an intercepted code should be.
        Assert.Equal(HttpStatusCode.BadRequest, (await TokenAsync(http, Exchange(verifier))).StatusCode);

        var second = await ConsentAsync(user, Request(client.ClientId, challenge));
        var form = Exchange(verifier);
        form["code"] = second["code"];
        var tokens = await ReadTokensAsync(await TokenAsync(http, form));
        Assert.Equal("read write", tokens.Scope);
        Assert.Equal(HttpStatusCode.BadRequest, (await TokenAsync(http, form)).StatusCode);
    }

    [Fact]
    public async Task Answers_go_only_to_addresses_the_client_registered()
    {
        var user = await TestUser.RegisterAsync(factory);
        using var http = Http(factory);
        var client = await RegisterAsync(http);
        var (_, challenge) = Pkce();

        var elsewhere = await user.Client.PostAsJsonAsync("/api/oauth/authorizations/preview", Request(client.ClientId, challenge, redirect: "https://evil.example/callback"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.False((await elsewhere.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("redirectUrl", out _));

        // A local agent picks a free port each time; the port may change, nothing else.
        var preview = await TestUser.ReadAsync<OAuthAuthorizationPreview>(
            await user.Client.PostAsJsonAsync("/api/oauth/authorizations/preview", Request(client.ClientId, challenge, redirect: "http://127.0.0.1:50999/callback"), Ct));
        Assert.Equal(("Claude Code", "127.0.0.1", true, true), (preview.ClientName, preview.RedirectHost, preview.LocalRedirect, preview.AsksToWrite));

        var noPkce = await user.Client.PostAsJsonAsync("/api/oauth/authorizations/preview", Request(client.ClientId, challenge) with { CodeChallenge = null }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noPkce.StatusCode);
        Assert.Contains("error=invalid_request", (await noPkce.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("redirectUrl").GetString(), StringComparison.Ordinal);

        var denied = await ConsentAsync(user, Request(client.ClientId, challenge), approve: false);
        Assert.Equal(("access_denied", "xyz"), (denied["error"], denied["state"]));

        var registration = await http.PostAsJsonAsync("/oauth/register", new { client_name = "x", redirect_uris = new[] { "http://evil.example/callback" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, registration.StatusCode);

        var other = await TestUser.RegisterAsync(factory);
        var foreign = await user.Client.PostAsJsonAsync("/api/oauth/authorizations",
            new { request = Request(client.ClientId, challenge), approve = true, workspaceId = other.WorkspaceId, allowWrite = true }, Ct);
        Assert.Equal(["workspaceId"], await TestUser.ErrorFieldsAsync(foreign));
    }

    [Fact]
    public async Task A_client_named_by_its_metadata_document_needs_no_registration()
    {
        const string document = "https://claude.example/oauth/client.json";
        var withDocument = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient<ClientMetadataDocuments>().ConfigurePrimaryHttpMessageHandler(() => new DocumentHandler(document))));
        var user = await TestUser.RegisterAsync(withDocument);
        using var http = Http(withDocument);
        var (verifier, challenge) = Pkce();

        var preview = await TestUser.ReadAsync<OAuthAuthorizationPreview>(
            await user.Client.PostAsJsonAsync("/api/oauth/authorizations/preview", Request(document, challenge), Ct));
        Assert.Equal(("Claude", "claude.example"), (preview.ClientName, preview.ClientHost));

        var answer = await ConsentAsync(user, Request(document, challenge));
        var tokens = await ReadTokensAsync(await TokenAsync(http, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = answer["code"],
            ["redirect_uri"] = Redirect,
            ["client_id"] = document,
            ["code_verifier"] = verifier,
        }));
        Assert.NotEmpty(tokens.AccessToken);

        var unknown = await user.Client.PostAsJsonAsync("/api/oauth/authorizations/preview", Request("https://claude.example/other.json", challenge), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public void Metadata_documents_are_read_only_from_public_addresses()
    {
        Assert.False(ClientMetadataDocuments.IsPublic(IPAddress.Parse("127.0.0.1")));
        Assert.False(ClientMetadataDocuments.IsPublic(IPAddress.Parse("10.1.2.3")));
        Assert.False(ClientMetadataDocuments.IsPublic(IPAddress.Parse("169.254.169.254")));
        Assert.False(ClientMetadataDocuments.IsPublic(IPAddress.Parse("::ffff:192.168.1.1")));
        Assert.False(ClientMetadataDocuments.IsPublic(IPAddress.Parse("fd00::1")));
        Assert.True(ClientMetadataDocuments.IsPublic(IPAddress.Parse("140.82.121.4")));
        Assert.False(ClientMetadataDocuments.IsDocumentUrl("http://claude.example/client.json"));
        Assert.False(ClientMetadataDocuments.IsDocumentUrl("https://claude.example/"));
        Assert.True(ClientMetadataDocuments.IsDocumentUrl("https://claude.example/client.json"));
    }

    /// <summary>Serves one client metadata document, as its host would.</summary>
    private sealed class DocumentHandler(string url) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.ToString() != url)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var json = JsonSerializer.Serialize(new { client_id = url, client_name = "Claude", redirect_uris = new[] { Redirect }, token_endpoint_auth_method = "none" });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
