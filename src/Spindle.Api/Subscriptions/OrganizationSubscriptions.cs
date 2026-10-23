using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Spindle.Domain.Subscriptions;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Subscriptions;

/// <summary>What an organization may do now, and until when its trial runs.</summary>
internal sealed record SubscriptionStanding(OrganizationAccess Access, DateTimeOffset? TrialEndsAt)
{
    /// <summary>No limits: a self-hosted server, or an organization that isn't on a trial.</summary>
    public static readonly SubscriptionStanding Open = new(OrganizationAccess.Full, null);
}

internal sealed class OrganizationSubscriptions(AppDbContext db, IOptions<SubscriptionOptions> options, TimeProvider timeProvider)
{
    public bool Enabled => options.Value.Enabled;

    /// <summary>How long the trial of a new organization lasts.</summary>
    public int TrialDays => options.Value.TrialDays;

    /// <summary>
    /// Starts the trial of a new organization of someone's own, in the current price cohort, which it keeps. A self-hosted
    /// server records it too, so every organization has a subscription row whether or not anyone looks at it.
    /// </summary>
    public void StartTrial(Guid organizationId) => db.OrganizationSubscriptions.Add(OrganizationSubscription.StartTrial(
        organizationId,
        timeProvider.GetUtcNow(),
        TimeSpan.FromDays(options.Value.TrialDays),
        options.Value.CurrentPriceCohort));

    /// <summary>Where each organization stands now. On a self-hosted server, every one is <see cref="SubscriptionStanding.Open"/>.</summary>
    public async Task<IReadOnlyDictionary<Guid, SubscriptionStanding>> StandingsAsync(
        IReadOnlyCollection<Guid> organizationIds,
        CancellationToken cancellationToken)
    {
        if (!Enabled || organizationIds.Count == 0)
        {
            return organizationIds.ToDictionary(id => id, _ => SubscriptionStanding.Open);
        }

        var subscriptions = await db.OrganizationSubscriptions.AsNoTracking()
            .Where(s => organizationIds.Contains(s.OrganizationId))
            .ToDictionaryAsync(s => s.OrganizationId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        // An organization without a row has never been limited, so it isn't now either.
        return organizationIds.ToDictionary(
            id => id,
            id => subscriptions.TryGetValue(id, out var subscription) ? Standing(subscription, now) : SubscriptionStanding.Open);
    }

    public static SubscriptionStanding Standing(OrganizationSubscription subscription, DateTimeOffset now) => new(
        subscription.AccessAt(now),
        subscription.Status == SubscriptionStatus.Trialing ? subscription.TrialEndsAt : null);

    /// <summary>
    /// The people the organization pays for: everyone in at least one of its workspaces, once however many they are
    /// in. Pending invitations are not counted. Organization membership isn't the measure, because removing someone
    /// from a workspace leaves them a member of the organization.
    /// </summary>
    public async Task<int> SeatsAsync(Guid organizationId, CancellationToken cancellationToken) =>
        (await SeatCountsAsync([organizationId], cancellationToken)).GetValueOrDefault(organizationId);

    /// <summary>The seats of each organization, as <see cref="SeatsAsync"/> counts them. Organizations without any are left out.</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> SeatCountsAsync(IReadOnlyCollection<Guid> organizationIds, CancellationToken cancellationToken) =>
        await (
            from member in db.WorkspaceMembers
            join workspace in db.Workspaces on member.WorkspaceId equals workspace.Id
            where organizationIds.Contains(workspace.OrganizationId)
            group member.UserId by workspace.OrganizationId into seats
            select new { OrganizationId = seats.Key, Count = seats.Distinct().Count() })
            .ToDictionaryAsync(s => s.OrganizationId, s => s.Count, cancellationToken);
}
