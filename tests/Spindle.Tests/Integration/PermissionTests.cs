using System.Net;
using System.Net.Http.Json;
using Spindle.Api.Clients;
using Spindle.Api.Projects;
using Spindle.Api.Tags;
using Spindle.Api.Team;
using Spindle.Api.TimeEntries;
using Spindle.Api.Workspaces;

namespace Spindle.Tests.Integration;

/// <summary>Workspace settings, and what admins, project managers and members may do.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PermissionTests(PostgresApiFactory factory)
{
    private static readonly TimeZoneInfo Zagreb = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zagreb");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    [Fact]
    public async Task Members_read_the_settings_and_admins_change_them()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);
        await admin.UpdateSettingsAsync(s => s["defaultHourlyRate"] = 80m);

        var seenByMember = await member.GetAsync<WorkspaceSettingsResponse>("/settings");
        var memberChanges = await member.PutAsync("/settings", new { name = "Taken", currency = "EUR" });
        var badCurrency = await admin.PutAsync("/settings", new { name = "Renamed", currency = "euro" });

        Assert.Null(seenByMember.DefaultHourlyRate);
        Assert.Equal(80m, (await admin.GetAsync<WorkspaceSettingsResponse>("/settings")).DefaultHourlyRate);
        Assert.Equal(HttpStatusCode.Forbidden, memberChanges.StatusCode);
        Assert.Equal(["currency"], await TestUser.ErrorFieldsAsync(badCurrency));
    }

    [Fact]
    public async Task Admins_can_keep_creating_projects_clients_and_tags_to_themselves()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);
        await admin.UpdateSettingsAsync(s =>
        {
            s["onlyAdminsCreateProjects"] = true;
            s["onlyAdminsCreateTags"] = true;
        });

        var project = await member.PostAsync("/projects", new { name = "Mine", color = "#6D28D9" });
        var client = await member.PostAsync("/clients", new { name = "Mine" });
        var tag = await member.PostAsync("/tags", new { name = "mine" });
        await admin.CreateAsync<ProjectResponse>("/projects", new { name = "Admin's", color = "#6D28D9" });
        await admin.UpdateSettingsAsync(s =>
        {
            s["onlyAdminsCreateProjects"] = false;
            s["onlyAdminsCreateTags"] = false;
        });
        var projectAllowed = await member.PostAsync("/projects", new { name = "Mine", color = "#6D28D9" });
        var tagAllowed = await member.PostAsync("/tags", new { name = "mine" });

        Assert.Equal(HttpStatusCode.Forbidden, project.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, client.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, tag.StatusCode);
        Assert.Equal(HttpStatusCode.Created, projectAllowed.StatusCode);
        Assert.Equal(HttpStatusCode.Created, tagAllowed.StatusCode);
    }

    [Fact]
    public async Task Only_admins_rename_or_delete_shared_clients_and_tags()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);
        var client = await admin.CreateAsync<ClientResponse>("/clients", new { name = "Acme" });
        var tag = await admin.CreateAsync<TagResponse>("/tags", new { name = "meeting" });

        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsync($"/clients/{client.Id}", new { name = "Mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/clients/{client.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsync($"/tags/{tag.Id}", new { name = "mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/tags/{tag.Id}")).StatusCode);
    }

    [Fact]
    public async Task Private_projects_are_open_to_their_members_and_changed_by_their_managers()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var owner = await factory.AddMemberAsync(admin);
        var colleague = await factory.AddMemberAsync(admin);
        var secret = await owner.CreateAsync<ProjectResponse>("/projects", new { name = "Secret", color = "#6D28D9", isPrivate = true });

        var hiddenGet = await colleague.Client.GetAsync(colleague.In($"/projects/{secret.Id}"), Ct);
        var hiddenList = await colleague.GetAsync<List<ProjectResponse>>("/projects");
        var hiddenTracking = await colleague.PostAsync("/time-entries", new { projectId = secret.Id, start = Now.AddHours(-1), stop = Now });
        var seenByAdmin = await admin.GetAsync<ProjectResponse>($"/projects/{secret.Id}");

        (await owner.PutAsync($"/projects/{secret.Id}/members/{colleague.UserId}", new { isManager = false })).EnsureSuccessStatusCode();
        var joined = await colleague.GetAsync<ProjectResponse>($"/projects/{secret.Id}");
        var tracked = await colleague.PostAsync("/time-entries", new { projectId = secret.Id, start = Now.AddHours(-1), stop = Now });
        var colleagueEdits = await colleague.PutAsync($"/projects/{secret.Id}", new { name = "Renamed", color = "#6D28D9", isPrivate = true });
        var ownerEdits = await owner.PutAsync($"/projects/{secret.Id}", new { name = "Renamed", color = "#6D28D9", isPrivate = true });

        Assert.Equal(HttpStatusCode.NotFound, hiddenGet.StatusCode);
        Assert.DoesNotContain(hiddenList, p => p.Id == secret.Id);
        Assert.Equal(["projectId"], await TestUser.ErrorFieldsAsync(hiddenTracking));
        Assert.True(seenByAdmin.CanManage);
        Assert.True(secret.CanManage);
        Assert.False(joined.CanManage);
        Assert.Equal(HttpStatusCode.Created, tracked.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, colleagueEdits.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ownerEdits.StatusCode);
    }

    [Fact]
    public async Task Members_cannot_change_time_on_locked_days_but_admins_can()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);
        var old = await member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Old", start = Now.AddHours(-30), stop = Now.AddHours(-29) });
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, Zagreb).DateTime);
        await admin.UpdateSettingsAsync(s => s["lockedBefore"] = today.ToString("yyyy-MM-dd"));

        var addOld = await member.PostAsync("/time-entries", new { start = Now.AddHours(-30), stop = Now.AddHours(-29) });
        var addToday = await member.PostAsync("/time-entries", new { start = Now.AddMinutes(-2), stop = Now.AddMinutes(-1) });
        var edit = await member.PutAsync($"/time-entries/{old.Id}", new { description = "Changed", start = old.Start, stop = old.Stop });
        var moveIntoLock = await member.PutAsync($"/time-entries/{(await TestUser.ReadAsync<TimeEntryResponse>(addToday)).Id}", new { start = Now.AddHours(-30), stop = Now.AddHours(-29) });
        var delete = await member.DeleteAsync($"/time-entries/{old.Id}");
        var bulkDelete = await member.PostAsync("/time-entries/bulk-delete", new { ids = new[] { old.Id } });
        var adminAddsOld = await admin.PostAsync("/time-entries", new { start = Now.AddHours(-30), stop = Now.AddHours(-29) });

        Assert.Equal(["start"], await TestUser.ErrorFieldsAsync(addOld));
        Assert.Equal(["start"], await TestUser.ErrorFieldsAsync(edit));
        Assert.Equal(["start"], await TestUser.ErrorFieldsAsync(moveIntoLock));
        Assert.Equal(["start"], await TestUser.ErrorFieldsAsync(delete));
        Assert.Equal(["ids"], await TestUser.ErrorFieldsAsync(bulkDelete));
        Assert.Equal(HttpStatusCode.Created, adminAddsOld.StatusCode);
    }

    [Fact]
    public async Task Finished_entries_need_the_required_details()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var project = await admin.CreateAsync<ProjectResponse>("/projects", new { name = "With tasks", color = "#6D28D9" });
        var task = await admin.CreateAsync<TaskResponse>($"/projects/{project.Id}/tasks", new { name = "Build" });
        var tag = await admin.CreateAsync<TagResponse>("/tags", new { name = "required" });
        await admin.UpdateSettingsAsync(s =>
        {
            s["requireProject"] = true;
            s["requireTag"] = true;
            s["requireTask"] = true;
        });

        var running = await admin.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Running", start = Now.AddMinutes(-5) });
        var stop = await admin.PostAsync($"/time-entries/{running.Id}/stop", new { });
        var bare = await admin.PostAsync("/time-entries", new { start = Now.AddHours(-2), stop = Now.AddHours(-1) });
        var noTask = await admin.PostAsync("/time-entries", new { projectId = project.Id, tagIds = new[] { tag.Id }, start = Now.AddHours(-2), stop = Now.AddHours(-1) });
        var complete = await admin.PostAsync("/time-entries", new
        {
            projectId = project.Id,
            taskId = task.Id,
            tagIds = new[] { tag.Id },
            start = Now.AddHours(-2),
            stop = Now.AddHours(-1),
        });

        Assert.Equal(["projectId", "tagIds"], await TestUser.ErrorFieldsAsync(stop));
        Assert.Equal(["projectId", "tagIds"], await TestUser.ErrorFieldsAsync(bare));
        Assert.Equal(["taskId"], await TestUser.ErrorFieldsAsync(noTask));
        Assert.Equal(HttpStatusCode.Created, complete.StatusCode);
    }

    [Fact]
    public async Task Rates_are_seen_and_set_by_admins_only()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);
        (await admin.PutAsync($"/members/{member.UserId}", new { role = "member", hourlyRate = 60m, laborCost = 35.5m })).EnsureSuccessStatusCode();
        var project = await member.CreateAsync<ProjectResponse>("/projects", new { name = "Rated", color = "#6D28D9" });
        (await admin.PutAsync($"/projects/{project.Id}", new { name = "Rated", color = "#6D28D9", hourlyRate = 90m })).EnsureSuccessStatusCode();

        (await member.PutAsync($"/projects/{project.Id}", new { name = "Rated", color = "#6D28D9", hourlyRate = 1m })).EnsureSuccessStatusCode();
        var memberAsSeenByAdmin = (await admin.GetAsync<List<MemberResponse>>("/members")).Single(m => m.UserId == member.UserId);
        var memberAsSeenByMember = (await member.GetAsync<List<MemberResponse>>("/members")).Single(m => m.UserId == member.UserId);

        Assert.Equal((60m, 35.5m), (memberAsSeenByAdmin.HourlyRate!.Value, memberAsSeenByAdmin.LaborCost!.Value));
        Assert.Null(memberAsSeenByMember.HourlyRate);
        Assert.Null(memberAsSeenByMember.LaborCost);
        Assert.Null((await member.GetAsync<ProjectResponse>($"/projects/{project.Id}")).HourlyRate);
        Assert.Equal(90m, (await admin.GetAsync<ProjectResponse>($"/projects/{project.Id}")).HourlyRate);
    }

    [Fact]
    public async Task Groups_are_managed_by_admins_and_lose_members_who_leave()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);
        var leaver = await factory.AddMemberAsync(admin);
        var stranger = await TestUser.RegisterAsync(factory);

        var group = await admin.CreateAsync<GroupResponse>("/groups", new { name = "Design", memberIds = new[] { member.UserId, leaver.UserId } });
        var memberCreates = await member.PostAsync("/groups", new { name = "Mine" });
        var foreignMember = await admin.PutAsync($"/groups/{group.Id}", new { name = "Design", memberIds = new[] { stranger.UserId } });
        (await admin.DeleteAsync($"/members/{leaver.UserId}")).EnsureSuccessStatusCode();
        var afterLeave = (await member.GetAsync<List<GroupResponse>>("/groups")).Single();
        var renamed = await TestUser.ReadAsync<GroupResponse>(await admin.PutAsync($"/groups/{group.Id}", new { name = "Designers", memberIds = new[] { admin.UserId } }));

        Assert.Equal(HttpStatusCode.Forbidden, memberCreates.StatusCode);
        Assert.Equal(["memberIds"], await TestUser.ErrorFieldsAsync(foreignMember));
        Assert.Equal([member.UserId], afterLeave.MemberIds);
        Assert.Equal(("Designers", admin.UserId), (renamed.Name, renamed.MemberIds.Single()));
    }
}
