using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;

namespace Spindle.Tests.Api;

public sealed class RequestValidationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task Register_reports_every_missing_field()
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new { }, TestContext.Current.CancellationToken);

        var problem = await ReadValidationProblemAsync(response);
        Assert.Equal(["email", "fullName", "password", "timeZone"], problem.Errors.Keys.Order());
    }

    [Fact]
    public async Task Register_rejects_invalid_values()
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "not-an-email",
            password = "short",
            fullName = "Ana",
            timeZone = "Central European Standard Time",
        }, TestContext.Current.CancellationToken);

        var problem = await ReadValidationProblemAsync(response);
        Assert.Equal(["email", "password", "timeZone"], problem.Errors.Keys.Order());
    }

    [Fact]
    public async Task Login_requires_email_and_password()
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { }, TestContext.Current.CancellationToken);

        var problem = await ReadValidationProblemAsync(response);
        Assert.Equal(["email", "password"], problem.Errors.Keys.Order());
    }

    [Fact]
    public async Task Me_requires_a_token()
    {
        var response = await client.GetAsync("/api/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_rejects_a_forged_token()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIwMDAwMDAwMC0wMDAwLTAwMDAtMDAwMC0wMDAwMDAwMDAwMDEifQ.c2lnbmF0dXJl"); // gitleaks:allow, forged on purpose

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<HttpValidationProblemDetails> ReadValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        return problem;
    }
}
