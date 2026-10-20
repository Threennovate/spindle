using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spindle.Api.AgentActivity;
using Spindle.Api.Projects;
using Spindle.Api.TimeEntries;
using Spindle.Domain.AgentActivity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Tests.Integration;

/// <summary>Agent activity a Mac uploads, the splits suggested from it, and who can see any of it: only its owner.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AgentActivityTests(PostgresApiFactory factory)
{
    // Monday 14 September 2026; test users live in Europe/Zagreb (UTC+2 then).
    private static readonly DateOnly Monday = new(2026, 9, 14);
    private static readonly Guid Mac = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(int hours, int minutes = 0) => new(Monday, new TimeOnly(hours, minutes), TimeSpan.FromHours(2));

    private static long Ms(int hours, int minutes = 0) => At(hours, minutes).ToUnixTimeMilliseconds();

    private static object Repository(string name) => new { identity = $"origin:github.com/acme/{name}", name };

    /// <summary>Back and forth between atlas and beacon from 9 to 10, with a session title.</summary>
    private static object[] Sessions() =>
    [
        new
        {
            source = "claude", sessionId = "claude:a", repository = Repository("atlas"), title = "Fix the login page",
            branches = new[] { "main" }, touches = new[] { Ms(9), Ms(9, 20), Ms(9, 40) },
            runs = new[] { new { start = Ms(9), end = Ms(9, 45) } }, parserVersion = 1,
        },
        new
        {
            source = "codex", sessionId = "codex:b", repository = Repository("beacon"), title = (string?)null,
            branches = Array.Empty<string>(), touches = new[] { Ms(9, 10), Ms(9, 30), Ms(9, 50) },
            runs = new[] { new { start = Ms(9, 10), end = Ms(9, 55) } }, parserVersion = 1,
        },
    ];

    private static Task<HttpResponseMessage> UploadAsync(TestUser user, object[] sessions) =>
        user.Client.PutAsJsonAsync("/api/me/agent-activity/sessions", new { deviceId = Mac, sessions }, Ct);

    private static async Task<AgentActivitySettingsResponse> SettingsAsync(TestUser user, bool sync, bool titles = false) =>
        await TestUser.ReadAsync<AgentActivitySettingsResponse>(
            await user.Client.PutAsJsonAsync("/api/me/agent-activity/settings", new { sync, titles }, Ct));

    private static Task<List<SplitSuggestionResponse>> SuggestionsAsync(TestUser user) =>
        user.GetAsync<List<SplitSuggestionResponse>>($"/time-entries/split-suggestions?from={At(0):O}&to={At(23):O}".Replace("+", "%2B"));

    private static Task<List<UntrackedTimeResponse>> UntrackedAsync(TestUser user) =>
        user.GetAsync<List<UntrackedTimeResponse>>($"/agent-activity/untracked?from={At(0):O}&to={At(24 - 1, 59):O}".Replace("+", "%2B"));

    private static Task<ProjectResponse> ProjectAsync(TestUser user, string name) =>
        user.CreateAsync<ProjectResponse>("/projects", new { name, color = "#0B83D9" });

    [Fact]
    public async Task Nothing_is_uploaded_or_suggested_until_the_person_turns_sync_on()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        await ProjectAsync(user, "Beacon");
        await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10), projectId = atlas.Id });

        var refused = await UploadAsync(user, Sessions());
        var me = await user.MeAsync();

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(new AgentActivitySettingsResponse(false, false, null), me.AgentActivity);
        Assert.Empty(await SuggestionsAsync(user));
    }

    [Fact]
    public async Task Uploaded_activity_suggests_the_split_the_Mac_would()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        var beacon = await ProjectAsync(user, "Beacon");
        var entry = await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10), projectId = atlas.Id });
        var settings = await SettingsAsync(user, sync: true);

        var saved = await TestUser.ReadAsync<AgentActivityUploadResponse>(await UploadAsync(user, Sessions()));
        var suggestion = Assert.Single(await SuggestionsAsync(user));

        Assert.True(settings.Sync);
        Assert.NotNull(settings.SyncedSince);
        Assert.Equal(2, saved.Saved);
        Assert.Equal(entry.Id, suggestion.EntryId);
        Assert.Equal([(atlas.Id, 50, 1800L), (beacon.Id, 50, 1800L)], suggestion.Parts.Select(p => (p.Target.ProjectId!.Value, p.Percent, p.AttentionSeconds)));
        Assert.Null(suggestion.Stretches);
        Assert.Equal(1, suggestion.Coverage);
        Assert.Equal(SplitStrength.Strong, suggestion.Strength);
    }

    [Fact]
    public async Task Any_agent_named_by_shape_counts_as_the_others_do_and_a_malformed_name_is_refused()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        var beacon = await ProjectAsync(user, "Beacon");
        await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10), projectId = atlas.Id });
        await SettingsAsync(user, sync: true);
        object Session(string source, string id, string repository, int minute) => new
        {
            source, sessionId = id, repository = Repository(repository), title = (string?)null, branches = new[] { "main" },
            touches = new[] { Ms(9, minute), Ms(9, minute + 20), Ms(9, minute + 40) },
            runs = new[] { new { start = Ms(9, minute), end = Ms(9, minute + 45) } }, parserVersion = 2,
        };

        var saved = await TestUser.ReadAsync<AgentActivityUploadResponse>(
            await UploadAsync(user, [Session("claude", "claude:a", "atlas", 0), Session("copilot", "copilot:b", "beacon", 10)]));
        // An agent this server has never heard of, as a newer app may send.
        var unknown = await TestUser.ReadAsync<AgentActivityUploadResponse>(await UploadAsync(user, [Session("gemini2", "gemini:c", "beacon", 10)]));
        var malformed = await Task.WhenAll(new[] { "Claude", "", "with space", "copilot-cli", "averyveryverylongname" }
            .Select(source => UploadAsync(user, [Session(source, "x:d", "beacon", 10)])));
        var suggestion = Assert.Single(await SuggestionsAsync(user));

        Assert.Equal(2, saved.Saved);
        Assert.Equal(1, unknown.Saved);
        Assert.All(malformed, response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
        Assert.Equal([(atlas.Id, 50), (beacon.Id, 50)], suggestion.Parts.Select(p => (p.Target.ProjectId!.Value, p.Percent)));
    }

    [Fact]
    public async Task A_suggestion_carries_its_evidence_and_how_sure_it_is()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        var beacon = await ProjectAsync(user, "Beacon");
        await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(11), projectId = atlas.Id });
        await SettingsAsync(user, sync: true);
        await UploadAsync(user, Sessions());

        var suggestion = Assert.Single(await SuggestionsAsync(user));

        // Attention until 10:05, then nothing: the time after it stays on Atlas, the entry's project. The logs account
        // for 65 of 120 minutes, too little for a strong suggestion.
        Assert.Equal((At(9), At(11)), (suggestion.Start, suggestion.End));
        Assert.Equal(SplitStrength.Weak, suggestion.Strength);
        Assert.Equal(atlas.Id, suggestion.Fallback!.ProjectId);
        Assert.Equal([new TimeRangeResponse(At(10, 5), At(11))], suggestion.Open);
        Assert.Equal(6, suggestion.Pieces.Count);
        Assert.Equal((beacon.Id, At(9, 50), At(10, 5)), (suggestion.Pieces[^1].Target.ProjectId!.Value, suggestion.Pieces[^1].Start, suggestion.Pieces[^1].End));
        Assert.Equal([At(9), At(9, 10), At(9, 20), At(9, 30), At(9, 40), At(9, 50)], suggestion.Prompts.Select(p => p.At));
        Assert.Equal([3, 3], suggestion.Parts.Select(p => p.Prompts));
    }

    [Fact]
    public async Task Untracked_agent_time_needs_a_prompt_of_its_own()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        var beacon = await ProjectAsync(user, "Beacon");
        await SettingsAsync(user, sync: true);
        await UploadAsync(user, Sessions());

        var all = await UntrackedAsync(user);
        var morning = await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(9, 30), projectId = atlas.Id });
        var afterMorning = await UntrackedAsync(user);
        await user.PutAsync($"/time-entries/{morning.Id}", new { start = At(9), stop = At(10), projectId = atlas.Id, description = "", tagIds = Array.Empty<Guid>(), isBillable = false });
        var afterHour = await UntrackedAsync(user);

        // Attention from the first prompt until 15 minutes after the last; Beacon had more of it.
        var stretch = Assert.Single(all);
        Assert.Equal((At(9), At(10, 5)), (stretch.Start, stretch.End));
        Assert.Equal([beacon.Id, atlas.Id], stretch.ProjectIds);
        Assert.Equal(0, stretch.UnlinkedSeconds);
        Assert.Equal([atlas.Id, beacon.Id], stretch.Parts!.Select(p => p.Target.ProjectId!.Value));
        Assert.Equal((At(9, 30), At(10, 5)), (Assert.Single(afterMorning).Start, afterMorning[0].End));
        // 10:00 to 10:05 is only the attention the logs assume after the last prompt.
        Assert.Empty(afterHour);
    }

    [Fact]
    public async Task Untracked_time_is_its_owners_alone()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);
        await ProjectAsync(admin, "Atlas");
        await SettingsAsync(member, sync: true);
        await UploadAsync(member, Sessions());

        Assert.Single(await UntrackedAsync(member));
        Assert.Empty(await UntrackedAsync(admin));
    }

    [Fact]
    public async Task Agents_now_names_the_projects_of_the_last_15_minutes_to_their_owner_only()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);
        var atlas = await ProjectAsync(admin, "Atlas");
        await ProjectAsync(admin, "Beacon");
        await SettingsAsync(member, sync: true);
        var now = DateTimeOffset.UtcNow;
        long Ago(int minutes) => (now - TimeSpan.FromMinutes(minutes)).ToUnixTimeMilliseconds();
        object Session(string id, string repository, long at) => new
        {
            source = "claude", sessionId = id, repository = Repository(repository), title = (string?)null, branches = Array.Empty<string>(),
            touches = new[] { at }, runs = new[] { new { start = at, end = at + 60_000 } }, parserVersion = 1,
        };
        // Beacon's attention ended 25 minutes ago; copper has no project.
        await UploadAsync(member, [Session("claude:beacon", "beacon", Ago(40)), Session("claude:atlas", "atlas", Ago(5)), Session("claude:copper", "copper", Ago(3))]);

        var recent = await member.GetAsync<List<AgentsNowResponse>>("/agent-activity/now");

        Assert.Equal([atlas.Id], recent.Select(p => p.ProjectId));
        Assert.InRange(recent[0].Seconds, 110, 130);
        Assert.Empty(await admin.GetAsync<List<AgentsNowResponse>>("/agent-activity/now"));
    }

    [Fact]
    public async Task A_repository_without_a_project_can_be_linked_to_one()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        var site = await ProjectAsync(user, "Client site");
        await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10), projectId = atlas.Id });
        await SettingsAsync(user, sync: true);
        await UploadAsync(user, Sessions());

        var unlinked = Assert.Single(await SuggestionsAsync(user)).Parts[1].Target.Repository;
        var linked = await user.PutAsync("/agent-activity/links", new { repository = unlinked, projectId = site.Id });
        var links = await user.Client.GetFromJsonAsync<List<RepositoryLinkResponse>>("/api/me/agent-activity/links", TestJson.Options, Ct);
        var after = Assert.Single(await SuggestionsAsync(user));

        Assert.Equal("beacon", unlinked!.Name);
        Assert.Equal(HttpStatusCode.NoContent, linked.StatusCode);
        Assert.Equal([("origin:github.com/acme/beacon", "beacon", site.Id, user.WorkspaceId)], links!.Select(l => (l.Identity, l.Name, l.ProjectId, l.WorkspaceId)));
        Assert.Empty(links!.Single().BranchRules);
        Assert.Equal([atlas.Id, site.Id], after.Parts.Select(p => p.Target.ProjectId!.Value));
    }

    [Fact]
    public async Task A_repository_set_to_no_project_stays_without_one_until_linked_again()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        var beacon = await ProjectAsync(user, "Beacon");
        await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10), projectId = atlas.Id });
        await SettingsAsync(user, sync: true);
        await UploadAsync(user, Sessions());
        var named = Assert.Single(await SuggestionsAsync(user));

        var both = await user.PutAsync("/agent-activity/links", new { repository = Repository("beacon"), projectId = beacon.Id, noProject = true });
        var none = await user.PutAsync("/agent-activity/links", new { repository = Repository("beacon"), noProject = true });
        var older = await user.Client.GetFromJsonAsync<List<RepositoryLinkResponse>>("/api/me/agent-activity/links", TestJson.Options, Ct);
        var links = await user.Client.GetFromJsonAsync<List<RepositoryLinkResponse>>("/api/me/agent-activity/links?includeNoProject=true", TestJson.Options, Ct);
        var after = Assert.Single(await SuggestionsAsync(user));

        // A project named like the repository took its work, until the person said it goes to no project.
        Assert.Equal([atlas.Id, beacon.Id], named.Parts.Select(p => p.Target.ProjectId!.Value));
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);
        Assert.Empty(older!);
        Assert.Equal([("origin:github.com/acme/beacon", (Guid?)null, (Guid?)null)], links!.Select(l => (l.Identity, l.ProjectId, l.WorkspaceId)));
        Assert.Equal("beacon", after.Parts[1].Target.Repository!.Name);

        // Forgetting the choice brings the name back.
        await user.PutAsync("/agent-activity/links", new { repository = Repository("beacon") });

        Assert.Equal([atlas.Id, beacon.Id], Assert.Single(await SuggestionsAsync(user)).Parts.Select(p => p.Target.ProjectId!.Value));
    }

    [Fact]
    public async Task Branch_rules_send_a_repositorys_work_on_their_branches_to_another_project_and_task()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        var support = await ProjectAsync(user, "Support");
        var maintenance = await user.CreateAsync<TaskResponse>($"/projects/{support.Id}/tasks", new { name = "Maintenance" });
        var design = await user.CreateAsync<TaskResponse>($"/projects/{atlas.Id}/tasks", new { name = "Design" });
        await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10), projectId = atlas.Id });
        await SettingsAsync(user, sync: true);
        // Two sessions in one repository, one after the other, on different branches.
        await UploadAsync(user,
        [
            new
            {
                source = "claude", sessionId = "claude:main", repository = Repository("atlas"), title = (string?)null,
                branches = new[] { "main" }, touches = new[] { Ms(9), Ms(9, 10), Ms(9, 20) },
                runs = new[] { new { start = Ms(9), end = Ms(9, 25) } }, parserVersion = 1,
            },
            new
            {
                source = "claude", sessionId = "claude:acme", repository = Repository("atlas"), title = (string?)null,
                branches = new[] { "acme/login" }, touches = new[] { Ms(9, 30), Ms(9, 40), Ms(9, 50) },
                runs = new[] { new { start = Ms(9, 30), end = Ms(9, 55) } }, parserVersion = 1,
            },
        ]);
        Assert.Empty(await SuggestionsAsync(user));

        var wrongTask = await user.PutAsync("/agent-activity/links", new
        {
            repository = Repository("atlas"), projectId = atlas.Id,
            branchRules = new[] { new { pattern = "acme/*", projectId = support.Id, taskId = (Guid?)design.Id } },
        });
        var linked = await user.PutAsync("/agent-activity/links", new
        {
            repository = Repository("atlas"), projectId = atlas.Id,
            branchRules = new[] { new { pattern = " ACME/* ", projectId = support.Id, taskId = (Guid?)maintenance.Id } },
        });
        // Linking again without rules keeps them.
        await user.PutAsync("/agent-activity/links", new { repository = Repository("atlas"), projectId = atlas.Id });
        var link = Assert.Single((await user.Client.GetFromJsonAsync<List<RepositoryLinkResponse>>("/api/me/agent-activity/links", TestJson.Options, Ct))!);
        var suggestion = Assert.Single(await SuggestionsAsync(user));

        Assert.Equal(HttpStatusCode.BadRequest, wrongTask.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, linked.StatusCode);
        Assert.Equal([("ACME/*", support.Id, (Guid?)maintenance.Id)], link.BranchRules.Select(r => (r.Pattern, r.ProjectId, r.TaskId)));
        Assert.Equal([(atlas.Id, 50), (support.Id, 50)], suggestion.Parts.Select(p => (p.Target.ProjectId!.Value, p.Percent)));
        Assert.Equal([atlas.Id, support.Id], suggestion.Stretches!.Select(s => s.Target.ProjectId!.Value));

        var cleared = await user.PutAsync("/agent-activity/links", new { repository = Repository("atlas"), projectId = atlas.Id, branchRules = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Empty(await SuggestionsAsync(user));
    }

    [Fact]
    public async Task Keep_stops_the_suggestion_on_every_device()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        await ProjectAsync(user, "Beacon");
        var entry = await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10), projectId = atlas.Id });
        await SettingsAsync(user, sync: true);
        await UploadAsync(user, Sessions());

        var kept = await user.PostAsync($"/time-entries/{entry.Id}/split-suggestion/dismiss", new { });
        var again = await user.PostAsync($"/time-entries/{entry.Id}/split-suggestion/dismiss", new { });
        var dismissed = await user.Client.GetFromJsonAsync<List<Guid>>("/api/me/agent-activity/dismissals", TestJson.Options, Ct);

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (kept.StatusCode, again.StatusCode));
        Assert.Equal([entry.Id], dismissed);
        Assert.Empty(await SuggestionsAsync(user));
    }

    [Fact]
    public async Task Activity_is_its_owners_alone()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);
        var atlas = await ProjectAsync(admin, "Atlas");
        await ProjectAsync(admin, "Beacon");
        var memberEntry = await member.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10), projectId = atlas.Id });
        await SettingsAsync(member, sync: true);
        await UploadAsync(member, Sessions());

        // The admin sees the member's time in reports, but no suggestion for it and cannot dismiss one.
        var dismiss = await admin.PostAsync($"/time-entries/{memberEntry.Id}/split-suggestion/dismiss", new { });

        Assert.Single(await SuggestionsAsync(member));
        Assert.Empty(await SuggestionsAsync(admin));
        Assert.Equal(HttpStatusCode.NotFound, dismiss.StatusCode);
    }

    [Fact]
    public async Task Titles_go_only_when_allowed_and_turning_sync_off_deletes_everything()
    {
        var user = await TestUser.RegisterAsync(factory);
        await SettingsAsync(user, sync: true);
        await UploadAsync(user, Sessions());
        var withoutTitles = await TitlesAsync(user);

        var allowed = await SettingsAsync(user, sync: true, titles: true);
        await UploadAsync(user, Sessions());
        var withTitles = await TitlesAsync(user);
        await SettingsAsync(user, sync: true, titles: false);
        var titlesOff = await TitlesAsync(user);
        await SettingsAsync(user, sync: false);

        Assert.Equal([null, null], withoutTitles);
        Assert.True(allowed.Titles);
        Assert.Equal(["Fix the login page", null], withTitles);
        Assert.Equal([null, null], titlesOff);
        Assert.Empty(await TitlesAsync(user));
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(user, Sessions())).StatusCode);
    }

    [Fact]
    public async Task Uploads_name_repositories_without_paths_and_reject_nonsense()
    {
        var user = await TestUser.RegisterAsync(factory);
        await SettingsAsync(user, sync: true);

        var path = await UploadAsync(user, [new
        {
            source = "claude", sessionId = "claude:x", repository = new { identity = "/Users/ivan/code/x", name = "x" },
            branches = Array.Empty<string>(), touches = new[] { Ms(9) }, runs = Array.Empty<object>(), parserVersion = 1,
        }]);
        var backwards = await UploadAsync(user, [new
        {
            source = "claude", sessionId = "claude:y", branches = Array.Empty<string>(), touches = Array.Empty<long>(),
            runs = new[] { new { start = Ms(10), end = Ms(9) } }, parserVersion = 1,
        }]);

        Assert.Contains("sessions[0].repository.identity", await TestUser.ErrorFieldsAsync(path));
        Assert.Equal(["sessions[0]"], await TestUser.ErrorFieldsAsync(backwards));
    }

    [Fact]
    public async Task A_deleted_account_takes_its_activity_along_and_an_export_includes_it()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        var entry = await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10), projectId = atlas.Id });
        await SettingsAsync(user, sync: true);
        await UploadAsync(user, Sessions());
        await user.PutAsync("/agent-activity/links", new { repository = Repository("atlas"), projectId = atlas.Id });
        await user.PostAsync($"/time-entries/{entry.Id}/split-suggestion/dismiss", new { });

        using var export = JsonDocument.Parse(await user.Client.GetStringAsync("/api/me/export", Ct));
        var activity = export.RootElement.GetProperty("agentActivity");
        var deleted = await user.Client.PostAsJsonAsync("/api/me/delete", new { password = "correct horse battery" }, Ct);

        Assert.Equal(2, activity.GetProperty("sessions").GetArrayLength());
        Assert.Equal("Atlas", activity.GetProperty("repositoryLinks")[0].GetProperty("project").GetString());
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.AgentSessions.AnyAsync(s => s.UserId == user.UserId, Ct));
        Assert.False(await db.AgentRepositoryLinks.AnyAsync(l => l.UserId == user.UserId, Ct));
        Assert.False(await db.SplitSuggestionDismissals.AnyAsync(d => d.UserId == user.UserId, Ct));
    }

    private async Task<List<string?>> TitlesAsync(TestUser user)
    {
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AgentSessions.Where(s => s.UserId == user.UserId).OrderBy(s => s.SessionId).Select(s => s.Title).ToListAsync(Ct);
    }
}
