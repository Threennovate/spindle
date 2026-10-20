using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Common;
using Spindle.Api.Subscriptions;
using Spindle.Domain.Organizations;
using Spindle.Domain.Subscriptions;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Operator;

/// <summary>Which organizations the list shows.</summary>
internal enum OrganizationFilter
{
    All,

    /// <summary>On a trial that hasn't ended.</summary>
    Trialing,

    /// <summary>On a trial that ends within two weeks.</summary>
    TrialEndingSoon,

    ReadOnly,

    /// <summary>Paid for, including a payment the provider is still retrying.</summary>
    Paying,

    Comped,
}

/// <param name="Hosted">Whether this is the hosted service. Without it the subscription counts are all zero.</param>
/// <param name="NewLast7Days">Organizations made in the last 7 days, which is to say sign-ups other than through an invitation.</param>
public sealed record OperatorOverviewResponse(
    bool Hosted,
    int Organizations,
    int NewLast7Days,
    int NewLast30Days,
    int Trialing,
    int TrialEndingSoon,
    int ReadOnly,
    int Paying,
    int Comped);

/// <summary>
/// An organization as operators see it: what it is, who to contact, and where its subscription stands. Never its
/// time, projects, clients or money.
/// </summary>
/// <param name="Status">Empty on a self-hosted server.</param>
/// <param name="PriceCohort">The prices it pays or will pay, e.g. <c>founding</c>. Empty on a self-hosted server.</param>
/// <param name="LastActivityAt">When someone in it last added or changed time.</param>
public sealed record OperatorOrganizationResponse(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    string? OwnerName,
    string? OwnerEmail,
    int Seats,
    SubscriptionStatus? Status,
    OrganizationAccess Access,
    DateTimeOffset? TrialEndsAt,
    DateTimeOffset? PaidUntil,
    string? PriceCohort,
    DateTimeOffset? LastActivityAt);

/// <param name="Total">How many organizations match, of which at most the newest 200 are listed.</param>
public sealed record OperatorOrganizationsResponse(int Total, IReadOnlyList<OperatorOrganizationResponse> Organizations);

public sealed record SubscriptionChangeResponse(
    DateTimeOffset At,
    string OperatorEmail,
    SubscriptionChangeKind Kind,
    SubscriptionStatus PreviousStatus,
    DateTimeOffset? TrialEndsAt);

public sealed record TrialRequest(DateTimeOffset TrialEndsAt);

/// <summary>A price cohort: the prices its organizations keep for good, and how many there are.</summary>
/// <param name="Current">New organizations join this one.</param>
/// <param name="HasPrices">Its prices are set up. An organization in a cohort without them can't pay, and its plan isn't touched.</param>
/// <param name="Prices">What a seat costs, as the payment provider has it. Empty if it couldn't be asked.</param>
/// <param name="Organizations">Organizations in the cohort, whatever their subscription.</param>
/// <param name="Paying">Of those, the ones paying, including a payment the provider is still retrying.</param>
public sealed record OperatorPriceCohortResponse(
    string Name,
    bool Current,
    bool HasPrices,
    IReadOnlyList<PlanPrice> Prices,
    int Organizations,
    int Paying);

internal static class OperatorEndpoints
{
    public const int PageSize = 200;

    private static readonly TimeSpan EndingSoon = TimeSpan.FromDays(14);

    public static IEndpointRouteBuilder MapOperatorEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/operator")
            .WithTags("Operator")
            .RequireAuthorization()
            .AddEndpointFilter<OperatorFilter>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/overview", GetOverviewAsync).WithName("getOperatorOverview");
        group.MapGet("/organizations", ListOrganizationsAsync).WithName("listOperatorOrganizations");
        group.MapGet("/price-cohorts", ListPriceCohortsAsync).WithName("listPriceCohorts");
        group.MapGet("/organizations/{organizationId:guid}/changes", ListChangesAsync).WithName("listSubscriptionChanges");
        group.MapPut("/organizations/{organizationId:guid}/trial", PutOnTrialAsync).WithName("putOnTrial");
        group.MapPost("/organizations/{organizationId:guid}/comp", CompAsync).WithName("compOrganization");

