using System.Net;
using System.Net.Http.Json;
using Spindle.Api.Me;
using Spindle.Api.Team;
using Spindle.Domain.Invitations;
using Spindle.Domain.Organizations;
using Spindle.Domain.Workspaces;

namespace Spindle.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class TeamTests(PostgresApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewEmail() => $"invitee-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task An_invited_person_registers_straight_into_the_team()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var email = NewEmail();

        var sent = await admin.CreateAsync<SentInvitationResponse>("/invitations", new { email = email.ToUpperInvariant(), role = "member" });
        var token = CapturingEmailSender.InvitationToken(factory.Emails.LastTo(email));
        var preview = await TestUser.ReadAsync<InvitationPreview>(await factory.CreateClient().GetAsync($"/api/invitations/{token}", Ct));
        var invitee = await TestUser.RegisterAsync(factory, email, token);
        var me = await invitee.MeAsync();

        Assert.True(sent.EmailSent);
        Assert.Equal($"https://track.example.com/invitations/{token}", sent.InviteUrl);
        Assert.Equal((email, InvitationStatus.Pending), (preview.Email, preview.Status));
        Assert.Equal(admin.WorkspaceId, me.DefaultWorkspaceId);
        Assert.Equal(WorkspaceRole.Member, Assert.Single(me.Workspaces).Role);
        Assert.Equal(OrganizationRole.Member, Assert.Single(me.Organizations).Role);
        Assert.Equal(2, (await admin.GetAsync<List<MemberResponse>>("/members")).Count);
        Assert.Empty(await admin.GetAsync<List<InvitationResponse>>("/invitations"));
    }

    [Fact]
    public async Task Registering_through_an_invitation_needs_the_invited_email()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var email = NewEmail();
        await admin.CreateAsync<SentInvitationResponse>("/invitations", new { email });
        var token = CapturingEmailSender.InvitationToken(factory.Emails.LastTo(email));

        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new
        {
            email = NewEmail(),
            password = "correct horse battery",
            fullName = "Someone Else",
            timeZone = "Europe/Zagreb",
            invitationToken = token,
        }, Ct);

        Assert.Equal(["email"], await TestUser.ErrorFieldsAsync(response));
    }

    [Fact]
    public async Task Someone_with_an_account_accepts_and_switches_to_the_team()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var existing = await TestUser.RegisterAsync(factory);
        var stranger = await TestUser.RegisterAsync(factory);
        await admin.CreateAsync<SentInvitationResponse>("/invitations", new { email = existing.Email, role = "admin" });
        var token = CapturingEmailSender.InvitationToken(factory.Emails.LastTo(existing.Email));

        var wrongPerson = await stranger.Client.PostAsync($"/api/invitations/{token}/accept", null, Ct);
        var accepted = await existing.Client.PostAsync($"/api/invitations/{token}/accept", null, Ct);
        var again = await existing.Client.PostAsync($"/api/invitations/{token}/accept", null, Ct);
        var me = await existing.MeAsync();

        Assert.Equal(HttpStatusCode.Forbidden, wrongPerson.StatusCode);
        Assert.Equal(admin.WorkspaceId, (await TestUser.ReadAsync<AcceptedInvitationResponse>(accepted)).WorkspaceId);
        Assert.Equal(["invitation"], await TestUser.ErrorFieldsAsync(again));
        Assert.Equal(admin.WorkspaceId, me.DefaultWorkspaceId);
        Assert.Equal(2, me.Workspaces.Count);
        Assert.Equal(WorkspaceRole.Admin, me.Workspaces.Single(w => w.Id == admin.WorkspaceId).Role);
    }

    [Fact]
    public async Task Sending_again_replaces_the_link_and_revoking_disables_it()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var email = NewEmail();
        var sent = await admin.CreateAsync<SentInvitationResponse>("/invitations", new { email });
        var firstToken = CapturingEmailSender.InvitationToken(factory.Emails.LastTo(email));

        var resent = await admin.CreateAsync<SentInvitationResponse>($"/invitations/{sent.Invitation.Id}/resend", new { });
        var secondToken = CapturingEmailSender.InvitationToken(factory.Emails.LastTo(email));
        var reinvited = await admin.CreateAsync<SentInvitationResponse>("/invitations", new { email });
        var thirdToken = CapturingEmailSender.InvitationToken(factory.Emails.LastTo(email));
        (await admin.DeleteAsync($"/invitations/{sent.Invitation.Id}")).EnsureSuccessStatusCode();
        var anonymous = factory.CreateClient();

        Assert.Equal(sent.Invitation.Id, resent.Invitation.Id);
        Assert.Equal(sent.Invitation.Id, reinvited.Invitation.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/invitations/{firstToken}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/invitations/{secondToken}", Ct)).StatusCode);
        Assert.Equal(InvitationStatus.Revoked, (await TestUser.ReadAsync<InvitationPreview>(await anonymous.GetAsync($"/api/invitations/{thirdToken}", Ct))).Status);
    }

    [Fact]
    public async Task An_invitation_still_works_when_the_email_cannot_be_sent()
    {
        var admin = await TestUser.RegisterAsync(factory);
        factory.Emails.Fail = true;
        try
        {
            var sent = await admin.CreateAsync<SentInvitationResponse>("/invitations", new { email = NewEmail() });

            Assert.False(sent.EmailSent);
            Assert.StartsWith("https://track.example.com/invitations/", sent.InviteUrl, StringComparison.Ordinal);
        }
        finally
        {
            factory.Emails.Fail = false;
        }
    }

    [Fact]
    public async Task Members_are_invited_once_and_only_by_admins()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var memberEmail = NewEmail();
        await admin.CreateAsync<SentInvitationResponse>("/invitations", new { email = memberEmail });
        var member = await TestUser.RegisterAsync(factory, memberEmail, CapturingEmailSender.InvitationToken(factory.Emails.LastTo(memberEmail)));

        var inviteExisting = await admin.PostAsync("/invitations", new { email = memberEmail });
        var memberInvites = await member.Client.PostAsJsonAsync(admin.In("/invitations"), new { email = NewEmail() }, Ct);
        var memberPromotesSelf = await member.Client.PutAsJsonAsync(admin.In($"/members/{member.UserId}"), new { role = "admin" }, Ct);
        var memberSeesTeam = await member.Client.GetAsync(admin.In("/members"), Ct);

        Assert.Equal(["email"], await TestUser.ErrorFieldsAsync(inviteExisting));
        Assert.Equal(HttpStatusCode.Forbidden, memberInvites.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberPromotesSelf.StatusCode);
        Assert.Equal(HttpStatusCode.OK, memberSeesTeam.StatusCode);
    }

    [Fact]
    public async Task The_last_admin_stays_and_removed_members_lose_access()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var memberEmail = NewEmail();
        await admin.CreateAsync<SentInvitationResponse>("/invitations", new { email = memberEmail });
        var member = await TestUser.RegisterAsync(factory, memberEmail, CapturingEmailSender.InvitationToken(factory.Emails.LastTo(memberEmail)));

        var demoteSelf = await admin.PutAsync($"/members/{admin.UserId}", new { role = "member" });
        var removeSelf = await admin.DeleteAsync($"/members/{admin.UserId}");
        var removeMember = await admin.DeleteAsync($"/members/{member.UserId}");
        var memberAccess = await member.Client.GetAsync(admin.In("/projects"), Ct);

        Assert.Equal(["role"], await TestUser.ErrorFieldsAsync(demoteSelf));
        Assert.Equal(["userId"], await TestUser.ErrorFieldsAsync(removeSelf));
        Assert.Equal(HttpStatusCode.NoContent, removeMember.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, memberAccess.StatusCode);
        var memberMe = await member.MeAsync();
        Assert.Null(memberMe.DefaultWorkspaceId);
        // Out of the organization's only workspace, they are out of the organization too.
        Assert.Empty(memberMe.Organizations);
        Assert.Empty(memberMe.Workspaces);
    }

    [Fact]
    public async Task Someone_removed_from_their_last_workspace_can_still_take_their_data_and_leave()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);
        (await admin.DeleteAsync($"/members/{member.UserId}")).EnsureSuccessStatusCode();

        var export = await member.Client.GetAsync("/api/me/export", Ct);
        var deleted = await member.Client.PostAsJsonAsync("/api/me/delete", new { password = "correct horse battery" }, Ct);

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        // The address is free again.
        await TestUser.RegisterAsync(factory, member.Email);
    }

    [Fact]
    public async Task Someone_in_another_of_the_organizations_workspaces_stays_in_the_organization()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(owner);
        var second = await TestUser.ReadAsync<WorkspaceSummary>(await owner.Client.PostAsJsonAsync("/api/workspaces", new { name = "Second" }, Ct));
        (await owner.Client.PostAsJsonAsync($"/api/workspaces/{second.Id}/invitations", new { email = member.Email }, Ct)).EnsureSuccessStatusCode();
        var token = CapturingEmailSender.InvitationToken(factory.Emails.LastTo(member.Email));
        (await member.Client.PostAsync($"/api/invitations/{token}/accept", null, Ct)).EnsureSuccessStatusCode();

        (await owner.DeleteAsync($"/members/{member.UserId}")).EnsureSuccessStatusCode();

        Assert.Single((await member.MeAsync()).Organizations);
    }

    [Fact]
    public async Task Owners_create_workspaces_and_users_switch_between_theirs()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var memberEmail = NewEmail();
        await owner.CreateAsync<SentInvitationResponse>("/invitations", new { email = memberEmail });
        var member = await TestUser.RegisterAsync(factory, memberEmail, CapturingEmailSender.InvitationToken(factory.Emails.LastTo(memberEmail)));

        var created = await TestUser.ReadAsync<WorkspaceSummary>(await owner.Client.PostAsJsonAsync("/api/workspaces", new { name = "Second" }, Ct));
        var afterCreate = await owner.MeAsync();
        var switchBack = await owner.Client.PutAsJsonAsync("/api/me/default-workspace", new { workspaceId = owner.WorkspaceId }, Ct);
        var memberCreates = await member.Client.PostAsJsonAsync("/api/workspaces", new { name = "Mine" }, Ct);
        var memberSwitchesToForeign = await member.Client.PutAsJsonAsync("/api/me/default-workspace", new { workspaceId = created.Id }, Ct);

        Assert.Equal(created.Id, afterCreate.DefaultWorkspaceId);
        Assert.Equal(2, afterCreate.Workspaces.Count);
        Assert.Equal(HttpStatusCode.NoContent, switchBack.StatusCode);
        Assert.Equal(owner.WorkspaceId, (await owner.MeAsync()).DefaultWorkspaceId);
        Assert.Equal(HttpStatusCode.Forbidden, memberCreates.StatusCode);
        Assert.Equal(["workspaceId"], await TestUser.ErrorFieldsAsync(memberSwitchesToForeign));
    }
}
