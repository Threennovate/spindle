using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Client;
using Spindle.Api.AgentActivity;
using Spindle.Api.Me;
using Spindle.Api.Projects;
using Spindle.Api.TimeEntries;
using static Spindle.Tests.Integration.AgentConnector;

namespace Spindle.Tests.Integration;

/// <summary>The MCP server's tools: the tools of the apps' former local MCP, on the server, for the person and workspace the agent was let into.</summary>
[Collection(PostgresCollection.Name)]
public sealed class McpToolTests(PostgresApiFactory factory)
{
    // Monday 14 September 2026; test users live in Europe/Zagreb (UTC+2 then).
    private static readonly DateOnly Monday = new(2026, 9, 14);
    private static readonly Guid Mac = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(int hours, int minutes = 0) => new(Monday, new TimeOnly(hours, minutes), TimeSpan.FromHours(2));

    private static long Ms(int hours, int minutes = 0) => At(hours, minutes).ToUnixTimeMilliseconds();

    private static Task<ProjectResponse> ProjectAsync(TestUser user, string name) =>
        user.CreateAsync<ProjectResponse>("/projects", new { name, color = "#0B83D9" });

    private async Task<McpClient> ConnectAsync(TestUser user, bool allowWrite = true) =>
        await ClientAsync(factory, (await SignInAsync(factory, user, allowWrite)).Tokens.AccessToken);

    [Fact]
    public async Task The_tools_are_the_local_mcps()
    {
        var user = await TestUser.RegisterAsync(factory);
        await using var mcp = await ConnectAsync(user);

        var tools = await mcp.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(
            ["continue_entry", "find_untracked_time", "get_evidence", "get_report", "get_timer", "list_entries", "list_projects", "list_proposals",
                "propose_changes", "propose_entries", "start_timer", "stop_timer", "undo_last_update", "update_entries", "withdraw_proposals"],
            tools.Select(t => t.Name).Order());
        Assert.True(tools.Single(t => t.Name == "list_entries").ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.False(tools.Single(t => t.Name == "propose_entries").ProtocolTool.Annotations!.ReadOnlyHint);
    }

    [Fact]
    public async Task Listing_and_reports_count_each_part_of_a_split_on_its_project()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        var beacon = await ProjectAsync(user, "Beacon");
        await user.CreateAsync<TimeEntryResponse>("/time-entries", new
        {
            start = At(9),
            stop = At(11),
            shares = new object[] { new { projectId = atlas.Id, percent = 75 }, new { projectId = beacon.Id, percent = 25 } },
        });
        await using var mcp = await ConnectAsync(user, allowWrite: false);

        var listed = await mcp.CallAsync("list_entries", new { from = "2026-09-14" });
        var report = await mcp.CallAsync("get_report", new { from = "2026-09-14", to = "2026-09-20" });

        Assert.Single(listed.GetProperty("entries").EnumerateArray());
        Assert.Equal(
            [("Atlas", 5400L), ("Beacon", 1800L)],
            listed.GetProperty("projects").EnumerateArray().Select(p => (p.GetProperty("project").GetString()!, p.GetProperty("seconds").GetInt64())));
        Assert.Equal(120, report.GetProperty("totals").GetProperty("minutes").GetInt64());
        Assert.Equal(
            [("Atlas", 90L), ("Beacon", 30L)],
            report.GetProperty("groups").EnumerateArray().Select(g => (g.GetProperty("name").GetString()!, g.GetProperty("minutes").GetInt64())));
    }

