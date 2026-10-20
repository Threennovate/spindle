using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spindle.Api.Subscriptions;
using Spindle.Domain.Subscriptions;
using Spindle.Infrastructure.Email;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class TrialReminderTests(PostgresApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Owners_hear_two_weeks_and_three_days_before_the_trial_ends_and_when_it_has_once_each()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        var member = await factory.AddMemberAsync(owner);

        await SendDueAsync();
        Assert.Empty(Reminders(owner.Email));

        await factory.MoveTrialEndAsync(organizationId, DateTimeOffset.UtcNow.AddDays(10).AddHours(-1));
        await SendDueAsync();
        await SendDueAsync();
        var twoWeeks = Assert.Single(Reminders(owner.Email));
        Assert.Contains("ends in 10 days", twoWeeks.Subject);
        Assert.Contains("https://track.example.com/plan", twoWeeks.TextBody);

        await factory.MoveTrialEndAsync(organizationId, DateTimeOffset.UtcNow.AddDays(2).AddHours(-1));
        await SendDueAsync();
        Assert.Contains("ends in 2 days", Reminders(owner.Email)[1].Subject);

        await factory.EndTrialAsync(organizationId);
        await SendDueAsync();
        await SendDueAsync();
        var emails = Reminders(owner.Email);
        Assert.Equal(3, emails.Count);
        Assert.Contains("has ended", emails[2].Subject);
        Assert.Contains("read-only", emails[2].TextBody);
        // Members aren't the ones who choose a plan.
        Assert.Empty(Reminders(member.Email));
    }

    [Fact]
    public async Task Missed_reminders_add_up_to_one_email()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;

        await factory.EndTrialAsync(organizationId);
        await SendDueAsync();

        Assert.Contains("has ended", Assert.Single(Reminders(owner.Email)).Subject);
    }

    [Fact]
    public async Task Moving_the_end_of_the_trial_starts_the_reminders_over()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        await factory.EndTrialAsync(organizationId);
        await SendDueAsync();

        await factory.MoveTrialEndAsync(organizationId, DateTimeOffset.UtcNow.AddDays(5).AddHours(-1));
        await SendDueAsync();
        await factory.EndTrialAsync(organizationId);
        await SendDueAsync();

        var emails = Reminders(owner.Email);
        Assert.Equal(3, emails.Count);
        Assert.Contains("ends in 5 days", emails[1].Subject);
        Assert.Contains("has ended", emails[2].Subject);
    }

    [Fact]
    public async Task Comped_organizations_hear_nothing()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        await factory.EndTrialAsync(organizationId);
        await using (var scope = factory.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().OrganizationSubscriptions
                .Where(s => s.OrganizationId == organizationId)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, SubscriptionStatus.Comped), Ct);
        }

        await SendDueAsync();

        Assert.Empty(Reminders(owner.Email));
    }

    [Fact]
    public async Task A_reminder_nobody_got_is_tried_again_on_the_next_run()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        await factory.EndTrialAsync(organizationId);

        factory.Emails.Fail = true;
        try
        {
            await SendDueAsync();
        }
        finally
        {
            factory.Emails.Fail = false;
        }

        Assert.Empty(Reminders(owner.Email));
        await SendDueAsync();
        Assert.Single(Reminders(owner.Email));
    }

    [Fact]
    public async Task A_self_hosted_server_sends_no_reminders()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        await factory.EndTrialAsync(organizationId);
        await using var selfHosted = factory.WithWebHostBuilder(builder => builder.UseSetting("Subscriptions:Enabled", "false"));

        await using var scope = selfHosted.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<TrialReminders>().SendDueAsync(Ct));
        Assert.Empty(Reminders(owner.Email));
    }

    // Owners also got an email to confirm their address when they signed up.
    private List<EmailMessage> Reminders(string email) =>
        factory.Emails.To(email).Where(message => message.Subject.Contains("trial", StringComparison.Ordinal)).ToList();

    private async Task SendDueAsync()
    {
        await using var scope = factory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TrialReminders>().SendDueAsync(Ct);
    }
}
