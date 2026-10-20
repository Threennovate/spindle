using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spindle.Tests.Api;

/// <summary>After a move to another domain: browsers on the old one go to the new one, apps keep working there.</summary>
public sealed class PreviousHostTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client() => factory
        .WithWebHostBuilder(builder => builder.UseSetting("App:PreviousHosts:0", "old.example.com"))
        .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static HttpRequestMessage Get(string path, string host) =>
        new(HttpMethod.Get, path) { Headers = { Host = host } };

    [Fact]
    public async Task A_page_on_the_old_domain_moves_to_the_same_page_on_the_new_one()
    {
        var response = await Client().SendAsync(Get("/timer?from=email", "old.example.com"), Ct);

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("https://track.example.com/timer?from=email", response.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/me")]
    [InlineData("/downloads/mac/appcast.xml")]
    [InlineData("/.well-known/apple-app-site-association")]
    public async Task What_apps_use_still_answers_on_the_old_domain(string path)
    {
        var response = await Client().SendAsync(Get(path, "old.example.com"), Ct);

        Assert.NotEqual(HttpStatusCode.MovedPermanently, response.StatusCode);
    }

    [Fact]
    public async Task The_new_domain_is_not_redirected()
    {
        var response = await Client().SendAsync(Get("/timer", "track.example.com"), Ct);

        Assert.NotEqual(HttpStatusCode.MovedPermanently, response.StatusCode);
    }
}
