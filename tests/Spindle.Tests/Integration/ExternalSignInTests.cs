using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Spindle.Api.Auth;
using Spindle.Api.Me;
using Spindle.Api.Subscriptions;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class ExternalSignInTests(PostgresApiFactory factory)
{
    private const string Verifier = "a-verifier-that-is-long-enough-for-pkce-0123456789";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_server_offers_google_then_apple()
    {
        using var client = factory.CreateClient();
        var server = await client.GetFromJsonAsync<ServerResponse>("/api/server", TestJson.Options, Ct);
        Assert.Equal(["google", "apple"], server!.SignInProviders!.Select(p => p.Name));
        Assert.Equal(["Google", "Apple"], server.SignInProviders!.Select(p => p.DisplayName));
    }

    [Fact]
    public async Task Google_creates_a_confirmed_account_without_a_password_and_signs_in_to_it_again()
    {
        var email = NewEmail();
        var subject = Guid.NewGuid().ToString("N");

        var first = await WebSignInAsync(factory, "google", subject, email, name: "Ana Horvat", returnTo: "/reports?range=week");
        Assert.True(first.NewAccount);
        Assert.Equal("/reports?range=week", first.ReturnTo);

        var me = await MeAsync(factory, first.AccessToken);
        Assert.Equal(email, me.Email);
        Assert.Equal("Ana Horvat", me.FullName);
        Assert.Equal("Europe/Zagreb", me.TimeZone);
        Assert.True(me.EmailConfirmed);
        Assert.False(me.HasPassword);
        Assert.NotNull(me.DefaultWorkspaceId);
        Assert.Empty(factory.Emails.To(email));

        var second = await WebSignInAsync(factory, "google", subject, "changed-" + email, name: "Someone Else");
        Assert.False(second.NewAccount);
        Assert.Equal(me.Id, (await MeAsync(factory, second.AccessToken)).Id);

        // The server redeemed the code with its secret and the PKCE verifier it kept in the state.
        var request = factory.IdentityProviders.TokenRequests["google"];
        Assert.Equal("google-secret", request["client_secret"]);
        Assert.Equal("https://track.example.com/api/auth/external/google/callback", request["redirect_uri"]);
        Assert.True(request["code_verifier"].Length >= 43);
    }

    [Fact]
    public async Task Apps_get_tokens_only_with_the_verifier_they_started_with()
    {
        var email = NewEmail();
        using var client = Client(factory);
        var start = await client.GetAsync(StartPath("google", $"redirectUri={Uri.EscapeDataString(ExternalSignInEndpoints.AppRedirectUri)}&codeChallenge={ExternalSignInEndpoints.Hash(Verifier)}&timeZone=Europe/Zagreb&acceptTerms=true"), Ct);
        var back = await CallbackAsync(client, "google", start, Guid.NewGuid().ToString("N"), email);
        Assert.StartsWith("spindle://auth/callback?code=", back.OriginalString, StringComparison.Ordinal);
        var code = QueryHelpers.ParseQuery(back.Query)["code"].ToString();

        var stolen = await client.PostAsJsonAsync("/api/auth/external/exchange", new { code, codeVerifier = new string('x', 43) }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, stolen.StatusCode);
        // A wrong verifier uses the code up.
        var late = await client.PostAsJsonAsync("/api/auth/external/exchange", new { code, codeVerifier = Verifier, deviceName = "iPhone" }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, late.StatusCode);

        start = await client.GetAsync(StartPath("google", $"redirectUri={Uri.EscapeDataString(ExternalSignInEndpoints.AppRedirectUri)}&codeChallenge={ExternalSignInEndpoints.Hash(Verifier)}&acceptTerms=true"), Ct);
        back = await CallbackAsync(client, "google", start, Guid.NewGuid().ToString("N"), NewEmail());
        code = QueryHelpers.ParseQuery(back.Query)["code"].ToString();
        var tokens = await TestUser.ReadAsync<ExternalSignInResponse>(
            await client.PostAsJsonAsync("/api/auth/external/exchange", new { code, codeVerifier = Verifier, deviceName = "iPhone" }, Ct));
        Assert.True(tokens.NewAccount);
        var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken }, Ct);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
    }

    [Fact]
    public async Task The_web_needs_the_cookie_from_the_browser_that_started()
    {
        using var client = Client(factory);
        var start = await client.GetAsync(StartPath("google", "acceptTerms=true"), Ct);
        var cookie = Assert.Single(start.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        var back = await CallbackAsync(client, "google", start, Guid.NewGuid().ToString("N"), NewEmail());
        Assert.StartsWith("https://track.example.com/external-sign-in?code=", back.OriginalString, StringComparison.Ordinal);

        // Someone else's browser, e.g. a victim sent the attacker's link, has no cookie.
        var code = QueryHelpers.ParseQuery(back.Query)["code"].ToString();
        var response = await client.PostAsJsonAsync("/api/auth/web/external/exchange", new { code }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_confirmed_account_with_the_address_gets_the_login_and_keeps_its_password()
    {
        var user = await TestUser.RegisterAsync(factory);
        await using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(u => u.Id == user.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true), Ct);
        }

        var signedIn = await WebSignInAsync(factory, "google", Guid.NewGuid().ToString("N"), user.Email.ToUpperInvariant());
        Assert.False(signedIn.NewAccount);
        Assert.Equal(user.UserId, (await MeAsync(factory, signedIn.AccessToken)).Id);

        using var password = factory.CreateClient();
        var login = await password.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "correct horse battery" }, Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(["google"], (await TestUser.ReadAsync<List<ExternalLoginResponse>>(await user.Client.GetAsync("/api/me/logins", Ct))).Select(l => l.Provider));
    }

    [Fact]
    public async Task An_unconfirmed_account_with_the_address_loses_the_password_someone_may_have_set_for_it()
    {
        var squatter = await TestUser.RegisterAsync(factory);
        List<Guid> before;
        await using (var scope = factory.CreateScope())
        {
            before = await scope.ServiceProvider.GetRequiredService<AppDbContext>().RefreshTokens
                .Where(t => t.UserId == squatter.UserId).Select(t => t.Id).ToListAsync(Ct);
        }

        var signedIn = await WebSignInAsync(factory, "google", Guid.NewGuid().ToString("N"), squatter.Email);
        Assert.Equal(squatter.UserId, (await MeAsync(factory, signedIn.AccessToken)).Id);

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = squatter.Email, password = "correct horse battery" }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        await using (var scope = factory.CreateScope())
        {
            // The squatter's sessions are signed out.
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().RefreshTokens
                .AnyAsync(t => before.Contains(t.Id) && t.RevokedAt == null, Ct));
        }
        var me = await MeAsync(factory, signedIn.AccessToken);
        Assert.True(me.EmailConfirmed);
        Assert.False(me.HasPassword);
    }

    [Fact]
    public async Task An_unconfirmed_email_from_the_provider_is_refused_with_a_reason()
    {
        using var client = Client(factory);
        var start = await client.GetAsync(StartPath("google", "acceptTerms=true"), Ct);
        var cookie = BindingCookie(start);
        var back = await CallbackAsync(client, "google", start, Guid.NewGuid().ToString("N"), NewEmail(), emailVerified: false);

        var response = await ExchangeWebAsync(client, back, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no confirmed email address", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_takes_people_by_invitation_refuses_strangers()
    {
        await using var closed = factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:AllowRegistration", "false"));
        using var client = Client(closed);
        var start = await client.GetAsync(StartPath("google", "acceptTerms=true"), Ct);
        var cookie = BindingCookie(start);
        var back = await CallbackAsync(client, "google", start, Guid.NewGuid().ToString("N"), NewEmail());

        var response = await ExchangeWebAsync(client, back, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("only by invitation", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apple_posts_a_form_with_the_name_and_gets_a_client_secret_signed_with_the_team_key()
    {
        var email = NewEmail();
        using var client = Client(factory);
        var start = await client.GetAsync(StartPath("apple", "acceptTerms=true&timeZone=America/New_York"), Ct);
        var cookie = BindingCookie(start);
        var authorize = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        Assert.Equal("form_post", authorize["response_mode"].ToString());
        Assert.Equal(FakeIdentityProviders.AppleServicesId, authorize["client_id"].ToString());

        factory.IdentityProviders.Issue("apple-code", new("apple", Guid.NewGuid().ToString("N"), email, true, null, authorize["nonce"].ToString()));
        var posted = await client.PostAsync("/api/auth/external/apple/callback", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["state"] = authorize["state"].ToString(),
            ["code"] = "apple-code",
            ["user"] = """{"name":{"firstName":"Marko","lastName":"Kovač"},"email":"x@example.com"}""",
        }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);

        var response = await ExchangeWebAsync(client, posted.Headers.Location!, cookie);
        var signedIn = await TestUser.ReadAsync<WebExternalSignInResponse>(response);
        var me = await MeAsync(factory, signedIn.AccessToken);
        Assert.Equal("Marko Kovač", me.FullName);
        Assert.Equal("America/New_York", me.TimeZone);
        // Apple only checked this address once, so the account confirms it by email like any other.
        Assert.False(me.EmailConfirmed);
        Assert.Single(factory.Emails.To(email));

        var secret = factory.IdentityProviders.TokenRequests["apple"]["client_secret"];
        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(secret, new TokenValidationParameters
        {
            ValidIssuer = "TEAM123456",
            ValidAudience = "https://appleid.apple.com",
            IssuerSigningKey = new ECDsaSecurityKey(factory.IdentityProviders.AppleKey),
        });
        Assert.True(validated.IsValid, validated.Exception?.Message);
        Assert.Equal(FakeIdentityProviders.AppleServicesId, ((JsonWebToken)validated.SecurityToken).Subject);
        Assert.Equal("KEY1234567", ((JsonWebToken)validated.SecurityToken).Kid);
    }

    [Fact]
    public async Task Apple_signs_in_more_than_once_with_the_same_key()
    {
        // Each sign-in signs a new client secret with the team's key, so the second must not find it gone.
        var first = await WebSignInAsync(factory, "apple", Guid.NewGuid().ToString("N"), NewEmail());
        var second = await WebSignInAsync(factory, "apple", Guid.NewGuid().ToString("N"), NewEmail());
        Assert.True(first.NewAccount);
        Assert.True(second.NewAccount);
    }

    [Fact]
    public async Task Turning_back_at_the_provider_returns_quietly_and_a_forged_state_goes_nowhere()
    {
        using var client = Client(factory);
        var start = await client.GetAsync(StartPath("google", $"redirectUri={Uri.EscapeDataString("http://127.0.0.1:53682/auth/callback")}&codeChallenge={ExternalSignInEndpoints.Hash(Verifier)}"), Ct);
        var state = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].ToString();

        var cancelled = await client.GetAsync($"/api/auth/external/google/callback?error=access_denied&state={Uri.EscapeDataString(state)}", Ct);
        Assert.Equal("http://127.0.0.1:53682/auth/callback?error=cancelled", cancelled.Headers.Location!.OriginalString);

        var forged = await client.GetAsync("/api/auth/external/google/callback?code=x&state=forged", Ct);
        Assert.Equal("https://track.example.com/external-sign-in?error=expired", forged.Headers.Location!.OriginalString);
        // The state of a Google sign-in doesn't work for Apple.
        var crossed = await client.PostAsync("/api/auth/external/apple/callback", new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = state, ["code"] = "x" }), Ct);
        Assert.Equal("https://track.example.com/external-sign-in?error=expired", crossed.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("https://evil.example/auth/callback")]
    [InlineData("http://192.168.1.10:8080/auth/callback")]
    [InlineData("http://127.0.0.1:8080/other")]
    [InlineData("spindle://auth/callback/../steal")]
    public async Task Codes_go_only_to_the_apps(string redirectUri)
    {
        using var client = Client(factory);
        var response = await client.GetAsync(StartPath("google", $"redirectUri={Uri.EscapeDataString(redirectUri)}&codeChallenge={ExternalSignInEndpoints.Hash(Verifier)}"), Ct);
        Assert.Equal(["redirectUri"], await TestUser.ErrorFieldsAsync(response));
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example")]
    public void Only_paths_on_this_site_are_kept_to_return_to(string path) =>
        Assert.Null(ExternalSignInEndpoints.SafeReturnPath(path));

    [Fact]
    public async Task An_account_without_a_password_confirms_by_having_signed_in_just_now()
    {
        var subject = Guid.NewGuid().ToString("N");
        var signedIn = await WebSignInAsync(factory, "google", subject, NewEmail());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", signedIn.AccessToken);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/me/passkeys/options", new { }, Ct)).StatusCode);
        var only = await client.DeleteAsync("/api/me/logins/google", Ct);
        Assert.Equal(["provider"], await TestUser.ErrorFieldsAsync(only));
        Assert.Equal(["currentPassword"], await TestUser.ErrorFieldsAsync(
            await client.PostAsJsonAsync("/api/me/password", new { currentPassword = "anything", newPassword = "a brand new password" }, Ct)));

        var me = await MeAsync(factory, signedIn.AccessToken);
        await using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.RefreshTokens.Where(t => t.UserId == me.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, t => t.CreatedAt.AddHours(-1)), Ct);
        }

        var stale = await client.PostAsJsonAsync("/api/me/delete", new { }, Ct);
        Assert.Equal(["password"], await TestUser.ErrorFieldsAsync(stale));

        var again = await WebSignInAsync(factory, "google", subject, me.Email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", again.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/me/delete", new { }, Ct)).StatusCode);

        // The login went with the account, so the same Google account makes a new one.
        var afterwards = await WebSignInAsync(factory, "google", subject, me.Email);
        Assert.True(afterwards.NewAccount);
        Assert.NotEqual(me.Id, (await MeAsync(factory, afterwards.AccessToken)).Id);
    }

    [Fact]
    public async Task An_address_Google_doesnt_run_makes_a_new_account_but_never_joins_one()
    {
        // e.g. a Google account made years ago with an address at a company that has since given it to someone else.
        var owner = await TestUser.RegisterAsync(factory);
        await using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(u => u.Id == owner.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true), Ct);
        }

        var refused = await TryWebSignInAsync(factory, "google", Guid.NewGuid().ToString("N"), owner.Email, hostedDomain: null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("already a Spindle account", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Empty(await TestUser.ReadAsync<List<ExternalLoginResponse>>(await owner.Client.GetAsync("/api/me/logins", Ct)));

        var squatter = await TestUser.RegisterAsync(factory);
        refused = await TryWebSignInAsync(factory, "google", Guid.NewGuid().ToString("N"), squatter.Email, hostedDomain: null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var email = NewEmail();
        var created = await WebSignInAsync(factory, "google", Guid.NewGuid().ToString("N"), email, hostedDomain: null);
        Assert.True(created.NewAccount);
        Assert.False((await MeAsync(factory, created.AccessToken)).EmailConfirmed);
        Assert.Single(factory.Emails.To(email));
    }

    [Fact]
    public async Task Gmail_is_Googles_own_and_joins_the_account()
    {
        var owner = await TestUser.RegisterAsync(factory, $"spindle-{Guid.NewGuid():N}@gmail.com");
        var signedIn = await WebSignInAsync(factory, "google", Guid.NewGuid().ToString("N"), owner.Email, hostedDomain: null);
        Assert.Equal(owner.UserId, (await MeAsync(factory, signedIn.AccessToken)).Id);
    }

    [Fact]
    public async Task The_person_with_the_inbox_takes_back_an_account_someone_made_with_their_address()
    {
        var subject = Guid.NewGuid().ToString("N");
        var email = NewEmail();
        var squatter = await WebSignInAsync(factory, "google", subject, email, hostedDomain: null);
        var account = (await MeAsync(factory, squatter.AccessToken)).Id;

        using var anonymous = factory.CreateClient();
        (await anonymous.PostAsJsonAsync("/api/auth/forgot-password", new { email }, Ct)).EnsureSuccessStatusCode();
        var token = System.Text.RegularExpressions.Regex.Match(factory.Emails.LastTo(email).TextBody, @"token=(?<token>[A-Za-z0-9_-]+)").Groups["token"].Value;
        (await anonymous.PostAsJsonAsync("/api/auth/reset-password", new { email, token, newPassword = "the real owner's password" }, Ct)).EnsureSuccessStatusCode();

        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.UserLogins.AnyAsync(l => l.UserId == account, Ct));
        // The squatter's Google account no longer gets in: the address is confirmed now, and Google doesn't run it.
        var again = await TryWebSignInAsync(factory, "google", subject, email, hostedDomain: null);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task A_session_signed_out_by_a_takeover_cant_confirm_anything_with_its_last_access_token()
    {
        var squatter = await TestUser.RegisterAsync(factory);
        await WebSignInAsync(factory, "google", Guid.NewGuid().ToString("N"), squatter.Email);

        // The squatter's access token still works for a few minutes and their session began just now.
        var passkey = await squatter.Client.PostAsJsonAsync("/api/me/passkeys/options", new { }, Ct);
        Assert.Equal(["password"], await TestUser.ErrorFieldsAsync(passkey));
        var delete = await squatter.Client.PostAsJsonAsync("/api/me/delete", new { }, Ct);
        Assert.Equal(["password"], await TestUser.ErrorFieldsAsync(delete));
    }

    /// <summary>Signs in on the web as a browser would: start, the provider, the callback, then the exchange with the cookie.</summary>
    private async Task<WebExternalSignInResponse> WebSignInAsync(
        WebApplicationFactory<Program> server, string provider, string subject, string email, string? name = null, string? returnTo = null,
        string? hostedDomain = Workspace) =>
        await TestUser.ReadAsync<WebExternalSignInResponse>(await TryWebSignInAsync(server, provider, subject, email, name, returnTo, hostedDomain));

    private async Task<HttpResponseMessage> TryWebSignInAsync(
        WebApplicationFactory<Program> server, string provider, string subject, string email, string? name = null, string? returnTo = null,
        string? hostedDomain = Workspace)
    {
        using var client = Client(server);
        var query = "acceptTerms=true&timeZone=Europe/Zagreb" + (returnTo is null ? "" : $"&returnTo={Uri.EscapeDataString(returnTo)}");
        var start = await client.GetAsync(StartPath(provider, query), Ct);
        var cookie = BindingCookie(start);
        var back = await CallbackAsync(client, provider, start, subject, email, name: name, hostedDomain: hostedDomain);
        return await ExchangeWebAsync(client, back, cookie);
    }

    /// <summary>The test addresses are at example.com; as a Google Workspace domain, Google runs their mailboxes.</summary>
    private const string Workspace = "example.com";

    private async Task<Uri> CallbackAsync(HttpClient client, string provider, HttpResponseMessage start, string subject, string email, bool emailVerified = true,
        string? name = null, string? hostedDomain = Workspace)
    {
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        var code = Guid.NewGuid().ToString("N");
        factory.IdentityProviders.Issue(code, new(provider, subject, email, emailVerified, name, authorize["nonce"].ToString(),
            HostedDomain: provider == "google" ? hostedDomain : null));
        var back = await client.GetAsync($"/api/auth/external/{provider}/callback?code={code}&state={Uri.EscapeDataString(authorize["state"].ToString())}", Ct);
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        return back.Headers.Location!;
    }

    private static Task<HttpResponseMessage> ExchangeWebAsync(HttpClient client, Uri back, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/web/external/exchange")
        {
            Content = JsonContent.Create(new { code = QueryHelpers.ParseQuery(back.Query)["code"].ToString() }),
        };
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request, Ct);
    }

    private static string BindingCookie(HttpResponseMessage start) =>
        Assert.Single(start.Headers.GetValues("Set-Cookie"), c => c.StartsWith(ExternalSignInEndpoints.BindingCookieName + "=", StringComparison.Ordinal)).Split(';')[0];

    private static async Task<MeResponse> MeAsync(WebApplicationFactory<Program> server, string accessToken)
    {
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await TestUser.ReadAsync<MeResponse>(await client.GetAsync("/api/me", Ct));
    }

    private static HttpClient Client(WebApplicationFactory<Program> server) =>
        server.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private static string StartPath(string provider, string query) => $"/api/auth/external/{provider}/start?{query}";

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";
}
