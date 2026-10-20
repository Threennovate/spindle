using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Spindle.Api.Common;
using Spindle.Api.Reports;
using Spindle.Api.TimeEntries;
using Spindle.Api.Workspaces;
using Spindle.Domain.Billing;
using Spindle.Domain.Projects;
using Spindle.Domain.TimeEntries;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Billing;

/// <summary>A project's tracked and billed time, day by day, for whoever enters what is billed.</summary>
/// <param name="Days">The days of the period, in order. Each row's lists follow them.</param>
/// <param name="SeparateBilledTime">Whether the project's billed time is entered separately; if not, it is billed as tracked.</param>
/// <param name="BillingMode">How days left empty are billed: as tracked, or as a full day.</param>
public sealed record ProjectBillingResponse(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<DateOnly> Days,
    bool SeparateBilledTime,
    string Currency,
    IReadOnlyList<BillingRow> Rows,
    BillingMode BillingMode);

/// <summary>One person's time on the project.</summary>
/// <param name="IsMember">Whether they are still in the workspace. Billed time can be entered for members only.</param>
/// <param name="TrackedDays">Billable time tracked on each day.</param>
/// <param name="BilledDays">Time entered as billed on each day; null where nothing was entered.</param>
/// <param name="DefaultDays">
/// What each day is billed as when nothing is entered: a full day on working days with time tracked, on projects
/// billed by the day, and otherwise the billable time tracked.
/// </param>
/// <param name="TrackedSeconds">All the time tracked in the period, billable or not.</param>
/// <param name="BilledSeconds">The time billed in the period.</param>
/// <param name="Amount">What the billed time comes to, at the rates of each day. Shown to admins only.</param>
/// <param name="LaborCost">What the tracked time cost. Shown to admins only.</param>
/// <param name="EntryDescriptions">
/// What the day's billable entries on the project say was done, each once, in the order they were tracked: how a
/// timesheet link describes the day unless it was described by hand.
/// </param>
/// <param name="Descriptions">What each day was described as by hand, for timesheet links; null where the entries describe it.</param>
public sealed record BillingRow(
    Guid UserId,
    string FullName,
    bool IsMember,
    IReadOnlyList<long> TrackedDays,
    IReadOnlyList<long?> BilledDays,
    long TrackedSeconds,
    long BilledSeconds,
    decimal? Amount,
    decimal? LaborCost,
    IReadOnlyList<long> DefaultDays,
    IReadOnlyList<IReadOnlyList<string>> EntryDescriptions,
    IReadOnlyList<string?> Descriptions);

public sealed record BilledTimeRequest
{
    /// <summary>The time billed on the day, from 0 to 24 hours.</summary>
    [Required]
    [Range(0, BilledTime.MaxSeconds)]
    public long? Seconds { get; init; }
}

public sealed record BilledDayDescriptionRequest
{
    /// <summary>What was done that day, as timesheet links show it.</summary>
    [Required]
    [StringLength(BilledTime.DescriptionMaxLength)]
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// The time billed on projects whose billed time is entered separately. Admins and the project's managers enter it,
/// for any member, day by day; days without an entry are billed as tracked.
/// </summary>
internal static class BillingEndpoints
{
    public const int MaxDays = 31;

    private const string CannotManage = "Only admins and the project's managers can enter its billed time.";

    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/projects/{projectId:guid}/billing").WithTags("Billing");

        group.MapGet("", GetAsync).WithName("getProjectBilling");
        group.MapPut("/{userId:guid}/{day}", SetAsync).WithName("setBilledTime");
        group.MapDelete("/{userId:guid}/{day}", ClearAsync).WithName("clearBilledTime");
        group.MapPut("/{userId:guid}/{day}/description", DescribeAsync).WithName("describeBilledDay");
        group.MapDelete("/{userId:guid}/{day}/description", ClearDescriptionAsync).WithName("clearBilledDayDescription");

        return workspace;
    }

