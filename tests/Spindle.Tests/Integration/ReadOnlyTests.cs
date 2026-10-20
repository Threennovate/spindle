using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Spindle.Api.Projects;
using Spindle.Api.Subscriptions;
using Spindle.Api.Team;
using Spindle.Api.TimeEntries;

namespace Spindle.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class ReadOnlyTests(PostgresApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_read_only_organization_refuses_changes_and_says_why()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        var member = await factory.AddMemberAsync(owner);
        var project = await owner.CreateAsync<ProjectResponse>("/projects", new { name = "Website", color = "#0B83D9" });
        await factory.EndTrialAsync(organizationId);

        var newEntry = await member.PostAsync("/time-entries", new { start = DateTimeOffset.UtcNow.AddHours(-1), stop = DateTimeOffset.UtcNow });
        var renamed = await owner.PutAsync($"/projects/{project.Id}", new { name = "Site", color = "#0B83D9" });
        var problem = await newEntry.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options, Ct);

        Assert.Equal(HttpStatusCode.PaymentRequired, newEntry.StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, renamed.StatusCode);
        Assert.Equal(ReadOnlyFilter.ProblemType, problem!.Type);
        Assert.Equal(organizationId.ToString(), problem.Extensions["organizationId"]?.ToString());
    }

    [Fact]
    public async Task A_read_only_organization_can_still_be_read_reported_exported_and_wound_down()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        var member = await factory.AddMemberAsync(owner);
        await owner.CreateAsync<SentInvitationResponse>("/invitations", new { email = $"pending-{Guid.NewGuid():N}@example.com" });
        var invitation = Assert.Single(await owner.GetAsync<List<InvitationResponse>>("/invitations"));
        var running = await owner.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Running", start = DateTimeOffset.UtcNow.AddMinutes(-30) });
        await factory.EndTrialAsync(organizationId);
        var week = new { from = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-7), to = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1) };

        var entries = await owner.Client.GetAsync(owner.In($"/time-entries?from={DateTimeOffset.UtcNow.AddDays(-1):O}&to={DateTimeOffset.UtcNow.AddDays(1):O}".Replace("+", "%2B")), Ct);
        var summary = await owner.PostAsync("/reports/summary", new { week.from, week.to, groupBy = "project" });
        var export = await owner.PostAsync("/reports/detailed/export?format=csv", new { week.from, week.to });
        var stopped = await owner.PostAsync($"/time-entries/{running.Id}/stop", new { });
        var revoked = await owner.DeleteAsync($"/invitations/{invitation.Id}");
        var removed = await owner.DeleteAsync($"/members/{member.UserId}");

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.NoContent],
            new[] { entries, summary, export, stopped, revoked, removed }.Select(r => r.StatusCode));
    }

    [Fact]
    public async Task Only_the_organization_whose_trial_ended_is_read_only()
    {
        var ended = await TestUser.RegisterAsync(factory);
        var other = await TestUser.RegisterAsync(factory);
        await factory.EndTrialAsync(Assert.Single((await ended.MeAsync()).Organizations).Id);

        var created = await other.PostAsync("/time-entries", new { start = DateTimeOffset.UtcNow.AddHours(-1), stop = DateTimeOffset.UtcNow });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task A_self_hosted_server_never_makes_an_organization_read_only()
    {
        var owner = await TestUser.RegisterAsync(factory);
        await factory.EndTrialAsync(Assert.Single((await owner.MeAsync()).Organizations).Id);
        await using var selfHosted = factory.WithWebHostBuilder(builder => builder.UseSetting("Subscriptions:Enabled", "false"));
        var client = selfHosted.CreateClient();
        client.DefaultRequestHeaders.Authorization = owner.Client.DefaultRequestHeaders.Authorization;

        var created = await client.PostAsJsonAsync(owner.In("/time-entries"), new { start = DateTimeOffset.UtcNow.AddHours(-1), stop = DateTimeOffset.UtcNow }, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }
}
