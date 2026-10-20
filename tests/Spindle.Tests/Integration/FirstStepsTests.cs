using System.Net;
using System.Net.Http.Json;
using Spindle.Api.Me;
using Spindle.Api.Team;
using Spindle.Api.Workspaces;

namespace Spindle.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class FirstStepsTests(PostgresApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_people_see_first_steps_until_they_put_them_away_on_every_device()
    {
        var user = await TestUser.RegisterAsync(factory);

        var before = await user.MeAsync();
        var hidden = await TestUser.ReadAsync<MeResponse>(await user.Client.PutAsJsonAsync("/api/me/first-steps", new { hidden = true }, Ct));
        var again = await TestUser.ReadAsync<MeResponse>(await user.Client.PutAsJsonAsync("/api/me/first-steps", new { hidden = false }, Ct));

        Assert.True(before.FirstSteps);
        Assert.False(hidden.FirstSteps);
        Assert.True(again.FirstSteps);
    }

    [Fact]
    public async Task Renaming_the_organization_renames_the_workspace_that_still_has_its_old_name()
    {
        var owner = await TestUser.RegisterAsync(factory, $"user-{Guid.NewGuid():N}@example.com", fullName: "Ana Horvat");
        var organizationId = (await owner.MeAsync()).Organizations.Single().Id;
        var second = await TestUser.ReadAsync<WorkspaceSummary>(await owner.Client.PostAsJsonAsync("/api/workspaces", new { name = "Internal" }, Ct));

        var renamed = await TestUser.ReadAsync<OrganizationSummary>(
            await owner.Client.PutAsJsonAsync($"/api/organizations/{organizationId}", new { name = "  Acme Studio " }, Ct));
        var me = await owner.MeAsync();

        Assert.Equal("Acme Studio", renamed.Name);
        Assert.Equal("Acme Studio", me.Organizations.Single().Name);
        Assert.Equal("Acme Studio", me.Workspaces.Single(w => w.Id == owner.WorkspaceId).Name);
        Assert.Equal("Internal", me.Workspaces.Single(w => w.Id == second.Id).Name);
    }

    [Fact]
    public async Task Only_owners_and_admins_rename_the_organization()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var organizationId = (await owner.MeAsync()).Organizations.Single().Id;
        var email = $"user-{Guid.NewGuid():N}@example.com";
        await owner.CreateAsync<SentInvitationResponse>("/invitations", new { email, role = "member" });
        var member = await TestUser.RegisterAsync(factory, email, CapturingEmailSender.InvitationToken(factory.Emails.LastTo(email)));
        var stranger = await TestUser.RegisterAsync(factory);

        var byMember = await member.Client.PutAsJsonAsync($"/api/organizations/{organizationId}", new { name = "Mine now" }, Ct);
        var byStranger = await stranger.Client.PutAsJsonAsync($"/api/organizations/{organizationId}", new { name = "Mine now" }, Ct);
        var empty = await owner.Client.PutAsJsonAsync($"/api/organizations/{organizationId}", new { name = "" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byStranger.StatusCode);
        Assert.Equal(["name"], await TestUser.ErrorFieldsAsync(empty));
        Assert.Equal("Test User", (await owner.MeAsync()).Organizations.Single().Name);
        // People who join get first steps of their own: the desktop app.
        Assert.True((await member.MeAsync()).FirstSteps);
    }
}
