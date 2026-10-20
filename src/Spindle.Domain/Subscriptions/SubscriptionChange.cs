namespace Spindle.Domain.Subscriptions;

public enum SubscriptionChangeKind
{
    /// <summary>Put on a trial: extended, reopened, or no longer comped.</summary>
    Trial,

    Comped,
}

/// <summary>
/// A change an operator made to an organization's subscription by hand, so we can later tell why an organization has
/// what it has. Who made it is kept by email, which stays readable after their account is gone.
/// </summary>
public sealed class SubscriptionChange
{
    public const int EmailMaxLength = 256;

    private SubscriptionChange()
    {
        OperatorEmail = string.Empty;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid OrganizationId { get; private set; }

    public DateTimeOffset At { get; private set; }

    public string OperatorEmail { get; private set; }

    public SubscriptionChangeKind Kind { get; private set; }

    /// <summary>What the subscription was before.</summary>
    public SubscriptionStatus PreviousStatus { get; private set; }

    /// <summary>The new end of the trial, for <see cref="SubscriptionChangeKind.Trial"/>.</summary>
    public DateTimeOffset? TrialEndsAt { get; private set; }

    public static SubscriptionChange Record(
        Guid organizationId,
        SubscriptionStatus previousStatus,
        SubscriptionChangeKind kind,
        DateTimeOffset? trialEndsAt,
        string operatorEmail,
        DateTimeOffset at) => new()
    {
        OrganizationId = organizationId,
        At = at,
        OperatorEmail = operatorEmail,
        Kind = kind,
        PreviousStatus = previousStatus,
        TrialEndsAt = trialEndsAt,
    };
}
