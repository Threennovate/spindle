using System.Net;
using Spindle.Api.Billing;
using Spindle.Api.Projects;
using Spindle.Api.Reports;
using Spindle.Api.Team;
using Spindle.Api.TimeEntries;
using Spindle.Api.Workspaces;
using Spindle.Domain.Projects;

namespace Spindle.Tests.Integration;

/// <summary>Rates that change over time, and time billed separately from the time tracked.</summary>
[Collection(PostgresCollection.Name)]
public sealed class BillingTests(PostgresApiFactory factory)
{
    // A past week, Monday 14 to Sunday 20 September 2026. Test users live in Europe/Zagreb (UTC+2 then).
    private static readonly DateOnly Monday = new(2026, 9, 14);

    /// <summary>An instant in Zagreb on a day of the test week.</summary>
    private static DateTimeOffset At(int day, int hours, int minutes = 0) =>
        new DateTimeOffset(Monday.AddDays(day), new TimeOnly(hours, minutes), TimeSpan.FromHours(2));

    [Fact]
    public async Task A_rate_changed_from_a_day_on_leaves_earlier_time_at_the_old_rate()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var project = await admin.CreateAsync<ProjectResponse>("/projects", new { name = "Website", color = "#0B83D9", isBillable = true, hourlyRate = 100m });
        await admin.CreateAsync<TimeEntryResponse>("/time-entries", new { projectId = project.Id, isBillable = true, start = At(0, 9), stop = At(0, 10) });
        await admin.CreateAsync<TimeEntryResponse>("/time-entries", new { projectId = project.Id, isBillable = true, start = At(2, 9), stop = At(2, 10) });

        var raised = await admin.PutAsync($"/projects/{project.Id}", new { name = "Website", color = "#0B83D9", isBillable = true, hourlyRate = 120m, hourlyRateFrom = Monday.AddDays(2) });
        var weekly = await admin.CreateAsync<WeeklyReport>("/reports/weekly", Week(new { groupBy = "project" }));
        var history = await admin.GetAsync<List<RatePeriodResponse>>($"/rates?kind=billable&projectId={project.Id}");

        Assert.Equal(120m, (await TestUser.ReadAsync<ProjectResponse>(raised)).HourlyRate);
        Assert.Equal(220m, weekly.Totals.Amount);
        Assert.Equal([(Monday.AddDays(2), 120m), ((DateOnly?)null, 100m)], history.Select(p => (p.EffectiveFrom, p.Amount!.Value)));