        return api;
    }

    // There are few organizations yet, so they are read whole and counted and filtered here. When that gets slow, the
    // counting moves into SQL.

    private static async Task<Ok<OperatorOverviewResponse>> GetOverviewAsync(
        AppDbContext db,
        OrganizationSubscriptions subscriptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var organizations = await LoadAsync(db, subscriptions.Enabled, now, cancellationToken);

        return TypedResults.Ok(new OperatorOverviewResponse(
            subscriptions.Enabled,
            organizations.Count,
            organizations.Count(o => o.CreatedAt > now.AddDays(-7)),
            organizations.Count(o => o.CreatedAt > now.AddDays(-30)),
            organizations.Count(o => Matches(o, OrganizationFilter.Trialing, now)),
            organizations.Count(o => Matches(o, OrganizationFilter.TrialEndingSoon, now)),
            organizations.Count(o => Matches(o, OrganizationFilter.ReadOnly, now)),
            organizations.Count(o => Matches(o, OrganizationFilter.Paying, now)),
            organizations.Count(o => Matches(o, OrganizationFilter.Comped, now))));
    }

    /// <summary>
    /// The newest organizations first: all, or only those <c>trialing</c>, with their <c>trialEndingSoon</c> (within two
    /// weeks), <c>readOnly</c>, <c>paying</c> or <c>comped</c>; and, if given, with the search in their name or owner's
    /// email.
    /// </summary>
    private static async Task<Results<Ok<OperatorOrganizationsResponse>, ValidationProblem>> ListOrganizationsAsync(
        AppDbContext db,
        OrganizationSubscriptions subscriptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        [FromQuery] string? filter = null,
        [FromQuery] string? search = null)
    {
        // In any case, like enums in JSON.
        var only = OrganizationFilter.All;
        if (filter is not null && (!Enum.TryParse(filter, ignoreCase: true, out only) || !Enum.IsDefined(only) || char.IsDigit(filter[0])))
        {
            return Problems.Validation("filter", "Filter by trialing, trialEndingSoon, readOnly, paying or comped.");
        }

        var now = timeProvider.GetUtcNow();
        var term = search?.Trim();
        var matching = (await LoadAsync(db, subscriptions.Enabled, now, cancellationToken))
            .Where(o => Matches(o, only, now))
            .Where(o => string.IsNullOrEmpty(term)
                || o.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (o.OwnerEmail?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(o => o.CreatedAt)
            .ToList();
        var page = matching.Take(PageSize).ToList();
        var ids = page.Select(o => o.Id).ToList();

        var seats = await subscriptions.SeatCountsAsync(ids, cancellationToken);
        var lastActivity = await (
            from entry in db.TimeEntries
            join workspace in db.Workspaces on entry.WorkspaceId equals workspace.Id
            where ids.Contains(workspace.OrganizationId)
            group entry.UpdatedAt by workspace.OrganizationId into changes
            select new { OrganizationId = changes.Key, At = changes.Max() })
            .ToDictionaryAsync(a => a.OrganizationId, a => a.At, cancellationToken);

        return TypedResults.Ok(new OperatorOrganizationsResponse(
            matching.Count,
            [.. page.Select(o => new OperatorOrganizationResponse(
                o.Id,
                o.Name,
                o.CreatedAt,
                o.OwnerName,
                o.OwnerEmail,
                seats.GetValueOrDefault(o.Id),
                o.Subscription?.Status,
                o.Access,
                o.Subscription?.TrialEndsAt,
                o.Subscription?.PaidUntil,
                o.Subscription?.PriceCohort,
                lastActivity.TryGetValue(o.Id, out var at) ? at : null))]));
    }

    /// <summary>
    /// The price cohorts set up on this server and the ones organizations are in, so a cohort missing from the
    /// settings shows too. None on a self-hosted server. Prices are changed in the provider's catalog and the settings.
    /// </summary>
    private static async Task<Ok<List<OperatorPriceCohortResponse>>> ListPriceCohortsAsync(
        AppDbContext db,
        OrganizationSubscriptions subscriptions,
        SubscriptionBilling billing,
        TimeProvider timeProvider,
        ILogger<SubscriptionBilling> logger,
        CancellationToken cancellationToken)
    {
        if (!subscriptions.Enabled)
        {
            return TypedResults.Ok(new List<OperatorPriceCohortResponse>());
        }

        var now = timeProvider.GetUtcNow();
        var organizations = await LoadAsync(db, hosted: true, now, cancellationToken);
        var names = billing.PriceCohorts
            .Concat(organizations.Select(o => o.Subscription?.PriceCohort).OfType<string>())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        var cohorts = new List<OperatorPriceCohortResponse>();
        foreach (var name in names)
        {
            IReadOnlyList<PlanPrice> prices = [];
            if (billing.CheckoutAvailable && billing.HasPrices(name))
            {
                try
                {
                    prices = await billing.PricesAsync(name, cancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or PaddleException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Could not load the prices of price cohort {PriceCohort} from the provider", name);
                }
            }

            var members = organizations.Where(o => o.Subscription?.PriceCohort == name).ToList();
            cohorts.Add(new OperatorPriceCohortResponse(
                name,
                name == billing.CurrentPriceCohort,
                billing.HasPrices(name),
                prices,
                members.Count,
                members.Count(o => Matches(o, OrganizationFilter.Paying, now))));
        }

        return TypedResults.Ok(cohorts);
    }

    private static async Task<Results<Ok<List<SubscriptionChangeResponse>>, NotFound>> ListChangesAsync(
        Guid organizationId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await db.Organizations.AnyAsync(o => o.Id == organizationId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(await db.SubscriptionChanges
            .Where(c => c.OrganizationId == organizationId)
            .OrderByDescending(c => c.At)
            .Select(c => new SubscriptionChangeResponse(c.At, c.OperatorEmail, c.Kind, c.PreviousStatus, c.TrialEndsAt))
            .ToListAsync(cancellationToken));
    }

    /// <summary>Extends a trial, reopens an ended one, or puts a comped organization back on one.</summary>
    private static Task<Results<NoContent, NotFound>> PutOnTrialAsync(
        Guid organizationId,
        TrialRequest request,
        AppDbContext db,
        OrganizationSubscriptions subscriptions,
        CurrentOperator current,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        ChangeAsync(organizationId, db, subscriptions, current, timeProvider, SubscriptionChangeKind.Trial, cancellationToken, (subscription, now) =>
        {
            subscription.PutOnTrial(request.TrialEndsAt, now);
            return subscription.TrialEndsAt;
        });

    /// <summary>Makes an organization free for good. Doing it again changes nothing.</summary>
    private static Task<Results<NoContent, NotFound>> CompAsync(
        Guid organizationId,
        AppDbContext db,
        OrganizationSubscriptions subscriptions,
        CurrentOperator current,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        ChangeAsync(organizationId, db, subscriptions, current, timeProvider, SubscriptionChangeKind.Comped, cancellationToken, (subscription, _) =>
        {
            subscription.Comp();
            return null;
        });

    /// <summary>Applies a change and records it, unless it changed nothing. Not found on a self-hosted server.</summary>
    private static async Task<Results<NoContent, NotFound>> ChangeAsync(
        Guid organizationId,
        AppDbContext db,
        OrganizationSubscriptions subscriptions,
        CurrentOperator current,
        TimeProvider timeProvider,
        SubscriptionChangeKind kind,
        CancellationToken cancellationToken,
        Func<OrganizationSubscription, DateTimeOffset, DateTimeOffset?> change)
    {
        if (!subscriptions.Enabled)
        {
            return TypedResults.NotFound();
        }

        var subscription = await db.OrganizationSubscriptions.SingleOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken);
        if (subscription is null || !await db.Organizations.AnyAsync(o => o.Id == organizationId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var now = timeProvider.GetUtcNow();
        var (status, trialEndsAt) = (subscription.Status, subscription.TrialEndsAt);
        var newTrialEnd = change(subscription, now);
        if (subscription.Status != status || subscription.TrialEndsAt != trialEndsAt)
        {
            db.SubscriptionChanges.Add(SubscriptionChange.Record(organizationId, status, kind, newTrialEnd, current.User.Email!, now));
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private sealed record Row(
        Guid Id,
        string Name,
        DateTimeOffset CreatedAt,
        string? OwnerName,
        string? OwnerEmail,
        OrganizationSubscription? Subscription,
        OrganizationAccess Access);

    private static async Task<List<Row>> LoadAsync(AppDbContext db, bool hosted, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var organizations = await (
            from organization in db.Organizations
            join subscription in db.OrganizationSubscriptions on organization.Id equals subscription.OrganizationId into subscriptions
            from subscription in subscriptions.DefaultIfEmpty()
            select new
            {
                organization.Id,
                organization.Name,
                organization.CreatedAt,
                Subscription = subscription,
                // The first owner, if the organization has several.
                Owner = (
                    from member in db.OrganizationMembers
                    join user in db.Users on member.UserId equals user.Id
                    where member.OrganizationId == organization.Id && member.Role == OrganizationRole.Owner
                    orderby member.CreatedAt
                    select new { user.FullName, user.Email })
                    .FirstOrDefault(),
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return [.. organizations.Select(o =>
        {
            // On a self-hosted server the rows are kept but mean nothing, so operators don't see them.
            var subscription = hosted ? o.Subscription : null;
            return new Row(
                o.Id,
                o.Name,
                o.CreatedAt,
                o.Owner?.FullName,
                o.Owner?.Email,
                subscription,
                subscription?.AccessAt(now) ?? OrganizationAccess.Full);
        })];
    }

    private static bool Matches(Row organization, OrganizationFilter filter, DateTimeOffset now)
    {
        var status = organization.Subscription?.Status;
        var full = organization.Access == OrganizationAccess.Full;
        return filter switch
        {
            OrganizationFilter.Trialing => status == SubscriptionStatus.Trialing && full,
            OrganizationFilter.TrialEndingSoon => status == SubscriptionStatus.Trialing && full
                && organization.Subscription!.TrialEndsAt <= now + EndingSoon,
            OrganizationFilter.ReadOnly => !full,
            OrganizationFilter.Paying => status is SubscriptionStatus.Active or SubscriptionStatus.PastDue
                || (status == SubscriptionStatus.Canceled && full),
            OrganizationFilter.Comped => status == SubscriptionStatus.Comped,
            _ => true,
        };
    }
}
