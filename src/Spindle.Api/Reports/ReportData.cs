using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Spindle.Api.Billing;
using Spindle.Api.Common;
using Spindle.Api.Workspaces;
using Spindle.Domain.Billing;
using Spindle.Domain.Projects;
using Spindle.Domain.Reports;
using Spindle.Domain.TimeEntries;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Reports;

/// <summary>What a row of a report stands for.</summary>
internal enum ReportEntryKind
{
    /// <summary>A time entry.</summary>
    Time,

    /// <summary>
    /// The difference between the time billed on a day of a project whose billed time is entered separately and the
    /// billable time tracked there: it adds billed time and money, but no tracked time.
    /// </summary>
    BilledTime,

    /// <summary>What an agreement is paid: a fixed price on the day it was done, a retainer's fee on each period's first day.</summary>
    Agreement,
}

/// <summary>
/// A finished entry as reports count it: rounded, in the caller's calendar, with its money worked out at the rates of
/// its day. Time on an agreement's tasks is paid for by the agreement, so it has no money of its own, except for a
/// retainer's time beyond its hours where that is billed. Or an adjustment, which adds money but no tracked time.
/// A split entry is in a report once for each of its parts, with the part's share of its time; each part counts
/// towards the agreement of its own task.
/// </summary>
/// <param name="Description">A split part's own description, when the parts have their own; otherwise the entry's.</param>
/// <param name="AgreementId">The agreement the time counts towards, or whose payment it is.</param>
/// <param name="Percent">The part of a split entry this row is; 100 for an entry on one project.</param>
internal sealed record ReportEntry(
    Guid Id,
    Guid UserId,
    string Description,
    Guid? ProjectId,
    Guid? TaskId,
    Guid[] TagIds,
    bool IsBillable,
    DateTimeOffset Start,
    DateTimeOffset Stop,
    DateOnly Day,
    long Seconds,
    decimal? Amount,
    decimal? LaborCost,
    long BilledSeconds,
    ReportEntryKind Kind = ReportEntryKind.Time,
    Guid? AgreementId = null,
    int Percent = 100,
    IReadOnlyList<TimeShare>? Shares = null)
{
    /// <summary>Whether it is money or billed time without tracked time, rather than a time entry.</summary>
    public bool IsAdjustment => Kind != ReportEntryKind.Time;
}

internal sealed record ReportProject(string Name, string Color, Guid? ClientId);

internal sealed record ReportTask(string Name, Guid ProjectId);

/// <summary>A workspace's agreements, and which agreement each task's time counts towards.</summary>
internal sealed class AgreementSet(IReadOnlyCollection<Agreement> agreements, IReadOnlyDictionary<Guid, Guid> taskAgreements)
{
    private readonly Dictionary<Guid, Agreement> byId = agreements.ToDictionary(a => a.Id);

    public static AgreementSet Empty { get; } = new([], new Dictionary<Guid, Guid>());

    public IReadOnlyCollection<Agreement> All => byId.Values;

    public IReadOnlyDictionary<Guid, Guid> TaskAgreements => taskAgreements;

    public Agreement? Find(Guid? agreementId) => agreementId is { } id ? byId.GetValueOrDefault(id) : null;

    /// <summary>The agreement time on the task counts towards, if any.</summary>
    public Guid? AgreementOf(Guid? taskId) => taskId is { } id && taskAgreements.TryGetValue(id, out var agreementId) ? agreementId : null;
}

/// <summary>Everything a report is made of: the entries it covers and the names to show for them.</summary>
internal sealed class ReportData
{
    public required string WorkspaceName { get; init; }

    public required string Currency { get; init; }

    /// <summary>The caller's time zone, which the report's days and times are in.</summary>
    public required DateTimeZone Zone { get; init; }

    /// <summary>The caller's first day of the week, where weekly bars begin.</summary>
    public DayOfWeek WeekStart { get; init; } = DayOfWeek.Monday;

    /// <summary>Whether amounts and costs are shown: rates are for admins' eyes only.</summary>
    public required bool ShowMoney { get; init; }

