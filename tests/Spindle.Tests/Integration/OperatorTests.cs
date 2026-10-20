using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spindle.Api.Operator;
using Spindle.Api.Subscriptions;
using Spindle.Api.TimeEntries;
using Spindle.Domain.Subscriptions;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class OperatorTests : IAsyncDisposable
{
    private readonly PostgresApiFactory factory;
    private readonly string operatorEmail = $"operator-{Guid.NewGuid():N}@example.com";
    private readonly WebApplicationFactory<Program> server;

    public OperatorTests(PostgresApiFactory factory)
    {
        this.factory = factory;
        // Written differently from how the person signs up, as someone might in configuration.
        server = factory.WithWebHostBuilder(builder => builder.UseSetting("Operators:Emails:0", $" {operatorEmail.ToUpperInvariant()} "));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => server.DisposeAsync();

    [Fact]
    public async Task Only_operators_who_confirmed_their_email_see_the_operator_pages()
    {
        var unconfirmed = await TestUser.RegisterAsync(server, operatorEmail);
        var someone = await TestUser.RegisterAsync(server);

        var beforeConfirming = await unconfirmed.Client.GetAsync("/api/operator/overview", Ct);
        var meBefore = await unconfirmed.MeAsync();
        await ConfirmEmailAsync(unconfirmed);

        Assert.Equal(HttpStatusCode.NotFound, beforeConfirming.StatusCode);
        Assert.False(meBefore.IsOperator);
        Assert.True((await unconfirmed.MeAsync()).IsOperator);
        Assert.Equal(HttpStatusCode.OK, (await unconfirmed.Client.GetAsync("/api/operator/overview", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await someone.Client.GetAsync("/api/operator/overview", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.CreateClient().GetAsync("/api/operator/overview", Ct)).StatusCode);
    }

    [Fact]
    public async Task Operators_see_who_an_organization_is_and_where_its_subscription_stands()
    {
        var operatorUser = await OperatorAsync();
        var before = await OverviewAsync(operatorUser);
        var owner = await TestUser.RegisterAsync(factory, $"owner-{Guid.NewGuid():N}@example.com", fullName: "Ana Anić");
        var member = await factory.AddMemberAsync(owner);
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Private work", start, stop = start.AddHours(1) });

        var listed = Assert.Single((await ListAsync(operatorUser, $"search={owner.Email.ToUpperInvariant()}")).Organizations);
        var after = await OverviewAsync(operatorUser);

        Assert.Equal(("Ana Anić", owner.Email, 2), (listed.OwnerName, listed.OwnerEmail, listed.Seats));
        Assert.Equal((SubscriptionStatus.Trialing, OrganizationAccess.Full), (listed.Status, listed.Access));
        Assert.NotNull(listed.TrialEndsAt);
        Assert.InRange(listed.LastActivityAt!.Value, start, DateTimeOffset.UtcNow);
        Assert.Contains(listed.Id, (await ListAsync(operatorUser, "filter=trialing")).Organizations.Select(o => o.Id));
        Assert.DoesNotContain(listed.Id, (await ListAsync(operatorUser, "filter=comped")).Organizations.Select(o => o.Id));
        Assert.True(after.Hosted);
        Assert.Equal(before.Organizations + 1, after.Organizations);
        Assert.Equal(before.NewLast7Days + 1, after.NewLast7Days);
        Assert.Equal(before.Trialing + 1, after.Trialing);
    }

    [Fact]
    public async Task Operators_reopen_trials_and_comp_organizations_and_each_change_is_kept()
    {
        var operatorUser = await OperatorAsync();
        var owner = await TestUser.RegisterAsync(factory);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        await factory.EndTrialAsync(organizationId);
        var readOnly = (await ListAsync(operatorUser, "filter=readOnly")).Organizations.Select(o => o.Id).ToList();
        var newEnd = DateTimeOffset.UtcNow.AddDays(30);

        var past = await operatorUser.Client.PutAsJsonAsync(TrialPath(organizationId), new { trialEndsAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, Ct);
        var reopened = await operatorUser.Client.PutAsJsonAsync(TrialPath(organizationId), new { trialEndsAt = newEnd }, Ct);
        var afterTrial = Assert.Single((await owner.MeAsync()).Organizations);
        var comped = await operatorUser.Client.PostAsync(CompPath(organizationId), null, Ct);
        var compedAgain = await operatorUser.Client.PostAsync(CompPath(organizationId), null, Ct);
        var changes = await TestUser.ReadAsync<List<SubscriptionChangeResponse>>(
            await operatorUser.Client.GetAsync($"/api/operator/organizations/{organizationId}/changes", Ct));

        Assert.Contains(organizationId, readOnly);
        Assert.Equal(["trialEndsAt"], await TestUser.ErrorFieldsAsync(past));
        Assert.Equal(HttpStatusCode.NoContent, reopened.StatusCode);
        Assert.Equal(OrganizationAccess.Full, afterTrial.Access);
        Assert.Equal(newEnd, afterTrial.TrialEndsAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (comped.StatusCode, compedAgain.StatusCode));
        Assert.Null(Assert.Single((await owner.MeAsync()).Organizations).TrialEndsAt);
        Assert.Equal(
            [
                (SubscriptionChangeKind.Comped, SubscriptionStatus.Trialing, false),
                (SubscriptionChangeKind.Trial, SubscriptionStatus.Trialing, true),
            ],
            changes.Select(c => (c.Kind, c.PreviousStatus, c.TrialEndsAt.HasValue)));
        Assert.All(changes, c => Assert.Equal(operatorEmail, c.OperatorEmail));
    }

    [Fact]
    public async Task On_a_self_hosted_server_operators_see_organizations_but_no_subscriptions()
    {
        await using var selfHosted = server.WithWebHostBuilder(builder => builder.UseSetting("Subscriptions:Enabled", "false"));
        var operatorUser = await TestUser.RegisterAsync(selfHosted, operatorEmail);
        await ConfirmEmailAsync(operatorUser);
        var organizationId = Assert.Single((await operatorUser.MeAsync()).Organizations).Id;

        var listed = Assert.Single((await ListAsync(operatorUser, $"search={operatorEmail}")).Organizations);
        var overview = await OverviewAsync(operatorUser);
        var trial = await operatorUser.Client.PutAsJsonAsync(TrialPath(organizationId), new { trialEndsAt = DateTimeOffset.UtcNow.AddDays(30) }, Ct);

        Assert.Equal((null, OrganizationAccess.Full, null), (listed.Status, listed.Access, listed.TrialEndsAt));
        Assert.Equal((false, 0), (overview.Hosted, overview.Trialing));
        Assert.Equal(HttpStatusCode.NotFound, trial.StatusCode);
    }

    private async Task<TestUser> OperatorAsync()
    {
        var user = await TestUser.RegisterAsync(server, operatorEmail);
        await ConfirmEmailAsync(user);
        return user;
    }

    private async Task ConfirmEmailAsync(TestUser user)
    {
        await using var scope = factory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users
            .Where(u => u.Id == user.UserId)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.EmailConfirmed, true), Ct);
    }

    private static async Task<OperatorOverviewResponse> OverviewAsync(TestUser user) =>
        await TestUser.ReadAsync<OperatorOverviewResponse>(await user.Client.GetAsync("/api/operator/overview", Ct));

    private static async Task<OperatorOrganizationsResponse> ListAsync(TestUser user, string query) =>
        await TestUser.ReadAsync<OperatorOrganizationsResponse>(await user.Client.GetAsync($"/api/operator/organizations?{query}", Ct));

    private static string TrialPath(Guid organizationId) => $"/api/operator/organizations/{organizationId}/trial";

    private static string CompPath(Guid organizationId) => $"/api/operator/organizations/{organizationId}/comp";
}
