using System.Net;
using Spindle.Api.Billing;
using Spindle.Api.Projects;
using Spindle.Api.Reports;
using Spindle.Api.TimeEntries;
using Spindle.Domain.Billing;

namespace Spindle.Tests.Integration;

/// <summary>Fixed prices and retainers agreed with clients, and how profitable they are against labor costs.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AgreementFlowTests(PostgresApiFactory factory)
{
    // A past week, Monday 14 to Sunday 20 September 2026. Test users live in Europe/Zagreb (UTC+2 then).
    private static readonly DateOnly Monday = new(2026, 9, 14);

    private static DateTimeOffset At(int day, int hours, int minutes = 0) =>
        new DateTimeOffset(Monday.AddDays(day), new TimeOnly(hours, minutes), TimeSpan.FromHours(2));

    [Fact]
    public async Task A_fixed_price_is_paid_in_full_when_done_and_time_on_it_is_not_billed_by_the_hour()
    {
        var team = await ArrangeAsync();
        var website = await team.Admin.CreateAsync<AgreementResponse>($"/projects/{team.Project.Id}/agreements", new
        {
            name = "Website build", kind = "fixedPrice", amount = 2400m, hours = 40m, startsOn = Monday.AddDays(-30),
        });
        var task = Assert.Single(website.TaskIds);

        await Track(team.Member, team.Project.Id, task, day: 0, hours: 2);
        await Track(team.Member, team.Project.Id, null, day: 1, hours: 1);
        var inProgress = await team.Admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { }));

        var done = await TestUser.ReadAsync<AgreementResponse>(await team.Admin.PutAsync($"/projects/{team.Project.Id}/agreements/{website.Id}", new
        {
            name = "Website build", kind = "fixedPrice", amount = 2400m, hours = 40m, startsOn = Monday.AddDays(-30), endsOn = Monday.AddDays(2),
        }));
        var afterDone = await team.Admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { groupBy = "user", subGroupBy = (string?)null }));
        var project = await team.Admin.GetAsync<ProjectResponse>($"/projects/{team.Project.Id}");
        var closed = await team.Member.PostAsync("/time-entries", new { projectId = team.Project.Id, taskId = task, start = At(3, 9), stop = At(3, 10) });
        var profitability = await team.Admin.GetAsync<ProfitabilityResponse>($"/profitability?projectId={team.Project.Id}");

        // Before it is done only the hour outside it is billed: 1 h at 100.
        Assert.Equal((3 * 3600L, 100m, 60m), (inProgress.Totals.Seconds, inProgress.Totals.Amount!.Value, inProgress.Totals.LaborCost!.Value));
        Assert.True(done.IsOver);
        Assert.Equal(2500m, afterDone.Totals.Amount);
        Assert.Equal(2400m, afterDone.Groups.Single(g => g.Key == "agreements").Amount);
        Assert.Equal("Website build", project.Tasks.Single(t => t.Id == task).Name);
        Assert.True(project.Tasks.Single(t => t.Id == task).IsDone);
        Assert.Equal(website.Id, project.Tasks.Single(t => t.Id == task).AgreementId);
        Assert.Equal(["taskId"], await TestUser.ErrorFieldsAsync(closed));

        var result = Assert.Single(profitability.Projects);
        var agreement = Assert.Single(result.Agreements);
        Assert.Equal((2 * 3600L, 2400m, 40m, 2360m), (agreement.TrackedSeconds, agreement.Revenue, agreement.LaborCost, agreement.Profit));
        Assert.Equal((3600L, 100m, 20m), (result.Outside.TrackedSeconds, result.Outside.Amount, result.Outside.LaborCost));
        Assert.Equal((2500m, 60m, 2440m), (result.Revenue, result.LaborCost, result.Profit));

        // Reopened, its task can take time again.
        await team.Admin.PutAsync($"/projects/{team.Project.Id}/agreements/{website.Id}", new { name = "Website build", kind = "fixedPrice", amount = 2400m, startsOn = Monday.AddDays(-30) });
        Assert.False((await team.Admin.GetAsync<ProjectResponse>($"/projects/{team.Project.Id}")).Tasks.Single(t => t.Id == task).IsDone);
    }

    [Fact]
    public async Task A_retainer_is_paid_each_period_and_bills_time_beyond_its_hours_where_agreed()
    {
        var team = await ArrangeAsync();
        var body = new { name = "Maintenance", kind = "retainer", amount = 300m, hours = 2m, period = "month", startsOn = Monday, billsOverage = true };
        var retainer = await team.Admin.CreateAsync<AgreementResponse>($"/projects/{team.Project.Id}/agreements", body);
        var task = Assert.Single(retainer.TaskIds);

        // 1:30, then 1:00 that crosses the 2 hours by 0:30, then 1:00 over them.
        await Track(team.Member, team.Project.Id, task, day: 0, hours: 1, minutes: 30);
        await Track(team.Member, team.Project.Id, task, day: 1, hours: 1);
        await Track(team.Member, team.Project.Id, task, day: 2, hours: 1);
        // It ends with the test week, so its periods stay the same whenever the test runs.
        (await team.Admin.PutAsync($"/projects/{team.Project.Id}/agreements/{retainer.Id}", new { body.name, body.kind, body.amount, body.hours, body.period, body.startsOn, body.billsOverage, endsOn = Monday.AddDays(6) })).EnsureSuccessStatusCode();

        var week = await team.Admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { }));
        var tuesdayOnly = await team.Admin.CreateAsync<SummaryReport>("/reports/summary", new { from = Monday.AddDays(1), to = Monday.AddDays(1) });
        var forIvo = await team.Admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { userIds = new[] { team.Member.UserId } }));
        var detailed = await team.Admin.CreateAsync<DetailedReport>("/reports/detailed", Week(new { }));
        var profitability = await team.Admin.GetAsync<ProfitabilityResponse>($"/profitability?projectId={team.Project.Id}");

        // The fee, and 1:30 beyond the hours at 100.
        Assert.Equal(450m, week.Totals.Amount);
        Assert.Equal(3, detailed.TotalCount);
        // Tuesday's hour is half over the limit, counting Monday's time outside the report.
        Assert.Equal(50m, tuesdayOnly.Totals.Amount);
        // The fee belongs to no one, so a report of someone's time leaves it out.
        Assert.Equal(150m, forIvo.Totals.Amount);

        var agreement = Assert.Single(Assert.Single(profitability.Projects).Agreements);
        var period = Assert.Single(agreement.Periods);
        Assert.Equal((Monday, Monday.AddDays(6)), (period.Start, period.End));
        Assert.Equal((12600L, (long?)7200, 5400L, (decimal?)300m, 150m, 70m), (period.Seconds, period.IncludedSeconds, period.OverSeconds, period.Fee, period.OverageAmount, period.LaborCost));
        Assert.Equal((450m, 150m, 380m), (agreement.Revenue, agreement.OverageAmount, agreement.Profit));

        // Without billing the overage, only the fee is paid.
        (await team.Admin.PutAsync($"/projects/{team.Project.Id}/agreements/{retainer.Id}", new { body.name, body.kind, body.amount, body.hours, body.period, body.startsOn, billsOverage = false, endsOn = Monday.AddDays(6) })).EnsureSuccessStatusCode();
        Assert.Equal(300m, (await team.Admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { }))).Totals.Amount);
    }

    [Fact]
    public async Task A_split_entry_counts_its_part_on_a_retainers_task_towards_the_retainer()
    {
        var team = await ArrangeAsync();
        var internalWork = await team.Admin.CreateAsync<ProjectResponse>("/projects", new { name = "Internal", color = "#9E5BD9", isBillable = false });
        var body = new { name = "Maintenance", kind = "retainer", amount = 300m, hours = 1m, period = "month", startsOn = Monday, billsOverage = true };
        var retainer = await team.Admin.CreateAsync<AgreementResponse>($"/projects/{team.Project.Id}/agreements", body);
        var task = Assert.Single(retainer.TaskIds);

        // Four hours, 40% on the retainer's task. The larger part is internal, so the entry itself has no task.
        await team.Admin.CreateAsync<TimeEntryResponse>("/time-entries", new
        {
            start = At(0, 9),
            stop = At(0, 13),
            shares = new object[]
            {
                new { projectId = internalWork.Id, percent = 60 },
                new { projectId = team.Project.Id, taskId = task, isBillable = true, percent = 40 },
            },
        });
        // It ends with the test week, so its periods stay the same whenever the test runs.
        (await team.Admin.PutAsync($"/projects/{team.Project.Id}/agreements/{retainer.Id}", new { body.name, body.kind, body.amount, body.hours, body.period, body.startsOn, body.billsOverage, endsOn = Monday.AddDays(6) })).EnsureSuccessStatusCode();

        var week = await team.Admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { }));
        var profitability = await team.Admin.GetAsync<ProfitabilityResponse>($"/profitability?projectId={team.Project.Id}");

        // 1:36 on the retainer, 0:36 beyond its hour at 100: the fee and 60.
        Assert.Equal(360m, week.Totals.Amount);
        var period = Assert.Single(Assert.Single(Assert.Single(profitability.Projects).Agreements).Periods);
        Assert.Equal((5760L, 2160L), (period.Seconds, period.OverSeconds));
    }

    [Fact]
    public async Task Earlier_time_moves_to_an_agreement_and_deleting_it_leaves_its_tasks()
    {
        var team = await ArrangeAsync();
        await Track(team.Member, team.Project.Id, null, day: 0, hours: 2);
        await Track(team.Member, team.Project.Id, null, day: 4, hours: 1);
        var design = await team.Admin.CreateAsync<TaskResponse>($"/projects/{team.Project.Id}/tasks", new { name = "Design" });
        var website = await team.Admin.CreateAsync<AgreementResponse>($"/projects/{team.Project.Id}/agreements", new
        {
            name = "Website build", kind = "fixedPrice", amount = 2400m, startsOn = Monday, taskIds = new[] { design.Id },
        });
        Assert.Equal(2, website.TaskIds.Count);
        Assert.Contains(design.Id, website.TaskIds);

        var moved = await team.Admin.CreateAsync<AgreementTimeResponse>($"/projects/{team.Project.Id}/agreements/{website.Id}/time", new { from = Monday, to = Monday.AddDays(2) });
        var summary = await team.Admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { }));
        var deleted = await team.Admin.DeleteAsync($"/projects/{team.Project.Id}/agreements/{website.Id}");
        var project = await team.Admin.GetAsync<ProjectResponse>($"/projects/{team.Project.Id}");

        Assert.Equal(1, moved.Count);
        Assert.Equal(100m, summary.Totals.Amount);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.All(project.Tasks, t => Assert.Null(t.AgreementId));
        Assert.Equal(300m, (await team.Admin.CreateAsync<SummaryReport>("/reports/summary", Week(new { }))).Totals.Amount);
    }

    [Fact]
    public async Task Agreements_are_for_admins_and_checked()
    {
        var team = await ArrangeAsync();
        var path = $"/projects/{team.Project.Id}/agreements";
        var website = await team.Admin.CreateAsync<AgreementResponse>(path, new { name = "Website build", kind = "fixedPrice", amount = 2400m, startsOn = Monday });

        var byMember = await team.Member.PostAsync(path, new { name = "Upkeep", kind = "retainer", amount = 100m, period = "month", startsOn = Monday });
        var memberReads = await team.Member.Client.GetAsync(team.Member.In("/profitability"), TestContext.Current.CancellationToken);
        var sameName = await team.Admin.PostAsync(path, new { name = "website build", kind = "fixedPrice", amount = 100m, startsOn = Monday, createTask = false });
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2);
        var doneTomorrow = await team.Admin.PutAsync($"{path}/{website.Id}", new { name = "Website build", kind = "fixedPrice", amount = 2400m, startsOn = Monday, endsOn = tomorrow });
        var noPeriod = await team.Admin.PostAsync(path, new { name = "Upkeep", kind = "retainer", amount = 100m, startsOn = Monday });
        var otherTask = await team.Admin.PutAsync($"{path}/{website.Id}", new { name = "Website build", kind = "fixedPrice", amount = 2400m, startsOn = Monday, taskIds = new[] { Guid.NewGuid() } });
        var overview = await team.Admin.GetAsync<ProfitabilityResponse>("/profitability");

        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberReads.StatusCode);
        Assert.Equal(["name"], await TestUser.ErrorFieldsAsync(sameName));
        Assert.Equal(["endsOn"], await TestUser.ErrorFieldsAsync(doneTomorrow));
        Assert.Equal(["period"], await TestUser.ErrorFieldsAsync(noPeriod));
        Assert.Equal(["taskIds"], await TestUser.ErrorFieldsAsync(otherTask));
        Assert.Equal("EUR", overview.Currency);
        Assert.Equal(team.Project.Id, Assert.Single(overview.Projects).ProjectId);
    }

    private static object Week(object extra)
    {
        var body = extra.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(extra));
        body.TryAdd("from", Monday);
        body.TryAdd("to", Monday.AddDays(6));
        return body;
    }

    private static Task<TimeEntryResponse> Track(TestUser user, Guid projectId, Guid? taskId, int day, int hours, int minutes = 0) =>
        user.CreateAsync<TimeEntryResponse>("/time-entries", new
        {
            projectId, taskId, isBillable = true, start = At(day, 9), stop = At(day, 9 + hours, minutes),
        });

    private sealed record Team(TestUser Admin, TestUser Member, ProjectResponse Project);

    /// <summary>Medexpert is billed at 100 an hour; Ivo's time costs 20 an hour.</summary>
    private async Task<Team> ArrangeAsync()
    {
        var admin = await TestUser.RegisterAsync(factory, $"ana-{Guid.NewGuid():N}@example.com", fullName: "Ana");
        var member = await factory.AddMemberAsync(admin, "Ivo");
        (await admin.PutAsync($"/members/{member.UserId}", new { role = "member", laborCost = 20m })).EnsureSuccessStatusCode();
        var project = await admin.CreateAsync<ProjectResponse>("/projects", new { name = "Medexpert", color = "#0B83D9", isBillable = true, hourlyRate = 100m });
        return new Team(admin, member, project);
    }
}
