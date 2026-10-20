using Spindle.Domain.Common;

namespace Spindle.Domain.Subscriptions;

public enum SubscriptionStatus
{
    /// <summary>The free trial every new organization starts with. It needs no card.</summary>
    Trialing,

    /// <summary>Paid, as the payment provider reports.</summary>
    Active,

    /// <summary>A payment failed and the provider is still retrying it. Nothing changes until it gives up.</summary>
    PastDue,

    /// <summary>No longer renewed. What was paid for runs to its end.</summary>
    Canceled,

    /// <summary>Free by our decision: our own organization, friends, contributors.</summary>
    Comped,
}

public enum BillingInterval
{
    Month,
    Year,
}

public enum OrganizationAccess
{
    Full,

    /// <summary>Everything can be read, reported and exported, but not changed.</summary>
    ReadOnly,
}

/// <summary>The emails that remind an organization's owners and admins of the end of its trial, in the order they go out.</summary>
public enum TrialReminder
{
    /// <summary>Two weeks before the trial ends.</summary>
    TwoWeeks,

    /// <summary>Three days before the trial ends.</summary>
    ThreeDays,

    /// <summary>The trial has ended and the organization is read-only.</summary>
    Ended,
}

/// <summary>
/// What an organization may do on the hosted service: a trial from sign-up, then what the payment provider reports.
/// It never deletes anything: when the trial or the money runs out, the organization becomes read-only. A
/// self-hosted server keeps these rows but never limits anyone by them.
/// </summary>
public sealed class OrganizationSubscription
{
    private OrganizationSubscription()
    {
    }

    public Guid OrganizationId { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    /// <summary>When the trial ends, for an organization that started with one.</summary>
    public DateTimeOffset? TrialEndsAt { get; private set; }

    /// <summary>The end of the period paid for. Empty until the first payment.</summary>
    public DateTimeOffset? PaidUntil { get; private set; }

    /// <summary>The last reminder of the trial sent to the organization, if any.</summary>
    public TrialReminder? TrialReminderSent { get; private set; }

    /// <summary>Which payment provider the subscription is with, e.g. <c>paddle</c>. Empty until someone subscribes.</summary>
    public string? Provider { get; private set; }

    /// <summary>The provider's ID of the customer who pays.</summary>
    public string? ProviderCustomerId { get; private set; }

    /// <summary>The provider's ID of the subscription, the latest one if there were several.</summary>
    public string? ProviderSubscriptionId { get; private set; }

    /// <summary>Where the provider says the subscription stands. Empty until someone subscribes.</summary>
    public ProviderSubscriptionStatus? ProviderStatus { get; private set; }

    public BillingInterval? Interval { get; private set; }

    /// <summary>The seats the provider bills for, which follow <c>OrganizationSubscriptions.SeatsAsync</c> within the hour.</summary>
    public int? BilledSeats { get; private set; }

    /// <summary>When the provider charges next: the end of the trial for someone who subscribed during it.</summary>
    public DateTimeOffset? NextChargeAt { get; private set; }

    /// <summary>The subscription was canceled and ends with the period paid for.</summary>
    public bool CancelsAtPeriodEnd { get; private set; }

    /// <summary>When the provider last changed the subscription, so an older event arriving late changes nothing.</summary>
    public DateTimeOffset? ProviderUpdatedAt { get; private set; }

    /// <summary>
    /// The end of the trial <see cref="TrialReminderSent"/> was about. When an operator moves the end, the reminders
    /// start over for the new date.
    /// </summary>
    public DateTimeOffset? TrialReminderSentFor { get; private set; }

    /// <summary>
    /// The prices the organization pays, e.g. <c>founding</c>: the ones new organizations got when it signed up, kept
    /// for good, also after canceling and coming back. Changing the prices for new organizations never changes them.
    /// Empty for an organization from before cohorts that has never paid; it joins the current one when it does.
    /// </summary>
    public string? PriceCohort { get; private set; }

    public static readonly TimeSpan FirstTrialReminder = TimeSpan.FromDays(14);

    public static readonly TimeSpan LastTrialReminder = TimeSpan.FromDays(3);

    public const int PriceCohortMaxLength = 40;

    public static OrganizationSubscription StartTrial(Guid organizationId, DateTimeOffset now, TimeSpan length, string? priceCohort = null)
    {
        if (length <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "A trial must last some time.");
        }

        return new OrganizationSubscription
        {
            OrganizationId = Guard.Id(organizationId),
            Status = SubscriptionStatus.Trialing,
            TrialEndsAt = now + length,
            PriceCohort = priceCohort,
        };
    }

    public static OrganizationSubscription Comped(Guid organizationId) => new()
    {
        OrganizationId = Guard.Id(organizationId),
        Status = SubscriptionStatus.Comped,
    };

    /// <summary>The payment provider decides about a subscription someone has paid for, not us.</summary>
    public bool IsPaid => Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue or SubscriptionStatus.Canceled;

