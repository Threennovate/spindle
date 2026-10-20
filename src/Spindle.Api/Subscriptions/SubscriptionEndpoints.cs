using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Spindle.Api.Common;
using Spindle.Api.Auth;
using Spindle.Domain.Organizations;
using Spindle.Domain.Subscriptions;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Subscriptions;

/// <param name="Hosted">Whether this is the hosted service, where organizations have a trial and then pay. False on a self-hosted server.</param>
/// <param name="RegistrationOpen">Whether anyone can create an account. When false, people join only through invitations.</param>
/// <param name="TermsUrl">The terms of service that new accounts agree to, if the server has them.</param>
/// <param name="PrivacyUrl">The privacy policy shown next to the terms.</param>
/// <param name="SourceUrl">
/// Where to get the source code of the version this server runs. Always set; nullable because servers from before it
/// leave it out, and the apps read those too.
/// </param>
public sealed record ServerResponse(bool Hosted, bool RegistrationOpen, string? TermsUrl, string? PrivacyUrl, string? SourceUrl)
{
    /// <summary>
    /// The providers people can sign in and sign up with besides a password, e.g. Google and Apple, in the order to show
    /// them. Not required, so apps can read servers from before it: missing means none.
    /// </summary>
    public IReadOnlyList<SignInProviderResponse>? SignInProviders { get; init; }
}

/// <param name="TrialEndsAt">While the organization is on its trial.</param>
/// <param name="PaidUntil">The end of the period paid for, once someone has paid.</param>
/// <param name="Seats">The people the organization pays for: everyone in at least one of its workspaces, counted once. Pending invitations are not counted.</param>
/// <param name="CheckoutAvailable">Whether this server takes payments yet.</param>
/// <param name="HasPlan">Someone chose a plan, and it renews or starts when the trial ends.</param>
/// <param name="Interval">How often the plan bills, once there is one.</param>
/// <param name="NextChargeAt">When the plan charges next: the end of the trial for a plan chosen during it.</param>
/// <param name="CancelsAtPeriodEnd">The plan was canceled and ends with the period paid for.</param>
/// <param name="Prices">What a seat costs per interval in the organization's price cohort, when paying is possible.</param>
public sealed record SubscriptionResponse(
    SubscriptionStatus Status,
    OrganizationAccess Access,
    DateTimeOffset? TrialEndsAt,
    DateTimeOffset? PaidUntil,
    int Seats,
    bool CheckoutAvailable,
    bool HasPlan,
    BillingInterval? Interval,
    DateTimeOffset? NextChargeAt,
    bool CancelsAtPeriodEnd,
    IReadOnlyList<PlanPrice> Prices);

public sealed record CheckoutRequestBody(BillingInterval Interval);

/// <param name="Url">The provider's page for the customer, valid for a short while.</param>
public sealed record ManageSubscriptionResponse(Uri Url);