    public required DateOnly From { get; init; }

    public required DateOnly To { get; init; }

    /// <summary>The entries, and the adjustments for separately entered billed time.</summary>
    public required IReadOnlyList<ReportEntry> Entries { get; init; }

    /// <summary>The entries alone, without billing adjustments.</summary>
    public IEnumerable<ReportEntry> TimeEntries => Entries.Where(e => !e.IsAdjustment);

    /// <summary>Whether some of the time was billed as entered separately, rather than as tracked.</summary>
    public bool HasBilledTime => Entries.Any(e => e.Kind == ReportEntryKind.BilledTime);

    public required IReadOnlyDictionary<Guid, string> Users { get; init; }

    public required IReadOnlyDictionary<Guid, ReportProject> Projects { get; init; }

    public required IReadOnlyDictionary<Guid, string> Clients { get; init; }

    public required IReadOnlyDictionary<Guid, ReportTask> Tasks { get; init; }

    public required IReadOnlyDictionary<Guid, string> Tags { get; init; }

    /// <summary>The workspace's agreements, for admins; none for anyone else.</summary>
    public AgreementSet Agreements { get; init; } = AgreementSet.Empty;

    /// <summary>The retainers' periods that overlap the report's days, and the time each took.</summary>
    public AgreementLedger Ledger { get; init; } = AgreementLedger.Empty;

    public ReportTotals Totals() => Total(Entries);

    public ReportTotals Total(IEnumerable<ReportEntry> entries)
    {
        long seconds = 0, billable = 0, billed = 0;
        decimal amount = 0, cost = 0;
        HashSet<Guid> counted = [];
        foreach (var entry in entries)
        {
            seconds += entry.Seconds;
            billable += entry.IsBillable ? entry.Seconds : 0;
            billed += entry.BilledSeconds;
            amount += entry.Amount ?? 0;
            cost += entry.LaborCost ?? 0;
            if (!entry.IsAdjustment)
            {
                // The parts of a split entry are one entry.
                counted.Add(entry.Id);
            }
        }

        return new ReportTotals(seconds, billable, ShowMoney ? amount : null, ShowMoney ? cost : null, counted.Count, ShowMoney ? billed : null);
    }
}

/// <summary>
/// Whose report it is: the workspace, the calendar its days are counted in, and whether everyone's time and money
/// count (an admin) or only <see cref="UserId"/>'s time.
/// </summary>
internal sealed record ReportScope(Guid WorkspaceId, Guid? UserId, bool IsAdmin, DateTimeZone Zone, DayOfWeek WeekStart);

/// <summary>Loads the entries a report covers, as far as the caller may see them.</summary>
internal sealed class ReportLoader(AppDbContext db, CurrentWorkspace workspace, RateBook rateBook)
{
    /// <summary>Reads the filter's entries, or says what is wrong with the filter.</summary>
    public async Task<(ReportData? Data, ValidationProblem? Problem)> LoadAsync(ReportFilter filter, int maxDays, CancellationToken cancellationToken)
    {
        var caller = await db.Users.Where(u => u.Id == workspace.UserId).Select(u => new { u.TimeZone, u.WeekStart }).SingleAsync(cancellationToken);
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(caller.TimeZone) ?? DateTimeZone.Utc;
        return await LoadAsync(filter, maxDays, new ReportScope(workspace.Id, workspace.UserId, workspace.IsAdmin, zone, (DayOfWeek)caller.WeekStart), cancellationToken);
    }

