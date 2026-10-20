using System.Net;
using Spindle.Api.Projects;
using Spindle.Api.Tags;
using Spindle.Api.TimeEntries;

namespace Spindle.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class TimeEntryFlowTests(PostgresApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    [Fact]
    public async Task Exact_lookup_follows_moved_entries_and_hides_deleted_or_other_people_entries()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(owner);
        var entry = await CreateAsync(owner, new { description = "Own work", start = Now.AddHours(-2), stop = Now.AddHours(-1) });
        var loaded = await owner.GetAsync<TimeEntryResponse>($"/time-entries/{entry.Id}");
        Assert.Equal(entry.Id, loaded.Id);

        var moved = await TestUser.ReadAsync<TimeEntryResponse>(await owner.PutAsync($"/time-entries/{entry.Id}",
            new { description = "Moved", start = Now.AddDays(-20), stop = Now.AddDays(-20).AddHours(1) }));
        Assert.Equal(moved.Start, (await owner.GetAsync<TimeEntryResponse>($"/time-entries/{entry.Id}")).Start);
        Assert.Equal(HttpStatusCode.NotFound, (await member.Client.GetAsync(owner.In($"/time-entries/{entry.Id}"), Ct)).StatusCode);
        var memberEntry = await CreateAsync(member, new { start = Now.AddHours(-2), stop = Now.AddHours(-1) });
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync(owner.In($"/time-entries/{memberEntry.Id}"), Ct)).StatusCode);
        (await owner.DeleteAsync($"/time-entries/{entry.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync(owner.In($"/time-entries/{entry.Id}"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Starting_a_timer_stops_the_running_one_where_the_new_one_starts()
    {
        var user = await TestUser.RegisterAsync(factory);
        var first = await StartAsync(user, "First", Now.AddMinutes(-30));

        var second = await StartAsync(user, "Second", Now.AddMinutes(-5));

        var entries = await ListAsync(user);
        Assert.Equal(second.Start, entries.Single(e => e.Id == first.Id).Stop);
        Assert.Null(entries.Single(e => e.Id == second.Id).Stop);
        Assert.Equal(second.Id, (await CurrentAsync(user))!.Id);
    }

    [Fact]
    public async Task Stopping_is_safe_to_repeat()
    {
        var user = await TestUser.RegisterAsync(factory);
        var entry = await StartAsync(user, "Work", Now.AddMinutes(-10));

        var stopped = await TestUser.ReadAsync<TimeEntryResponse>(await user.PostAsync($"/time-entries/{entry.Id}/stop", new { }));
        var again = await TestUser.ReadAsync<TimeEntryResponse>(await user.PostAsync($"/time-entries/{entry.Id}/stop", new { }));

        Assert.NotNull(stopped.Stop);
        Assert.Equal(stopped.Stop, again.Stop);
        Assert.Null(await CurrentAsync(user));
    }

    [Fact]
    public async Task Times_that_break_the_rules_are_rejected_next_to_the_field()
    {
        var user = await TestUser.RegisterAsync(factory);

        var endBeforeStart = await user.PostAsync("/time-entries", new { start = Now, stop = Now.AddMinutes(-1) });
        var futureTimer = await user.PostAsync("/time-entries", new { start = Now.AddHours(1) });
        var noStart = await user.PostAsync("/time-entries", new { description = "No start" });

        Assert.Equal(["stop"], await TestUser.ErrorFieldsAsync(endBeforeStart));
        Assert.Equal(["start"], await TestUser.ErrorFieldsAsync(futureTimer));
        Assert.Equal(["start"], await TestUser.ErrorFieldsAsync(noStart));
    }

    [Fact]
    public async Task A_stopped_entry_cannot_be_made_to_run_again()
    {
        var user = await TestUser.RegisterAsync(factory);
        var entry = await CreateAsync(user, new { description = "Done", start = Now.AddHours(-2), stop = Now.AddHours(-1) });

        var response = await user.PutAsync($"/time-entries/{entry.Id}", new { description = "Done", start = entry.Start });

        Assert.Equal(["stop"], await TestUser.ErrorFieldsAsync(response));
    }

    [Fact]
    public async Task Billable_defaults_to_the_project_setting()
    {
        var user = await TestUser.RegisterAsync(factory);
        var project = await CreateProjectAsync(user, "Billable work", isBillable: true);

        var byDefault = await CreateAsync(user, new { projectId = project.Id, start = Now.AddHours(-2), stop = Now.AddHours(-1) });
        var overridden = await CreateAsync(user, new { projectId = project.Id, isBillable = false, start = Now.AddHours(-1), stop = Now });

        Assert.True(byDefault.IsBillable);
        Assert.False(overridden.IsBillable);
    }

    [Fact]
    public async Task Projects_tasks_and_tags_must_belong_to_the_workspace()
    {
        var user = await TestUser.RegisterAsync(factory);
        var stranger = await TestUser.RegisterAsync(factory);
        var strangersProject = await CreateProjectAsync(stranger, "Not yours");
        var strangersTag = await stranger.CreateAsync<TagResponse>("/tags", new { name = "not-yours" });
        var project = await CreateProjectAsync(user, "Mine");
        var otherProject = await CreateProjectAsync(user, "Also mine");
        var otherTask = await user.CreateAsync<TaskResponse>($"/projects/{otherProject.Id}/tasks", new { name = "Elsewhere" });

        var foreignProject = await user.PostAsync("/time-entries", new { projectId = strangersProject.Id, start = Now });
        var mismatchedTask = await user.PostAsync("/time-entries", new { projectId = project.Id, taskId = otherTask.Id, start = Now });
        var foreignTag = await user.PostAsync("/time-entries", new { tagIds = new[] { strangersTag.Id }, start = Now });

        Assert.Equal(["projectId"], await TestUser.ErrorFieldsAsync(foreignProject));
        Assert.Equal(["taskId"], await TestUser.ErrorFieldsAsync(mismatchedTask));
        Assert.Equal(["tagIds"], await TestUser.ErrorFieldsAsync(foreignTag));
    }

    [Fact]
    public async Task An_archived_project_stays_on_its_entries_but_cannot_be_picked_again()
    {
        var user = await TestUser.RegisterAsync(factory);
        var project = await CreateProjectAsync(user, "Wrapped up");
        var entry = await CreateAsync(user, new { projectId = project.Id, start = Now.AddHours(-2), stop = Now.AddHours(-1) });
        (await user.PostAsync($"/projects/{project.Id}/archive", new { })).EnsureSuccessStatusCode();

        var edit = await user.PutAsync($"/time-entries/{entry.Id}", new { description = "Renamed", projectId = project.Id, start = entry.Start, stop = entry.Stop });
        var newEntry = await user.PostAsync("/time-entries", new { projectId = project.Id, start = Now });

        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(["projectId"], await TestUser.ErrorFieldsAsync(newEntry));
    }

    [Fact]
    public async Task Creating_with_a_client_chosen_id_is_safe_to_repeat()
    {
        var user = await TestUser.RegisterAsync(factory);
        var stranger = await TestUser.RegisterAsync(factory);
        var id = Guid.NewGuid();
        var body = new { id, description = "Offline", start = Now.AddHours(-1), stop = Now };

        var first = await user.PostAsync("/time-entries", body);
        var retry = await user.PostAsync("/time-entries", body);
        var hijack = await stranger.PostAsync("/time-entries", body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(id, (await TestUser.ReadAsync<TimeEntryResponse>(retry)).Id);
        Assert.Equal(HttpStatusCode.Conflict, hijack.StatusCode);
        Assert.Single(await ListAsync(user));
    }

    [Fact]
    public async Task Users_only_see_and_change_their_own_workspace()
    {
        var user = await TestUser.RegisterAsync(factory);
        var stranger = await TestUser.RegisterAsync(factory);
        var entry = await CreateAsync(user, new { description = "Private", start = Now.AddHours(-1), stop = Now });

        var list = await stranger.Client.GetAsync(user.In($"/time-entries?from={Iso(Now.AddDays(-1))}&to={Iso(Now.AddDays(1))}"), Ct);
        var edit = await stranger.Client.PutAsync(user.In($"/time-entries/{entry.Id}"), JsonContent(new { start = Now }), Ct);
        var viaOwnWorkspace = await stranger.DeleteAsync($"/time-entries/{entry.Id}");

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, viaOwnWorkspace.StatusCode);
    }

    [Fact]
    public async Task The_list_holds_entries_that_start_in_the_range_newest_first()
    {
        var user = await TestUser.RegisterAsync(factory);
        var older = await CreateAsync(user, new { description = "Older", start = Now.AddDays(-3), stop = Now.AddDays(-3).AddHours(1) });
        var newer = await CreateAsync(user, new { description = "Newer", start = Now.AddHours(-2), stop = Now.AddHours(-1) });
        await CreateAsync(user, new { description = "Too old", start = Now.AddDays(-10), stop = Now.AddDays(-10).AddHours(1) });

        var entries = await ListAsync(user, from: Now.AddDays(-7));

        Assert.Equal([newer.Id, older.Id], entries.Select(e => e.Id));
    }

    [Fact]
    public async Task Suggestions_offer_recent_distinct_entries_that_can_still_be_started()
    {
        var user = await TestUser.RegisterAsync(factory);
        var website = await CreateProjectAsync(user, "Website");
        var archived = await CreateProjectAsync(user, "Old site");
        await CreateAsync(user, new { description = "Design review", projectId = website.Id, start = Now.AddHours(-5), stop = Now.AddHours(-4) });
        await CreateAsync(user, new { description = "Design review", projectId = website.Id, start = Now.AddHours(-3), stop = Now.AddHours(-2) });
        await CreateAsync(user, new { description = "Standup", start = Now.AddHours(-2), stop = Now.AddHours(-1) });
        await CreateAsync(user, new { description = "Design legacy", projectId = archived.Id, start = Now.AddHours(-1), stop = Now });
        (await user.PostAsync($"/projects/{archived.Id}/archive", new { })).EnsureSuccessStatusCode();

        var byDescription = await user.GetAsync<List<TimeEntrySuggestion>>("/time-entries/suggestions?search=design");
        var byProjectName = await user.GetAsync<List<TimeEntrySuggestion>>("/time-entries/suggestions?search=websi");
        var all = await user.GetAsync<List<TimeEntrySuggestion>>("/time-entries/suggestions");

        Assert.Equal(["Design review"], byDescription.Select(s => s.Description));
        Assert.Equal(website.Id, byProjectName.Single().ProjectId);
        Assert.Equal(["Design review", "Standup"], all.Select(s => s.Description));
    }

    [Fact]
    public async Task Descriptions_complete_what_is_typed_with_word_starts_and_the_project_first()
    {
        var user = await TestUser.RegisterAsync(factory);
        var website = await CreateProjectAsync(user, "Website");
        var app = await CreateProjectAsync(user, "App");
        await CreateAsync(user, new { description = "Prefixed", projectId = app.Id, start = Now.AddHours(-6), stop = Now.AddHours(-5) });
        await CreateAsync(user, new { description = "Fix login", projectId = app.Id, start = Now.AddHours(-5), stop = Now.AddHours(-4) });
        await CreateAsync(user, new { description = "Fix checkout", projectId = website.Id, start = Now.AddHours(-4), stop = Now.AddHours(-3) });
        await CreateAsync(user, new
        {
            start = Now.AddHours(-3),
            stop = Now.AddHours(-2),
            describeParts = true,
            shares = new object[]
            {
                new { projectId = website.Id, percent = 50, description = "Fix footer" },
                new { projectId = app.Id, percent = 50, description = "Review release" },
            },
        });

        var forApp = await user.GetAsync<List<string>>($"/time-entries/descriptions?search=fix&projectId={app.Id}");
        var forWebsite = await user.GetAsync<List<string>>($"/time-entries/descriptions?search=fix&projectId={website.Id}");

        Assert.Equal(["Fix login", "Fix footer", "Fix checkout", "Prefixed"], forApp);
        Assert.Equal(["Fix footer", "Fix checkout", "Fix login", "Prefixed"], forWebsite);
        Assert.Empty(await user.GetAsync<List<string>>("/time-entries/descriptions?search=fix%20login"));
    }

    [Fact]
    public async Task Suggestions_search_case_insensitively_and_treat_wildcards_as_literal_text()
    {
        var user = await TestUser.RegisterAsync(factory);
        await CreateAsync(user, new { description = "Review 100%_done\\draft", start = Now.AddHours(-3), stop = Now.AddHours(-2) });
        await CreateAsync(user, new { description = "Review 100XXdone draft", start = Now.AddHours(-2), stop = Now.AddHours(-1) });

        var matches = await user.GetAsync<List<TimeEntrySuggestion>>(
            "/time-entries/suggestions?search=" + Uri.EscapeDataString("100%_DONE\\draft"));
        var report = await TestUser.ReadAsync<System.Text.Json.JsonElement>(await user.PostAsync("/reports/summary", new
        {
            from = DateOnly.FromDateTime(Now.UtcDateTime.AddDays(-1)),
            to = DateOnly.FromDateTime(Now.UtcDateTime.AddDays(1)),
            description = "100%_DONE\\draft",
        }));

        Assert.Equal("Review 100%_done\\draft", Assert.Single(matches).Description);
        Assert.Equal(1, report.GetProperty("totals").GetProperty("entryCount").GetInt32());
    }

    [Fact]
    public async Task Deleting_a_project_task_or_tag_keeps_the_tracked_time()
    {
        var user = await TestUser.RegisterAsync(factory);
        var project = await CreateProjectAsync(user, "Short-lived");
        var task = await user.CreateAsync<TaskResponse>($"/projects/{project.Id}/tasks", new { name = "Research" });
        var tag = await user.CreateAsync<TagResponse>("/tags", new { name = "deep-work" });
        var keep = await user.CreateAsync<TagResponse>("/tags", new { name = "keep" });
        var withTask = await CreateAsync(user, new { projectId = project.Id, taskId = task.Id, tagIds = new[] { tag.Id, keep.Id }, start = Now.AddHours(-2), stop = Now.AddHours(-1) });

        (await user.DeleteAsync($"/projects/{project.Id}/tasks/{task.Id}")).EnsureSuccessStatusCode();
        var afterTask = (await ListAsync(user)).Single(e => e.Id == withTask.Id);
        (await user.DeleteAsync($"/tags/{tag.Id}")).EnsureSuccessStatusCode();
        (await user.DeleteAsync($"/projects/{project.Id}")).EnsureSuccessStatusCode();
        var afterAll = (await ListAsync(user)).Single(e => e.Id == withTask.Id);

        Assert.Equal(project.Id, afterTask.ProjectId);
        Assert.Null(afterTask.TaskId);
        Assert.Null(afterAll.ProjectId);
        Assert.Equal([keep.Id], afterAll.TagIds);
        Assert.Equal(withTask.Stop! - withTask.Start, afterAll.Stop! - afterAll.Start);
    }

    [Fact]
    public async Task Projects_report_the_time_tracked_on_them_and_their_tasks()
    {
        var user = await TestUser.RegisterAsync(factory);
        var project = await CreateProjectAsync(user, "Measured");
        var task = await user.CreateAsync<TaskResponse>($"/projects/{project.Id}/tasks", new { name = "Build" });
        await CreateAsync(user, new { projectId = project.Id, taskId = task.Id, start = Now.AddHours(-3), stop = Now.AddHours(-2) });
        await CreateAsync(user, new { projectId = project.Id, start = Now.AddHours(-2), stop = Now.AddHours(-1).AddMinutes(-30) });

        var measured = await user.GetAsync<ProjectResponse>($"/projects/{project.Id}");

        Assert.Equal(5400, measured.TrackedSeconds);
        Assert.Equal(3600, measured.Tasks.Single().TrackedSeconds);
    }

    [Fact]
    public async Task Several_entries_can_be_changed_at_once_keeping_their_other_details()
    {
        var user = await TestUser.RegisterAsync(factory);
        var project = await CreateProjectAsync(user, "Bulk");
        var meeting = await user.CreateAsync<TagResponse>("/tags", new { name = "meeting" });
        var remote = await user.CreateAsync<TagResponse>("/tags", new { name = "remote" });
        var review = await user.CreateAsync<TagResponse>("/tags", new { name = "review" });
        var one = await CreateAsync(user, new { description = "One", tagIds = new[] { meeting.Id }, start = Now.AddHours(-3), stop = Now.AddHours(-2) });
        var two = await CreateAsync(user, new { description = "Two", tagIds = new[] { meeting.Id, remote.Id }, start = Now.AddHours(-2), stop = Now.AddHours(-1) });

        var updated = await TestUser.ReadAsync<List<TimeEntryResponse>>(await user.PostAsync("/time-entries/bulk-update", new
        {
            ids = new[] { one.Id, two.Id },
            project = new { projectId = project.Id, taskId = (Guid?)null },
            isBillable = true,
            addTagIds = new[] { review.Id },
            removeTagIds = new[] { meeting.Id },
        }));

        var byId = updated.ToDictionary(e => e.Id);
        Assert.Equal("One", byId[one.Id].Description);
        Assert.Equal("Two", byId[two.Id].Description);
        Assert.All(updated, e => Assert.Equal((project.Id, true), (e.ProjectId!.Value, e.IsBillable)));
        Assert.Equal([review.Id], byId[one.Id].TagIds);
        Assert.Equal([remote.Id, review.Id], byId[two.Id].TagIds.Order());
    }

    [Fact]
    public async Task Bulk_changes_apply_to_all_entries_or_none()
    {
        var user = await TestUser.RegisterAsync(factory);
        var stranger = await TestUser.RegisterAsync(factory);
        var mine = await CreateAsync(user, new { description = "Mine", start = Now.AddHours(-2), stop = Now.AddHours(-1) });
        var theirs = await CreateAsync(stranger, new { description = "Theirs", start = Now.AddHours(-2), stop = Now.AddHours(-1) });

        var update = await user.PostAsync("/time-entries/bulk-update", new { ids = new[] { mine.Id, theirs.Id }, description = "Taken over" });
        var delete = await user.PostAsync("/time-entries/bulk-delete", new { ids = new[] { mine.Id, theirs.Id } });

        Assert.Equal(["ids"], await TestUser.ErrorFieldsAsync(update));
        Assert.Equal(["ids"], await TestUser.ErrorFieldsAsync(delete));
        Assert.Equal("Mine", (await ListAsync(user)).Single().Description);
        Assert.Equal("Theirs", (await ListAsync(stranger)).Single().Description);
    }

    [Fact]
    public async Task Several_entries_can_be_deleted_at_once()
    {
        var user = await TestUser.RegisterAsync(factory);
        var one = await CreateAsync(user, new { description = "One", start = Now.AddHours(-3), stop = Now.AddHours(-2) });
        var two = await CreateAsync(user, new { description = "Two", start = Now.AddHours(-2), stop = Now.AddHours(-1) });
        var kept = await CreateAsync(user, new { description = "Kept", start = Now.AddHours(-1), stop = Now });

        var response = await user.PostAsync("/time-entries/bulk-delete", new { ids = new[] { one.Id, two.Id } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal([kept.Id], (await ListAsync(user)).Select(e => e.Id));
    }

    private static async Task<TimeEntryResponse> StartAsync(TestUser user, string description, DateTimeOffset start) =>
        await CreateAsync(user, new { description, start });

    private static Task<TimeEntryResponse> CreateAsync(TestUser user, object body) => user.CreateAsync<TimeEntryResponse>("/time-entries", body);

    private static Task<ProjectResponse> CreateProjectAsync(TestUser user, string name, bool isBillable = false) =>
        user.CreateAsync<ProjectResponse>("/projects", new { name, color = "#6D28D9", isBillable });

    private static Task<List<TimeEntryResponse>> ListAsync(TestUser user, DateTimeOffset? from = null) =>
        user.GetAsync<List<TimeEntryResponse>>($"/time-entries?from={Iso(from ?? Now.AddDays(-30))}&to={Iso(Now.AddDays(1))}");

    private static async Task<TimeEntryResponse?> CurrentAsync(TestUser user)
    {
        var response = await user.Client.GetAsync("/api/me/time-entries/current", Ct);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await TestUser.ReadAsync<TimeEntryResponse>(response);
    }

    private static string Iso(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O"));

    private static System.Net.Http.Json.JsonContent JsonContent(object body) => System.Net.Http.Json.JsonContent.Create(body);
}
