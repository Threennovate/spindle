using System.Net;
using Spindle.Api.AgentActivity;
using Spindle.Api.Projects;
using Spindle.Api.TimeEntries;

namespace Spindle.Tests.Integration;

/// <summary>Entries and changes agents propose, kept on the server until the person saves or discards them, and only theirs.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AgentProposalTests(PostgresApiFactory factory)
{
    // Monday 14 September 2026; test users live in Europe/Zagreb (UTC+2 then).
    private static readonly DateOnly Monday = new(2026, 9, 14);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(int hours, int minutes = 0) => new(Monday, new TimeOnly(hours, minutes), TimeSpan.FromHours(2));

    private static Task<ProjectResponse> ProjectAsync(TestUser user, string name) =>
        user.CreateAsync<ProjectResponse>("/projects", new { name, color = "#0B83D9" });

    private static Task<ProposalPlanResponse> ProposeAsync(TestUser user, params object[] entries) =>
        user.CreateAsync<ProposalPlanResponse>("/agent-proposals/entries", new { entries, client = "claude-code" });

    private static Task<ProposalPlanResponse> ChangeAsync(TestUser user, params object[] changes) =>
        user.CreateAsync<ProposalPlanResponse>("/agent-proposals/changes", new { changes, client = "codex" });

    private static Task<AgentProposalsResponse> ListAsync(TestUser user) => user.GetAsync<AgentProposalsResponse>("/agent-proposals");

    [Fact]
    public async Task Proposed_entries_wait_until_the_person_adds_them()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");

        var plan = await ProposeAsync(
            user,
            // Wall-clock time in the person's zone.
            new { start = "2026-09-14 09:00", stop = "2026-09-14T10:30", description = "Fix the login redirect", projectId = atlas.Id.ToString(), reason = "Commits on fix/login" },
            new { start = "2026-09-14T11:00:00+02:00", stop = "2026-09-14T10:00:00+02:00" },
            new { start = "2026-09-14 12:00", stop = "2026-09-14 13:00", projectId = Guid.NewGuid().ToString() },
            new { start = "2026-09-14 09:00", stop = "2026-09-14 10:30" });

        var proposal = Assert.Single(plan.Proposals);
        Assert.Equal(At(9), proposal.Fields.Start);
        Assert.Equal(At(10, 30), proposal.Fields.Stop);
        Assert.Equal("claude-code", proposal.Client);
        Assert.Equal([1, 2, 3], plan.Refused.Select(r => r.Index));
        Assert.Equal("The same time is already tracked or proposed.", plan.Refused[2].Reason);

        var waiting = Assert.Single((await ListAsync(user)).Pending);
        Assert.Empty(waiting.Blocking);
        Assert.Equal(atlas.Id, waiting.Saves!.ProjectId);
        Assert.Empty(await user.GetAsync<List<TimeEntryResponse>>($"/time-entries?from={Uri.EscapeDataString(At(0).ToString("O"))}&to={Uri.EscapeDataString(At(23).ToString("O"))}"));

        var applied = await user.CreateAsync<AppliedAgentProposalResponse>($"/agent-proposals/{proposal.Id}/apply", new { });

        Assert.Equal("Fix the login redirect", applied.Entry.Description);
        Assert.Equal(atlas.Id, applied.Entry.ProjectId);
        Assert.Equal((At(9), At(10, 30)), (applied.Entry.Start, applied.Entry.Stop!.Value));
        var after = await ListAsync(user);
        Assert.Empty(after.Pending);
        var outcome = Assert.Single(after.Outcomes);
        Assert.Equal(("added", applied.Entry.Id), (outcome.Outcome, outcome.EntryId));

        var again = await user.PostAsync($"/agent-proposals/{proposal.Id}/apply", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task A_proposed_split_is_saved_with_each_part_described()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        var beacon = await ProjectAsync(user, "Beacon");

        var plan = await ProposeAsync(user, new
        {
            start = "2026-09-14 14:00",
            stop = "2026-09-14 16:00",
            split = new object[]
            {
                new { projectId = atlas.Id.ToString(), percent = 60, description = "Review the APNs fix" },
                new { projectId = beacon.Id.ToString(), percent = 40, description = "Plan the import" },
            },
        });
        var applied = await user.CreateAsync<AppliedAgentProposalResponse>($"/agent-proposals/{Assert.Single(plan.Proposals).Id}/apply", new { });

        Assert.Equal(atlas.Id, applied.Entry.ProjectId);
        Assert.Equal(
            [(atlas.Id, 60, "Review the APNs fix"), (beacon.Id, 40, "Plan the import")],
            applied.Entry.Shares.Select(s => (s.ProjectId!.Value, s.Percent, s.Description!)));
        Assert.Equal("Review the APNs fix; Plan the import", applied.Entry.Description);
    }

    [Fact]
    public async Task A_newer_change_replaces_the_one_waiting_and_applies_to_the_entry_as_it_is()
    {
        var user = await TestUser.RegisterAsync(factory);
        var atlas = await ProjectAsync(user, "Atlas");
        var beacon = await ProjectAsync(user, "Beacon");
        var entry = await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10), projectId = atlas.Id, description = "Standup" });

        var first = await ChangeAsync(user, new { id = entry.Id.ToString(), stop = "2026-09-14 10:15" });
        var unchanged = await ChangeAsync(user, new { id = entry.Id.ToString(), projectId = atlas.Id.ToString() });
        var second = await ChangeAsync(user, new { id = entry.Id.ToString(), projectId = beacon.Id.ToString(), reason = "Branch beacon/import" });

        Assert.Single(first.Proposals);
        Assert.Equal("That is how the entry already is.", Assert.Single(unchanged.Refused).Reason);
        var waiting = Assert.Single((await ListAsync(user)).Pending);
        Assert.Equal(second.Proposals[0].Id, waiting.Id);
        Assert.Equal(entry.Id, waiting.EntryId);
        Assert.Equal(atlas.Id, waiting.Original!.ProjectId);
        Assert.Empty(waiting.Warnings);

        await user.PutAsync($"/time-entries/{entry.Id}", new { start = At(9), stop = At(10), projectId = atlas.Id, description = "Standup and review" });
        Assert.Equal(["The entry changed after this was proposed."], Assert.Single((await ListAsync(user)).Pending).Warnings);

        var applied = await user.CreateAsync<AppliedAgentProposalResponse>($"/agent-proposals/{waiting.Id}/apply", new { });

        Assert.Equal((beacon.Id, "Standup and review"), (applied.Entry.ProjectId!.Value, applied.Entry.Description));
        Assert.Equal("changed", Assert.Single((await ListAsync(user)).Outcomes).Outcome);
    }

    [Fact]
    public async Task What_stands_in_the_way_is_told_and_blocks_saving()
    {
        var user = await TestUser.RegisterAsync(factory);
        var entry = await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10) });
        await ProposeAsync(user, new { start = "2026-09-14 09:30", stop = "2026-09-14 11:00", description = "Pairing" });
        var change = await ChangeAsync(user, new { id = entry.Id.ToString(), description = "Write the release notes" });
        await user.DeleteAsync($"/time-entries/{entry.Id}");

        var pending = (await ListAsync(user)).Pending;

        Assert.Equal(["The entry was deleted."], pending.Single(p => p.EntryId == entry.Id).Blocking);
        var conflict = await user.PostAsync($"/agent-proposals/{change.Proposals[0].Id}/apply", new { });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Empty(pending.Single(p => p.EntryId is null).Warnings);
    }

    [Fact]
    public async Task Overlaps_are_warnings()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10), description = "Plan the release" });
        await ProposeAsync(
            user,
            new { start = "2026-09-14 09:30", stop = "2026-09-14 11:00" },
            new { start = "2026-09-14 10:30", stop = "2026-09-14 12:00" });

        var pending = (await ListAsync(user)).Pending;

        Assert.Equal([At(9, 30), At(10, 30)], pending.Select(p => p.Saves!.Start!.Value));
        Assert.Equal(["Overlaps “Plan the release”, 09:00–10:00.", "Overlaps another proposal."], pending[0].Warnings);
        Assert.Equal(["Overlaps another proposal."], pending[1].Warnings);
        Assert.All(pending, p => Assert.Empty(p.Blocking));
    }

    [Fact]
    public async Task Discarding_saving_after_editing_withdrawing_and_taking_back()
    {
        var user = await TestUser.RegisterAsync(factory);
        var plan = await ProposeAsync(
            user,
            new { start = "2026-09-14 09:00", stop = "2026-09-14 10:00" },
            new { start = "2026-09-14 11:00", stop = "2026-09-14 12:00" },
            new { start = "2026-09-14 13:00", stop = "2026-09-14 14:00" });
        var (discarded, edited, withdrawn) = (plan.Proposals[0].Id, plan.Proposals[1].Id, plan.Proposals[2].Id);

        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync($"/agent-proposals/{discarded}/resolve", new { outcome = "discarded" })).StatusCode);
        var saved = await user.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(11), stop = At(12, 30), description = "Edited" });
        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync($"/agent-proposals/{edited}/resolve", new { outcome = "saved", entryId = saved.Id })).StatusCode);
        var taken = await user.CreateAsync<WithdrawAgentProposalsResponse>("/agent-proposals/withdraw", new { ids = new[] { withdrawn, discarded } });

        Assert.Equal([withdrawn], taken.Withdrawn);
        var after = await ListAsync(user);
        Assert.Empty(after.Pending);
        Assert.Equal(
            [(edited, "added", (Guid?)saved.Id), (discarded, "discarded", null)],
            after.Outcomes.Select(o => (o.ProposalId, o.Outcome, o.EntryId)).OrderBy(o => o.Outcome));

        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync($"/agent-proposals/{discarded}/reopen", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await user.PostAsync($"/agent-proposals/{withdrawn}/reopen", new { })).StatusCode);
        Assert.Equal(discarded, Assert.Single((await ListAsync(user)).Pending).Id);
    }

    [Fact]
    public async Task Proposals_are_only_their_owners()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);
        var plan = await ProposeAsync(member, new { start = "2026-09-14 09:00", stop = "2026-09-14 10:00" });
        var adminEntry = await admin.CreateAsync<TimeEntryResponse>("/time-entries", new { start = At(9), stop = At(10) });

        var refused = await ChangeAsync(member, new { id = adminEntry.Id.ToString(), description = "Not mine" });

        Assert.Empty((await ListAsync(admin)).Pending);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/agent-proposals/{plan.Proposals[0].Id}/apply", new { })).StatusCode);
        Assert.Empty((await admin.CreateAsync<WithdrawAgentProposalsResponse>("/agent-proposals/withdraw", new { ids = new[] { plan.Proposals[0].Id } })).Withdrawn);
        Assert.Equal("There is no such entry among the person's entries. List entries first.", Assert.Single(refused.Refused).Reason);
        Assert.Single((await ListAsync(member)).Pending);
    }
}