        // Without a date the current rate is corrected, for all the time it covers.
        (await admin.PutAsync($"/projects/{project.Id}", new { name = "Website", color = "#0B83D9", isBillable = true, hourlyRate = 130m })).EnsureSuccessStatusCode();
        Assert.Equal(230m, (await admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { }))).Totals.Amount);

        // Removing a period lets the one before it run on.
        (await admin.DeleteAsync($"/rates/{history[0].Id}")).EnsureSuccessStatusCode();
        Assert.Equal(200m, (await admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { }))).Totals.Amount);
        Assert.Equal(100m, (await admin.GetAsync<ProjectResponse>($"/projects/{project.Id}")).HourlyRate);
    }

    [Fact]
    public async Task Labor_costs_and_member_rates_have_a_history_too()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin, "Ivo");
        (await admin.PutAsync($"/members/{member.UserId}", new { role = "member", hourlyRate = 70m, laborCost = 20m })).EnsureSuccessStatusCode();
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { isBillable = true, start = At(0, 9), stop = At(0, 10) });
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { isBillable = true, start = At(3, 9), stop = At(3, 10) });

        // Unchanged rates sent along with a changed one start no new period.
        (await admin.PutAsync($"/members/{member.UserId}", new
        {
            role = "member", hourlyRate = 70m, hourlyRateFrom = Monday.AddDays(3), laborCost = 25m, laborCostFrom = Monday.AddDays(3),
        })).EnsureSuccessStatusCode();
        var totals = (await admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { }))).Totals;
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2);
        var future = await admin.PutAsync($"/members/{member.UserId}", new { role = "member", laborCost = 30m, laborCostFrom = tomorrow });

        Assert.Equal((140m, 45m), (totals.Amount!.Value, totals.LaborCost!.Value));
        Assert.Single(await admin.GetAsync<List<RatePeriodResponse>>($"/rates?kind=billable&userId={member.UserId}"));
        Assert.Equal(2, (await admin.GetAsync<List<RatePeriodResponse>>($"/rates?kind=laborCost&userId={member.UserId}")).Count);
        Assert.Equal(["laborCostFrom"], await TestUser.ErrorFieldsAsync(future));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.GetAsync(member.In("/rates"), TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Leaving_a_project_ends_a_rate_there_without_changing_the_time_before()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin, "Ivo");
        await admin.UpdateSettingsAsync(s => s["defaultHourlyRate"] = 50m);
        var project = await admin.CreateAsync<ProjectResponse>("/projects", new { name = "Website", color = "#0B83D9", isBillable = true });
        (await admin.PutAsync($"/projects/{project.Id}/members/{member.UserId}", new { hourlyRate = 90m })).EnsureSuccessStatusCode();
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { projectId = project.Id, isBillable = true, start = At(0, 9), stop = At(0, 10) });

        (await admin.DeleteAsync($"/projects/{project.Id}/members/{member.UserId}")).EnsureSuccessStatusCode();
        var history = await admin.GetAsync<List<RatePeriodResponse>>($"/rates?projectId={project.Id}&userId={member.UserId}");

        Assert.Equal(90m, (await admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { }))).Totals.Amount);
        Assert.Equal([null, 90m], history.Select(p => p.Amount));
        Assert.Equal(50m, (await admin.GetAsync<WorkspaceSettingsResponse>("/settings")).DefaultHourlyRate);
    }

    [Fact]
    public async Task Managers_enter_the_time_billed_and_reports_bill_it()
    {
        var team = await ArrangeAsync();

        // Ivo tracked 1:30 on Monday and 2:00 on Tuesday; the client is billed 2:00 and 0:00, plus 1:00 on Wednesday.
        await team.Manager.PutAsync($"/projects/{team.Project.Id}/billing/{team.Member.UserId}/{Monday:yyyy-MM-dd}", new { seconds = 7200 });
        await team.Manager.PutAsync($"/projects/{team.Project.Id}/billing/{team.Member.UserId}/{Monday.AddDays(1):yyyy-MM-dd}", new { seconds = 0 });
        var wednesday = await team.Manager.PutAsync($"/projects/{team.Project.Id}/billing/{team.Member.UserId}/{Monday.AddDays(2):yyyy-MM-dd}", new { seconds = 3600 });

        var billing = await team.Manager.GetAsync<ProjectBillingResponse>($"/projects/{team.Project.Id}/billing?from={Monday:yyyy-MM-dd}&to={Monday.AddDays(6):yyyy-MM-dd}");
        var summary = await team.Admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { groupBy = "user", subGroupBy = "description" }));
        var detailed = await team.Admin.CreateAsync<DetailedReport>("/reports/detailed", Week(new { }));
        var weekly = await team.Admin.CreateAsync<WeeklyReport>("/reports/weekly", Week(new { groupBy = "project" }));

        Assert.Equal(HttpStatusCode.NoContent, wednesday.StatusCode);
        var ivo = billing.Rows.Single(r => r.UserId == team.Member.UserId);
        Assert.Equal([5400L, 7200, 0, 0, 0, 0, 0], ivo.TrackedDays);
        Assert.Equal([7200L, 0, 3600, null, null, null, null], ivo.BilledDays);
        Assert.Equal((4 * 3600L, 3 * 3600L), (ivo.TrackedSeconds, ivo.BilledSeconds));
        Assert.Null(ivo.Amount);

        // Tracked time stays as tracked; billed time and money follow what was entered. Ivo is billed at 100.
        Assert.Equal((4 * 3600L, 3 * 3600L, 300m, 3), (summary.Totals.Seconds, summary.Totals.BilledSeconds!.Value, summary.Totals.Amount!.Value, summary.Totals.EntryCount));
        var ivoGroup = summary.Groups.Single(g => g.Id == team.Member.UserId);
        Assert.Equal((3 * 3600L, 300m), (ivoGroup.BilledSeconds!.Value, ivoGroup.Amount!.Value));
        // Monday +0:30, Tuesday −2:00, Wednesday +1:00.
        var adjustment = ivoGroup.SubGroups.Single(g => g.Key == "billed");
        Assert.Equal(("Billed time adjustment", 0L, -1800L, -50m), (adjustment.Name, adjustment.Seconds, adjustment.BilledSeconds!.Value, adjustment.Amount!.Value));
        Assert.Equal(3, detailed.TotalCount);
        Assert.Equal(300m, detailed.Totals.Amount);
        Assert.Equal(300m, weekly.Totals.Amount);
    }

    [Fact]
    public async Task Billed_time_counts_only_where_it_is_entered_separately()
    {
        var team = await ArrangeAsync();
        await team.Manager.PutAsync($"/projects/{team.Project.Id}/billing/{team.Member.UserId}/{Monday:yyyy-MM-dd}", new { seconds = 7200 });

        var filtered = await team.Admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { description = "Design" }));
        (await team.Manager.PutAsync($"/projects/{team.Project.Id}", new { name = "Website", color = "#0B83D9", isBillable = true, separateBilledTime = false })).EnsureSuccessStatusCode();
        var asTracked = await team.Admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { }));
        var refused = await team.Manager.PutAsync($"/projects/{team.Project.Id}/billing/{team.Member.UserId}/{Monday:yyyy-MM-dd}", new { seconds = 3600 });

        Assert.Equal((5400L, 150m), (filtered.Totals.BilledSeconds!.Value, filtered.Totals.Amount!.Value));
        Assert.Equal((5400L + 7200, 350m), (asTracked.Totals.BilledSeconds!.Value, asTracked.Totals.Amount!.Value));
        Assert.Equal(["separateBilledTime"], await TestUser.ErrorFieldsAsync(refused));
    }

    [Fact]
    public async Task Only_admins_and_managers_enter_billed_time_and_only_admins_see_money()
    {
        var team = await ArrangeAsync();
        var path = $"/projects/{team.Project.Id}/billing/{team.Member.UserId}/{Monday:yyyy-MM-dd}";
        await team.Admin.UpdateSettingsAsync(s => s["lockedBefore"] = Monday.AddDays(1).ToString("yyyy-MM-dd"));

        var byMember = await team.Member.PutAsync(path, new { seconds = 3600 });
        var memberReads = await team.Member.Client.GetAsync(team.Member.In($"/projects/{team.Project.Id}/billing?from={Monday:yyyy-MM-dd}&to={Monday:yyyy-MM-dd}"), TestContext.Current.CancellationToken);
        var lockedForManager = await team.Manager.PutAsync(path, new { seconds = 3600 });
        var byAdmin = await team.Admin.PutAsync(path, new { seconds = 3600 });
        var tooLong = await team.Admin.PutAsync(path, new { seconds = 25 * 3600 });
        var stranger = await team.Admin.PutAsync($"/projects/{team.Project.Id}/billing/{Guid.NewGuid()}/{Monday:yyyy-MM-dd}", new { seconds = 3600 });
        var asAdmin = await team.Admin.GetAsync<ProjectBillingResponse>($"/projects/{team.Project.Id}/billing?from={Monday:yyyy-MM-dd}&to={Monday.AddDays(6):yyyy-MM-dd}");
        var memberReport = await team.Member.CreateAsync<SummaryReport>("/reports/summary", Week(new { }));
        (await team.Admin.DeleteAsync(path)).EnsureSuccessStatusCode();
        var cleared = await team.Admin.GetAsync<ProjectBillingResponse>($"/projects/{team.Project.Id}/billing?from={Monday:yyyy-MM-dd}&to={Monday:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberReads.StatusCode);
        Assert.Equal(["day"], await TestUser.ErrorFieldsAsync(lockedForManager));
        Assert.Equal(HttpStatusCode.NoContent, byAdmin.StatusCode);
        Assert.Equal(["seconds"], await TestUser.ErrorFieldsAsync(tooLong));
        Assert.Equal(["userId"], await TestUser.ErrorFieldsAsync(stranger));
        var ivo = asAdmin.Rows.Single(r => r.UserId == team.Member.UserId);
        Assert.Equal((300m, 80m), (ivo.Amount!.Value, ivo.LaborCost!.Value));
        Assert.Null(memberReport.Totals.BilledSeconds);
        Assert.Null(cleared.Rows.Single(r => r.UserId == team.Member.UserId).BilledDays[0]);
    }

    [Fact]
    public async Task Projects_billed_by_the_day_bill_a_full_day_wherever_someone_worked()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin, "Ivo");
        var body = new { name = "Retainer", color = "#0B83D9", isBillable = true, hourlyRate = 50m, billingMode = "fullDay", dailyHours = 8m };
        var project = await admin.CreateAsync<ProjectResponse>("/projects", body);

        // Monday billable, Tuesday only non-billable time, Saturday a weekend day; Friday entered by hand.
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { projectId = project.Id, isBillable = true, start = At(0, 9), stop = At(0, 12) });
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { projectId = project.Id, isBillable = false, start = At(1, 9), stop = At(1, 10) });
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { projectId = project.Id, isBillable = true, start = At(5, 9), stop = At(5, 11) });
        (await admin.PutAsync($"/projects/{project.Id}/billing/{member.UserId}/{Monday.AddDays(4):yyyy-MM-dd}", new { seconds = 4 * 3600 })).EnsureSuccessStatusCode();

        var billing = await admin.GetAsync<ProjectBillingResponse>($"/projects/{project.Id}/billing?from={Monday:yyyy-MM-dd}&to={Monday.AddDays(6):yyyy-MM-dd}");
        var summary = await admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { }));

        var ivo = billing.Rows.Single(r => r.UserId == member.UserId);
        Assert.Equal((BillingMode.FullDay, 8m), (project.BillingMode, project.DailyHours!.Value));
        Assert.Equal([8 * 3600L, 8 * 3600, 0, 0, 0, 2 * 3600, 0], ivo.DefaultDays);
        Assert.Equal((22 * 3600L, 1100m), (ivo.BilledSeconds, ivo.Amount!.Value));
        Assert.Equal((6 * 3600L, 22 * 3600L, 1100m), (summary.Totals.Seconds, summary.Totals.BilledSeconds!.Value, summary.Totals.Amount!.Value));

        // A shorter day from Tuesday on leaves Monday as it was billed.
        var shorter = await admin.PutAsync($"/projects/{project.Id}", new { body.name, body.color, body.isBillable, body.hourlyRate, body.billingMode, dailyHours = 6m, dailyHoursFrom = Monday.AddDays(1) });
        var withoutHours = await admin.PutAsync($"/projects/{project.Id}", new { body.name, body.color, body.billingMode });
        // Apps that predate the billing mode leave it, and the daily hours, as they are.
        var byOldApp = await TestUser.ReadAsync<ProjectResponse>(await admin.PutAsync($"/projects/{project.Id}", new { body.name, body.color, body.isBillable, body.hourlyRate }));

        Assert.Equal(6m, (await TestUser.ReadAsync<ProjectResponse>(shorter)).DailyHours);
        Assert.Equal(20 * 3600L, (await admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { }))).Totals.BilledSeconds);
        Assert.Equal(2, (await admin.GetAsync<List<RatePeriodResponse>>($"/rates?kind=dailyHours&projectId={project.Id}")).Count);
        Assert.Equal(["dailyHours"], await TestUser.ErrorFieldsAsync(withoutHours));
        Assert.Equal((BillingMode.FullDay, 6m, true), (byOldApp.BillingMode, byOldApp.DailyHours!.Value, byOldApp.SeparateBilledTime));
    }

    private static object Week(object extra)
    {
        var body = extra.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(extra));
        body.TryAdd("from", Monday);
        body.TryAdd("to", Monday.AddDays(6));
        return body;
    }

    private sealed record Team(TestUser Admin, TestUser Manager, TestUser Member, ProjectResponse Project);

    /// <summary>
    /// Website (rate 100) has its billed time entered separately. Maja manages it; Ivo (labor cost 20) tracked 1:30 on
    /// Monday and 2:00 on Tuesday there, and 0:30 that is not billable.
    /// </summary>
    private async Task<Team> ArrangeAsync()
    {
        var admin = await TestUser.RegisterAsync(factory, $"ana-{Guid.NewGuid():N}@example.com", fullName: "Ana");
        var manager = await factory.AddMemberAsync(admin, "Maja");
        var member = await factory.AddMemberAsync(admin, "Ivo");
        (await admin.PutAsync($"/members/{member.UserId}", new { role = "member", laborCost = 20m })).EnsureSuccessStatusCode();
        var project = await admin.CreateAsync<ProjectResponse>("/projects", new { name = "Website", color = "#0B83D9", isBillable = true, hourlyRate = 100m, separateBilledTime = true });
        (await admin.PutAsync($"/projects/{project.Id}/members/{manager.UserId}", new { isManager = true })).EnsureSuccessStatusCode();

        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Design", projectId = project.Id, isBillable = true, start = At(0, 9), stop = At(0, 10, 30) });
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Build", projectId = project.Id, isBillable = true, start = At(1, 9), stop = At(1, 11) });
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Chat", projectId = project.Id, isBillable = false, start = At(1, 13), stop = At(1, 13, 30) });

        Assert.True(project.SeparateBilledTime);
        return new Team(admin, manager, member, project);
    }
}
