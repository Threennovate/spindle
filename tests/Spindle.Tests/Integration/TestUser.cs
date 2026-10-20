using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Spindle.Api.Auth;
using Spindle.Api.Me;

namespace Spindle.Tests.Integration;

internal static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <summary>A freshly registered user with a signed-in client and their own workspace.</summary>
internal sealed class TestUser(HttpClient client, Guid userId, Guid workspaceId, string email)
{
    public string Email { get; } = email;

    public HttpClient Client { get; } = client;

    public Guid UserId { get; } = userId;

    public Guid WorkspaceId { get; } = workspaceId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Task<TestUser> RegisterAsync(WebApplicationFactory<Program> factory) =>
        RegisterAsync(factory, $"user-{Guid.NewGuid():N}@example.com");

    /// <summary>Registers, optionally through an invitation, and signs in.</summary>
    public static async Task<TestUser> RegisterAsync(
        WebApplicationFactory<Program> factory,
        string email,
        string? invitationToken = null,
        string fullName = "Test User")
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "correct horse battery",
            fullName,
            timeZone = "Europe/Zagreb",
            invitationToken,
            acceptTerms = true,
        }, Ct);
        response.EnsureSuccessStatusCode();

        var tokens = await response.Content.ReadFromJsonAsync<AuthTokensResponse>(Ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        var me = await client.GetFromJsonAsync<MeResponse>("/api/me", TestJson.Options, Ct);

        return new TestUser(client, me!.Id, me.DefaultWorkspaceId!.Value, email);
    }

    public async Task<MeResponse> MeAsync() => (await Client.GetFromJsonAsync<MeResponse>("/api/me", TestJson.Options, Ct))!;

    /// <summary>A path inside this user's workspace, e.g. <c>/time-entries</c>.</summary>
    public string In(string path) => $"/api/workspaces/{WorkspaceId}{path}";

    public Task<HttpResponseMessage> PostAsync(string path, object body) => Client.PostAsJsonAsync(In(path), body, Ct);

    public Task<HttpResponseMessage> PutAsync(string path, object body) => Client.PutAsJsonAsync(In(path), body, Ct);

    public Task<HttpResponseMessage> DeleteAsync(string path) => Client.DeleteAsync(In(path), Ct);

    public async Task<T> GetAsync<T>(string path) => await ReadAsync<T>(await Client.GetAsync(In(path), Ct));

    /// <summary>Posts and expects success.</summary>
    public async Task<T> CreateAsync<T>(string path, object body) => await ReadAsync<T>(await PostAsync(path, body));

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected success but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Options, Ct))!;
    }

    /// <summary>Asserts a 400 and returns the fields that have errors.</summary>
    public static async Task<IReadOnlyCollection<string>> ErrorFieldsAsync(HttpResponseMessage response)
    {
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Http.HttpValidationProblemDetails>(Ct);
        return problem!.Errors.Keys.Order().ToList();
    }
}
