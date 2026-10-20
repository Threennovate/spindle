using Spindle.Api.Clients;
using Spindle.Api.Projects;
using Spindle.Api.Reports;
using Spindle.Api.TimeEntries;

namespace Spindle.Tests.Integration;

/// <summary>Entries whose time is split between projects: how they are kept, changed and counted.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SplitEntryTests(PostgresApiFactory factory)
{
    // Monday 14 September 2026; test users live in Europe/Zagreb (UTC+2 then).
    private static readonly DateOnly Monday = new(2026, 9, 14);

    private static DateTimeOffset At(int hours, int minutes = 0) =>
        new(Monday, new TimeOnly(hours, minutes), TimeSpan.FromHours(2));

    [Fact]
    public async Task A_split_entry_keeps_its_parts_and_shows_its_largest_as_its_project()
    {
        var user = await TestUser.RegisterAsync(factory);
        var website = await ProjectAsync(user, "Website", isBillable: true);
        var research = await ProjectAsync(user, "Research");

        var entry = await user.CreateAsync<TimeEntryResponse>("/time-entries", new
        {
            description = "Two agents at once",
            start = At(9),
            stop = At(12),
            shares = new object[]
            {
                new { projectId = research.Id, percent = 40 },
                new { projectId = website.Id, percent = 60 },
            },
        });

        Assert.Equal(website.Id, entry.ProjectId);
        Assert.True(entry.IsBillable);
        Assert.Equal(
            [new TimeShareResponse(research.Id, null, false, 40, null), new TimeShareResponse(website.Id, null, true, 60, null)],
            entry.Shares);
    }

    [Fact]
    public async Task Parts_that_do_not_add_up_are_rejected()
    {
        var user = await TestUser.RegisterAsync(factory);
        var website = await ProjectAsync(user, "Website");
        var research = await ProjectAsync(user, "Research");

        var response = await user.PostAsync("/time-entries", new
        {
            start = At(9),
            stop = At(10),
            shares = new object[] { new { projectId = website.Id, percent = 50 }, new { projectId = research.Id, percent = 30 } },
        });

        Assert.Equal(["shares"], await TestUser.ErrorFieldsAsync(response));
    }

    [Fact]
    public async Task Each_part_is_checked_and_errors_name_it()
    {
        var user = await TestUser.RegisterAsync(factory);
        var website = await ProjectAsync(user, "Website");

        var response = await user.PostAsync("/time-entries", new
        {
            start = At(9),
            stop = At(10),
            shares = new object[] { new { projectId = website.Id, percent = 50 }, new { projectId = Guid.NewGuid(), percent = 50 } },
        });

        Assert.Equal(["shares[1].projectId"], await TestUser.ErrorFieldsAsync(response));
    }

    [Fact]
    public async Task Apps_that_know_nothing_of_splits_keep_them_unless_they_change_the_project()
    {
        var user = await TestUser.RegisterAsync(factory);
        var website = await ProjectAsync(user, "Website");
        var research = await ProjectAsync(user, "Research");
        var entry = await SplitAsync(user, website, research);

        // An older app changes the description and sends back the project it was shown.
        var renamed = await TestUser.ReadAsync<TimeEntryResponse>(await user.PutAsync($"/time-entries/{entry.Id}", new
        {
            description = "Renamed", projectId = entry.ProjectId, start = entry.Start, stop = entry.Stop,
        }));
        Assert.Equal("Renamed", renamed.Description);
        Assert.Equal(entry.Shares, renamed.Shares);

        // Choosing another project there means all of it goes to that one.
        var moved = await TestUser.ReadAsync<TimeEntryResponse>(await user.PutAsync($"/time-entries/{entry.Id}", new
        {
            description = "Renamed", projectId = research.Id, start = entry.Start, stop = entry.Stop,
        }));
        Assert.Empty(moved.Shares);
        Assert.Equal(research.Id, moved.ProjectId);
    }

    [Fact]
    public async Task An_empty_list_of_parts_ends_the_split()
    {
        var user = await TestUser.RegisterAsync(factory);
        var website = await ProjectAsync(user, "Website");
        var research = await ProjectAsync(user, "Research");
        var entry = await SplitAsync(user, website, research);

        var single = await TestUser.ReadAsync<TimeEntryResponse>(await user.PutAsync($"/time-entries/{entry.Id}", new
        {
            projectId = research.Id, start = entry.Start, stop = entry.Stop, shares = Array.Empty<object>(),
        }));

        Assert.Empty(single.Shares);
        Assert.Equal(research.Id, single.ProjectId);
    }

    [Fact]
    public async Task Reports_count_each_part_on_its_project_with_its_rate()
    {
        var user = await TestUser.RegisterAsync(factory);
        var acme = await user.CreateAsync<ClientResponse>("/clients", new { name = "Acme" });
        var website = await user.CreateAsync<ProjectResponse>("/projects", new { name = "Website", color = "#0B83D9", clientId = acme.Id, isBillable = true, hourlyRate = 100m });
        var research = await ProjectAsync(user, "Research");
        await SplitAsync(user, website, research);

        var summary = await user.CreateAsync<SummaryReport>("/reports/summary", Day(new { groupBy = "project" }));

        // 3 hours, 60/40: 1 h 48 min on Website at 100 an hour, 1 h 12 min on Research.
        Assert.Equal(new ReportTotals(3 * 3600, 6480, 180m, 0m, 1, 6480), summary.Totals);
        Assert.Equal([("Website", 6480L), ("Research", 4320L)], summary.Groups.Select(g => (g.Name, g.Seconds)));

        var byClient = await user.CreateAsync<SummaryReport>("/reports/summary", Day(new { groupBy = "project", clientIds = new[] { acme.Id } }));
        Assert.Equal([("Website", 6480L)], byClient.Groups.Select(g => (g.Name, g.Seconds)));

        var detailed = await user.CreateAsync<DetailedReport>("/reports/detailed", Day(new { projectIds = new[] { research.Id } }));
        var row = Assert.Single(detailed.Entries);
        Assert.Equal((research.Id, 40, 4320L), (row.ProjectId, row.Percent, row.Seconds));
        Assert.Equal(2, row.Shares.Count);
        Assert.Equal(1, detailed.TotalCount);
    }

    [Fact]
    public async Task Projects_count_their_part_of_split_time()
    {
        var user = await TestUser.RegisterAsync(factory);
        var website = await ProjectAsync(user, "Website");
        var research = await ProjectAsync(user, "Research");
        await SplitAsync(user, website, research);

        var projects = await user.GetAsync<List<ProjectResponse>>("/projects");

        Assert.Equal(6480, projects.Single(p => p.Id == website.Id).TrackedSeconds);
        Assert.Equal(4320, projects.Single(p => p.Id == research.Id).TrackedSeconds);
    }

    [Fact]
    public async Task Deleting_a_project_leaves_its_part_without_a_project()
    {
        var user = await TestUser.RegisterAsync(factory);
        var website = await ProjectAsync(user, "Website");
        var research = await ProjectAsync(user, "Research");
        var entry = await SplitAsync(user, website, research);

        (await user.DeleteAsync($"/projects/{research.Id}")).EnsureSuccessStatusCode();

        var entries = await user.GetAsync<List<TimeEntryResponse>>($"/time-entries?from={Iso(At(0))}&to={Iso(At(23))}");
        var kept = entries.Single(e => e.Id == entry.Id);
        Assert.Equal([(website.Id, 60), (null, 40)], kept.Shares.Select(s => (s.ProjectId, s.Percent)));
    }

    [Fact]
    public async Task Changing_several_entries_to_one_project_ends_their_splits_but_billable_keeps_them()
    {
        var user = await TestUser.RegisterAsync(factory);
        var website = await ProjectAsync(user, "Website");
        var research = await ProjectAsync(user, "Research");
        var entry = await SplitAsync(user, website, research);

        var billable = await user.CreateAsync<List<TimeEntryResponse>>("/time-entries/bulk-update", new { ids = new[] { entry.Id }, isBillable = true });
        Assert.All(billable.Single().Shares, s => Assert.True(s.IsBillable));

        var moved = await user.CreateAsync<List<TimeEntryResponse>>("/time-entries/bulk-update", new { ids = new[] { entry.Id }, project = new { projectId = website.Id } });
        Assert.Empty(moved.Single().Shares);
    }

    [Fact]
    public async Task Parts_described_on_their_own_show_only_their_own_description_on_their_project()
    {
        var user = await TestUser.RegisterAsync(factory);
        var website = await ProjectAsync(user, "Website");
        var research = await ProjectAsync(user, "Research");
        var entry = await DescribedSplitAsync(user, website, research, "  Fix the checkout ", "Read up on payments");

        Assert.Equal("Fix the checkout; Read up on payments", entry.Description);
        Assert.Equal(["Fix the checkout", "Read up on payments"], entry.Shares.Select(s => s.Description));

        var detailed = await user.CreateAsync<DetailedReport>("/reports/detailed", Day(new { }));
        Assert.Equal(
            [(website.Id, "Fix the checkout"), (research.Id, "Read up on payments")],
            detailed.Entries.Select(e => (e.ProjectId!.Value, e.Description)).OrderBy(e => e.Description));

        // Searching for one part's words finds that part, not the other.
        var searched = await user.CreateAsync<DetailedReport>("/reports/detailed", Day(new { description = "checkout" }));
        Assert.Equal([website.Id], searched.Entries.Select(e => e.ProjectId!.Value));

        var byDescription = await user.CreateAsync<SummaryReport>("/reports/summary", Day(new { groupBy = "project", subGroupBy = "description" }));
        Assert.Equal(["Fix the checkout"], byDescription.Groups.Single(g => g.Name == "Website").SubGroups.Select(c => c.Name));
    }

    [Fact]
    public async Task Apps_that_know_nothing_of_part_descriptions_keep_them_until_they_change_the_description()
    {
        var user = await TestUser.RegisterAsync(factory);
        var website = await ProjectAsync(user, "Website");
        var research = await ProjectAsync(user, "Research");
        var entry = await DescribedSplitAsync(user, website, research, "Fix the checkout", "");

        // An older app sends the parts and the description it was shown, with another share.
        var reshared = await TestUser.ReadAsync<TimeEntryResponse>(await user.PutAsync($"/time-entries/{entry.Id}", new
        {
            entry.Description,
            start = entry.Start,
            stop = entry.Stop,
            shares = new object[] { new { projectId = website.Id, percent = 70 }, new { projectId = research.Id, percent = 30 } },
        }));
        Assert.Equal([("Fix the checkout", 70), ("", 30)], reshared.Shares.Select(s => (s.Description, s.Percent)));

        // A description changed there is one description for all parts again.
        var renamed = await TestUser.ReadAsync<TimeEntryResponse>(await user.PutAsync($"/time-entries/{entry.Id}", new
        {
            description = "Checkout and payments",
            projectId = reshared.ProjectId,
            start = entry.Start,
            stop = entry.Stop,
        }));
        Assert.Equal("Checkout and payments", renamed.Description);
        Assert.All(renamed.Shares, s => Assert.Null(s.Description));
    }

    [Fact]
    public async Task One_description_for_all_parts_replaces_their_own()
    {
        var user = await TestUser.RegisterAsync(factory);
        var website = await ProjectAsync(user, "Website");
        var research = await ProjectAsync(user, "Research");
        var entry = await DescribedSplitAsync(user, website, research, "Fix the checkout", "Read up on payments");

        var shared = await TestUser.ReadAsync<TimeEntryResponse>(await user.PutAsync($"/time-entries/{entry.Id}", new
        {
            description = "Fix the checkout; Read up on payments",
            start = entry.Start,
            stop = entry.Stop,
            describeParts = false,
            shares = entry.Shares.Select(s => new { s.ProjectId, s.Percent, s.Description }).ToArray(),
        }));
        Assert.All(shared.Shares, s => Assert.Null(s.Description));

        var again = await DescribedSplitAsync(user, website, research, "Fix the checkout", "Read up on payments");
        var bulk = await user.CreateAsync<List<TimeEntryResponse>>("/time-entries/bulk-update", new { ids = new[] { again.Id }, description = "Payments" });
        Assert.Equal("Payments", bulk.Single().Description);
        Assert.All(bulk.Single().Shares, s => Assert.Null(s.Description));

        // Deleting a project joins its part, with its description, to the part left without one.
        var third = await DescribedSplitAsync(user, website, research, "Fix the checkout", "Read up on payments");
        (await user.DeleteAsync($"/projects/{research.Id}")).EnsureSuccessStatusCode();
        var entries = await user.GetAsync<List<TimeEntryResponse>>($"/time-entries?from={Iso(At(0))}&to={Iso(At(23))}");
        Assert.Equal([(website.Id, "Fix the checkout"), (null, "Read up on payments")], entries.Single(e => e.Id == third.Id).Shares.Select(s => (s.ProjectId, s.Description)));
    }

    [Fact]
    public async Task A_required_description_is_required_of_each_part_described_on_its_own()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.UpdateSettingsAsync(settings => settings["requireDescription"] = true);
        var website = await ProjectAsync(user, "Website");
        var research = await ProjectAsync(user, "Research");

        var response = await user.PostAsync("/time-entries", DescribedSplit(website, research, "Fix the checkout", " "));

        Assert.Equal(["shares[1].description"], await TestUser.ErrorFieldsAsync(response));
    }

    private static Task<TimeEntryResponse> SplitAsync(TestUser user, ProjectResponse first, ProjectResponse second) =>
        user.CreateAsync<TimeEntryResponse>("/time-entries", new
        {
            description = "Two agents at once",
            start = At(9),
            stop = At(12),
            shares = new object[] { new { projectId = first.Id, percent = 60 }, new { projectId = second.Id, percent = 40 } },
        });

    private static Task<TimeEntryResponse> DescribedSplitAsync(TestUser user, ProjectResponse first, ProjectResponse second, string firstDescription, string secondDescription) =>
        user.CreateAsync<TimeEntryResponse>("/time-entries", DescribedSplit(first, second, firstDescription, secondDescription));

    private static object DescribedSplit(ProjectResponse first, ProjectResponse second, string firstDescription, string secondDescription) => new
    {
        start = At(9),
        stop = At(12),
        describeParts = true,
        shares = new object[]
        {
            new { projectId = first.Id, percent = 60, description = firstDescription },
            new { projectId = second.Id, percent = 40, description = secondDescription },
        },
    };

    private static Task<ProjectResponse> ProjectAsync(TestUser user, string name, bool isBillable = false) =>
        user.CreateAsync<ProjectResponse>("/projects", new { name, color = "#6D28D9", isBillable });

    private static Dictionary<string, object?> Day(object extra)
    {
        var body = extra.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(extra));
        body["from"] = Monday;
        body["to"] = Monday;
        return body;
    }

    private static string Iso(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O"));
}
