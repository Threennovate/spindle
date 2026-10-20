namespace Spindle.Domain.Subscriptions;

public enum ProviderSubscriptionStatus
{
    /// <summary>Chosen during our trial; the first charge comes when it ends.</summary>
    Trialing,

    Active,

    /// <summary>A payment failed and the provider is retrying it.</summary>
    PastDue,

    Paused,

    Canceled,
}

/// <summary>
/// A subscription as the payment provider reports it, in our words. The provider's own events are translated into this
/// at the edge, so nothing else depends on which provider it is.
/// </summary>
/// <param name="Provider">Which provider, e.g. <c>paddle</c>.</param>
/// <param name="Seats">How many seats it bills for.</param>
/// <param name="CurrentPeriodEndsAt">The end of the period paid for, while it is active.</param>
/// <param name="NextBilledAt">When it charges next, if it will.</param>
/// <param name="EndedAt">When it was canceled or paused, once it has been.</param>
/// <param name="CancelsAtPeriodEnd">Canceled, but runs to the end of the period paid for.</param>
/// <param name="UpdatedAt">When the provider last changed it.</param>
public sealed record ProviderSubscription(
    string Provider,
    string SubscriptionId,
    string CustomerId,
    ProviderSubscriptionStatus Status,
    BillingInterval Interval,
    int Seats,
    DateTimeOffset? CurrentPeriodEndsAt,
    DateTimeOffset? NextBilledAt,
    DateTimeOffset? EndedAt,
    bool CancelsAtPeriodEnd,
    DateTimeOffset UpdatedAt);

/// <summary>
/// An event from a payment provider that has been handled, so the same one delivered again changes nothing. Providers
/// deliver at least once, and sometimes more.
/// </summary>
public sealed class ProviderEventReceipt
{
    public const int ProviderMaxLength = 20;

    public const int EventIdMaxLength = 100;

    private ProviderEventReceipt()
    {
        Provider = string.Empty;
        EventId = string.Empty;
    }

    public string Provider { get; private set; }

    public string EventId { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public static ProviderEventReceipt Record(string provider, string eventId, DateTimeOffset receivedAt) => new()
    {
        Provider = provider,
        EventId = eventId,
        ReceivedAt = receivedAt,
    };
}
