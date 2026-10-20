using Spindle.Domain.Common;
using Spindle.Domain.Subscriptions;

namespace Spindle.Tests.Domain;

public sealed class OrganizationSubscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_trial_gives_full_access_until_it_ends_and_then_read_only()
    {
        var trial = OrganizationSubscription.StartTrial(Guid.NewGuid(), Now, TimeSpan.FromDays(90));

        Assert.Equal(SubscriptionStatus.Trialing, trial.Status);
        Assert.Equal(Now.AddDays(90), trial.TrialEndsAt);
        Assert.Equal(OrganizationAccess.Full, trial.AccessAt(Now.AddDays(90).AddTicks(-1)));
        Assert.Equal(OrganizationAccess.ReadOnly, trial.AccessAt(Now.AddDays(90)));
    }

    [Fact]
    public void An_organization_keeps_the_price_cohort_it_joined()
    {
        var founding = OrganizationSubscription.StartTrial(Guid.NewGuid(), Now, TimeSpan.FromDays(90), "founding");
        var older = OrganizationSubscription.StartTrial(Guid.NewGuid(), Now, TimeSpan.FromDays(90));

        Assert.Equal("founding", founding.JoinPriceCohort("standard"));
        Assert.Equal("standard", older.JoinPriceCohort("standard"));
        Assert.Equal(("founding", "standard"), (founding.PriceCohort, older.PriceCohort));
    }

    [Fact]
    public void A_trial_must_last_some_time()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OrganizationSubscription.StartTrial(Guid.NewGuid(), Now, TimeSpan.Zero));
    }

    [Fact]
    public void A_comped_organization_is_never_limited()
    {
        var comped = OrganizationSubscription.Comped(Guid.NewGuid());

        Assert.Null(comped.TrialEndsAt);
        Assert.Equal(OrganizationAccess.Full, comped.AccessAt(Now.AddYears(10)));
    }

    [Fact]
    public void An_operator_reopens_an_ended_trial_or_puts_a_comped_organization_back_on_one()
    {
        var ended = OrganizationSubscription.StartTrial(Guid.NewGuid(), Now.AddDays(-100), TimeSpan.FromDays(90));
        var comped = OrganizationSubscription.Comped(Guid.NewGuid());

        ended.PutOnTrial(Now.AddDays(30), Now);
        comped.PutOnTrial(Now.AddDays(30), Now);

        Assert.Equal(OrganizationAccess.Full, ended.AccessAt(Now));
        Assert.Equal((SubscriptionStatus.Trialing, Now.AddDays(30)), (comped.Status, comped.TrialEndsAt));
        Assert.Throws<DomainValidationException>(() => ended.PutOnTrial(Now, Now));
    }

    [Fact]
    public void The_trial_is_reminded_of_two_weeks_and_three_days_before_it_ends_and_when_it_has()
    {
        var trial = OrganizationSubscription.StartTrial(Guid.NewGuid(), Now, TimeSpan.FromDays(90));
        var endsAt = Now.AddDays(90);

        Assert.Null(trial.TrialReminderDueAt(endsAt.AddDays(-15)));
        Assert.Equal(TrialReminder.TwoWeeks, trial.TrialReminderDueAt(endsAt.AddDays(-14)));
        Assert.Equal(TrialReminder.TwoWeeks, trial.TrialReminderDueAt(endsAt.AddDays(-3).AddTicks(-1)));
        Assert.Equal(TrialReminder.ThreeDays, trial.TrialReminderDueAt(endsAt.AddDays(-3)));
        Assert.Equal(TrialReminder.Ended, trial.TrialReminderDueAt(endsAt));
        Assert.Null(OrganizationSubscription.Comped(Guid.NewGuid()).TrialReminderDueAt(endsAt));
    }

    private static ProviderSubscription Reported(
        ProviderSubscriptionStatus status,
        DateTimeOffset updatedAt,
        string id = "sub_1",
        DateTimeOffset? periodEndsAt = null,
        DateTimeOffset? nextBilledAt = null,
        DateTimeOffset? endedAt = null,
        bool cancelsAtPeriodEnd = false) =>
        new("paddle", id, "ctm_1", status, BillingInterval.Month, 3, periodEndsAt, nextBilledAt, endedAt, cancelsAtPeriodEnd, updatedAt);

    [Fact]
    public void A_plan_chosen_during_the_trial_keeps_the_trial_and_its_access_until_the_first_charge()
    {
        var trial = OrganizationSubscription.StartTrial(Guid.NewGuid(), Now, TimeSpan.FromDays(90));

        Assert.True(trial.Apply(Reported(ProviderSubscriptionStatus.Trialing, Now, nextBilledAt: Now.AddDays(91))));

        Assert.Equal(SubscriptionStatus.Trialing, trial.Status);
        Assert.True(trial.HasPlan);
        Assert.Equal((BillingInterval.Month, 3, "sub_1", "ctm_1"), (trial.Interval!.Value, trial.BilledSeats!.Value, trial.ProviderSubscriptionId, trial.ProviderCustomerId));
        // The first charge may come a little after the trial's end; nothing locks in between.
        Assert.Equal(OrganizationAccess.Full, trial.AccessAt(Now.AddDays(90).AddHours(5)));
        Assert.Null(trial.TrialReminderDueAt(Now.AddDays(89)));
    }

    [Fact]
    public void Paying_makes_it_active_until_the_period_ends_and_canceling_runs_to_that_end()
    {
        var trial = OrganizationSubscription.StartTrial(Guid.NewGuid(), Now, TimeSpan.FromDays(90));
        var periodEnd = Now.AddDays(120);

        trial.Apply(Reported(ProviderSubscriptionStatus.Active, Now.AddDays(90), periodEndsAt: periodEnd));
        Assert.Equal((SubscriptionStatus.Active, periodEnd), (trial.Status, trial.PaidUntil!.Value));

        trial.Apply(Reported(ProviderSubscriptionStatus.Active, Now.AddDays(95), periodEndsAt: periodEnd, cancelsAtPeriodEnd: true));
        Assert.True(trial.CancelsAtPeriodEnd);

        trial.Apply(Reported(ProviderSubscriptionStatus.Canceled, Now.AddDays(120), endedAt: periodEnd));
        Assert.Equal(SubscriptionStatus.Canceled, trial.Status);
        Assert.False(trial.HasPlan);
        Assert.Equal(OrganizationAccess.Full, trial.AccessAt(periodEnd.AddTicks(-1)));
        Assert.Equal(OrganizationAccess.ReadOnly, trial.AccessAt(periodEnd));
    }

    [Fact]
    public void Canceling_right_away_ends_access_then()
    {
        var subscription = OrganizationSubscription.StartTrial(Guid.NewGuid(), Now.AddDays(-100), TimeSpan.FromDays(90));
        subscription.Apply(Reported(ProviderSubscriptionStatus.Active, Now, periodEndsAt: Now.AddDays(30)));

        subscription.Apply(Reported(ProviderSubscriptionStatus.Canceled, Now.AddDays(5), endedAt: Now.AddDays(5)));

        Assert.Equal(Now.AddDays(5), subscription.PaidUntil);
        Assert.Equal(OrganizationAccess.ReadOnly, subscription.AccessAt(Now.AddDays(6)));
    }

    [Fact]
    public void A_plan_canceled_before_its_first_charge_leaves_the_trial_as_it_was()
    {
        var trial = OrganizationSubscription.StartTrial(Guid.NewGuid(), Now, TimeSpan.FromDays(90));
        trial.Apply(Reported(ProviderSubscriptionStatus.Trialing, Now));

        trial.Apply(Reported(ProviderSubscriptionStatus.Canceled, Now.AddDays(10), endedAt: Now.AddDays(10)));

        Assert.Equal(SubscriptionStatus.Trialing, trial.Status);
        Assert.False(trial.HasPlan);
        Assert.Equal(OrganizationAccess.Full, trial.AccessAt(Now.AddDays(89)));
        Assert.Equal(OrganizationAccess.ReadOnly, trial.AccessAt(Now.AddDays(90)));
    }

    [Fact]
    public void Older_news_and_an_old_subscription_ending_change_nothing()
    {
        var subscription = OrganizationSubscription.StartTrial(Guid.NewGuid(), Now, TimeSpan.FromDays(90));
        subscription.Apply(Reported(ProviderSubscriptionStatus.Active, Now.AddDays(2), id: "sub_new", periodEndsAt: Now.AddDays(32)));

        Assert.False(subscription.Apply(Reported(ProviderSubscriptionStatus.Trialing, Now.AddDays(1), id: "sub_new")));
        Assert.False(subscription.Apply(Reported(ProviderSubscriptionStatus.Canceled, Now.AddDays(3), id: "sub_old", endedAt: Now.AddDays(3))));
        Assert.Equal((SubscriptionStatus.Active, "sub_new"), (subscription.Status, subscription.ProviderSubscriptionId));
    }

    [Fact]
    public void Operators_dont_change_the_trial_of_an_organization_with_a_plan()
    {
        var trial = OrganizationSubscription.StartTrial(Guid.NewGuid(), Now, TimeSpan.FromDays(90));
        trial.Apply(Reported(ProviderSubscriptionStatus.Trialing, Now));

        Assert.Throws<DomainValidationException>(() => trial.PutOnTrial(Now.AddDays(200), Now));
        Assert.Throws<DomainValidationException>(trial.Comp);
    }
}
