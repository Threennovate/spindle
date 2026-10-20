using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Spindle.Api.Common;
using Spindle.Api.Email;
using Spindle.Domain.Organizations;
using Spindle.Domain.Subscriptions;
using Spindle.Infrastructure.Email;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Subscriptions;

/// <summary>
/// Emails an organization's owners and admins two weeks and three days before its trial ends, and when it has ended.
/// Each reminder goes out once per end of the trial: it is claimed on the subscription row before it is sent, so
/// running twice, even at the same time, doesn't send it twice.
/// </summary>
internal sealed class TrialReminders(
    AppDbContext db,
    IEmailSender emailSender,
    IOptions<SubscriptionOptions> subscriptions,
    IOptions<AppOptions> app,
    TimeProvider timeProvider,
    ILogger<TrialReminders> logger)
{
    /// <summary>Where the emails send people to choose a plan.</summary>
    public const string PlanPath = "/plan";

    /// <summary>Sends the reminders that are due, and returns how many organizations got one.</summary>
    public async Task<int> SendDueAsync(CancellationToken cancellationToken)
    {
        if (!subscriptions.Value.Enabled)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        var horizon = now + OrganizationSubscription.FirstTrialReminder;
        var candidates = await db.OrganizationSubscriptions.AsNoTracking()
            .Where(s => s.Status == SubscriptionStatus.Trialing && s.TrialEndsAt <= horizon)
            .Where(s => s.TrialReminderSentFor != s.TrialEndsAt || s.TrialReminderSent != TrialReminder.Ended)
            .ToListAsync(cancellationToken);

        var reminded = 0;
        foreach (var subscription in candidates)
        {
            if (subscription.TrialReminderDueAt(now) is { } due && await RemindAsync(subscription, due, now, cancellationToken))
            {
                reminded++;
            }
        }

        return reminded;
    }

    private async Task<bool> RemindAsync(OrganizationSubscription subscription, TrialReminder due, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var endsAt = subscription.TrialEndsAt!.Value;
        var claimed = await Subscription(subscription.OrganizationId, endsAt, subscription.TrialReminderSent, subscription.TrialReminderSentFor)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.TrialReminderSent, due)
                .SetProperty(s => s.TrialReminderSentFor, endsAt), cancellationToken);
        if (claimed == 0)
        {
            // Something else changed it since it was read: another run sent it, or an operator moved the trial.
            return false;
        }

        var organizationName = await db.Organizations.Where(o => o.Id == subscription.OrganizationId).Select(o => o.Name).SingleAsync(cancellationToken);
        var recipients = await (
            from member in db.OrganizationMembers
            join user in db.Users on member.UserId equals user.Id
            where member.OrganizationId == subscription.OrganizationId
                && (member.Role == OrganizationRole.Owner || member.Role == OrganizationRole.Admin)
                && user.DeletedAt == null && user.Email != null
            select user.Email!)
            .Distinct()
            .ToListAsync(cancellationToken);

        var link = app.Value.Link(PlanPath);
        var daysLeft = (int)Math.Ceiling((endsAt - now).TotalDays);
        var sent = 0;
        foreach (var email in recipients)
        {
            var message = due == TrialReminder.Ended
                ? EmailTemplates.TrialEnded(email, organizationName, link)
                : EmailTemplates.TrialEnding(email, organizationName, daysLeft, link);
            try
            {
                await emailSender.SendAsync(message, cancellationToken);
                sent++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Could not send the trial reminder {Reminder} of organization {OrganizationId}", due, subscription.OrganizationId);
            }
        }

        if (sent == 0 && recipients.Count > 0)
        {
            // Nobody got it, so it is given back and tried again on the next run.
            await Subscription(subscription.OrganizationId, endsAt, due, endsAt)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.TrialReminderSent, subscription.TrialReminderSent)
                    .SetProperty(s => s.TrialReminderSentFor, subscription.TrialReminderSentFor), CancellationToken.None);
            return false;
        }

        return sent > 0;
    }

    private IQueryable<OrganizationSubscription> Subscription(Guid organizationId, DateTimeOffset endsAt, TrialReminder? sent, DateTimeOffset? sentFor) =>
        db.OrganizationSubscriptions.Where(s => s.OrganizationId == organizationId
            && s.Status == SubscriptionStatus.Trialing
            && s.TrialEndsAt == endsAt
            && s.TrialReminderSent == sent
            && s.TrialReminderSentFor == sentFor);
}

/// <summary>
/// Sends the trial reminders that are due once an hour, on the hosted service only. It assumes one instance of the
/// API, as SignalR does; a second one would not send anything twice, only look for nothing.
/// </summary>
internal sealed class TrialReminderService(
    IServiceScopeFactory scopes,
    IOptions<SubscriptionOptions> subscriptions,
    TimeProvider timeProvider,
    ILogger<TrialReminderService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!subscriptions.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var reminded = await scope.ServiceProvider.GetRequiredService<TrialReminders>().SendDueAsync(stoppingToken);
                if (reminded > 0)
                {
                    logger.LogInformation("Reminded {Count} organizations of the end of their trial", reminded);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Could not send the trial reminders");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
