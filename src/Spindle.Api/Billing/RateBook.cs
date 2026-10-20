using Microsoft.EntityFrameworkCore;
using NodaTime;
using Spindle.Api.Workspaces;
using Spindle.Domain.Billing;
using Spindle.Domain.Common;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Billing;

/// <summary>
/// The current workspace's rates over time. A rate changed without a date is corrected: its newest period takes the
/// new amount. A rate changed from a day on starts a new period then, so time before that day keeps its old rate.
/// The changes are saved by the caller, together with whatever else it changes.
/// </summary>
internal sealed class RateBook(AppDbContext db, CurrentWorkspace workspace, TimeProvider timeProvider)
{
    private DateOnly? today;

    /// <summary>All of the workspace's rates, for working out what time is billed at and costs.</summary>
    public Task<RateCard> CardAsync(CancellationToken cancellationToken) => CardAsync(workspace.Id, cancellationToken);

    /// <summary>All of a workspace's rates, for whoever is not inside it, such as a shared report's reader.</summary>
    public async Task<RateCard> CardAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        new((await db.Rates.AsNoTracking().Where(r => r.WorkspaceId == workspaceId).ToListAsync(cancellationToken)).Select(r => r.Period));

    /// <summary>
    /// Sets the scope's rate, from <paramref name="from"/> on if given. Nothing changes when the rate is already that
    /// amount, so forms can send every rate they show.
    /// </summary>
    /// <param name="field">The request's field for the amount, for validation errors.</param>
    /// <param name="fromField">The request's field for the first day.</param>
    public async Task SetAsync(RateScope scope, decimal? amount, DateOnly? from, string field, string fromField, CancellationToken cancellationToken)
    {
        Rate.Check(scope.Kind, amount, field);
        var periods = await PeriodsAsync(scope, cancellationToken);

        if (from is not { } day)
        {
            var newest = periods.LastOrDefault();
            if (newest is null)
            {
                if (amount is not null)
                {
                    db.Rates.Add(Rate.Create(workspace.Id, scope, null, amount, field));
                }
            }
            else if (newest.Amount != amount)
            {
                newest.Change(amount, field);
            }

            return;
        }

        if (day > await TodayAsync(cancellationToken))
        {
            throw new DomainValidationException(fromField, "A new rate can start today at the latest.");
        }

        if (periods.FirstOrDefault(p => p.EffectiveFrom == day) is { } existing)
        {
            existing.Change(amount, field);
        }
        else if (periods.LastOrDefault(p => (p.EffectiveFrom ?? DateOnly.MinValue) <= day)?.Amount != amount)
        {
            db.Rates.Add(Rate.Create(workspace.Id, scope, day, amount, field));
        }
    }

    /// <summary>Ends the scope's rate after today, e.g. when someone leaves a project. Time until then keeps it.</summary>
    public async Task EndAsync(RateScope scope, CancellationToken cancellationToken)
    {
        var periods = await PeriodsAsync(scope, cancellationToken);
        if (periods.LastOrDefault()?.Amount is null)
        {
            return;
        }

        var tomorrow = (await TodayAsync(cancellationToken)).AddDays(1);
        if (periods.FirstOrDefault(p => p.EffectiveFrom == tomorrow) is { } existing)
        {
            existing.Change(null, "hourlyRate");
        }
        else
        {
            db.Rates.Add(Rate.Create(workspace.Id, scope, tomorrow, null, "hourlyRate"));
        }
    }

    /// <summary>Today in the caller's calendar.</summary>
    public async Task<DateOnly> TodayAsync(CancellationToken cancellationToken)
    {
        if (today is null)
        {
            var id = await db.Users.Where(u => u.Id == workspace.UserId).Select(u => u.TimeZone).SingleAsync(cancellationToken);
            var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(id) ?? DateTimeZone.Utc;
            today = Instant.FromDateTimeOffset(timeProvider.GetUtcNow()).InZone(zone).Date.ToDateOnly();
        }

        return today.Value;
    }

    /// <summary>The scope's periods oldest first, tracked for changes, including those added in this request.</summary>
    private async Task<List<Rate>> PeriodsAsync(RateScope scope, CancellationToken cancellationToken)
    {
        var (kind, projectId, userId) = scope;
        await db.Rates
            .Where(r => r.WorkspaceId == workspace.Id && r.Kind == kind && r.ProjectId == projectId && r.UserId == userId)
            .LoadAsync(cancellationToken);

        return db.Rates.Local
            .Where(r => r.WorkspaceId == workspace.Id && r.Scope == scope && db.Entry(r).State != EntityState.Deleted)
            .OrderBy(r => r.EffectiveFrom ?? DateOnly.MinValue)
            .ToList();
    }
}
