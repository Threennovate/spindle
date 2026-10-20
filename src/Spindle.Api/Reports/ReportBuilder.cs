using Spindle.Api.TimeEntries;

namespace Spindle.Api.Reports;

/// <summary>How one group of a report is named, e.g. a project with its client and color.</summary>
internal sealed record GroupLabel(string Key, Guid? Id, string Name, string? Detail, string? Color);

/// <summary>Turns a report's entries into the summary, detailed and weekly reports.</summary>
internal static class ReportBuilder
{
    public const string None = "none";

    /// <summary>The key of the group of billing adjustments, when grouping by task, tag or description.</summary>
    public const string BilledTimeAdjustment = "billed";

    /// <summary>The key of the group of what agreements are paid, when grouping by anything but project or client.</summary>
    public const string AgreementPayments = "agreements";

    public static SummaryReport Summary(ReportData data, ReportGrouping groupBy, ReportGrouping? subGroupBy)
    {
        var resolution = ResolutionFor(data.From, data.To);
        var buckets = BucketStarts(data.From, data.To, resolution, data.WeekStart)
            .ToDictionary(start => start, _ => (Seconds: 0L, Billable: 0L));
        foreach (var entry in data.Entries)
        {
            var start = BucketStart(entry.Day, resolution, data.WeekStart);
            var (seconds, billable) = buckets[start];
            buckets[start] = (seconds + entry.Seconds, billable + (entry.IsBillable ? entry.Seconds : 0));
        }

        return new SummaryReport(
            data.Currency,
            data.Totals(),
            resolution,
            buckets.Select(b => new ReportBucket(b.Key, b.Value.Seconds, b.Value.Billable)).ToList(),
            Groups(data, data.Entries, groupBy, subGroupBy));
    }

    public static DetailedReport Detailed(ReportData data, DetailedReportRequest request)
    {
        var page = Sorted(data, request)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(entry => Describe(data, entry))
            .ToList();

        return new DetailedReport(data.Currency, data.Totals(), request.Page, request.PageSize, data.TimeEntries.Count(), page);
    }

    /// <summary>
    /// All of the report's time entries in the requested order. Billing adjustments are not entries; they count in
    /// the totals only.
    /// </summary>
    public static IEnumerable<ReportEntry> Sorted(ReportData data, DetailedReportRequest request)
    {
        var entries = data.TimeEntries;
        return request.Sort switch
        {
            ReportSort.Duration => Order(entries, e => e.Seconds, request.Descending),
            ReportSort.Description => Order(entries, e => e.Description, request.Descending, StringComparer.InvariantCultureIgnoreCase),
            ReportSort.User => Order(entries, e => UserName(data, e.UserId), request.Descending, StringComparer.InvariantCultureIgnoreCase),
            ReportSort.Project => Order(
                entries,
                e => e.ProjectId is { } id && data.Projects.TryGetValue(id, out var project) ? project.Name : string.Empty,
                request.Descending,
                StringComparer.InvariantCultureIgnoreCase),
            _ => Order(entries, e => e.Start, request.Descending),
        };
    }

    public static WeeklyReport Weekly(ReportData data, ReportGrouping groupBy)
    {
        var days = Enumerable.Range(0, data.To.DayNumber - data.From.DayNumber + 1).Select(data.From.AddDays).ToList();
        var index = days.Select((day, i) => (day, i)).ToDictionary(d => d.day, d => d.i);

        var rows = data.Entries
            .SelectMany(entry => Labels(data, entry, groupBy).Select(label => (label, entry)))
            .GroupBy(x => x.label.Key)
            .Select(group =>
            {
                var label = group.First().label;
                var perDay = new long[days.Count];
                foreach (var (_, entry) in group)
                {
                    perDay[index[entry.Day]] += entry.Seconds;
                }

                var totals = data.Total(group.Select(x => x.entry));
                return new WeeklyRow(label.Key, label.Id, label.Name, label.Detail, label.Color, perDay, totals.Seconds, totals.Amount);
            })
            .OrderByDescending(row => row.Seconds)
            .ThenBy(row => row.Name, StringComparer.InvariantCultureIgnoreCase)
            .ToList();

        var dayTotals = new long[days.Count];
        foreach (var entry in data.Entries)
        {
            dayTotals[index[entry.Day]] += entry.Seconds;
        }

        return new WeeklyReport(data.Currency, data.Totals(), days, dayTotals, rows);
    }