    [Fact]
    public async Task Agents_fill_empty_descriptions_and_can_take_it_back()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        var empty = await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10), projectId = atlas.Id });
        var described = await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(10), stop = At(11), projectId = atlas.Id, description = "Standup" });
        await using var mcp = await ConnectAsync(user);

        var update = await mcp.CallAsync("update_entries", new
        {
            updates = new object[]
            {
                new { id = empty.Id.ToString(), description = "Fix the login redirect" },
                new { id = described.Id.ToString(), description = "Something else" },
            },
        });

        Assert.Equal([empty.Id], update.GetProperty("changed").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));
        Assert.Equal("It already has a description. Overwrite only if the person asked to.", Assert.Single(update.GetProperty("skipped").EnumerateArray()).GetProperty("reason").GetString());
        Assert.Equal("Fix the login redirect", (await user.GetAsync<TimeEntryResponse>($"/time-entries/{empty.Id}")).Description);

        var undo = await mcp.CallAsync("undo_last_update");
        Assert.Equal([empty.Id], undo.GetProperty("restored").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal("", (await user.GetAsync<TimeEntryResponse>($"/time-entries/{empty.Id}")).Description);
    }

    [Fact]
    public async Task Proposals_wait_in_the_persons_inbox_with_the_agents_name()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        await using var mcp = await ConnectAsync(user);

        var proposed = await mcp.CallAsync("propose_entries", new
        {
            entries = new object[]
            {
                new { start = "2026-09-14T14:00", stop = "2026-09-14T15:30", description = "Review the APNs fix", project_id = atlas.Id.ToString(), reason = "Commits on fix/apns" },
                new { start = "2026-09-14T16:00", stop = "2026-09-14T15:00" },
            },
        });

        var proposal = Assert.Single(proposed.GetProperty("proposed").EnumerateArray());
        Assert.Equal("Atlas", proposal.GetProperty("project").GetProperty("name").GetString());
        Assert.Equal("2026-09-14T14:00:00+02:00", proposal.GetProperty("start").GetString());
        Assert.Equal(1, Assert.Single(proposed.GetProperty("refused").EnumerateArray()).GetProperty("index").GetInt32());

        var inbox = await user.GetAsync<AgentProposalsResponse>("/agent-proposals");
        var waiting = Assert.Single(inbox.Pending);
        Assert.Equal(("Claude Code", "Commits on fix/apns"), (waiting.Client, waiting.Reason));

        var listed = await mcp.CallAsync("list_proposals");
        Assert.Equal(waiting.Id, Assert.Single(listed.GetProperty("waiting").EnumerateArray()).GetProperty("id").GetGuid());

        var withdrawn = await mcp.CallAsync("withdraw_proposals", new { proposal_ids = new[] { waiting.Id.ToString(), "nonsense" } });
        Assert.Equal([waiting.Id], withdrawn.GetProperty("withdrawn").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal(["nonsense"], withdrawn.GetProperty("unknown_ids").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty((await user.GetAsync<AgentProposalsResponse>("/agent-proposals")).Pending);
    }

    [Fact]
    public async Task An_agent_allowed_only_to_read_changes_nothing()
    {
        var user = await TestUser.RegisterAsync(factory);
        await using var mcp = await ConnectAsync(user, allowWrite: false);

        var refused = await mcp.FailAsync("start_timer", new { description = "Write tests" });

        Assert.Contains("read only", refused, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, (await mcp.CallAsync("get_timer")).GetProperty("running").ValueKind);
    }

    [Fact]
    public async Task The_timer_starts_and_stops()
    {
        var user = await TestUser.RegisterAsync(factory);
        await using var mcp = await ConnectAsync(user);

        var started = await mcp.CallAsync("start_timer", new { description = "Write tests" });
        var running = (await mcp.CallAsync("get_timer")).GetProperty("running");
        var stopped = await mcp.CallAsync("stop_timer");

        Assert.Equal(started.GetProperty("started").GetProperty("id").GetGuid(), running.GetProperty("id").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, stopped.GetProperty("stopped").GetProperty("stop").ValueKind);
        Assert.Equal("No timer is running.", await mcp.FailAsync("stop_timer"));
    }

    [Fact]
    public async Task A_read_only_organization_can_still_stop_its_timer()
    {
        var user = await TestUser.RegisterAsync(factory);
        await using var mcp = await ConnectAsync(user);
        await mcp.CallAsync("start_timer", new { description = "Write tests" });
        await factory.EndTrialAsync(Assert.Single((await user.MeAsync()).Organizations).Id);

        Assert.Contains("read-only", await mcp.FailAsync("propose_entries", new { entries = new[] { new { start = "2026-09-14T09:00", stop = "2026-09-14T10:00" } } }), StringComparison.Ordinal);
        await mcp.CallAsync("stop_timer");
    }

    [Fact]
    public async Task A_timer_in_another_workspace_is_only_said_to_run()
    {
        var user = await TestUser.RegisterAsync(factory);
        var other = await TimerElsewhereAsync(user);
        await using var mcp = await ConnectAsync(user, allowWrite: false);

        var timer = await mcp.CallAsync("get_timer");

        Assert.Equal(JsonValueKind.Null, timer.GetProperty("running").ValueKind);
        Assert.Equal(JsonValueKind.String, timer.GetProperty("running_elsewhere").ValueKind);
        Assert.DoesNotContain("Confidential", timer.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(other.Id.ToString(), timer.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Starting_a_timer_leaves_one_in_another_workspace_running()
    {
        var user = await TestUser.RegisterAsync(factory);
        var earlier = await user.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Write tests", start = At(9), stop = At(10) });
        var other = await TimerElsewhereAsync(user);
        await using var mcp = await ConnectAsync(user);

        Assert.Contains("another workspace", await mcp.FailAsync("start_timer", new { description = "Write tests" }), StringComparison.Ordinal);
        Assert.Contains("another workspace", await mcp.FailAsync("continue_entry", new { entry_id = earlier.Id }), StringComparison.Ordinal);

        var running = await user.Client.GetFromJsonAsync<TimeEntryResponse>("/api/me/time-entries/current", TestJson.Options, Ct);
        Assert.Equal(other.Id, running!.Id);
        Assert.Null(running.Stop);
    }

    /// <summary>Starts the person's timer in a second workspace of theirs, one the agent isn't let into.</summary>
    private static async Task<TimeEntryResponse> TimerElsewhereAsync(TestUser user)
    {
        var second = await TestUser.ReadAsync<WorkspaceSummary>(await user.Client.PostAsJsonAsync("/api/workspaces", new { name = "Second" }, Ct));
        return await TestUser.ReadAsync<TimeEntryResponse>(await user.Client.PostAsJsonAsync(
            $"/api/workspaces/{second.Id}/time-entries", new { description = "Confidential client work", start = DateTimeOffset.UtcNow.AddMinutes(-5) }, Ct));
    }

    [Fact]
    public async Task Without_activity_sync_evidence_says_so()
    {
        var user = await TestUser.RegisterAsync(factory);
        var entry = await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10) });
        await using var mcp = await ConnectAsync(user, allowWrite: false);

        var evidence = await mcp.CallAsync("get_evidence", new { entry_ids = new[] { entry.Id.ToString() } });
        var untracked = await mcp.CallAsync("find_untracked_time", new { from = "2026-09-14" });

        Assert.Contains("sync is off", Assert.Single(evidence.GetProperty("entries").EnumerateArray()).GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Empty(untracked.GetProperty("stretches").EnumerateArray());
        Assert.Contains("sync is off", untracked.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Synced_activity_gives_the_repository_branch_and_window_and_untracked_time()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        await TestUser.ReadAsync<AgentActivitySettingsResponse>(await user.Client.PutAsJsonAsync("/api/me/agent-activity/settings", new { sync = true, titles = true }, Ct));
        await TestUser.ReadAsync<AgentActivityUploadResponse>(await user.Client.PutAsJsonAsync("/api/me/agent-activity/sessions", new
        {
            deviceId = Mac,
            sessions = new object[]
            {
                new
                {
                    source = "claude", sessionId = "claude:a", repository = new { identity = "origin:github.com/acme/atlas", name = "atlas" },
                    title = "Fix the login page", branches = new[] { "fix/login" },
                    touches = new[] { Ms(9), Ms(9, 20), Ms(9, 40), Ms(14), Ms(14, 10), Ms(14, 25) },
                    runs = new[] { new { start = Ms(9), end = Ms(9, 50) }, new { start = Ms(14), end = Ms(14, 35) } }, parserVersion = 1,
                },
            },
        }, Ct));
        await user.PutAsync("/agent-activity/links", new { repository = new { identity = "origin:github.com/acme/atlas", name = "atlas" }, projectId = atlas.Id });
        var entry = await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10), projectId = atlas.Id });
        await using var mcp = await ConnectAsync(user, allowWrite: false);

        var evidence = Assert.Single((await mcp.CallAsync("get_evidence", new { entry_ids = new[] { entry.Id.ToString() } })).GetProperty("entries").EnumerateArray());
        var repository = Assert.Single(evidence.GetProperty("repositories").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, evidence.GetProperty("note").ValueKind);
        Assert.Equal(("atlas", "github.com/acme/atlas"), (repository.GetProperty("name").GetString(), repository.GetProperty("git").GetProperty("origin").GetString()));
        Assert.Equal("2026-09-14T09:00:00+02:00", repository.GetProperty("git").GetProperty("since").GetString());
        Assert.Equal(["fix/login"], repository.GetProperty("branches").EnumerateArray().Select(b => b.GetString()));
        Assert.Equal(["Fix the login page"], repository.GetProperty("session_titles").EnumerateArray().Select(b => b.GetString()));
        Assert.Equal(3, repository.GetProperty("prompts").GetInt32());
        Assert.Equal(atlas.Id, repository.GetProperty("project").GetProperty("id").GetGuid());

        var untracked = await mcp.CallAsync("find_untracked_time", new { from = "2026-09-14" });
        var stretch = Assert.Single(untracked.GetProperty("stretches").EnumerateArray());
        Assert.Equal("2026-09-14T14:00:00+02:00", stretch.GetProperty("start").GetString());
        Assert.Equal("Atlas", Assert.Single(stretch.GetProperty("runs").EnumerateArray()).GetProperty("project").GetProperty("name").GetString());
    }
}