    /// <summary>Reads the filter's entries for the scope, or says what is wrong with the filter.</summary>
    public async Task<(ReportData? Data, ValidationProblem? Problem)> LoadAsync(ReportFilter filter, int maxDays, ReportScope scope, CancellationToken cancellationToken)
    {
        var from = filter.From!.Value;
        var to = filter.To!.Value;
        if (to < from || to.DayNumber - from.DayNumber >= maxDays)
        {
            return (null, Problems.Validation("to", $"Choose at most {maxDays} days, ending on or after the first one."));
        }

        if (filter.Rounding != RoundingMode.None && !DurationRounding.AllowedMinutes.Contains(filter.RoundingMinutes))
        {
            return (null, Problems.Validation("roundingMinutes", "Round to 1, 5, 6, 10, 12, 15, 30 or 60 minutes."));
        }

        var zone = scope.Zone;
        var start = LocalDate.FromDateOnly(from).AtStartOfDayInZone(zone).ToDateTimeOffset();
        var end = LocalDate.FromDateOnly(to).PlusDays(1).AtStartOfDayInZone(zone).ToDateTimeOffset();

        var entries = await Query(scope, filter, start, end).ToListAsync(cancellationToken);
        var workspaceSettings = await db.Workspaces.AsNoTracking()
            .Where(w => w.Id == scope.WorkspaceId)
            .Select(w => new { w.Name, w.Currency })
            .SingleAsync(cancellationToken);
        var rates = scope.IsAdmin ? await rateBook.CardAsync(scope.WorkspaceId, cancellationToken) : RateCard.Empty;
        // Agreements are about money, so they are admin business too. For anyone else time is time.
        var agreements = scope.IsAdmin ? await AgreementsAsync(scope.WorkspaceId, cancellationToken) : AgreementSet.Empty;
        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.WorkspaceId == scope.WorkspaceId)
            .ToDictionaryAsync(p => p.Id, p => new ReportProject(p.Name, p.Color, p.ClientId), cancellationToken);
        var partFilter = PartFilter(filter, projects);

        // The whole entry is rounded, then divided, so its parts add up to what it would count unsplit.
        var rows = entries
            .SelectMany(e =>
            {
                var seconds = DurationRounding.Apply((long)(e.Stop!.Value - e.Start).TotalSeconds, filter.Rounding, filter.RoundingMinutes);
                var day = Instant.FromDateTimeOffset(e.Start).InZone(zone).Date.ToDateOnly();
                var cost = rates.LaborCostFor(e.UserId, day);
                return TimeShare.Divide(e.Parts, seconds)
                    .Where(p => partFilter(p.Part))
                    .Select(p =>
                    {
                        var agreementId = agreements.AgreementOf(p.Part.TaskId);
                        var rate = p.Part.IsBillable && agreementId is null ? rates.RateFor(e.UserId, p.Part.ProjectId, day) : null;
                        return new ReportEntry(
                            e.Id,
                            e.UserId,
                            p.Part.DescriptionOr(e.Description),
                            p.Part.ProjectId,
                            p.Part.TaskId,
                            e.TagIds,
                            p.Part.IsBillable,
                            e.Start,
                            e.Stop.Value,
                            day,
                            p.Seconds,
                            rate is { } perHour ? RateCard.Amount(perHour, p.Seconds) : null,
                            cost is { } costPerHour ? RateCard.Amount(costPerHour, p.Seconds) : null,
                            p.Part.IsBillable ? p.Seconds : 0,
                            AgreementId: agreementId,
                            Percent: p.Part.Percent,
                            Shares: e.IsSplit ? e.Shares : null);
                    });
            })
            .ToList();

        var ledger = AgreementLedger.Empty;
        // Billing is admin business, like money.
        if (scope.IsAdmin)
        {
            ledger = await LedgerAsync(scope.WorkspaceId, agreements, from, to, zone, filter, cancellationToken);
            rows = rows.Select(row => WithOverage(row, agreements, ledger, rates)).ToList();
            rows.AddRange(await AdjustmentsAsync(scope.WorkspaceId, filter, from, to, start, end, zone, rates, agreements, cancellationToken));
            rows.AddRange(Payments(filter, from, to, zone, rates, agreements, projects));
        }

