using System.ComponentModel.DataAnnotations;
using Spindle.Api.TimeEntries;
using Spindle.Domain.Reports;

namespace Spindle.Api.Reports;

/// <summary>What a report's time is grouped by.</summary>
public enum ReportGrouping
{
    Project,
    Client,
    User,
    Tag,
    Task,
    Description,
}

public enum ReportSort
{
    Date,
    Duration,
    Description,
    User,
    Project,
}

/// <summary>How long each bar of a report's chart is.</summary>
public enum ReportResolution
{
    Day,
    Week,
    Month,
}

/// <summary>
/// Which time a report covers: finished entries that start on the given days, in the caller's time zone.
/// In the id lists, <see cref="Guid.Empty"/> stands for "without", e.g. time without a project.
/// </summary>
public record ReportFilter
{
    public const int MaxDays = 366;

    /// <summary>The first day.</summary>
    [Required]
    public DateOnly? From { get; init; }

    /// <summary>The last day, included.</summary>
    [Required]
    public DateOnly? To { get; init; }

    /// <summary>Whose time. Members only ever see their own; admins see everyone's unless they choose.</summary>
    [MaxLength(1000)]
    public Guid[] UserIds { get; init; } = [];

    /// <summary>Time of the groups' members, together with <see cref="UserIds"/>.</summary>
    [MaxLength(1000)]
    public Guid[] GroupIds { get; init; } = [];

    [MaxLength(1000)]
    public Guid[] ProjectIds { get; init; } = [];

    [MaxLength(1000)]
    public Guid[] ClientIds { get; init; } = [];

    [MaxLength(1000)]
    public Guid[] TaskIds { get; init; } = [];

    /// <summary>Entries with any of these tags.</summary>
    [MaxLength(1000)]
    public Guid[] TagIds { get; init; } = [];

    /// <summary>Only billable time, only non-billable time, or both when left out.</summary>
    public bool? Billable { get; init; }

    /// <summary>Entries whose description contains this.</summary>
    [StringLength(200)]
    public string? Description { get; init; }

    /// <summary>How each entry's duration is rounded before it is added up and billed.</summary>
    public RoundingMode Rounding { get; init; }

    /// <summary>What durations are rounded to: 1, 5, 6, 10, 12, 15, 30 or 60 minutes.</summary>
    public int RoundingMinutes { get; init; } = 15;
}

public sealed record SummaryReportRequest : ReportFilter
{
    public ReportGrouping GroupBy { get; init; } = ReportGrouping.Project;

    /// <summary>What each group is broken down by, if anything.</summary>
    public ReportGrouping? SubGroupBy { get; init; } = ReportGrouping.Description;
}

public sealed record DetailedReportRequest : ReportFilter
{
    public const int MaxPageSize = 200;

    public ReportSort Sort { get; init; } = ReportSort.Date;

    public bool Descending { get; init; } = true;

    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, MaxPageSize)]
    public int PageSize { get; init; } = 50;
}

public sealed record WeeklyReportRequest : ReportFilter
{
    public const int MaxWeeklyDays = 31;

    public ReportGrouping GroupBy { get; init; } = ReportGrouping.Project;
}

/// <param name="Amount">
/// What the billed time comes to in the workspace currency, at the rates of each day. Shown to admins only.
/// </param>
/// <param name="LaborCost">What the time cost, from the members' labor costs of each day. Shown to admins only.</param>
/// <param name="BilledSeconds">
/// The time billed: the billable time, except on projects whose billed time is entered separately, where the days
/// entered count as entered. Shown to admins only.
/// </param>
public sealed record ReportTotals(long Seconds, long BillableSeconds, decimal? Amount, decimal? LaborCost, int EntryCount, long? BilledSeconds);

/// <param name="Start">The first day of the bar: a day, the first day of a week (as the caller's weeks start), or the first of a month.</param>
public sealed record ReportBucket(DateOnly Start, long Seconds, long BillableSeconds);

/// <param name="Key">Identifies the group within the report, e.g. a project's id, or <c>none</c> for time without one.</param>
/// <param name="Detail">Context for the name: a project's client, or a task's project.</param>
/// <param name="BilledSeconds">The time billed; see <see cref="ReportTotals.BilledSeconds"/>. Shown to admins only.</param>
public sealed record SummaryGroup(
    string Key,
    Guid? Id,
    string Name,
    string? Detail,
    string? Color,
    long Seconds,
    long BillableSeconds,
    long? BilledSeconds,
    decimal? Amount,
    decimal? LaborCost,
    IReadOnlyList<SummaryGroup> SubGroups);

/// <param name="Currency">The currency of all amounts, e.g. <c>EUR</c>.</param>
public sealed record SummaryReport(
    string Currency,
    ReportTotals Totals,
    ReportResolution Resolution,
    IReadOnlyList<ReportBucket> Buckets,
    IReadOnlyList<SummaryGroup> Groups);

/// <summary>
/// An entry in the detailed report. A split entry has a row for each of its parts: <see cref="Seconds"/> and the money
/// are the part's, <see cref="Percent"/> says how much of the entry it is, and <see cref="Shares"/> lists all parts.
/// </summary>
public sealed record DetailedEntry(
    Guid Id,
    Guid UserId,
    string UserName,
    string Description,
    Guid? ProjectId,
    string? ProjectName,
    string? ProjectColor,
    string? ClientName,
    Guid? TaskId,
    string? TaskName,
    IReadOnlyList<Guid> TagIds,
    IReadOnlyList<string> Tags,
    bool IsBillable,
    DateTimeOffset Start,
    DateTimeOffset Stop,
    long Seconds,
    decimal? Amount,
    decimal? LaborCost,
    int Percent,
    IReadOnlyList<TimeShareResponse> Shares);

public sealed record DetailedReport(
    string Currency,
    ReportTotals Totals,
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<DetailedEntry> Entries);

/// <param name="Days">Seconds on each of the report's days, in order.</param>
public sealed record WeeklyRow(
    string Key,
    Guid? Id,
    string Name,
    string? Detail,
    string? Color,
    IReadOnlyList<long> Days,
    long Seconds,
    decimal? Amount);

public sealed record WeeklyReport(
    string Currency,
    ReportTotals Totals,
    IReadOnlyList<DateOnly> Days,
    IReadOnlyList<long> DayTotals,
    IReadOnlyList<WeeklyRow> Rows);