    /// <summary>Someone chose a plan and the provider hasn't ended it: it renews, or starts when the trial ends.</summary>
    public bool HasPlan => ProviderStatus is ProviderSubscriptionStatus.Trialing or ProviderSubscriptionStatus.Active or ProviderSubscriptionStatus.PastDue;

    /// <summary>
    /// Takes in what the provider says about the organization's subscription. Returns false, changing nothing, for news
    /// older than what is known, and for an old subscription ending after a newer one started.
    /// </summary>
    public bool Apply(ProviderSubscription subscription)
    {
        if (subscription.SubscriptionId == ProviderSubscriptionId)
        {
            if (ProviderUpdatedAt >= subscription.UpdatedAt)
            {
                return false;
            }
        }
        else if (HasPlan && subscription.Status is ProviderSubscriptionStatus.Canceled or ProviderSubscriptionStatus.Paused)
        {
            return false;
        }

        Provider = subscription.Provider;
        ProviderCustomerId = subscription.CustomerId;
        ProviderSubscriptionId = subscription.SubscriptionId;
        ProviderStatus = subscription.Status;
        Interval = subscription.Interval;
        BilledSeats = subscription.Seats;
        NextChargeAt = subscription.NextBilledAt;
        CancelsAtPeriodEnd = subscription.CancelsAtPeriodEnd;
        ProviderUpdatedAt = subscription.UpdatedAt;

        switch (subscription.Status)
        {
            // Chosen during the trial: still the trial, which the first charge ends.
            case ProviderSubscriptionStatus.Trialing:
                Status = SubscriptionStatus.Trialing;
                break;
            case ProviderSubscriptionStatus.Active:
                Status = SubscriptionStatus.Active;
                PaidUntil = subscription.CurrentPeriodEndsAt ?? PaidUntil;
                break;
            case ProviderSubscriptionStatus.PastDue:
                Status = SubscriptionStatus.PastDue;
                break;
            // Never paid for: the trial goes on to its own end, as if no plan had been chosen.
            case ProviderSubscriptionStatus.Canceled or ProviderSubscriptionStatus.Paused when PaidUntil is null && TrialEndsAt is not null:
                Status = SubscriptionStatus.Trialing;
                break;
            default:
                Status = SubscriptionStatus.Canceled;
                if (subscription.EndedAt is { } endedAt && (PaidUntil is null || endedAt < PaidUntil))
                {
                    PaidUntil = endedAt;
                }

                break;
        }

        return true;
    }

    /// <summary>
    /// Puts the organization on a trial until <paramref name="endsAt"/>: extends one, reopens an ended one, or undoes
    /// comping it.
    /// </summary>
    public void PutOnTrial(DateTimeOffset endsAt, DateTimeOffset now)
    {
        if (IsPaid || HasPlan)
        {
            throw new DomainValidationException("trialEndsAt", "This organization pays; its provider manages the subscription.");
        }

        if (endsAt <= now)
        {
            throw new DomainValidationException("trialEndsAt", "The trial has to end in the future.");
        }

        Status = SubscriptionStatus.Trialing;
        TrialEndsAt = endsAt;
    }

    /// <summary>Puts an organization without a price cohort in <paramref name="cohort"/>. One that has a cohort keeps it.</summary>
    public string JoinPriceCohort(string cohort)
    {
        PriceCohort ??= cohort;
        return PriceCohort;
    }

    /// <summary>Makes the organization free for good.</summary>
    public void Comp()
    {
        if (IsPaid || HasPlan)
        {
            throw new DomainValidationException("status", "This organization pays; cancel its subscription with the provider first.");
        }

        Status = SubscriptionStatus.Comped;
    }

    /// <summary>
    /// The reminder of the trial's end that should go out now, or null if none. Only the latest one that is due: if
    /// reminders were missed, the organization gets one email, not all of them at once.
    /// </summary>
    public TrialReminder? TrialReminderDueAt(DateTimeOffset now)
    {
        // Someone who already chose a plan isn't reminded to choose one.
        if (Status != SubscriptionStatus.Trialing || HasPlan || TrialEndsAt is not { } endsAt)
        {
            return null;
        }

        var left = endsAt - now;
        TrialReminder? due = left <= TimeSpan.Zero ? TrialReminder.Ended
            : left <= LastTrialReminder ? TrialReminder.ThreeDays
            : left <= FirstTrialReminder ? TrialReminder.TwoWeeks
            : null;
        var sent = TrialReminderSentFor == endsAt ? TrialReminderSent : null;
        return due is not null && (sent is null || due > sent) ? due : null;
    }

    public OrganizationAccess AccessAt(DateTimeOffset now) => Status switch
    {
        // With a plan chosen, the first charge may come a little after the trial's end; nothing locks in between.
        SubscriptionStatus.Trialing => now < TrialEndsAt || HasPlan ? OrganizationAccess.Full : OrganizationAccess.ReadOnly,
        SubscriptionStatus.Canceled => now < PaidUntil ? OrganizationAccess.Full : OrganizationAccess.ReadOnly,
        _ => OrganizationAccess.Full,
    };
}