    /// <summary>
    /// The groups an entry belongs to. Usually one; an entry with several tags counts in each of their groups when
    /// grouping by tag, so tag groups can add up to more than the total. Billing adjustments have no task, tag or
    /// description, so by those they form a group of their own. What agreements are paid belongs to their project
    /// only, so by anything else it forms a group of its own.
    /// </summary>
    public static IEnumerable<GroupLabel> Labels(ReportData data, ReportEntry entry, ReportGrouping grouping)
    {
        if (entry.Kind == ReportEntryKind.Agreement && grouping is not (ReportGrouping.Project or ReportGrouping.Client))
        {
            yield return new GroupLabel(AgreementPayments, null, "Agreements", null, null);
            yield break;
        }

        if (entry.IsAdjustment && grouping is ReportGrouping.Tag or ReportGrouping.Task or ReportGrouping.Description)
        {
            yield return new GroupLabel(BilledTimeAdjustment, null, "Billed time adjustment", null, null);
            yield break;
        }

        switch (grouping)
        {
            case ReportGrouping.Project:
                yield return ProjectLabel(data, entry.ProjectId);
                break;

            case ReportGrouping.Client:
                var clientId = entry.ProjectId is { } projectId && data.Projects.TryGetValue(projectId, out var project) ? project.ClientId : null;
                yield return clientId is { } id && data.Clients.TryGetValue(id, out var client)
                    ? new GroupLabel(id.ToString(), id, client, null, null)
                    : new GroupLabel(None, null, "No client", null, null);
                break;

            case ReportGrouping.User:
                yield return new GroupLabel(entry.UserId.ToString(), entry.UserId, UserName(data, entry.UserId), null, null);
                break;

            case ReportGrouping.Tag:
                var tags = entry.TagIds.Where(data.Tags.ContainsKey).ToList();
                if (tags.Count == 0)
                {
                    yield return new GroupLabel(None, null, "No tag", null, null);
                }

                foreach (var tagId in tags)
                {
                    yield return new GroupLabel(tagId.ToString(), tagId, data.Tags[tagId], null, null);
                }

                break;

            case ReportGrouping.Task:
                if (entry.TaskId is { } taskId && data.Tasks.TryGetValue(taskId, out var task))
                {
                    var taskProject = data.Projects.GetValueOrDefault(task.ProjectId);
                    yield return new GroupLabel(taskId.ToString(), taskId, task.Name, taskProject?.Name, taskProject?.Color);
                }
                else
                {
                    yield return new GroupLabel(None, null, "No task", null, null);
                }

                break;

            default:
                yield return entry.Description.Length == 0
                    ? new GroupLabel(None, null, "No description", null, null)
                    : new GroupLabel($"d:{entry.Description}", null, entry.Description, null, null);
                break;
        }
    }

    public static ReportResolution ResolutionFor(DateOnly from, DateOnly to) => (to.DayNumber - from.DayNumber + 1) switch
    {
        <= 31 => ReportResolution.Day,
        <= 183 => ReportResolution.Week,
        _ => ReportResolution.Month,
    };

