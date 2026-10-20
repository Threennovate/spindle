using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spindle.Api.Common;
using Spindle.Api.Subscriptions;
using Spindle.Api.Team;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class RegistrationTests(PostgresApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_server_with_terms_takes_new_accounts_only_if_they_agree_and_remembers_which_terms()
    {
        await using var server = factory.WithWebHostBuilder(builder => builder
            .UseSetting("App:TermsUrl", "https://spindle.example.com/terms")
            .UseSetting("App:PrivacyUrl", "https://spindle.example.com/privacy")
            .UseSetting("App:TermsVersion", "2026-10-01"));
        var client = server.CreateClient();

        var info = await client.GetFromJsonAsync<ServerResponse>("/api/server", TestJson.Options, Ct);
        var refused = await client.PostAsJsonAsync("/api/auth/register", Body(NewEmail(), acceptTerms: false), Ct);
        var email = NewEmail();
        var accepted = await client.PostAsJsonAsync("/api/auth/register", Body(email, acceptTerms: true), Ct);

        Assert.Equal(("https://spindle.example.com/terms", "https://spindle.example.com/privacy"), (info!.TermsUrl, info.PrivacyUrl));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(["acceptTerms"], (await refused.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct))!.Errors.Keys);
        accepted.EnsureSuccessStatusCode();
        var user = await UserAsync(email);
        Assert.NotNull(user.TermsAcceptedAt);
        Assert.Equal("2026-10-01", user.TermsVersion);
    }

    [Fact]
    public async Task A_server_without_terms_asks_nobody_to_agree()
    {
        var client = factory.CreateClient();
        var email = NewEmail();

        var info = await client.GetFromJsonAsync<ServerResponse>("/api/server", TestJson.Options, Ct);
        (await client.PostAsJsonAsync("/api/auth/register", Body(email, acceptTerms: false), Ct)).EnsureSuccessStatusCode();

        Assert.Equal((true, (string?)null, (string?)null), (info!.RegistrationOpen, info.TermsUrl, info.PrivacyUrl));
        Assert.Null((await UserAsync(email)).TermsAcceptedAt);
    }

    [Fact]
    public async Task A_closed_server_takes_new_people_only_through_invitations()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var invited = NewEmail();
        await owner.CreateAsync<SentInvitationResponse>("/invitations", new { email = invited });
        await using var closed = factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:AllowRegistration", "false"));
        var client = closed.CreateClient();

        var info = await client.GetFromJsonAsync<ServerResponse>("/api/server", TestJson.Options, Ct);
        var stranger = await client.PostAsJsonAsync("/api/auth/register", Body(NewEmail(), acceptTerms: true), Ct);

        Assert.False(info!.RegistrationOpen);
        Assert.Equal(HttpStatusCode.BadRequest, stranger.StatusCode);
        Assert.Equal(["invitationToken"], (await stranger.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct))!.Errors.Keys);
        await TestUser.RegisterAsync(closed, invited, CapturingEmailSender.InvitationToken(factory.Emails.LastTo(invited)));
    }

    [Fact]
    public void Terms_need_a_version()
    {
        using var server = factory.WithWebHostBuilder(builder => builder.UseSetting("App:TermsUrl", "https://spindle.example.com/terms"));

        Assert.Throws<OptionsValidationException>(() => server.Services.GetRequiredService<IOptions<AppOptions>>().Value);
    }

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private static object Body(string email, bool acceptTerms) => new
    {
        email,
        password = "correct horse battery",
        fullName = "New Person",
        timeZone = "Europe/Zagreb",
        acceptTerms,
    };

    private async Task<Infrastructure.Identity.AppUser> UserAsync(string email)
    {
        await using var scope = factory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u => u.Email == email, Ct);
    }
}