    /// <summary>Everyone's tracked and billed time on the project, for the days from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static async Task<Results<Ok<ProjectBillingResponse>, NotFound, ValidationProblem, ProblemHttpResult>> GetAsync(
        Guid projectId,
        DateOnly from,
        DateOnly to,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        RateBook rates,
        CancellationToken cancellationToken)
    {
        var project = await policy.VisibleProjects().AsNoTracking().SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return TypedResults.NotFound();
        }

        if (!await policy.CanManageProjectAsync(projectId, cancellationToken))
        {
            return WorkspacePolicy.Forbidden(CannotManage);
        }

        if (to < from || to.DayNumber - from.DayNumber >= MaxDays)
        {
            return Problems.Validation("to", $"Choose at most {MaxDays} days, ending on or after the first one.");
        }

        var zone = await CallerZoneAsync(db, workspace, cancellationToken);
        var start = LocalDate.FromDateOnly(from).AtStartOfDayInZone(zone).ToDateTimeOffset();
        var end = LocalDate.FromDateOnly(to).PlusDays(1).AtStartOfDayInZone(zone).ToDateTimeOffset();
        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays).ToList();

        // A split entry's part on the project counts with its share of the time.
        var entries = (await db.TimeEntries.AsNoTracking()
                .Where(e => e.WorkspaceId == workspace.Id && (e.ProjectId == projectId || e.IsSplit) && e.Stop != null && e.Start >= start && e.Start < end)
                .Select(e => new { e.UserId, e.ProjectId, e.TaskId, e.IsBillable, e.Shares, e.Description, e.Start, Stop = e.Stop!.Value })
                .ToListAsync(cancellationToken))
            .OrderBy(e => e.Start)
            .SelectMany(e => TimeShare.Divide(TimeShare.PartsOf(e.Shares, e.ProjectId, e.TaskId, e.IsBillable), (long)(e.Stop - e.Start).TotalSeconds)
                .Where(p => p.Part.ProjectId == projectId)
                .Select(p => new
                {
                    e.UserId,
                    p.Part.IsBillable,
                    Day = Instant.FromDateTimeOffset(e.Start).InZone(zone).Date.ToDateOnly(),
                    p.Seconds,
                    Description = p.Part.DescriptionOr(e.Description),
                }))
            .ToList();
        var entered = project.SeparateBilledTime
            ? await db.BilledTimes.AsNoTracking()
                .Where(b => b.ProjectId == projectId && b.Day >= from && b.Day <= to)
                .ToDictionaryAsync(b => (b.UserId, b.Day), cancellationToken)
            : [];
        var billed = entered.Where(b => b.Value.Seconds is not null).ToDictionary(b => b.Key, b => b.Value.Seconds!.Value);

        var projectMembers = await db.ProjectMembers.Where(m => m.ProjectId == projectId).Select(m => m.UserId).ToListAsync(cancellationToken);
        var userIds = projectMembers.Concat(entries.Select(e => e.UserId)).Concat(entered.Keys.Select(k => k.UserId)).Distinct().ToList();
        var people = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, IsMember = db.WorkspaceMembers.Any(m => m.WorkspaceId == workspace.Id && m.UserId == u.Id) })
            .ToListAsync(cancellationToken);

        // Rates in it stay with admins; everyone here needs the daily hours.
        var card = await rates.CardAsync(cancellationToken);
        var rows = people
            .Select(person =>
            {
                var own = entries.Where(e => e.UserId == person.Id).ToList();
                var trackedDays = days.Select(day => own.Where(e => e.Day == day && e.IsBillable).Sum(e => e.Seconds)).ToList();
                var billedDays = days.Select(day => billed.TryGetValue((person.Id, day), out var seconds) ? seconds : (long?)null).ToList();
                var defaultDays = days
                    .Select((day, i) => BilledDays.Default(
                        project.BillingMode, card.At(RateScope.DailyHours(projectId), day), day, own.Any(e => e.Day == day), trackedDays[i]))
                    .ToList();

                decimal amount = 0, cost = 0;
                for (var i = 0; i < days.Count; i++)
                {
                    var day = days[i];
                    if (card.RateFor(person.Id, projectId, day) is { } rate)
                    {
                        // As reports count it: the day as entered or as a full day, or else each entry to the cent.
                        var asDay = billedDays[i] ?? (defaultDays[i] != trackedDays[i] ? defaultDays[i] : null);
                        amount += asDay is { } seconds
                            ? RateCard.Amount(rate, seconds)
                            : own.Where(e => e.Day == day && e.IsBillable).Sum(e => RateCard.Amount(rate, e.Seconds));
                    }

                    if (card.LaborCostFor(person.Id, day) is { } costPerHour)
                    {
                        cost += own.Where(e => e.Day == day).Sum(e => RateCard.Amount(costPerHour, e.Seconds));
                    }
                }

                return new BillingRow(
                    person.Id,
                    person.FullName,
                    person.IsMember,
                    trackedDays,
                    billedDays,
                    own.Sum(e => e.Seconds),
                    days.Select((_, i) => billedDays[i] ?? defaultDays[i]).Sum(),
                    policy.IsAdmin ? amount : null,
                    policy.IsAdmin ? cost : null,
                    defaultDays,
                    days.Select(day => SharedTimesheets.Describe(own.Where(e => e.Day == day && e.IsBillable).Select(e => e.Description))).ToList(),
                    days.Select(day => entered.GetValueOrDefault((person.Id, day))?.Description).ToList());
            })
            .OrderBy(row => row.FullName, StringComparer.InvariantCultureIgnoreCase)
            .ToList();

        var currency = await db.Workspaces.Where(w => w.Id == workspace.Id).Select(w => w.Currency).SingleAsync(cancellationToken);
        return TypedResults.Ok(new ProjectBillingResponse(from, to, days, project.SeparateBilledTime, currency, rows, project.BillingMode));
    }

    /// <summary>Enters the time billed for someone on a day, in place of what they tracked.</summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> SetAsync(
        Guid projectId,
        Guid userId,
        DateOnly day,
        BilledTimeRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        TimeEntryRules rules,
        CancellationToken cancellationToken)
    {
        if (await CheckAsync(projectId, userId, day, workspace, db, policy, rules, cancellationToken) is { } problem)
        {
            return problem;
        }

        var billed = await DayAsync(db, workspace, projectId, userId, day, cancellationToken);
        billed.Change(request.Seconds!.Value);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Bills the day as tracked again.</summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> ClearAsync(
        Guid projectId,
        Guid userId,
        DateOnly day,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        TimeEntryRules rules,
        CancellationToken cancellationToken)
    {
        if (await CheckAsync(projectId, userId, day, workspace, db, policy, rules, cancellationToken) is { } problem)
        {
            return problem;
        }

        var billed = await db.BilledTimes.SingleOrDefaultAsync(b => b.ProjectId == projectId && b.UserId == userId && b.Day == day, cancellationToken);
        if (billed is not null)
        {
            billed.Change(null);
            await SaveDayAsync(db, billed, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    /// <summary>Describes what was done on someone's day, in place of their entries' descriptions, for timesheet links.</summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DescribeAsync(
        Guid projectId,
        Guid userId,
        DateOnly day,
        BilledDayDescriptionRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        TimeEntryRules rules,
        CancellationToken cancellationToken)
    {
        if (await CheckAsync(projectId, userId, day, workspace, db, policy, rules, cancellationToken) is { } problem)
        {
            return problem;
        }

        var billed = await DayAsync(db, workspace, projectId, userId, day, cancellationToken);
        billed.Describe(request.Description);
        await SaveDayAsync(db, billed, cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Describes the day from its entries again.</summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> ClearDescriptionAsync(
        Guid projectId,
        Guid userId,
        DateOnly day,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        TimeEntryRules rules,
        CancellationToken cancellationToken)
    {
        if (await CheckAsync(projectId, userId, day, workspace, db, policy, rules, cancellationToken) is { } problem)
        {
            return problem;
        }

        var billed = await db.BilledTimes.SingleOrDefaultAsync(b => b.ProjectId == projectId && b.UserId == userId && b.Day == day, cancellationToken);
        if (billed is not null)
        {
            billed.Describe(null);
            await SaveDayAsync(db, billed, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    /// <summary>What is entered for someone's day on the project, added when there is nothing yet.</summary>
    private static async Task<BilledTime> DayAsync(
        AppDbContext db,
        CurrentWorkspace workspace,
        Guid projectId,
        Guid userId,
        DateOnly day,
        CancellationToken cancellationToken)
    {
        var billed = await db.BilledTimes.SingleOrDefaultAsync(b => b.ProjectId == projectId && b.UserId == userId && b.Day == day, cancellationToken);
        if (billed is null)
        {
            billed = BilledTime.Create(workspace.Id, projectId, userId, day);
            db.BilledTimes.Add(billed);
        }

        return billed;
    }

    /// <summary>Saves the day, or removes it once nothing is entered for it.</summary>
    private static async Task SaveDayAsync(AppDbContext db, BilledTime billed, CancellationToken cancellationToken)
    {
        if (billed.IsEmpty)
        {
            db.BilledTimes.Remove(billed);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Why the caller cannot change the time billed for the person on the day, if they cannot.</summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>?> CheckAsync(
        Guid projectId,
        Guid userId,
        DateOnly day,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        TimeEntryRules rules,
        CancellationToken cancellationToken)
    {
        var project = await policy.VisibleProjects().AsNoTracking().SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
        {
            return TypedResults.NotFound();
        }

        if (!await policy.CanManageProjectAsync(projectId, cancellationToken))
        {
            return WorkspacePolicy.Forbidden(CannotManage);
        }

        if (!project.SeparateBilledTime)
        {
            return Problems.Validation("separateBilledTime", "Billed time is not entered separately on this project.");
        }

        if (!await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == workspace.Id && m.UserId == userId, cancellationToken))
        {
            return Problems.Validation("userId", "Billed time can be entered for members of the workspace only.");
        }

        if (await rules.LockedAsync(day, cancellationToken) is { } locked)
        {
            return Problems.Validation("day", locked);
        }

        return null;
    }

    private static async Task<DateTimeZone> CallerZoneAsync(AppDbContext db, CurrentWorkspace workspace, CancellationToken cancellationToken)
    {
        var id = await db.Users.Where(u => u.Id == workspace.UserId).Select(u => u.TimeZone).SingleAsync(cancellationToken);
        return DateTimeZoneProviders.Tzdb.GetZoneOrNull(id) ?? DateTimeZone.Utc;
    }
}
