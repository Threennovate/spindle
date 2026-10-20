using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spindle.Api.Auth;
using Spindle.Api.Me;
using Spindle.Domain.Organizations;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class AuthFlowTests(PostgresApiFactory factory)
{
    private const string Password = "correct horse battery";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly HttpClient client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Registration_creates_an_organization_and_workspace_for_the_user()
    {
        var email = NewEmail();

        var tokens = await RegisterAsync(email);
        var me = await GetMeAsync(tokens.AccessToken);

        Assert.Equal(email, me.Email);
        Assert.Equal("Ana Anić", me.FullName);
        Assert.Equal("Europe/Zagreb", me.TimeZone);

        var organization = Assert.Single(me.Organizations);
        Assert.Equal("Ana Anić", organization.Name);
        Assert.Equal(OrganizationRole.Owner, organization.Role);

        var workspace = Assert.Single(me.Workspaces);
        Assert.Equal(organization.Id, workspace.OrganizationId);
        Assert.Equal(WorkspaceRole.Admin, workspace.Role);
        Assert.Equal(workspace.Id, me.DefaultWorkspaceId);
    }

    [Fact]
    public async Task An_email_can_only_be_registered_once_regardless_of_case()
    {
        var email = NewEmail();
        await RegisterAsync(email);

        var response = await client.PostAsJsonAsync("/api/auth/register", RegisterBody(email.ToUpperInvariant()), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct);
        Assert.Equal(["email"], problem!.Errors.Keys);
    }

    [Fact]
    public async Task Signing_in_returns_tokens_that_work()
    {
        var email = NewEmail();
        await RegisterAsync(email);

        var tokens = await LoginAsync(email);
        var me = await GetMeAsync(tokens.AccessToken);

        Assert.Equal(email, me.Email);
    }

    [Fact]
    public async Task Unknown_email_and_wrong_password_get_the_same_answer()
    {
        var email = NewEmail();
        await RegisterAsync(email);

        var wrongPassword = await PostLoginAsync(email, "wrong password");
        var unknownEmail = await PostLoginAsync(NewEmail(), Password);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        Assert.Equal(await TitleAsync(wrongPassword), await TitleAsync(unknownEmail));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        var email = NewEmail();
        await RegisterAsync(email);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostLoginAsync(email, "wrong password")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(email, "wrong password")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(email, Password)).StatusCode);
    }

    [Fact]
    public async Task Refreshing_returns_a_new_working_token_pair()
    {
        var first = await RegisterAsync(NewEmail());

        var second = await RefreshAsync(first.RefreshToken);

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        await GetMeAsync(second.AccessToken);
    }

    [Fact]
    public async Task A_refresh_whose_response_was_lost_can_be_retried()
    {
        var original = await RegisterAsync(NewEmail());
        await RefreshAsync(original.RefreshToken); // The client never receives this response.

        var retried = await RefreshAsync(original.RefreshToken);
        var next = await RefreshAsync(retried.RefreshToken);

        await GetMeAsync(next.AccessToken);
    }

    [Fact]
    public async Task Reusing_an_old_refresh_token_ends_that_session_only()
    {
        var email = NewEmail();
        var laptop = await RegisterAsync(email);
        var phone = await LoginAsync(email);
        var laptopCurrent = await RefreshAsync(laptop.RefreshToken);
        await MoveRotationsPastGracePeriodAsync(email);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefreshAsync(laptop.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefreshAsync(laptopCurrent.RefreshToken)).StatusCode);
        await RefreshAsync(phone.RefreshToken);
    }

    [Fact]
    public async Task Signing_out_ends_only_that_session()
    {
        var email = NewEmail();
        var laptop = await RegisterAsync(email);
        var phone = await LoginAsync(email);

        var response = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = laptop.RefreshToken }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefreshAsync(laptop.RefreshToken)).StatusCode);
        await RefreshAsync(phone.RefreshToken);
    }

    [Fact]
    public async Task Signing_out_with_an_unknown_token_reveals_nothing()
    {
        var response = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = "unknown" }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private static object RegisterBody(string email) => new
    {
        email,
        password = Password,
        fullName = "Ana Anić",
        timeZone = "Europe/Zagreb",
        deviceName = "Integration tests",
    };

    private async Task<AuthTokensResponse> RegisterAsync(string email) =>
        await ReadOkAsync<AuthTokensResponse>(await client.PostAsJsonAsync("/api/auth/register", RegisterBody(email), Ct));

    private Task<HttpResponseMessage> PostLoginAsync(string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { email, password }, Ct);

    private async Task<AuthTokensResponse> LoginAsync(string email) =>
        await ReadOkAsync<AuthTokensResponse>(await PostLoginAsync(email, Password));

    private Task<HttpResponseMessage> PostRefreshAsync(string refreshToken) =>
        client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken }, Ct);

    private async Task<AuthTokensResponse> RefreshAsync(string refreshToken) =>
        await ReadOkAsync<AuthTokensResponse>(await PostRefreshAsync(refreshToken));

    private async Task<MeResponse> GetMeAsync(string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await ReadOkAsync<MeResponse>(await client.SendAsync(request, Ct));
    }

    /// <summary>Makes every rotation of this user's tokens look older than the reuse grace period.</summary>
    private async Task MoveRotationsPastGracePeriodAsync(string email)
    {
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var normalizedEmail = email.ToUpperInvariant();

        await db.RefreshTokens
            .Where(t => t.RevokedAt != null && db.Users.Any(u => u.Id == t.UserId && u.NormalizedEmail == normalizedEmail))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, t => t.RevokedAt!.Value.AddMinutes(-1)), Ct);
    }

    private static async Task<T> ReadOkAsync<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected success but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");

        var body = await response.Content.ReadFromJsonAsync<T>(Json, Ct);
        Assert.NotNull(body);
        return body;
    }

    private static async Task<string?> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))?.Title;
}
