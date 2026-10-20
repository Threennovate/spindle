using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Spindle.Api.Billing;
using Spindle.Api.Projects;
using Spindle.Api.Reports;
using Spindle.Api.TimeEntries;
using Spindle.Domain.Reports;

namespace Spindle.Tests.Integration;

/// <summary>Timesheet links: what a client sees without an account, and how admins manage them.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SharedReportTests(PostgresApiFactory factory)
{
    // A past week, Monday 14 to Sunday 20 September 2026. Test users live in Europe/Zagreb (UTC+2 then).
    private static readonly DateOnly Monday = new(2026, 9, 14);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(int day, int hours, int minutes = 0) =>
        new DateTimeOffset(Monday.AddDays(day), new TimeOnly(hours, minutes), TimeSpan.FromHours(2));

    [Fact]
    public async Task A_link_shows_the_time_billed_by_day_and_nothing_the_client_should_not_see()
    {
        var team = await ArrangeAsync();
        var billing = $"/projects/{team.Website.Id}/billing/{team.Member.UserId}";
        (await team.Admin.PutAsync($"{billing}/{Monday:yyyy-MM-dd}", new { seconds = 7200 })).EnsureSuccessStatusCode();
        (await team.Admin.PutAsync($"{billing}/{Monday.AddDays(1):yyyy-MM-dd}/description", new { description = "Build the checkout" })).EnsureSuccessStatusCode();
        (await team.Admin.PutAsync($"{billing}/{Monday.AddDays(2):yyyy-MM-dd}", new { seconds = 3600 })).EnsureSuccessStatusCode();
        (await team.Admin.PutAsync($"{billing}/{Monday.AddDays(2):yyyy-MM-dd}/description", new { description = "Call with the client" })).EnsureSuccessStatusCode();

        var link = await team.Admin.CreateAsync<SharedReportResponse>("/shared-reports", new
        {
            title = "Acme",
            period = "fixed",
            from = Monday,
            to = Monday.AddDays(6),
            projectIds = new[] { team.Website.Id },
            showAmounts = true,
        });
        var response = await factory.CreateClient().GetAsync(ApiPath(link), Ct);
        var json = await response.Content.ReadAsStringAsync(Ct);
        var timesheet = await TestUser.ReadAsync<SharedTimesheet>(response);

        Assert.Equal(("Acme", Monday, Monday.AddDays(6), false), (timesheet.Title, timesheet.From, timesheet.To, timesheet.CanStepBack));
        // Monday as billed, Tuesday as tracked but described by hand, Wednesday billed with no time tracked, and
        // Thursday's split entry with its 60 % on the project. Tuesday's non-billable chat is left out.
        Assert.Equal(
            [
                (Monday, 7200L, 200m, "Design"),
                (Monday.AddDays(1), 7200L, 200m, "Build the checkout"),
                (Monday.AddDays(2), 3600L, 100m, "Call with the client"),
                (Monday.AddDays(3), 4320L, 120m, "Review"),
            ],
            timesheet.Days.Select(d => (d.Day, d.Seconds, d.Amount!.Value, string.Join(", ", d.Descriptions))));
        Assert.Equal((7200L * 2 + 3600 + 4320, 620m), (timesheet.Seconds, timesheet.Amount!.Value));
        Assert.All(timesheet.Days, day => Assert.Null(day.Person));
        // Not the tracked time, the other project, the chat, the person, or where the rest of the split went.
        Assert.DoesNotContain("Research", json);
        Assert.DoesNotContain("Chat", json);
        Assert.DoesNotContain("Ivo", json);

        var pdf = await factory.CreateClient().GetAsync($"{ApiPath(link)}/export?format=pdf", Ct);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        var csv = await (await factory.CreateClient().GetAsync($"{ApiPath(link)}/export?format=csv", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("Call with the client", csv);
        // Durations stay durations in the files, not a count of seconds.
        Assert.Contains("1:12:00", csv);
        Assert.DoesNotContain("4320", csv);
    }

    [Fact]
    public async Task Days_described_by_hand_show_in_the_billing_card_until_cleared()
    {
        var team = await ArrangeAsync();
        var billing = $"/projects/{team.Website.Id}/billing/{team.Member.UserId}";
        var week = $"/projects/{team.Website.Id}/billing?from={Monday:yyyy-MM-dd}&to={Monday.AddDays(6):yyyy-MM-dd}";
        (await team.Manager.PutAsync($"{billing}/{Monday:yyyy-MM-dd}/description", new { description = "  Workshop  " })).EnsureSuccessStatusCode();

        var described = (await team.Manager.GetAsync<ProjectBillingResponse>(week)).Rows.Single(r => r.UserId == team.Member.UserId);
        var tooLong = await team.Manager.PutAsync($"{billing}/{Monday:yyyy-MM-dd}/description", new { description = new string('x', 2001) });
        var byMember = await team.Member.PutAsync($"{billing}/{Monday:yyyy-MM-dd}/description", new { description = "Mine" });
        (await team.Manager.DeleteAsync($"{billing}/{Monday:yyyy-MM-dd}/description")).EnsureSuccessStatusCode();
        var cleared = (await team.Manager.GetAsync<ProjectBillingResponse>(week)).Rows.Single(r => r.UserId == team.Member.UserId);

        Assert.Equal("Workshop", described.Descriptions[0]);
        Assert.Equal(["Design"], described.EntryDescriptions[0]);
        // A description alone bills the day as tracked.
        Assert.Null(described.BilledDays[0]);
        Assert.Equal(["Build"], described.EntryDescriptions[1]);
        Assert.Equal(["description"], await TestUser.ErrorFieldsAsync(tooLong));
        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.Null(cleared.Descriptions[0]);
    }

    [Fact]
    public async Task A_link_narrowed_within_a_day_shows_only_its_entries_descriptions()
    {
        var team = await ArrangeAsync();
        var billing = $"/projects/{team.Website.Id}/billing/{team.Member.UserId}";
        (await team.Admin.PutAsync($"{billing}/{Monday:yyyy-MM-dd}/description", new { description = "Design, and the internal audit" })).EnsureSuccessStatusCode();

        var link = await team.Admin.CreateAsync<SharedReportResponse>("/shared-reports", new
        {
            title = "Acme",
            period = "fixed",
            from = Monday,
            to = Monday.AddDays(6),
            projectIds = new[] { team.Website.Id },
            description = "Design",
        });
        var response = await factory.CreateClient().GetAsync(ApiPath(link), Ct);
        var json = await response.Content.ReadAsStringAsync(Ct);
        var timesheet = await TestUser.ReadAsync<SharedTimesheet>(response);

        // The day's text speaks of work the link leaves out, so the entry's own description shows instead.
        Assert.Equal([(Monday, 5400L, "Design")], timesheet.Days.Select(d => (d.Day, d.Seconds, string.Join(", ", d.Descriptions))));
        Assert.DoesNotContain("audit", json);
    }

    [Fact]
    public async Task A_relative_link_steps_back_and_admins_manage_it()
    {
        var team = await ArrangeAsync();
        var link = await team.Admin.CreateAsync<SharedReportResponse>("/shared-reports", new { title = "Acme", period = "thisMonth", showPeople = true, hourFormat = "decimal" });
        var anonymous = factory.CreateClient();

        var current = await TestUser.ReadAsync<SharedTimesheet>(await anonymous.GetAsync(ApiPath(link), Ct));
        var earlier = await TestUser.ReadAsync<SharedTimesheet>(await anonymous.GetAsync($"{ApiPath(link)}?step=-1", Ct));
        var ahead = await anonymous.GetAsync($"{ApiPath(link)}?step=1", Ct);
        var listed = Assert.Single(await team.Admin.GetAsync<List<SharedReportResponse>>("/shared-reports"));
        var byMember = await team.Member.Client.GetAsync(team.Member.In("/shared-reports"), Ct);

        Assert.Equal(1, current.From.Day);
        Assert.Equal(current.From.AddMonths(-1), earlier.From);
        Assert.True(earlier.CanStepBack);
        Assert.Equal(HttpStatusCode.BadRequest, ahead.StatusCode);
        Assert.NotNull(listed.LastOpenedAt);
        Assert.Equal(("Ana", HourFormat.Decimal, true), (listed.CreatedBy, listed.HourFormat, listed.ShowPeople));
        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);

        // A new address stops the old one; removing the link stops it for good.
        var moved = await team.Admin.CreateAsync<SharedReportResponse>($"/shared-reports/{link.Id}/new-address", new { });
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(ApiPath(link), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(ApiPath(moved), Ct)).StatusCode);
        (await team.Admin.DeleteAsync($"/shared-reports/{link.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(ApiPath(moved), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_link_is_from_the_workspace_the_one_person_whose_time_it_counts_or_a_name_it_was_given()
    {
        var team = await ArrangeAsync();
        var week = new { period = "fixed", from = Monday, to = Monday.AddDays(6) };
        var byWorkspace = await team.Admin.CreateAsync<SharedReportResponse>("/shared-reports", new { title = "Acme", week.period, week.from, week.to });
        var byPerson = await team.Admin.CreateAsync<SharedReportResponse>("/shared-reports", new { title = "Acme", week.period, week.from, week.to, sender = "person" });
        var byName = await team.Admin.CreateAsync<SharedReportResponse>("/shared-reports", new { title = "Acme", week.period, week.from, week.to, sender = "custom", senderName = "  Ivo Design  " });
        var nameless = await team.Admin.PostAsync("/shared-reports", new { title = "Acme", sender = "custom", senderName = " " });
        var anonymous = factory.CreateClient();
        async Task<string> FromAsync(SharedReportResponse link) => (await TestUser.ReadAsync<SharedTimesheet>(await anonymous.GetAsync(ApiPath(link), Ct))).Workspace;

        var workspace = await FromAsync(byWorkspace);
        Assert.Equal(("Ivo", "Ivo Design"), (await FromAsync(byPerson), await FromAsync(byName)));
        Assert.Equal((SharedSender.Custom, "Ivo Design"), (byName.Sender, byName.SenderName));
        Assert.Equal(HttpStatusCode.BadRequest, nameless.StatusCode);

        // Once someone else's time is in the period, it is the workspace's timesheet again.
        await team.Admin.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Kick-off", projectId = team.Website.Id, isBillable = true, start = At(4, 9), stop = At(4, 10) });
        Assert.Equal(workspace, await FromAsync(byPerson));
        Assert.NotEqual("Ivo", workspace);
    }

    [Fact]
    public async Task A_link_leaves_today_out_unless_it_was_set_to_show_it()
    {
        var team = await ArrangeAsync();
        var zagreb = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zagreb");
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zagreb);
        var today = DateOnly.FromDateTime(now.DateTime);
        var midnight = new DateTimeOffset(now.Date, now.Offset);
        await team.Member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Yesterday", projectId = team.Website.Id, isBillable = true, start = midnight.AddHours(-3), stop = midnight.AddHours(-2) });
        await team.Member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Today", projectId = team.Website.Id, isBillable = true, start = midnight, stop = midnight.AddMinutes(1) });
        var days = new { period = "fixed", from = today.AddDays(-1), to = today };
        var hiding = await team.Admin.CreateAsync<SharedReportResponse>("/shared-reports", new { title = "Acme", days.period, days.from, days.to });
        var showing = await team.Admin.CreateAsync<SharedReportResponse>("/shared-reports", new { title = "Acme", days.period, days.from, days.to, showToday = true });
        var anonymous = factory.CreateClient();

        var withoutToday = await TestUser.ReadAsync<SharedTimesheet>(await anonymous.GetAsync(ApiPath(hiding), Ct));
        var withToday = await TestUser.ReadAsync<SharedTimesheet>(await anonymous.GetAsync(ApiPath(showing), Ct));

        Assert.Equal((false, true), (hiding.ShowToday, showing.ShowToday));
        Assert.Equal((today, today.AddDays(-1), 3600L), (withoutToday.To, withoutToday.CountedTo, withoutToday.Seconds));
        Assert.Equal(["Yesterday"], withoutToday.Days.SelectMany(d => d.Descriptions));
        Assert.Equal((today, 3660L), (withToday.CountedTo, withToday.Seconds));
    }

    [Fact]
    public async Task A_read_only_organization_s_links_rest_until_it_pays()
    {
        var team = await ArrangeAsync();
        var link = await team.Admin.CreateAsync<SharedReportResponse>("/shared-reports", new { title = "Acme", period = "lastMonth" });
        await factory.EndTrialAsync(Assert.Single((await team.Admin.MeAsync()).Organizations).Id);

        var response = await factory.CreateClient().GetAsync(ApiPath(link), Ct);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options, Ct);
        var removed = await team.Admin.DeleteAsync($"/shared-reports/{link.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(SharedReportEndpoints.UnavailableType, problem!.Type);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    [Fact]
    public async Task A_split_part_described_on_its_own_shows_only_its_own_description()
    {
        var team = await ArrangeAsync();
        var research = (await team.Admin.GetAsync<List<ProjectResponse>>("/projects")).Single(p => p.Name == "Research");
        await team.Member.CreateAsync<TimeEntryResponse>("/time-entries", new
        {
            start = At(4, 9),
            stop = At(4, 11),
            describeParts = true,
            shares = new object[]
            {
                new { projectId = team.Website.Id, percent = 50, description = "Fix the checkout" },
                new { projectId = research.Id, percent = 50, description = "Interview their users" },
            },
        });

        var link = await team.Admin.CreateAsync<SharedReportResponse>("/shared-reports", new
        {
            title = "Acme",
            period = "fixed",
            from = Monday,
            to = Monday.AddDays(6),
            projectIds = new[] { team.Website.Id },
        });
        var response = await factory.CreateClient().GetAsync(ApiPath(link), Ct);
        var json = await response.Content.ReadAsStringAsync(Ct);
        var timesheet = await TestUser.ReadAsync<SharedTimesheet>(response);

        Assert.Equal("Fix the checkout", string.Join(", ", timesheet.Days.Single(d => d.Day == Monday.AddDays(4)).Descriptions));
        Assert.DoesNotContain("Interview", json);

        var week = await team.Manager.GetAsync<ProjectBillingResponse>($"/projects/{team.Website.Id}/billing?from={Monday:yyyy-MM-dd}&to={Monday.AddDays(6):yyyy-MM-dd}");
        Assert.Equal(["Fix the checkout"], week.Rows.Single(r => r.UserId == team.Member.UserId).EntryDescriptions[4]);
    }

    /// <summary>The public API path of a link, from its address.</summary>
    private static string ApiPath(SharedReportResponse link) => $"/api/shared/{link.Url[(link.Url.LastIndexOf('/') + 1)..]}";

    private sealed record Team(TestUser Admin, TestUser Manager, TestUser Member, ProjectResponse Website);

    /// <summary>
    /// Website (rate 100) has its billed time entered separately; Research (rate 50) is another client's. Maja manages
    /// Website. Ivo tracked Design 1:30 on Monday and Build 2:00 on Tuesday there, a non-billable chat on Tuesday, and
    /// on Thursday a review split 60/40 between Website and Research.
    /// </summary>
    private async Task<Team> ArrangeAsync()
    {
        var admin = await TestUser.RegisterAsync(factory, $"ana-{Guid.NewGuid():N}@example.com", fullName: "Ana");
        var manager = await factory.AddMemberAsync(admin, "Maja");
        var member = await factory.AddMemberAsync(admin, "Ivo");
        var website = await admin.CreateAsync<ProjectResponse>("/projects", new { name = "Website", color = "#0B83D9", isBillable = true, hourlyRate = 100m, separateBilledTime = true });
        var research = await admin.CreateAsync<ProjectResponse>("/projects", new { name = "Research", color = "#E2A23B", isBillable = true, hourlyRate = 50m });
        (await admin.PutAsync($"/projects/{website.Id}/members/{manager.UserId}", new { isManager = true })).EnsureSuccessStatusCode();

        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Design", projectId = website.Id, isBillable = true, start = At(0, 9), stop = At(0, 10, 30) });
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Build", projectId = website.Id, isBillable = true, start = At(1, 9), stop = At(1, 11) });
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Chat", projectId = website.Id, isBillable = false, start = At(1, 13), stop = At(1, 13, 30) });
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new
        {
            description = "Review",
            start = At(3, 14),
            stop = At(3, 16),
            shares = new object[] { new { projectId = website.Id, percent = 60 }, new { projectId = research.Id, percent = 40 } },
        });

        return new Team(admin, manager, member, website);
    }
}