    public static DetailedEntry Describe(ReportData data, ReportEntry entry)
    {
        var project = entry.ProjectId is { } projectId ? data.Projects.GetValueOrDefault(projectId) : null;
        var tags = entry.TagIds.Where(data.Tags.ContainsKey).ToList();
        return new DetailedEntry(
            entry.Id,
            entry.UserId,
            UserName(data, entry.UserId),
            entry.Description,
            project is null ? null : entry.ProjectId,
            project?.Name,
            project?.Color,
            project?.ClientId is { } clientId ? data.Clients.GetValueOrDefault(clientId) : null,
            entry.TaskId,
            entry.TaskId is { } taskId ? data.Tasks.GetValueOrDefault(taskId)?.Name : null,
            tags,
            tags.Select(id => data.Tags[id]).Order(StringComparer.InvariantCultureIgnoreCase).ToList(),
            entry.IsBillable,
            entry.Start,
            entry.Stop,
            entry.Seconds,
            data.ShowMoney ? entry.Amount : null,
            data.ShowMoney ? entry.LaborCost : null,
            entry.Percent,
            TimeShareResponse.From(entry.Shares ?? []));
    }

    private static List<SummaryGroup> Groups(ReportData data, IEnumerable<ReportEntry> entries, ReportGrouping groupBy, ReportGrouping? subGroupBy) =>
        entries
            .SelectMany(entry => Labels(data, entry, groupBy).Select(label => (label, entry)))
            .GroupBy(x => x.label.Key)
            .Select(group =>
            {
                var label = group.First().label;
                var members = group.Select(x => x.entry).ToList();
                var totals = data.Total(members);
                var subGroups = subGroupBy is { } sub && sub != groupBy ? Groups(data, members, sub, null) : [];
                return new SummaryGroup(
                    label.Key,
                    label.Id,
                    label.Name,
                    label.Detail,
                    label.Color,
                    totals.Seconds,
                    totals.BillableSeconds,
                    totals.BilledSeconds,
                    totals.Amount,
                    totals.LaborCost,
                    subGroups);
            })
            .OrderByDescending(g => g.Seconds)
            .ThenBy(g => g.Name, StringComparer.InvariantCultureIgnoreCase)
            .ToList();

    private static GroupLabel ProjectLabel(ReportData data, Guid? projectId) =>
        projectId is { } id && data.Projects.TryGetValue(id, out var project)
            ? new GroupLabel(id.ToString(), id, project.Name, project.ClientId is { } clientId ? data.Clients.GetValueOrDefault(clientId) : null, project.Color)
            : new GroupLabel(None, null, "No project", null, null);

    private static string UserName(ReportData data, Guid userId) => data.Users.GetValueOrDefault(userId) ?? "Former member";

    private static IEnumerable<DateOnly> BucketStarts(DateOnly from, DateOnly to, ReportResolution resolution, DayOfWeek weekStart)
    {
        for (var start = BucketStart(from, resolution, weekStart); start <= to; start = Next(start, resolution))
        {
            yield return start;
        }
    }

    private static DateOnly BucketStart(DateOnly day, ReportResolution resolution, DayOfWeek weekStart) => resolution switch
    {
        ReportResolution.Week => day.AddDays(-(((int)day.DayOfWeek - (int)weekStart + 7) % 7)),
        ReportResolution.Month => new DateOnly(day.Year, day.Month, 1),
        _ => day,
    };

    private static DateOnly Next(DateOnly start, ReportResolution resolution) => resolution switch
    {
        ReportResolution.Week => start.AddDays(7),
        ReportResolution.Month => start.AddMonths(1),
        _ => start.AddDays(1),
    };

    /// <summary>Sorts by the key, then newest first, so equal keys keep a stable, sensible order.</summary>
    private static IOrderedEnumerable<ReportEntry> Order<TKey>(
        IEnumerable<ReportEntry> entries,
        Func<ReportEntry, TKey> key,
        bool descending,
        IComparer<TKey>? comparer = null) =>
        (descending ? entries.OrderByDescending(key, comparer) : entries.OrderBy(key, comparer))
            .ThenByDescending(e => e.Start)
            .ThenBy(e => e.Id);
}
