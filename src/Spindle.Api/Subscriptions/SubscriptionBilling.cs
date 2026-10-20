using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Spindle.Api.Common;
using Spindle.Domain.Subscriptions;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Subscriptions;

/// <summary>
/// Paying for the hosted service: starting a checkout, taking in what the provider reports, and keeping the
/// provider's subscription in line with ours (its seats, and its first charge at the end of our trial). Our
/// <see cref="OrganizationSubscription"/> stays the one record of what an organization may do; the provider only
/// tells it about payments.
/// </summary>
internal sealed class SubscriptionBilling(
    AppDbContext db,
    ISubscriptionProvider provider,
    OrganizationSubscriptions subscriptions,
    IOptions<SubscriptionOptions> options,
    TimeProvider timeProvider,
    ILogger<SubscriptionBilling> logger)
{
    /// <summary>Whether people can pay on this server: the hosted service, with the provider set up.</summary>
    public bool CheckoutAvailable => options.Value.Enabled && provider.IsConfigured;

    /// <summary>The price cohort new organizations join.</summary>
    public string? CurrentPriceCohort => options.Value.CurrentPriceCohort;

    /// <summary>The price cohorts with their prices set up on this server.</summary>
    public IReadOnlyCollection<string> PriceCohorts => provider.PriceCohorts;

    public bool HasPrices(string priceCohort) => provider.HasPrices(priceCohort);

    public Task<IReadOnlyList<PlanPrice>> PricesAsync(string priceCohort, CancellationToken cancellationToken) =>
        provider.PricesAsync(priceCohort, cancellationToken);

    /// <summary>
    /// The prices of the organization's price cohort, or of the current one for an organization from before cohorts.
    /// None if its cohort has no prices on this server.
    /// </summary>
    public async Task<IReadOnlyList<PlanPrice>> PricesAsync(OrganizationSubscription subscription, CancellationToken cancellationToken)
    {
        var cohort = subscription.PriceCohort ?? options.Value.CurrentPriceCohort;
        if (cohort is null || !provider.HasPrices(cohort))
        {
            logger.LogError("Organization {OrganizationId} is in price cohort {PriceCohort}, which has no prices", subscription.OrganizationId, cohort);
            return [];
        }

        return await provider.PricesAsync(cohort, cancellationToken);
    }

    /// <summary>
    /// Prepares the provider's checkout for the organization's current seats, at the price of its price cohort. During
    /// the trial, the days left of it come along, so the first charge is when it ends. Null if its cohort has no prices
    /// on this server.
    /// </summary>
    public async Task<CheckoutStart?> StartCheckoutAsync(
        OrganizationSubscription subscription,
        BillingInterval interval,
        string buyerEmail,
        CancellationToken cancellationToken)
    {
        var cohort = subscription.PriceCohort;
        if (cohort is null)
        {
            // From before cohorts: it joins the current one now, and keeps it.
            var tracked = await db.OrganizationSubscriptions.SingleAsync(s => s.OrganizationId == subscription.OrganizationId, cancellationToken);
            cohort = tracked.JoinPriceCohort(options.Value.CurrentPriceCohort!);
            await db.SaveChangesAsync(cancellationToken);
        }

        if (!provider.HasPrices(cohort))
        {
            logger.LogError("Organization {OrganizationId} is in price cohort {PriceCohort}, which has no prices", subscription.OrganizationId, cohort);
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var trialDays = subscription.Status == SubscriptionStatus.Trialing && subscription.TrialEndsAt is { } endsAt && endsAt > now
            ? (int)Math.Ceiling((endsAt - now).TotalDays)
            : 0;
        var seats = Math.Max(1, await subscriptions.SeatsAsync(subscription.OrganizationId, cancellationToken));

        return await provider.StartCheckoutAsync(
            new CheckoutRequest(subscription.OrganizationId, cohort, interval, seats, trialDays, buyerEmail, subscription.ProviderCustomerId),
            cancellationToken);
    }

    public Task<Uri> ManageUrlAsync(OrganizationSubscription subscription, CancellationToken cancellationToken) =>
        provider.ManageUrlAsync(subscription.ProviderCustomerId!, subscription.ProviderSubscriptionId!, cancellationToken);

    /// <summary>
    /// Takes in a provider event once: an event delivered again changes nothing. Returns the subscription it changed,
    /// if it changed one.
    /// </summary>
    public async Task<OrganizationSubscription?> HandleAsync(ProviderEvent providerEvent, CancellationToken cancellationToken)
    {
        if (await db.ProviderEventReceipts.AnyAsync(r => r.Provider == provider.Name && r.EventId == providerEvent.EventId, cancellationToken))
        {
            return null;
        }

        OrganizationSubscription? changed = null;
        if (providerEvent.Subscription is { } reported)
        {
            // The checkout names the organization; a subscription the provider made later is found by its ID.
            var subscription = providerEvent.OrganizationId is { } organizationId
                ? await db.OrganizationSubscriptions.SingleOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken)
                : null;
            subscription ??= await db.OrganizationSubscriptions
                .SingleOrDefaultAsync(s => s.ProviderSubscriptionId == reported.SubscriptionId, cancellationToken);

            if (subscription is null)
            {
                logger.LogWarning("No organization for {Provider} subscription {SubscriptionId}", provider.Name, reported.SubscriptionId);
            }
            else if (subscription.Apply(reported))
            {
                changed = subscription;
            }
        }

        db.ProviderEventReceipts.Add(ProviderEventReceipt.Record(provider.Name, providerEvent.EventId, timeProvider.GetUtcNow()));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            // The same event, delivered twice at once: the other delivery took it in.
            return null;
        }

        return changed;
    }

    /// <summary>
    /// Brings the provider's subscription in line with the organization: the price of its price cohort, its seats now,
    /// and the end of its trial. A subscription whose cohort has no prices on this server is left alone, so a mistake
    /// in the settings never moves anyone to another price.
    /// </summary>
    public async Task ReconcileAsync(OrganizationSubscription subscription, CancellationToken cancellationToken)
    {
        if (!CheckoutAvailable || !subscription.HasPlan || subscription.ProviderSubscriptionId is not { } subscriptionId)
        {
            return;
        }

        if (subscription.PriceCohort is not { } cohort || !provider.HasPrices(cohort))
        {
            logger.LogError(
                "Not reconciling the subscription of organization {OrganizationId}: its price cohort {PriceCohort} has no prices",
                subscription.OrganizationId,
                subscription.PriceCohort);
            return;
        }

        var seats = Math.Max(1, await subscriptions.SeatsAsync(subscription.OrganizationId, cancellationToken));
        var firstChargeAt = subscription.Status == SubscriptionStatus.Trialing ? subscription.TrialEndsAt : null;
        await provider.ReconcileAsync(
            subscriptionId,
            new SubscriptionTarget(cohort, subscription.Interval ?? BillingInterval.Month, seats, firstChargeAt),
            cancellationToken);
    }

    /// <summary>Reconciles the organization's plan, if it has one.</summary>
    public async Task ReconcileOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (await db.OrganizationSubscriptions.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken) is { } subscription)
        {
            await ReconcileAsync(subscription, cancellationToken);
        }
    }

    /// <summary>Reconciles every organization with a plan; one failing doesn't stop the others. Returns how many failed.</summary>
    public async Task<int> ReconcileAllAsync(CancellationToken cancellationToken)
    {
        if (!CheckoutAvailable)
        {
            return 0;
        }

        var withPlans = await db.OrganizationSubscriptions.AsNoTracking()
            .Where(s => s.ProviderSubscriptionId != null
                && (s.ProviderStatus == ProviderSubscriptionStatus.Trialing
                    || s.ProviderStatus == ProviderSubscriptionStatus.Active
                    || s.ProviderStatus == ProviderSubscriptionStatus.PastDue))
            .ToListAsync(cancellationToken);

        var failed = 0;
        foreach (var subscription in withPlans)
        {
            try
            {
                await ReconcileAsync(subscription, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                logger.LogError(exception, "Could not reconcile the subscription of organization {OrganizationId}", subscription.OrganizationId);
            }
        }

        return failed;
    }

    /// <summary>Ends the provider's subscriptions of organizations that are gone. Best effort: they are logged if it fails.</summary>
    public async Task CancelEndedAsync(IEnumerable<string> subscriptionIds, CancellationToken cancellationToken)
    {
        if (!provider.IsConfigured)
        {
            return;
        }

        foreach (var subscriptionId in subscriptionIds)
        {
            try
            {
                await provider.CancelAsync(subscriptionId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Could not cancel {Provider} subscription {SubscriptionId} of a deleted organization", provider.Name, subscriptionId);
            }
        }
    }
}

/// <summary>
/// Reconciles the provider's subscriptions with ours once an hour, on the hosted service with a provider: seats that
/// changed, and trials whose first charge isn't at their end yet. Assumes one API instance, like the reminders.
/// </summary>
internal sealed class SubscriptionSyncService(
    IServiceScopeFactory scopes,
    IOptions<SubscriptionOptions> subscriptions,
    TimeProvider timeProvider,
    ILogger<SubscriptionSyncService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!subscriptions.Value.Enabled || !subscriptions.Value.Paddle.IsConfigured)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var failed = await scope.ServiceProvider.GetRequiredService<SubscriptionBilling>().ReconcileAllAsync(stoppingToken);
                if (failed > 0)
                {
                    logger.LogWarning("{Count} subscriptions could not be reconciled with the provider", failed);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Could not reconcile the subscriptions with the provider");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
