using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Spindle.Api.Auth;

namespace Spindle.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class WebAuthFlowTests(PostgresApiFactory factory)
{
    private const string Password = "correct horse battery";

    // A browser-like client: keeps cookies, and uses HTTPS so the Secure cookie is sent back.
    private readonly HttpClient browser = factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true,
    });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sign_in_keeps_the_refresh_token_out_of_reach_of_scripts()
    {
        var response = await browser.PostAsJsonAsync("/api/auth/web/register", RegisterBody(NewEmail()), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{WebAuthEndpoints.RefreshCookieName}=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"path={WebAuthEndpoints.RefreshCookiePath}", cookie, StringComparison.OrdinalIgnoreCase);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(["accessToken", "accessTokenExpiresAt"], body.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_reloaded_page_gets_a_new_access_token_from_the_cookie()
    {
        var email = NewEmail();
        await ReadAccessTokenAsync(await browser.PostAsJsonAsync("/api/auth/web/register", RegisterBody(email), Ct));

        var first = await ReadAccessTokenAsync(await browser.PostAsync("/api/auth/web/refresh", null, Ct));
        var second = await ReadAccessTokenAsync(await browser.PostAsync("/api/auth/web/refresh", null, Ct));

        Assert.NotEqual(first.AccessToken, second.AccessToken);
        Assert.Equal(email, await GetMeEmailAsync(second.AccessToken));
    }

    [Fact]
    public async Task Signing_in_with_the_wrong_password_sets_no_cookie()
    {
        var email = NewEmail();
        await browser.PostAsJsonAsync("/api/auth/web/register", RegisterBody(email), Ct);
        using var otherBrowser = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        var response = await otherBrowser.PostAsJsonAsync("/api/auth/web/login", new { email, password = "wrong password" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Signing_out_ends_the_session_and_clears_the_cookie()
    {
        var email = NewEmail();
        await browser.PostAsJsonAsync("/api/auth/web/register", RegisterBody(email), Ct);

        var logout = await browser.PostAsync("/api/auth/web/logout", null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains("expires=Thu, 01 Jan 1970", Assert.Single(logout.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/api/auth/web/refresh", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Refresh_without_a_cookie_is_rejected()
    {
        using var freshBrowser = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        var response = await freshBrowser.PostAsync("/api/auth/web/refresh", null, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_api_routes_are_not_found_rather_than_the_web_app()
    {
        var response = await browser.GetAsync("/api/does-not-exist", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await browser.GetAsync("/health", Ct);

        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        // Kept out of search results; the website is what people should find.
        Assert.Equal("noindex, nofollow", response.Headers.GetValues("X-Robots-Tag").Single());
    }

    private static string NewEmail() => $"web-{Guid.NewGuid():N}@example.com";

    private static object RegisterBody(string email) => new
    {
        email,
        password = Password,
        fullName = "Ana Anić",
        timeZone = "Europe/Zagreb",
        deviceName = "Browser",
    };

    private static async Task<AccessTokenResponse> ReadAccessTokenAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<AccessTokenResponse>(Ct))!;
    }

    private async Task<string?> GetMeEmailAsync(string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await browser.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("email").GetString();
    }
}
