using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spindle.Api.Auth;
using Spindle.Api.Me;
using Spindle.Api.Subscriptions;
using Spindle.Api.Team;
using Spindle.Api.Workspaces;
using Spindle.Domain.Subscriptions;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class SubscriptionTests(PostgresApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Signing_up_starts_a_90_day_trial_of_the_new_organization()
    {
        var before = DateTimeOffset.UtcNow;
        var owner = await TestUser.RegisterAsync(factory);
        var organization = Assert.Single((await owner.MeAsync()).Organizations);
        var subscription = await SubscriptionAsync(owner, organization.Id);

        Assert.Equal(OrganizationAccess.Full, organization.Access);
        Assert.InRange(organization.TrialEndsAt!.Value, before.AddDays(90), DateTimeOffset.UtcNow.AddDays(90));
        Assert.Equal(SubscriptionStatus.Trialing, subscription.Status);
        Assert.Equal(OrganizationAccess.Full, subscription.Access);
        Assert.Equal(organization.TrialEndsAt, subscription.TrialEndsAt);
        Assert.Null(subscription.PaidUntil);
        Assert.Equal(1, subscription.Seats);
        Assert.False(subscription.CheckoutAvailable);
        Assert.True((await factory.CreateClient().GetFromJsonAsync<ServerResponse>("/api/server", TestJson.Options, Ct))!.Hosted);
    }

    [Fact]
    public async Task A_seat_is_a_person_in_any_of_the_organizations_workspaces_counted_once()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        var member = await factory.AddMemberAsync(owner);
        var leaving = await factory.AddMemberAsync(owner);
        await owner.CreateAsync<SentInvitationResponse>("/invitations", new { email = $"pending-{Guid.NewGuid():N}@example.com" });

        // The member joins a second workspace of the same organization too.
        var second = await TestUser.ReadAsync<WorkspaceSummary>(await owner.Client.PostAsJsonAsync("/api/workspaces", new { name = "Second" }, Ct));
        await owner.Client.PostAsJsonAsync($"/api/workspaces/{second.Id}/invitations", new { email = member.Email }, Ct);
        var accepted = await member.Client.PostAsync($"/api/invitations/{CapturingEmailSender.InvitationToken(factory.Emails.LastTo(member.Email))}/accept", null, Ct);
        accepted.EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/members/{leaving.UserId}")).EnsureSuccessStatusCode();

        // Owner and member; the one who left and the pending invitation don't count, nor does the second workspace.
        Assert.Equal(2, (await SubscriptionAsync(owner, organizationId)).Seats);
    }

    [Fact]
    public async Task Only_the_organizations_owners_and_admins_see_its_plan()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        var member = await factory.AddMemberAsync(owner);
        var stranger = await TestUser.RegisterAsync(factory);

        var memberSees = Assert.Single((await member.MeAsync()).Organizations);

        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.GetAsync(SubscriptionPath(organizationId), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.GetAsync(SubscriptionPath(organizationId), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(SubscriptionPath(organizationId), Ct)).StatusCode);
        // Members still learn whether they can change anything, and until when.
        Assert.Equal((OrganizationAccess.Full, (await SubscriptionAsync(owner, organizationId)).TrialEndsAt), (memberSees.Access, memberSees.TrialEndsAt));
    }

    [Fact]
    public async Task An_ended_trial_leaves_the_organization_read_only()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        await factory.EndTrialAsync(organizationId);

        var organization = Assert.Single((await owner.MeAsync()).Organizations);
        var subscription = await SubscriptionAsync(owner, organizationId);

        Assert.Equal(OrganizationAccess.ReadOnly, organization.Access);
        Assert.Equal((SubscriptionStatus.Trialing, OrganizationAccess.ReadOnly), (subscription.Status, subscription.Access));
    }

    [Fact]
    public async Task Deleting_the_only_account_takes_the_subscription_along()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;

        var deleted = await owner.Client.PostAsJsonAsync("/api/me/delete", new { password = "correct horse battery" }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using var scope = factory.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().OrganizationSubscriptions
            .AnyAsync(s => s.OrganizationId == organizationId, Ct));
    }

    [Fact]
    public async Task A_self_hosted_server_limits_nobody_and_shows_no_plan()
    {
        await using var selfHosted = factory.WithWebHostBuilder(builder => builder.UseSetting("Subscriptions:Enabled", "false"));
        var client = selfHosted.CreateClient();
        var registered = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"self-{Guid.NewGuid():N}@example.com",
            password = "correct horse battery",
            fullName = "Self Hoster",
            timeZone = "Europe/Zagreb",
        }, Ct);
        var tokens = await TestUser.ReadAsync<AuthTokensResponse>(registered);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var server = await client.GetFromJsonAsync<ServerResponse>("/api/server", TestJson.Options, Ct);
        var organization = Assert.Single((await client.GetFromJsonAsync<MeResponse>("/api/me", TestJson.Options, Ct))!.Organizations);

        Assert.False(server!.Hosted);
        Assert.Equal(OrganizationAccess.Full, organization.Access);
        Assert.Null(organization.TrialEndsAt);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(SubscriptionPath(organization.Id), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_server_links_its_source_code_and_a_fork_can_link_its_own()
    {
        await using var fork = factory.WithWebHostBuilder(builder => builder.UseSetting("App:SourceUrl", "https://git.example.com/spindle-fork"));

        var standard = await factory.CreateClient().GetFromJsonAsync<ServerResponse>("/api/server", TestJson.Options, Ct);
        var forked = await fork.CreateClient().GetFromJsonAsync<ServerResponse>("/api/server", TestJson.Options, Ct);

        Assert.Equal("https://github.com/Threennovate/spindle", standard!.SourceUrl);
        Assert.Equal("https://git.example.com/spindle-fork", forked!.SourceUrl);
    }

    private static string SubscriptionPath(Guid organizationId) => $"/api/organizations/{organizationId}/subscription";

    private static async Task<SubscriptionResponse> SubscriptionAsync(TestUser user, Guid organizationId) =>
        await TestUser.ReadAsync<SubscriptionResponse>(await user.Client.GetAsync(SubscriptionPath(organizationId), Ct));
}
