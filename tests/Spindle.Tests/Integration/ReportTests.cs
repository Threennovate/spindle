using System.Net;
using Spindle.Api.Clients;
using Spindle.Api.Projects;
using Spindle.Api.Reports;
using Spindle.Api.Tags;
using Spindle.Api.Team;
using Spindle.Api.TimeEntries;
using Spindle.Domain.Reports;

namespace Spindle.Tests.Integration;

/// <summary>Summary, detailed and weekly reports: what they add up, for whom, and at what rate.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ReportTests(PostgresApiFactory factory)
{
    // A past week, Monday 14 to Sunday 20 September 2026. Test users live in Europe/Zagreb (UTC+2 then).
    private static readonly DateOnly Monday = new(2026, 9, 14);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An instant in Zagreb on a day of the test week.</summary>
    private static DateTimeOffset At(int day, int hours, int minutes = 0) =>
        new DateTimeOffset(Monday.AddDays(day), new TimeOnly(hours, minutes), TimeSpan.FromHours(2));

    [Fact]
    public async Task The_summary_adds_up_time_and_money_by_group()
    {
        var team = await ArrangeAsync();

        var summary = await team.Admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { groupBy = "project", subGroupBy = "user" }));

        Assert.Equal("EUR", summary.Currency);
        Assert.Equal(new ReportTotals(5 * 3600, 4 * 3600, 425m, 40m, 4, 4 * 3600), summary.Totals);
        Assert.Equal(ReportResolution.Day, summary.Resolution);
        Assert.Equal(7, summary.Buckets.Count);
        Assert.Equal((3 * 3600L, 2 * 3600L), (summary.Buckets[0].Seconds, summary.Buckets[0].BillableSeconds));
        Assert.Equal((2 * 3600L, 2 * 3600L), (summary.Buckets[1].Seconds, summary.Buckets[1].BillableSeconds));

        // Groups come largest first. Rates: Ana on Website 120, Website 100, Ivo 70, the workspace 50.
        Assert.Equal(["Website", "Internal", "No project"], summary.Groups.Select(g => g.Name));
        var website = summary.Groups[0];
        Assert.Equal((team.Website.Id, "Acme", team.Website.Color), (website.Id, website.Detail, website.Color));
        Assert.Equal((3.5 * 3600, 390m, 30m), ((double)website.Seconds, website.Amount, website.LaborCost));
        Assert.Equal([("Ana", 240m), ("Ivo", 150m)], website.SubGroups.Select(s => (s.Name, s.Amount!.Value)));
        Assert.Equal((0m, "none", 35m), (summary.Groups[1].Amount!.Value, summary.Groups[2].Key, summary.Groups[2].Amount!.Value));
    }

    [Fact]
    public async Task Members_see_only_their_own_time_and_no_money()
    {
        var team = await ArrangeAsync();

        var summary = await team.Member.CreateAsync<SummaryReport>("/reports/summary", Week(new { groupBy = "user" }));
        var detailed = await team.Member.CreateAsync<DetailedReport>("/reports/detailed", Week(new { }));
        var askedForAna = await team.Member.CreateAsync<SummaryReport>("/reports/summary", Week(new { userIds = new[] { team.Admin.UserId } }));

        Assert.Equal(new ReportTotals(2 * 3600, 2 * 3600, null, null, 2, null), summary.Totals);
        Assert.Equal("Ivo", summary.Groups.Single().Name);
        Assert.All(detailed.Entries, e => Assert.Null(e.Amount));
        Assert.Equal(0, askedForAna.Totals.EntryCount);
    }

    [Fact]
    public async Task Filters_narrow_the_time_down()
    {
        var team = await ArrangeAsync();

        async Task<(long Seconds, int Count)> TotalAsync(object filter)
        {
            var totals = (await team.Admin.CreateAsync<SummaryReport>("/reports/summary", Week(filter))).Totals;
            return (totals.Seconds, totals.EntryCount);
        }

        Assert.Equal((3 * 3600L, 2), await TotalAsync(new { userIds = new[] { team.Admin.UserId } }));
        Assert.Equal((2 * 3600L, 2), await TotalAsync(new { groupIds = new[] { team.Designers.Id } }));
        Assert.Equal((4 * 3600L, 3), await TotalAsync(new { projectIds = new[] { team.Website.Id, Guid.Empty } }));
        Assert.Equal((1 * 3600L + 1800, 2), await TotalAsync(new { clientIds = new[] { Guid.Empty } }));
        Assert.Equal((2 * 3600L, 1), await TotalAsync(new { tagIds = new[] { team.Meeting.Id } }));
        Assert.Equal((3 * 3600L, 3), await TotalAsync(new { tagIds = new[] { Guid.Empty } }));
        Assert.Equal((1 * 3600L, 1), await TotalAsync(new { billable = false }));
        Assert.Equal((5400L, 1), await TotalAsync(new { description = "DESIGN" }));
        Assert.Equal((0L, 0), await TotalAsync(new { projectIds = new[] { team.Website.Id }, billable = false }));
    }

    [Fact]
    public async Task Rounding_applies_to_each_entry_before_it_is_added_up_and_billed()
    {
        var team = await ArrangeAsync();
        await team.Member.CreateAsync<TimeEntryResponse>("/time-entries", new { projectId = team.Website.Id, isBillable = true, start = At(2, 9), stop = At(2, 9, 1) });

        var exact = await team.Admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { projectIds = new[] { team.Website.Id } }));
        var roundedUp = await team.Admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { projectIds = new[] { team.Website.Id }, rounding = "up", roundingMinutes = 15 }));

        Assert.Equal((3.5 * 3600 + 60, 391.67m), ((double)exact.Totals.Seconds, exact.Totals.Amount!.Value));
        Assert.Equal((3.75 * 3600, 415m), ((double)roundedUp.Totals.Seconds, roundedUp.Totals.Amount!.Value));
    }

    [Fact]
    public async Task Days_are_counted_in_the_callers_time_zone()
    {
        var admin = await TestUser.RegisterAsync(factory);
        // 23:30 on Monday and 00:30 on Tuesday in Zagreb are both Monday in UTC.
        await admin.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Late", start = At(0, 23, 30), stop = At(0, 23, 45) });
        await admin.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Early", start = At(1, 0, 30), stop = At(1, 1) });
        // A timer that still runs is left out until it stops.
        await admin.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Running", start = DateTimeOffset.UtcNow.AddMinutes(-5) });

        var weekly = await admin.CreateAsync<WeeklyReport>("/reports/weekly", Week(new { groupBy = "description" }));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var withToday = await admin.CreateAsync<SummaryReport>("/reports/summary", new { from = today.AddDays(-1), to = today.AddDays(1) });

        Assert.Equal([900L, 1800L, 0, 0, 0, 0, 0], weekly.DayTotals);
        Assert.Equal(0, withToday.Totals.EntryCount);
    }

    [Fact]
    public async Task The_detailed_report_lists_entries_a_page_at_a_time()
    {
        var team = await ArrangeAsync();

        var firstPage = await team.Admin.CreateAsync<DetailedReport>("/reports/detailed", Week(new { sort = "duration", descending = false, pageSize = 2 }));
        var lastPage = await team.Admin.CreateAsync<DetailedReport>("/reports/detailed", Week(new { sort = "duration", descending = false, pageSize = 2, page = 2 }));

        Assert.Equal((4, 1, 2), (firstPage.TotalCount, firstPage.Page, firstPage.Entries.Count));
        Assert.Equal([1800L, 3600L], firstPage.Entries.Select(e => e.Seconds));
        Assert.Equal([5400L, 7200L], lastPage.Entries.Select(e => e.Seconds));
        var longest = lastPage.Entries[1];
        Assert.Equal(
            ("Ana", "Planning", "Website", "Acme", "Wireframes", 240m),
            (longest.UserName, longest.Description, longest.ProjectName, longest.ClientName, longest.TaskName, longest.Amount!.Value));
        Assert.Equal(["meeting"], longest.Tags);
        Assert.Equal(At(0, 9), longest.Start);
        Assert.Equal(new ReportTotals(5 * 3600, 4 * 3600, 425m, 40m, 4, 4 * 3600), firstPage.Totals);
    }

    [Fact]
    public async Task The_weekly_report_has_a_row_per_group_and_a_column_per_day()
    {
        var team = await ArrangeAsync();

        var weekly = await team.Admin.CreateAsync<WeeklyReport>("/reports/weekly", Week(new { groupBy = "user" }));

        Assert.Equal(Enumerable.Range(0, 7).Select(Monday.AddDays), weekly.Days);
        Assert.Equal([3 * 3600L, 2 * 3600L, 0, 0, 0, 0, 0], weekly.DayTotals);
        Assert.Equal(["Ana", "Ivo"], weekly.Rows.Select(r => r.Name));
        Assert.Equal([3 * 3600L, 0, 0, 0, 0, 0, 0], weekly.Rows[0].Days);
        Assert.Equal((2 * 3600L, 185m), (weekly.Rows[1].Seconds, weekly.Rows[1].Amount!.Value));
    }

    [Fact]
    public async Task Longer_periods_are_charted_by_week_or_month()
    {
        var admin = await TestUser.RegisterAsync(factory);

        var quarter = await admin.CreateAsync<SummaryReport>("/reports/summary", new { from = new DateOnly(2026, 7, 1), to = new DateOnly(2026, 9, 30) });
        var year = await admin.CreateAsync<SummaryReport>("/reports/summary", new { from = new DateOnly(2026, 1, 1), to = new DateOnly(2026, 12, 31) });

        Assert.Equal((ReportResolution.Week, new DateOnly(2026, 6, 29)), (quarter.Resolution, quarter.Buckets[0].Start));
        Assert.Equal(14, quarter.Buckets.Count);
        Assert.Equal((ReportResolution.Month, 12), (year.Resolution, year.Buckets.Count));
    }

    [Fact]
    public async Task Invalid_periods_and_rounding_are_refused()
    {
        var admin = await TestUser.RegisterAsync(factory);

        var backwards = await admin.PostAsync("/reports/summary", new { from = Monday, to = Monday.AddDays(-1) });
        var tooLong = await admin.PostAsync("/reports/detailed", new { from = Monday, to = Monday.AddDays(366) });
        var longWeek = await admin.PostAsync("/reports/weekly", new { from = Monday, to = Monday.AddDays(31) });
        var oddRounding = await admin.PostAsync("/reports/summary", new { from = Monday, to = Monday, rounding = "up", roundingMinutes = 7 });
        var noPeriod = await admin.PostAsync("/reports/summary", new { });
        var hugePage = await admin.PostAsync("/reports/detailed", new { from = Monday, to = Monday, pageSize = 1000 });

        Assert.Equal(["to"], await TestUser.ErrorFieldsAsync(backwards));
        Assert.Equal(["to"], await TestUser.ErrorFieldsAsync(tooLong));
        Assert.Equal(["to"], await TestUser.ErrorFieldsAsync(longWeek));
        Assert.Equal(["roundingMinutes"], await TestUser.ErrorFieldsAsync(oddRounding));
        Assert.Equal(["from", "to"], await TestUser.ErrorFieldsAsync(noPeriod));
        Assert.Equal(["pageSize"], await TestUser.ErrorFieldsAsync(hugePage));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/reports/weekly", new { from = Monday, to = Monday.AddDays(30) })).StatusCode);
    }

    [Fact]
    public async Task Reports_export_as_csv()
    {
        var team = await ArrangeAsync();
        await team.Admin.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "=HYPERLINK(\"x\")", start = At(2, 9), stop = At(2, 9, 30) });

        var response = await team.Admin.PostAsync("/reports/detailed/export?format=csv", Week(new { sort = "date", descending = false }));
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        var lines = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var summary = await team.Admin.PostAsync("/reports/summary/export?format=csv", Week(new { groupBy = "project", subGroupBy = "user" }));
        var summaryLines = (await summary.Content.ReadAsStringAsync(Ct)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var byMember = await team.Member.PostAsync("/reports/detailed/export?format=csv", Week(new { }));

        Assert.Equal("text/csv; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("spindle-detailed-2026-09-14-to-2026-09-20.csv", response.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("Date,Start,End,Duration,Hours,Member,Project,Client,Task,Description,Tags,Billable,Amount (EUR),Labor cost (EUR)", lines[0]);
        Assert.Equal("2026-09-14,09:00:00,11:00:00,2:00:00,2.00,Ana,Website,Acme,Wireframes,Planning,meeting,Yes,240.00,", lines[1]);
        Assert.Equal("2026-09-15,09:00:00,10:30:00,1:30:00,1.50,Ivo,Website,Acme,,Design,,Yes,150.00,30.00", lines[3]);
        Assert.Equal("2026-09-16,09:00:00,09:30:00,0:30:00,0.50,Ana,,,,\"'=HYPERLINK(\"\"x\"\")\",,No,,", lines[^1]);
        Assert.Equal(6, lines.Length);
        Assert.Equal(
            ["Project,Client,Member,Duration,Hours,Amount (EUR),Labor cost (EUR)", "Website,Acme,,3:30:00,3.50,390.00,30.00", "Website,Acme,Ana,2:00:00,2.00,240.00,0.00"],
            summaryLines[..3]);
        Assert.DoesNotContain("Amount", (await byMember.Content.ReadAsStringAsync(Ct)).Split("\r\n")[0]);
    }

    [Fact]
    public async Task Reports_export_as_excel_with_real_numbers()
    {
        var team = await ArrangeAsync();

        var response = await team.Admin.PostAsync("/reports/weekly/export?format=xlsx", Week(new { groupBy = "user" }));
        using var workbook = new ClosedXML.Excel.XLWorkbook(await response.Content.ReadAsStreamAsync(Ct));
        var sheet = workbook.Worksheet(1);

        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(("Weekly", "Weekly report"), (sheet.Name, sheet.Cell(1, 1).GetString()));
        var english = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
        Assert.Equal(
            ["Member", Monday.ToString("ddd d MMM", english), Monday.AddDays(1).ToString("ddd d MMM", english)],
            Enumerable.Range(1, 3).Select(c => sheet.Cell(5, c).GetString()));
        Assert.Equal("Ana", sheet.Cell(6, 1).GetString());
        Assert.Equal(TimeSpan.FromHours(3), sheet.Cell(6, 2).GetTimeSpan());
        Assert.Equal("[h]:mm:ss", sheet.Cell(6, 2).Style.NumberFormat.Format);
        Assert.Equal(185d, sheet.Cell(7, 10).GetDouble());
        Assert.Equal(("Total", TimeSpan.FromHours(5)), (sheet.Cell(8, 1).GetString(), sheet.Cell(8, 9).GetTimeSpan()));
    }

    [Fact]
    public async Task Reports_export_as_pdf()
    {
        var team = await ArrangeAsync();

        var summary = await team.Admin.PostAsync("/reports/summary/export?format=pdf", Week(new { groupBy = "client", subGroupBy = "project" }));
        var detailed = await team.Admin.PostAsync("/reports/detailed/export?format=pdf", Week(new { }));
        var bytes = await summary.Content.ReadAsByteArrayAsync(Ct);
        Assert.True(summary.IsSuccessStatusCode, System.Text.Encoding.UTF8.GetString(bytes));

        Assert.Equal("application/pdf", summary.Content.Headers.ContentType!.MediaType);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.Contains("Geist", System.Text.Encoding.Latin1.GetString(bytes));
        Assert.Equal(HttpStatusCode.OK, detailed.StatusCode);
        Assert.Equal("spindle-detailed-2026-09-14-to-2026-09-20.pdf", detailed.Content.Headers.ContentDisposition!.FileNameStar);
    }

    [Fact]
    public async Task People_save_reports_for_themselves()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);

        var saved = await admin.CreateAsync<SavedReportResponse>("/reports/saved", new { name = "Monthly billing", kind = "summary", parameters = """{"period":"lastMonth","groupBy":"client"}""" });
        var duplicate = await admin.PostAsync("/reports/saved", new { name = "monthly billing", kind = "detailed", parameters = "{}" });
        var notAnObject = await admin.PostAsync("/reports/saved", new { name = "Broken", kind = "summary", parameters = "[1]" });
        var changed = await TestUser.ReadAsync<SavedReportResponse>(
            await admin.PutAsync($"/reports/saved/{saved.Id}", new { name = "Billing", kind = "weekly", parameters = """{"period":"thisMonth"}""" }));
        var seenByMember = await member.GetAsync<List<SavedReportResponse>>("/reports/saved");
        var deletedByMember = await member.DeleteAsync($"/reports/saved/{saved.Id}");

        Assert.Equal((ReportKind.Summary, """{"period":"lastMonth","groupBy":"client"}"""), (saved.Kind, saved.Parameters));
        Assert.Equal(["name"], await TestUser.ErrorFieldsAsync(duplicate));
        Assert.Equal(["parameters"], await TestUser.ErrorFieldsAsync(notAnObject));
        Assert.Equal(("Billing", ReportKind.Summary, """{"period":"thisMonth"}"""), (changed.Name, changed.Kind, changed.Parameters));
        Assert.Empty(seenByMember);
        Assert.Equal(HttpStatusCode.NotFound, deletedByMember.StatusCode);

        (await admin.DeleteAsync($"/reports/saved/{saved.Id}")).EnsureSuccessStatusCode();
        Assert.Empty(await admin.GetAsync<List<SavedReportResponse>>("/reports/saved"));
    }

    private static object Week(object extra)
    {
        var body = extra.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(extra));
        body.TryAdd("from", Monday);
        body.TryAdd("to", Monday.AddDays(6));
        return body;
    }

    private sealed record Team(TestUser Admin, TestUser Member, ProjectResponse Website, GroupResponse Designers, TagResponse Meeting);

    /// <summary>
    /// Ana (admin, rate 120 on Website) and Ivo (rate 70, labor cost 20) track time in a workspace with a default
    /// rate of 50, on Website (Acme, rate 100) and Internal (no client, no rate).
    /// </summary>
    private async Task<Team> ArrangeAsync()
    {
        var admin = await TestUser.RegisterAsync(factory, $"ana-{Guid.NewGuid():N}@example.com", fullName: "Ana");
        var member = await factory.AddMemberAsync(admin, "Ivo");
        await admin.UpdateSettingsAsync(s => s["defaultHourlyRate"] = 50m);
        (await admin.PutAsync($"/members/{member.UserId}", new { role = "member", hourlyRate = 70m, laborCost = 20m })).EnsureSuccessStatusCode();

        var acme = await admin.CreateAsync<ClientResponse>("/clients", new { name = "Acme" });
        var website = await admin.CreateAsync<ProjectResponse>("/projects", new { name = "Website", color = "#0B83D9", clientId = acme.Id, isBillable = true, hourlyRate = 100m });
        var internalWork = await admin.CreateAsync<ProjectResponse>("/projects", new { name = "Internal", color = "#9E5BD9" });
        (await admin.PutAsync($"/projects/{website.Id}/members/{admin.UserId}", new { isManager = true, hourlyRate = 120m })).EnsureSuccessStatusCode();
        var task = await admin.CreateAsync<TaskResponse>($"/projects/{website.Id}/tasks", new { name = "Wireframes" });
        var meeting = await admin.CreateAsync<TagResponse>("/tags", new { name = "meeting" });
        var designers = await admin.CreateAsync<GroupResponse>("/groups", new { name = "Designers", memberIds = new[] { member.UserId } });

        await admin.CreateAsync<TimeEntryResponse>("/time-entries", new
        {
            description = "Planning", projectId = website.Id, taskId = task.Id, tagIds = new[] { meeting.Id }, isBillable = true, start = At(0, 9), stop = At(0, 11),
        });
        await admin.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Admin", projectId = internalWork.Id, isBillable = false, start = At(0, 13), stop = At(0, 14) });
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Design", projectId = website.Id, isBillable = true, start = At(1, 9), stop = At(1, 10, 30) });
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Support", isBillable = true, start = At(1, 11), stop = At(1, 11, 30) });

        // Outside the week: the Sunday before, and the Monday after.
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Before", start = At(-1, 9), stop = At(-1, 10) });
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "After", start = At(7, 9), stop = At(7, 10) });

        return new Team(admin, member, website, designers, meeting);
    }
}
