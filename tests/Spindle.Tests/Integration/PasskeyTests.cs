using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spindle.Api.Auth;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class PasskeyTests(PostgresApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Native_challenges_use_expiring_single_use_tokens_and_are_separate_from_browser_challenges()
    {
        using var client = factory.CreateClient(new() { HandleCookies = false });
        var response = await client.PostAsync("/api/auth/passkeys/options", null, Ct);
        var options = await TestUser.ReadAsync<NativePasskeyOptionsResponse>(response);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var hash = Hash(options.ChallengeToken);
        var body = new { options.ChallengeToken, credentialJson = "{}" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/passkeys/login", body, Ct)).StatusCode);
        await using var scope = factory.CreateScope();
        Assert.False(await Db(scope).PasskeyChallenges.AnyAsync(c => c.TokenHash == hash, Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/passkeys/login", body, Ct)).StatusCode);

        response = await client.PostAsync("/api/auth/web/passkeys/options", null, Ct);
        var token = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0].Split('=')[1];
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/passkeys/login", new { challengeToken = token, credentialJson = "{}" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Apple_domain_association_lists_both_signed_app_identifiers()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/.well-known/apple-app-site-association", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(new[] { "9ARDA688G5.com.spindletrack.mac", "9ARDA688G5.com.spindletrack.iphone" },
            json.RootElement.GetProperty("webcredentials").GetProperty("apps").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task Creation_requires_authentication_and_password_confirmation()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me/passkeys", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/me/passkeys/options", new { password = "correct horse battery" }, Ct)).StatusCode);
        var user = await TestUser.RegisterAsync(factory);
        var wrong = await user.Client.PostAsJsonAsync("/api/me/passkeys/options", new { password = "wrong password" }, Ct);
        Assert.Equal(["password"], await TestUser.ErrorFieldsAsync(wrong));
        Assert.False(wrong.Headers.Contains("Set-Cookie"));
        var options = await user.Client.PostAsJsonAsync("/api/me/passkeys/options", new { password = "correct horse battery" }, Ct);
        var result = await TestUser.ReadAsync<PasskeyOptionsResponse>(options);
        using var json = JsonDocument.Parse(result.OptionsJson);
        Assert.Equal(user.UserId.ToString(), Encoding.UTF8.GetString(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(json.RootElement.GetProperty("user").GetProperty("id").GetString()!)));
        Assert.Equal("required", json.RootElement.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());
        Assert.Equal("required", json.RootElement.GetProperty("authenticatorSelection").GetProperty("residentKey").GetString());
        Assert.Contains("httponly", Assert.Single(options.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_challenge_is_consumed_even_when_the_credential_is_invalid_and_cannot_be_replayed()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsync("/api/auth/web/passkeys/options", null, Ct);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
        var token = cookie.Split('=')[1];
        var hash = Hash(token);
        using var other = factory.CreateClient(new() { HandleCookies = false });
        other.DefaultRequestHeaders.Add("Cookie", cookie);
        var invalid = await other.PostAsJsonAsync("/api/auth/web/passkeys/login", new { credentialJson = "{}" }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        await using var scope = factory.CreateScope();
        Assert.False(await Db(scope).PasskeyChallenges.AnyAsync(c => c.TokenHash == hash, Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.PostAsJsonAsync("/api/auth/web/passkeys/login", new { credentialJson = "{}" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Expired_challenges_and_cross_account_registration_are_rejected()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var other = await TestUser.RegisterAsync(factory);
        var response = await owner.Client.PostAsJsonAsync("/api/me/passkeys/options", new { password = "correct horse battery" }, Ct);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
        other.Client.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(["credentialJson"], await TestUser.ErrorFieldsAsync(await other.Client.PostAsJsonAsync("/api/me/passkeys", new { name = "Stolen", credentialJson = "{}" }, Ct)));

        using var client = factory.CreateClient(new() { HandleCookies = false });
        response = await client.PostAsync("/api/auth/web/passkeys/options", null, Ct);
        cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
        var hash = Hash(cookie.Split('=')[1]);
        await using var scope = factory.CreateScope();
        await Db(scope).PasskeyChallenges.Where(c => c.TokenHash == hash).ExecuteUpdateAsync(set => set.SetProperty(c => c.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/web/passkeys/login", new { credentialJson = "{}" }, Ct)).StatusCode);
        Assert.False(await Db(scope).PasskeyChallenges.AnyAsync(c => c.TokenHash == hash, Ct));
    }

    [Fact]
    public async Task People_can_only_list_and_remove_their_own_passkeys_and_deletion_erases_them()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var other = await TestUser.RegisterAsync(factory);
        await using var scope = factory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = (await users.FindByIdAsync(owner.UserId.ToString()))!;
        var passkey = new UserPasskeyInfo(RandomNumberGenerator.GetBytes(32), [1, 2, 3], DateTimeOffset.UtcNow, 0, [], true, false, false, [], []) { Name = "Laptop" };
        Assert.True((await users.AddOrUpdatePasskeyAsync(user, passkey)).Succeeded);
        var id = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(passkey.CredentialId);
        Assert.Empty((await other.Client.GetFromJsonAsync<PasskeyResponse[]>("/api/me/passkeys", Ct))!);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.DeleteAsync($"/api/me/passkeys/{id}", Ct)).StatusCode);
        Assert.Equal("Laptop", Assert.Single((await owner.Client.GetFromJsonAsync<PasskeyResponse[]>("/api/me/passkeys", Ct))!).Name);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/me/passkeys/{id}", Ct)).StatusCode);
        Db(scope).ChangeTracker.Clear();
        user = (await users.FindByIdAsync(owner.UserId.ToString()))!;
        Assert.True((await users.AddOrUpdatePasskeyAsync(user, passkey)).Succeeded);
        await owner.Client.PostAsJsonAsync("/api/me/passkeys/options", new { password = "correct horse battery" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.PostAsJsonAsync("/api/me/delete", new { password = "correct horse battery" }, Ct)).StatusCode);
        Assert.False(await Db(scope).UserPasskeys.AnyAsync(p => p.UserId == owner.UserId, Ct));
        Assert.False(await Db(scope).PasskeyChallenges.AnyAsync(c => c.UserId == owner.UserId, Ct));
    }

    private static AppDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
