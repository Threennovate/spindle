using System.Net;
using Spindle.Api.Clients;
using Spindle.Api.Import;
using Spindle.Api.Projects;
using Spindle.Api.Reports;
using Spindle.Api.Tags;
using Spindle.Api.TimeEntries;

namespace Spindle.Tests.Integration;

/// <summary>Bringing time over from Toggl Track, Clockify, Harvest or another Spindle workspace.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ImportTests(PostgresApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string TogglFile(TestUser admin, TestUser member) => $"""
        User,Email,Client,Project,Task,Description,Billable,Start date,Start time,End date,End time,Duration,Tags,Amount (EUR)
        Ana,{admin.Email.ToUpperInvariant()},Acme,Website,Wireframes,Homepage,Yes,2026-08-03,09:00:00,2026-08-03,11:00:00,02:00:00,"design, client",
        Ivo,{member.Email},Acme,Website,,Review,Yes,2026-08-04,13:00:00,2026-08-04,14:30:00,01:30:00,design,
        Ivo,{member.Email},,Internal,,Planning,No,2026-08-05,09:00:00,2026-08-05,09:30:00,00:30:00,,
        Marko,marko@elsewhere.example,,,,Not ours,No,2026-08-05,10:00:00,2026-08-05,11:00:00,01:00:00,,
        Ivo,{member.Email},,,,Broken,No,someday,09:00:00,,10:00:00,01:00:00,,
        """;

    [Fact]
    public async Task A_preview_shows_what_an_import_would_do()
    {
        var admin = await TestUser.RegisterAsync(factory, $"ana-{Guid.NewGuid():N}@example.com", fullName: "Ana");
        var member = await factory.AddMemberAsync(admin, "Ivo");
        await admin.CreateAsync<TagResponse>("/tags", new { name = "Design" });

        var preview = await admin.CreateAsync<ImportPreview>("/imports/preview", new { content = TogglFile(admin, member), timeZone = "Europe/Zagreb" });

        Assert.Equal(("Toggl Track", DateOrder.YearMonthDay, false), (preview.Format, preview.DateOrder, preview.DateOrderGuessed));
        Assert.Equal((5, 3, 0, 1), (preview.Rows, preview.Ready, preview.Duplicates, preview.Skipped));
        Assert.Equal((new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 5), 4 * 3600L), (preview.From, preview.To, preview.Seconds));
        Assert.Equal(
            [("Ana", (Guid?)admin.UserId, 1), ("Ivo", member.UserId, 2), ("Marko", null, 1)],
            preview.People.Select(p => (p.Name, p.UserId, p.Entries)));
        Assert.Equal(["Acme"], preview.NewClients);
        Assert.Equal([new ImportNewProject("Internal", null), new ImportNewProject("Website", "Acme")], preview.NewProjects);
        Assert.Equal(["Website · Wireframes"], preview.NewTasks);
        Assert.Equal(["client"], preview.NewTags);
        var problem = Assert.Single(preview.Problems);
        Assert.Equal((6, 1), (problem.Line, preview.ProblemCount));
    }

    [Fact]
    public async Task Importing_creates_what_is_missing_and_a_second_import_adds_nothing()
    {
        var admin = await TestUser.RegisterAsync(factory, $"ana-{Guid.NewGuid():N}@example.com", fullName: "Ana");
        var member = await factory.AddMemberAsync(admin, "Ivo");
        var file = TogglFile(admin, member);

        var first = await admin.CreateAsync<ImportResult>("/imports", new { content = file, timeZone = "Europe/Zagreb" });
        var second = await admin.CreateAsync<ImportResult>("/imports", new { content = file, timeZone = "Europe/Zagreb" });

        Assert.Equal(new ImportResult(3, 0, 1, 1, 1, 2, 1, 2), first);
        Assert.Equal(new ImportResult(0, 3, 1, 1, 0, 0, 0, 0), second);

        var report = await admin.CreateAsync<DetailedReport>("/reports/detailed", new { from = "2026-08-01", to = "2026-08-31", sort = "date", descending = false });
        var homepage = report.Entries[0];
        // 09:00 in Zagreb in August is 07:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 8, 3, 7, 0, 0, TimeSpan.Zero), homepage.Start);
        Assert.Equal(("Ana", "Website", "Acme", "Wireframes", true), (homepage.UserName, homepage.ProjectName, homepage.ClientName, homepage.TaskName, homepage.IsBillable));
        Assert.Equal(["client", "design"], homepage.Tags);
        Assert.Equal(["Ana", "Ivo", "Ivo"], report.Entries.Select(e => e.UserName));

        var projects = await admin.GetAsync<List<ProjectResponse>>("/projects");
        Assert.True(projects.Single(p => p.Name == "Website").IsBillable);
        Assert.False(projects.Single(p => p.Name == "Internal").IsBillable);
        Assert.Equal(["Acme"], (await admin.GetAsync<List<ClientResponse>>("/clients")).Select(c => c.Name));
    }

    [Fact]
    public async Task Someone_who_is_not_a_member_can_be_assigned_to_one()
    {
        var admin = await TestUser.RegisterAsync(factory, $"ana-{Guid.NewGuid():N}@example.com", fullName: "Ana");
        var member = await factory.AddMemberAsync(admin, "Ivo");

        var result = await admin.CreateAsync<ImportResult>("/imports", new
        {
            content = TogglFile(admin, member),
            assignments = new[] { new { key = "marko@elsewhere.example", userId = (Guid?)admin.UserId }, new { key = member.Email, userId = (Guid?)null } },
        });

        // Marko's hour becomes Ana's; Ivo's rows are left out.
        Assert.Equal((2, 2), (result.Created, result.Skipped));
    }

    private const string HarvestHeader = "Date,Client,Project,Project Code,Task,Notes,Hours,Hours Rounded,Billable?,Invoiced?,Approved?,First Name,Last Name,Roles,Employee?,Billable Rate,Billable Amount,Cost Rate,Cost Amount,Currency,External Reference URL";

    private static readonly string[] HarvestRows =
    [
        "2026-08-03,Acme,Website,,Design,Homepage,1.5,1.5,Yes,No,No,Ana,Anić,,Yes,100,150,0,0,Euro - EUR,",
        "2026-08-03,,Internal,,Admin,Standup,0.25,0.25,No,No,No,Ana,Anić,,Yes,0,0,0,0,Euro - EUR,",
        "2026-08-03,,Internal,,Admin,Standup,0.25,0.25,No,No,No,Ana,Anić,,Yes,0,0,0,0,Euro - EUR,",
        "2026-08-04,Acme,Website,,Development,Review,2,2,Yes,No,No,Ivo,Ivić,,Yes,100,200,0,0,Euro - EUR,",
    ];

    private static string HarvestFile(params int[] rows) => string.Join('\n', [HarvestHeader, .. rows.Select(i => HarvestRows[i])]);

    [Fact]
    public async Task A_Harvest_export_imports_with_its_hours_placed_from_nine()
    {
        var admin = await TestUser.RegisterAsync(factory, $"ana-{Guid.NewGuid():N}@example.com", fullName: "Ana Anić");
        var member = await factory.AddMemberAsync(admin, "Ivo Ivić");

        var preview = await admin.CreateAsync<ImportPreview>("/imports/preview", new { content = HarvestFile(0, 1, 2, 3), timeZone = "Europe/Zagreb" });
        var result = await admin.CreateAsync<ImportResult>("/imports", new { content = HarvestFile(0, 1, 2, 3), timeZone = "Europe/Zagreb" });
        var report = await admin.CreateAsync<DetailedReport>("/reports/detailed", new { from = "2026-08-01", to = "2026-08-31", sort = "date", descending = false });

        Assert.Equal(("Harvest", 4, 4, 4, 0), (preview.Format, preview.Rows, preview.Placed, preview.Ready, preview.Duplicates));
        Assert.Equal([("Ana Anić", (Guid?)admin.UserId, 3), ("Ivo Ivić", member.UserId, 1)], preview.People.Select(p => (p.Name, p.UserId, p.Entries)));
        Assert.Equal(4, result.Created);
        // 09:00 in Zagreb in August is 07:00 UTC; Ana's entries follow one another, the standups twice.
        Assert.Equal(
            [
                ("Ana Anić", "Homepage", new DateTimeOffset(2026, 8, 3, 7, 0, 0, TimeSpan.Zero), true),
                ("Ana Anić", "Standup", new DateTimeOffset(2026, 8, 3, 8, 30, 0, TimeSpan.Zero), false),
                ("Ana Anić", "Standup", new DateTimeOffset(2026, 8, 3, 8, 45, 0, TimeSpan.Zero), false),
                ("Ivo Ivić", "Review", new DateTimeOffset(2026, 8, 4, 7, 0, 0, TimeSpan.Zero), true),
            ],
            report.Entries.Select(e => (e.UserName, e.Description, e.Start, e.IsBillable)));
    }

    [Fact]
    public async Task A_Harvest_export_imported_again_adds_nothing_even_when_its_hours_land_elsewhere()
    {
        var admin = await TestUser.RegisterAsync(factory, $"ana-{Guid.NewGuid():N}@example.com", fullName: "Ana Anić");
        await factory.AddMemberAsync(admin, "Ivo Ivić");
        await admin.CreateAsync<ImportResult>("/imports", new { content = HarvestFile(0, 1, 2, 3), timeZone = "Europe/Zagreb" });

        // Without the homepage hour, the standups would be placed from 9:00 instead of after it.
        var again = await admin.CreateAsync<ImportPreview>("/imports/preview", new { content = HarvestFile(1, 2, 3), timeZone = "Europe/Zagreb" });
        var more = await admin.CreateAsync<ImportPreview>("/imports/preview", new { content = HarvestFile(0, 1, 1, 2, 3), timeZone = "Europe/Zagreb" });

        Assert.Equal((0, 3), (again.Ready, again.Duplicates));
        // A third standup that day is new.
        Assert.Equal((1, 4), (more.Ready, more.Duplicates));
    }

    [Fact]
    public async Task Only_admins_import()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);

        var response = await member.PostAsync("/imports/preview", new { content = TogglFile(admin, member) });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_file_that_is_not_a_time_report_is_refused()
    {
        var admin = await TestUser.RegisterAsync(factory);

        var response = await admin.PostAsync("/imports", new { content = "Name,Price\nApples,3" });

        Assert.Equal(["content"], await TestUser.ErrorFieldsAsync(response));
    }

    [Fact]
    public async Task Three_Tracks_own_export_imports_into_another_workspace()
    {
        var source = await TestUser.RegisterAsync(factory, $"ana-{Guid.NewGuid():N}@example.com", fullName: "Ana");
        var project = await source.CreateAsync<ProjectResponse>("/projects", new { name = "Website", color = "#0B83D9", isBillable = true });
        var start = new DateTimeOffset(2026, 8, 3, 23, 0, 0, TimeSpan.FromHours(2));
        await source.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Release, v2", projectId = project.Id, isBillable = true, start, stop = start.AddHours(2) });
        var export = await (await source.PostAsync("/reports/detailed/export?format=csv", new { from = "2026-08-01", to = "2026-08-31" })).Content.ReadAsStringAsync(Ct);
        var target = await TestUser.RegisterAsync(factory, source.Email.Replace("ana-", "ana2-", StringComparison.Ordinal), fullName: "Ana");

        var result = await target.CreateAsync<ImportResult>("/imports", new { content = export, timeZone = "Europe/Zagreb" });
        var report = await target.CreateAsync<DetailedReport>("/reports/detailed", new { from = "2026-08-01", to = "2026-08-31" });

        Assert.Equal(1, result.Created);
        var entry = Assert.Single(report.Entries);
        Assert.Equal(("Release, v2", "Website", start, start.AddHours(2)), (entry.Description, entry.ProjectName, entry.Start, entry.Stop));
    }
}