        return (new ReportData
        {
            WorkspaceName = workspaceSettings.Name,
            Currency = workspaceSettings.Currency,
            Zone = zone,
            WeekStart = scope.WeekStart,
            ShowMoney = scope.IsAdmin,
            From = from,
            To = to,
            Entries = rows,
            Users = await UserNamesAsync(rows, cancellationToken),
            Projects = projects,
            Clients = await db.Clients.AsNoTracking()
                .Where(c => c.WorkspaceId == scope.WorkspaceId)
                .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken),
            Tasks = await db.Tasks.AsNoTracking()
                .Where(t => t.WorkspaceId == scope.WorkspaceId)
                .ToDictionaryAsync(t => t.Id, t => new ReportTask(t.Name, t.ProjectId), cancellationToken),
            Tags = await db.Tags.AsNoTracking()
                .Where(t => t.WorkspaceId == scope.WorkspaceId)
                .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken),
            Agreements = agreements,
            Ledger = ledger,
        }, null);
    }

    private async Task<AgreementSet> AgreementsAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var agreements = await db.Agreements.AsNoTracking().Where(a => a.WorkspaceId == workspaceId).ToListAsync(cancellationToken);
        if (agreements.Count == 0)
        {
            return AgreementSet.Empty;
        }

        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && t.AgreementId != null)
            .ToDictionaryAsync(t => t.Id, t => t.AgreementId!.Value, cancellationToken);
        return new AgreementSet(agreements, tasks);
    }

    /// <summary>
    /// The retainers' periods that overlap the report, with all the time on their tasks in those periods, whoever
    /// tracked it and whatever else the report filters by: a period's hours are used up in the order time was tracked.
    /// </summary>
    private async Task<AgreementLedger> LedgerAsync(
        Guid workspaceId,
        AgreementSet agreements,
        DateOnly from,
        DateOnly to,
        DateTimeZone zone,
        ReportFilter filter,
        CancellationToken cancellationToken)
    {
        var retainers = agreements.All.Where(a => a.Kind == AgreementKind.Retainer).ToList();
        var periods = retainers.SelectMany(a => a.Periods(from, to)).ToList();
        if (periods.Count == 0)
        {
            return AgreementLedger.Empty;
        }

        var start = LocalDate.FromDateOnly(periods.Min(p => p.Start)).AtStartOfDayInZone(zone).ToDateTimeOffset();
        var end = LocalDate.FromDateOnly(periods.Max(p => p.End)).PlusDays(1).AtStartOfDayInZone(zone).ToDateTimeOffset();
        var retainerIds = retainers.Select(a => a.Id).ToHashSet();
        var taskIds = agreements.TaskAgreements.Where(t => retainerIds.Contains(t.Value)).Select(t => t.Key).ToArray();
        var retainerTasks = taskIds.ToHashSet();
        // A split entry counts the part on a retainer's task, with that part's share of the rounded time.
        var time = await db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && e.Stop != null && ((e.TaskId != null && taskIds.Contains(e.TaskId.Value)) || e.IsSplit)
                && e.Start >= start && e.Start < end)
            .Select(e => new { e.Id, e.ProjectId, e.TaskId, e.IsBillable, e.Shares, e.Start, Stop = e.Stop!.Value })
            .ToListAsync(cancellationToken);

        return AgreementLedger.Build(
            retainers,
            time.SelectMany(e => TimeShare.Divide(
                    TimeShare.PartsOf(e.Shares, e.ProjectId, e.TaskId, e.IsBillable),
                    DurationRounding.Apply((long)(e.Stop - e.Start).TotalSeconds, filter.Rounding, filter.RoundingMinutes))
                .Where(p => p.Part.TaskId is { } taskId && retainerTasks.Contains(taskId))
                .Select(p => new AgreementTime(
                    e.Id,
                    agreements.TaskAgreements[p.Part.TaskId!.Value],
                    e.Start,
                    Instant.FromDateTimeOffset(e.Start).InZone(zone).Date.ToDateOnly(),
                    p.Seconds))),
            from,
            to);
    }

    /// <summary>A retainer's time beyond its hours, where that is billed, at the hourly rate that applies to it.</summary>
    private static ReportEntry WithOverage(ReportEntry row, AgreementSet agreements, AgreementLedger ledger, RateCard rates) =>
        agreements.Find(row.AgreementId) is { Kind: AgreementKind.Retainer, BillsOverage: true }
        && ledger.OverSeconds(row.Id) is > 0 and var over
        && rates.RateFor(row.UserId, row.ProjectId, row.Day) is { } perHour
            ? row with { Amount = RateCard.Amount(perHour, over) }
            : row;

    /// <summary>
    /// What agreements are paid within the report: a fixed price on the day it was done, a retainer's fee on the first
    /// day of each period. Payments belong to no one and have no task, tags or description, so they count only in
    /// reports that do not filter by those, and only in reports of billable time or of all time.
    /// </summary>
    private static IEnumerable<ReportEntry> Payments(
        ReportFilter filter,
        DateOnly from,
        DateOnly to,
        DateTimeZone zone,
        RateCard rates,
        AgreementSet agreements,
        IReadOnlyDictionary<Guid, ReportProject> projects)
    {
        if (filter.Billable == false
            || !string.IsNullOrWhiteSpace(filter.Description)
            || filter.UserIds.Length > 0
            || filter.GroupIds.Length > 0
            || (filter.TaskIds.Length > 0 && !filter.TaskIds.Contains(Guid.Empty))
            || (filter.TagIds.Length > 0 && !filter.TagIds.Contains(Guid.Empty)))
        {
            yield break;
        }

        foreach (var agreement in agreements.All)
        {
            var clientId = projects.GetValueOrDefault(agreement.ProjectId)?.ClientId;
            if ((filter.ProjectIds.Length > 0 && !filter.ProjectIds.Contains(agreement.ProjectId))
                || (filter.ClientIds.Length > 0 && !filter.ClientIds.Contains(clientId ?? Guid.Empty)))
            {
                continue;
            }

            if (agreement is { Kind: AgreementKind.FixedPrice, EndsOn: { } done, Amount: { } price } && done >= from && done <= to)
            {
                yield return Payment(agreement, done, price, zone);
            }

            foreach (var period in agreement.Periods(from, to).Where(p => p.Start >= from))
            {
                if (agreement.Fee(period.Start, rates) is { } fee)
                {
                    yield return Payment(agreement, period.Start, fee, zone);
                }
            }
        }
    }

    private static ReportEntry Payment(Agreement agreement, DateOnly day, decimal amount, DateTimeZone zone)
    {
        var dayStart = LocalDate.FromDateOnly(day).AtStartOfDayInZone(zone).ToDateTimeOffset();
        return new ReportEntry(
            agreement.Id,
            Guid.Empty,
            string.Empty,
            agreement.ProjectId,
            null,
            [],
            true,
            dayStart,
            dayStart,
            day,
            0,
            amount,
            null,
            0,
            ReportEntryKind.Agreement,
            agreement.Id);
    }

    /// <summary>
    /// Where projects bill apart from what is tracked, the difference between each day as billed (entered by hand, or
    /// a full day on projects billed by the day) and the billable time tracked on it, rounded as the report rounds.
    /// Adjustments have no task, tags or description, so filters on those keep them only when asking for time without one.
    /// Time on agreements is paid for by them, so it is left out.
    /// </summary>
    private async Task<List<ReportEntry>> AdjustmentsAsync(
        Guid workspaceId,
        ReportFilter filter,
        DateOnly from,
        DateOnly to,
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeZone zone,
        RateCard rates,
        AgreementSet agreements,
        CancellationToken cancellationToken)
    {
        if (filter.Billable == false
            || !string.IsNullOrWhiteSpace(filter.Description)
            || (filter.TaskIds.Length > 0 && !filter.TaskIds.Contains(Guid.Empty))
            || (filter.TagIds.Length > 0 && !filter.TagIds.Contains(Guid.Empty)))
        {
            return [];
        }

        var projectQuery = db.Projects.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && p.BillingMode != BillingMode.AsTracked);
        if (filter.ProjectIds.Length > 0)
        {
            var ids = filter.ProjectIds;
            projectQuery = projectQuery.Where(p => ids.Contains(p.Id));
        }

        if (filter.ClientIds.Length > 0)
        {
            var ids = filter.ClientIds;
            var withoutClient = ids.Contains(Guid.Empty);
            projectQuery = projectQuery.Where(p => (p.ClientId != null && ids.Contains(p.ClientId.Value)) || (withoutClient && p.ClientId == null));
        }

        var modes = await projectQuery.ToDictionaryAsync(p => p.Id, p => p.BillingMode, cancellationToken);
        if (modes.Count == 0)
        {
            return [];
        }

        HashSet<Guid>? people = null;
        if (filter.UserIds.Length > 0 || filter.GroupIds.Length > 0)
        {
            var groupIds = filter.GroupIds;
            people = [.. filter.UserIds, .. await db.GroupMembers.Where(m => groupIds.Contains(m.GroupId)).Select(m => m.UserId).ToListAsync(cancellationToken)];
        }

        var projectIds = modes.Keys.ToArray();
        var entered = (await db.BilledTimes.AsNoTracking()
                .Where(b => b.WorkspaceId == workspaceId && b.Day >= from && b.Day <= to && projectIds.Contains(b.ProjectId) && b.Seconds != null)
                .ToListAsync(cancellationToken))
            .Where(b => people is null || people.Contains(b.UserId))
            .ToDictionary(b => (b.ProjectId, b.UserId, b.Day));

        // All the time tracked on those days, whatever else the report filters by: a day is billed as a whole.
        // Each part of a split entry counts on its own project.
        var tracked = (await db.TimeEntries.AsNoTracking()
                .Where(e => e.WorkspaceId == workspaceId && e.Stop != null && e.Start >= start && e.Start < end
                    && ((e.ProjectId != null && projectIds.Contains(e.ProjectId.Value)) || e.IsSplit))
                .Select(e => new { e.ProjectId, e.TaskId, e.UserId, e.IsBillable, e.Shares, e.Start, Stop = e.Stop!.Value })
                .ToListAsync(cancellationToken))
            .Where(e => people is null || people.Contains(e.UserId))
            .SelectMany(e => TimeShare.Divide(
                    TimeShare.PartsOf(e.Shares, e.ProjectId, e.TaskId, e.IsBillable),
                    DurationRounding.Apply((long)(e.Stop - e.Start).TotalSeconds, filter.Rounding, filter.RoundingMinutes))
                .Where(p => p.Part.ProjectId is { } id && modes.ContainsKey(id) && agreements.AgreementOf(p.Part.TaskId) is null)
                .Select(p => new { ProjectId = p.Part.ProjectId!.Value, e.UserId, p.Part.IsBillable, p.Seconds, e.Start }))
            .ToLookup(e => (e.ProjectId, e.UserId, Instant.FromDateTimeOffset(e.Start).InZone(zone).Date.ToDateOnly()));

        var adjustments = new List<ReportEntry>();
        foreach (var key in entered.Keys.Concat(tracked.Select(g => g.Key)).Distinct())
        {
            var (projectId, userId, day) = key;
            var billable = tracked[key]
                .Where(e => e.IsBillable)
                .Select(e => e.Seconds)
                .ToList();
            var byHand = entered.GetValueOrDefault(key);
            var billed = byHand?.Seconds
                ?? BilledDays.Default(modes[projectId], rates.At(RateScope.DailyHours(projectId), day), day, tracked[key].Any(), billable.Sum());
            if (byHand is null && billed == billable.Sum())
            {
                continue;
            }

            var amount = rates.RateFor(userId, projectId, day) is { } perHour
                ? RateCard.Amount(perHour, billed) - billable.Sum(seconds => RateCard.Amount(perHour, seconds))
                : (decimal?)null;
            var dayStart = LocalDate.FromDateOnly(day).AtStartOfDayInZone(zone).ToDateTimeOffset();
            var adjustment = new ReportEntry(
                byHand?.Id ?? Guid.Empty,
                userId,
                string.Empty,
                projectId,
                null,
                [],
                true,
                dayStart,
                dayStart,
                day,
                0,
                amount,
                null,
                billed - billable.Sum(),
                Kind: ReportEntryKind.BilledTime);
            if (adjustment.BilledSeconds != 0 || adjustment.Amount is not (null or 0))
            {
                adjustments.Add(adjustment);
            }
        }

        return adjustments;
    }

    private IQueryable<Domain.TimeEntries.TimeEntry> Query(ReportScope scope, ReportFilter filter, DateTimeOffset start, DateTimeOffset end)
    {
        var query = db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == scope.WorkspaceId && e.Stop != null && e.Start >= start && e.Start < end);

        // Members see their own time only.
        if (!scope.IsAdmin)
        {
            var self = scope.UserId;
            query = query.Where(e => e.UserId == self);
        }

        if (filter.UserIds.Length > 0 || filter.GroupIds.Length > 0)
        {
            var userIds = filter.UserIds;
            var groupIds = filter.GroupIds;
            query = query.Where(e => userIds.Contains(e.UserId)
                || db.GroupMembers.Any(m => m.UserId == e.UserId && groupIds.Contains(m.GroupId)));
        }

        // Project, client, task and billable belong to each part of a split entry: split entries are read whole and
        // their parts filtered once loaded (see PartFilter).
        if (filter.ProjectIds.Length > 0)
        {
            var ids = filter.ProjectIds;
            var withoutProject = ids.Contains(Guid.Empty);
            query = query.Where(e => e.IsSplit || (e.ProjectId != null && ids.Contains(e.ProjectId.Value)) || (withoutProject && e.ProjectId == null));
        }

        if (filter.ClientIds.Length > 0)
        {
            var ids = filter.ClientIds;
            var withoutClient = ids.Contains(Guid.Empty);
            query = query.Where(e => e.IsSplit
                || db.Projects.Any(p => p.Id == e.ProjectId && p.ClientId != null && ids.Contains(p.ClientId.Value))
                || (withoutClient && (e.ProjectId == null || db.Projects.Any(p => p.Id == e.ProjectId && p.ClientId == null))));
        }

        if (filter.TaskIds.Length > 0)
        {
            var ids = filter.TaskIds;
            var withoutTask = ids.Contains(Guid.Empty);
            query = query.Where(e => e.IsSplit || (e.TaskId != null && ids.Contains(e.TaskId.Value)) || (withoutTask && e.TaskId == null));
        }

        if (filter.TagIds.Length > 0)
        {
            var ids = filter.TagIds;
            var withoutTags = ids.Contains(Guid.Empty);
            query = query.Where(e => e.TagIds.Any(t => ids.Contains(t)) || (withoutTags && e.TagIds.Length == 0));
        }

        if (filter.Billable is { } billable)
        {
            query = query.Where(e => e.IsSplit || e.IsBillable == billable);
        }

        if (!string.IsNullOrWhiteSpace(filter.Description))
        {
            var text = filter.Description.Trim();
            query = query.Where(e => EF.Functions.ILike(e.Description, LikePattern.Contains(text), "\\"));
        }

        return query;
    }

    /// <summary>Whether a part of an entry is in the report, by the filters that apply to each part of a split entry.</summary>
    private static Func<TimeShare, bool> PartFilter(ReportFilter filter, IReadOnlyDictionary<Guid, ReportProject> projects)
    {
        static bool Matches(Guid[] ids, Guid? id) => ids.Length == 0 || (id is { } value ? ids.Contains(value) : ids.Contains(Guid.Empty));

        // The entry's description matched in the query; a part with its own description has to match by itself.
        var text = filter.Description?.Trim();
        return part => Matches(filter.ProjectIds, part.ProjectId)
            && Matches(filter.ClientIds, part.ProjectId is { } projectId ? projects.GetValueOrDefault(projectId)?.ClientId : null)
            && Matches(filter.TaskIds, part.TaskId)
            && (filter.Billable is not { } billable || part.IsBillable == billable)
            && (string.IsNullOrEmpty(text) || part.Description is null || part.Description.Contains(text, StringComparison.InvariantCultureIgnoreCase));
    }

    /// <summary>Names of the people in the report, including those who have since left the workspace.</summary>
    private async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<ReportEntry> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.UserId).Distinct().ToArray();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
    }
}
