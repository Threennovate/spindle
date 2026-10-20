using Spindle.Domain.Subscriptions;

namespace Spindle.Api.Subscriptions;

/// <summary>A price someone can choose: per seat, for one billing period.</summary>
/// <param name="UnitAmount">Per seat, in the currency's main unit, e.g. 5.00. Taxes are as the provider shows them for the buyer's country.</param>
public sealed record PlanPrice(BillingInterval Interval, decimal UnitAmount, string Currency);

/// <summary>What the browser needs to open the provider's checkout for one organization.</summary>
/// <param name="Environment">The provider's environment, e.g. <c>sandbox</c>, which its browser script must be told.</param>
/// <param name="ClientToken">The provider's public token for its browser script.</param>
/// <param name="TransactionId">The checkout prepared on the server, with its price, seats and trial fixed.</param>
/// <param name="CustomerId">The provider's customer the checkout is for, e.g. Paddle's <c>ctm_</c> ID, which its browser script is told for Paddle Retain.</param>
public sealed record CheckoutStart(string Provider, string Environment, string ClientToken, string TransactionId, string CustomerId);

/// <param name="PriceCohort">The organization's price cohort, whose price for the interval the checkout charges.</param>
/// <param name="TrialDays">Days left of our trial, shown and kept free in the checkout; 0 charges right away.</param>
/// <param name="CustomerId">The provider's customer from an earlier subscription, so they aren't asked again.</param>
internal sealed record CheckoutRequest(
    Guid OrganizationId,
    string PriceCohort,
    BillingInterval Interval,
    int Seats,
    int TrialDays,
    string BuyerEmail,
    string? CustomerId);

/// <summary>What a subscription with the provider should be.</summary>
/// <param name="PriceCohort">The organization's price cohort: the subscription is on its price for the interval.</param>
/// <param name="FirstChargeAt">While it is in our trial: when the trial ends, which is when the first charge comes.</param>
internal sealed record SubscriptionTarget(string PriceCohort, BillingInterval Interval, int Seats, DateTimeOffset? FirstChargeAt);

/// <param name="EventId">The provider's ID of the event, to handle it once.</param>
/// <param name="OrganizationId">The organization the checkout was for, which the provider carries along.</param>
internal sealed record ProviderEvent(string EventId, Guid? OrganizationId, ProviderSubscription? Subscription);

/// <summary>
/// Everything that depends on the payment provider. The rest of the code speaks of organizations, seats and
/// <see cref="ProviderSubscription"/>s, so another provider means another implementation of this and nothing else.
/// </summary>
internal interface ISubscriptionProvider
{
    string Name { get; }

    /// <summary>Whether this server can take payments: the provider's settings are all there.</summary>
    bool IsConfigured { get; }

    /// <summary>The price cohorts with their prices set up, monthly and yearly.</summary>
    IReadOnlyCollection<string> PriceCohorts { get; }

    /// <summary>Whether the price cohort has its prices with the provider, monthly and yearly.</summary>
    bool HasPrices(string priceCohort);

    Task<IReadOnlyList<PlanPrice>> PricesAsync(string priceCohort, CancellationToken cancellationToken);

    Task<CheckoutStart> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken);

    /// <summary>A short-lived link to the provider's page where the customer changes their card, sees invoices or cancels.</summary>
    Task<Uri> ManageUrlAsync(string customerId, string subscriptionId, CancellationToken cancellationToken);

    /// <summary>Brings the subscription in line with <paramref name="target"/>, changing nothing if it already is.</summary>
    Task ReconcileAsync(string subscriptionId, SubscriptionTarget target, CancellationToken cancellationToken);

    /// <summary>Ends the subscription now, for an organization that is gone.</summary>
    Task CancelAsync(string subscriptionId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a webhook: null if its signature doesn't check out. An event that isn't about a subscription comes back
    /// with no subscription.
    /// </summary>
    Task<ProviderEvent?> ReadWebhookAsync(HttpRequest request, CancellationToken cancellationToken);
}