internal static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder api)
    {
        // Read before signing in, e.g. by an app checking the address of a self-hosted server.
        api.MapGet("/server", GetServer)
            .WithName("getServer")
            .WithTags("Server")
            .AllowAnonymous();

        api.MapGet("/organizations/{organizationId:guid}/subscription", GetSubscriptionAsync)
            .WithName("getSubscription")
            .WithTags("Subscriptions")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var subscription = api.MapGroup("/organizations/{organizationId:guid}/subscription")
            .WithTags("Subscriptions")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        subscription.MapPost("/checkout", StartCheckoutAsync).WithName("startCheckout").ProducesProblem(StatusCodes.Status502BadGateway);
        subscription.MapPost("/manage", ManageAsync).WithName("manageSubscription").ProducesProblem(StatusCodes.Status502BadGateway);

        // The provider calls this; the signature, not a sign-in, says it's the provider.
        api.MapPost("/subscriptions/webhooks/{provider}", ReceiveWebhookAsync)
            .WithName("receiveSubscriptionWebhook")
            .WithTags("Subscriptions")
            .AllowAnonymous()
            .ExcludeFromDescription();

        return api;
    }

    private static Ok<ServerResponse> GetServer(
        OrganizationSubscriptions subscriptions, IOptions<AppOptions> app, IOptions<AuthOptions> auth, IEnumerable<IExternalSignInProvider> providers) =>
        TypedResults.Ok(new ServerResponse(
            subscriptions.Enabled,
            auth.Value.AllowRegistration,
            app.Value.HasTerms ? app.Value.TermsUrl : null,
            app.Value.HasTerms ? app.Value.PrivacyUrl : null,
            app.Value.SourceUrl)
        {
            SignInProviders = ExternalSignInEndpoints.Offered(providers),
        });

    /// <summary>
    /// The organization's trial or plan, for its owners and admins. Not found on a self-hosted server, and to anyone
    /// outside the organization.
    /// </summary>
    private static async Task<Results<Ok<SubscriptionResponse>, NotFound, ProblemHttpResult>> GetSubscriptionAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        AppDbContext db,
        OrganizationSubscriptions subscriptions,
        SubscriptionBilling billing,
        TimeProvider timeProvider,
        ILogger<SubscriptionBilling> logger,
        CancellationToken cancellationToken)
    {
        var (subscription, forbidden) = await ManagedAsync(organizationId, principal, db, subscriptions, cancellationToken);
        if (subscription is null)
        {
            return forbidden ? Forbidden() : TypedResults.NotFound();
        }

        IReadOnlyList<PlanPrice> prices = [];
        if (billing.CheckoutAvailable)
        {
            try
            {
                prices = await billing.PricesAsync(subscription, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or PaddleException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // The plan still shows; only the prices are missing until the provider answers again.
                logger.LogWarning(exception, "Could not load the prices from the provider");
            }
        }

        var standing = OrganizationSubscriptions.Standing(subscription, timeProvider.GetUtcNow());
        return TypedResults.Ok(new SubscriptionResponse(
            subscription.Status,
            standing.Access,
            standing.TrialEndsAt,
            subscription.PaidUntil,
            await subscriptions.SeatsAsync(organizationId, cancellationToken),
            billing.CheckoutAvailable,
            subscription.HasPlan,
            subscription.HasPlan ? subscription.Interval : null,
            subscription.HasPlan ? subscription.NextChargeAt : null,
            subscription.HasPlan && subscription.CancelsAtPeriodEnd,
            prices));
    }

    /// <summary>
    /// Prepares the provider's checkout for the organization, for its owners and admins. The price, seats and the
    /// days left of the trial are fixed here, not in the browser.
    /// </summary>
    private static async Task<Results<Ok<CheckoutStart>, NotFound, ProblemHttpResult>> StartCheckoutAsync(
        Guid organizationId,
        CheckoutRequestBody request,
        ClaimsPrincipal principal,
        AppDbContext db,
        OrganizationSubscriptions subscriptions,
        SubscriptionBilling billing,
        CancellationToken cancellationToken)
    {
        var (subscription, forbidden) = await ManagedAsync(organizationId, principal, db, subscriptions, cancellationToken);
        if (subscription is null)
        {
            return forbidden ? Forbidden() : TypedResults.NotFound();
        }

        if (!billing.CheckoutAvailable)
        {
            return Conflict("Paying isn't open on this server yet.");
        }

        if (subscription.HasPlan)
        {
            return Conflict("This organization already has a plan. Change it under Manage subscription.");
        }

        if (subscription.Status == SubscriptionStatus.Comped)
        {
            return Conflict("This organization is free; there is nothing to pay.");
        }

        var userId = principal.GetUserId();
        var email = await db.Users.Where(u => u.Id == userId).Select(u => u.Email).SingleAsync(cancellationToken);
        try
        {
            return await billing.StartCheckoutAsync(subscription, request.Interval, email!, cancellationToken) is { } start
                ? TypedResults.Ok(start)
                : Conflict("Paying isn't open for this organization yet. Write to us and we'll sort it out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or PaddleException)
        {
            return ProviderUnavailable(exception);
        }
    }

    /// <summary>A link to the provider's page where the plan's card, invoices and cancellation are, for its owners and admins.</summary>
    private static async Task<Results<Ok<ManageSubscriptionResponse>, NotFound, ProblemHttpResult>> ManageAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        AppDbContext db,
        OrganizationSubscriptions subscriptions,
        SubscriptionBilling billing,
        CancellationToken cancellationToken)
    {
        var (subscription, forbidden) = await ManagedAsync(organizationId, principal, db, subscriptions, cancellationToken);
        if (subscription is null)
        {
            return forbidden ? Forbidden() : TypedResults.NotFound();
        }

        if (!billing.CheckoutAvailable || subscription.ProviderCustomerId is null || subscription.ProviderSubscriptionId is null)
        {
            return Conflict("This organization has no plan to manage yet.");
        }

        try
        {
            return TypedResults.Ok(new ManageSubscriptionResponse(await billing.ManageUrlAsync(subscription, cancellationToken)));
        }
        catch (Exception exception) when (exception is HttpRequestException or PaddleException)
        {
            return ProviderUnavailable(exception);
        }
    }

    /// <summary>
    /// What the provider reports, taken in once per event. A plan chosen during the trial is then moved to charge
    /// first when the trial ends; if that fails, the hourly reconciliation tries again.
    /// </summary>
    private static async Task<Results<Ok, NotFound, UnauthorizedHttpResult>> ReceiveWebhookAsync(
        string provider,
        HttpRequest request,
        ISubscriptionProvider subscriptionProvider,
        SubscriptionBilling billing,
        ILogger<SubscriptionBilling> logger,
        CancellationToken cancellationToken)
    {
        if (!billing.CheckoutAvailable || provider != subscriptionProvider.Name)
        {
            return TypedResults.NotFound();
        }

        if (await subscriptionProvider.ReadWebhookAsync(request, cancellationToken) is not { } providerEvent)
        {
            return TypedResults.Unauthorized();
        }

        if (await billing.HandleAsync(providerEvent, cancellationToken) is { HasPlan: true } changed)
        {
            try
            {
                await billing.ReconcileAsync(changed, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or PaddleException)
            {
                logger.LogWarning(exception, "Could not reconcile the subscription of organization {OrganizationId} yet", changed.OrganizationId);
            }
        }

        return TypedResults.Ok();
    }

    /// <summary>
    /// The organization's subscription, for its owners and admins on the hosted service; otherwise why not. Not found
    /// on a self-hosted server and to anyone outside the organization.
    /// </summary>
    private static async Task<(OrganizationSubscription? Subscription, bool Forbidden)> ManagedAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        AppDbContext db,
        OrganizationSubscriptions subscriptions,
        CancellationToken cancellationToken)
    {
        if (!subscriptions.Enabled)
        {
            return (null, false);
        }

        var userId = principal.GetUserId();
        var role = await db.OrganizationMembers
            .Where(m => m.OrganizationId == organizationId && m.UserId == userId)
            .Select(m => (OrganizationRole?)m.Role)
            .SingleOrDefaultAsync(cancellationToken);
        if (role is null || role == OrganizationRole.Member)
        {
            return (null, role == OrganizationRole.Member);
        }

        var subscription = await db.OrganizationSubscriptions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken);
        return (subscription, false);
    }

    private static ProblemHttpResult Forbidden() =>
        TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the organization's owners and admins see its plan.");

    private static ProblemHttpResult Conflict(string title) => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title);

    private static ProblemHttpResult ProviderUnavailable(Exception exception) => TypedResults.Problem(
        statusCode: StatusCodes.Status502BadGateway,
        title: "The payment provider didn't answer as expected. Try again in a moment.",
        detail: exception is PaddleException paddle ? paddle.Code : null);
}
