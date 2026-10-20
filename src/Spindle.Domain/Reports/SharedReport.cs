using Spindle.Domain.Common;

namespace Spindle.Domain.Reports;

/// <summary>Which days a shared report covers when it is opened.</summary>
public enum SharedPeriod
{
    ThisWeek,
    LastWeek,
    ThisMonth,
    LastMonth,

    /// <summary>The same days whenever it is opened.</summary>
    Fixed,
}

/// <summary>How hours are written for whoever reads a shared report.</summary>
public enum HourFormat
{
    /// <summary>Hours and minutes, e.g. 3:40.</summary>
    Clock,

    /// <summary>Hours as a decimal number, e.g. 3.67, as invoices often have them.</summary>
    Decimal,
}

/// <summary>Whose name a shared report is from, at the top of the page and in its files.</summary>
public enum SharedSender
{
    /// <summary>The workspace's name.</summary>
    Workspace,

    /// <summary>The name of the one person whose time it counts; the workspace's when it counts several people's.</summary>
    Person,

    /// <summary>A name the link was given, e.g. a freelancer's trading name.</summary>
    Custom,
}

/// <summary>Which time a shared report counts: the report filter it was made from, without its days.</summary>
public sealed record SharedReportFilter(
    Guid[] UserIds,
    Guid[] GroupIds,
    Guid[] ProjectIds,
    Guid[] ClientIds,
    Guid[] TaskIds,
    Guid[] TagIds,
    string? Description,
    RoundingMode Rounding,
    int RoundingMinutes);

/// <summary>What a shared report shows, and in whose calendar it counts days.</summary>
/// <param name="From">The first day of a <see cref="SharedPeriod.Fixed"/> period.</param>
/// <param name="To">The last day of a <see cref="SharedPeriod.Fixed"/> period, included.</param>
public sealed record SharedReportSettings(
    string Title,
    SharedPeriod Period,
    DateOnly? From,
    DateOnly? To,
    string TimeZone,
    DayOfWeek WeekStart,
    SharedReportFilter Filter,
    bool ShowPeople,
    bool ShowAmounts,
    HourFormat HourFormat,
    SharedSender Sender = SharedSender.Workspace,
    string? SenderName = null,
    bool ShowToday = true);

/// <summary>
/// A timesheet anyone with its link can read without an account: the billed time of a report, worked out again
/// whenever it is opened. It belongs to the workspace, not to whoever made it.
/// </summary>
public sealed class SharedReport : Entity
{
    public const int TitleMaxLength = 100;
    public const int SenderNameMaxLength = 100;
    public const int MaxDays = 366;

    /// <summary>How far back a relative period can be stepped, in periods: ten years of months.</summary>
    public const int MaxStepsBack = 120;

    private SharedReport()
    {
        Title = string.Empty;
        Token = string.Empty;
        TimeZone = string.Empty;
        UserIds = GroupIds = ProjectIds = ClientIds = TaskIds = TagIds = [];
    }

    public Guid WorkspaceId { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public string Title { get; private set; }

    /// <summary>The secret part of the link.</summary>
    public string Token { get; private set; }

    public SharedPeriod Period { get; private set; }

    public DateOnly? From { get; private set; }

    public DateOnly? To { get; private set; }

    /// <summary>The time zone days are counted in: that of whoever last set the report up.</summary>
    public string TimeZone { get; private set; }

    public DayOfWeek WeekStart { get; private set; }

    public Guid[] UserIds { get; private set; }

    public Guid[] GroupIds { get; private set; }

    public Guid[] ProjectIds { get; private set; }

    public Guid[] ClientIds { get; private set; }

    public Guid[] TaskIds { get; private set; }

    public Guid[] TagIds { get; private set; }

    public string? Description { get; private set; }

    public RoundingMode Rounding { get; private set; }

    public int RoundingMinutes { get; private set; }

    public bool ShowPeople { get; private set; }

    public bool ShowAmounts { get; private set; }

    public HourFormat HourFormat { get; private set; }

    public SharedSender Sender { get; private set; }

    /// <summary>
    /// Whether today's time counts while today is in the period. Left out, the timesheet counts up to yesterday, so
    /// nobody reads half a day as a whole one.
    /// </summary>
    public bool ShowToday { get; private set; }

    /// <summary>The name the timesheet is from when <see cref="Sender"/> is <see cref="SharedSender.Custom"/>.</summary>
    public string? SenderName { get; private set; }

    /// <summary>When someone last opened the link.</summary>
    public DateTimeOffset? LastOpenedAt { get; private set; }

    public SharedReportFilter Filter => new(UserIds, GroupIds, ProjectIds, ClientIds, TaskIds, TagIds, Description, Rounding, RoundingMinutes);

    /// <summary>Whether whoever reads it can step back to earlier periods of the same length.</summary>
    public bool IsRelative => Period != SharedPeriod.Fixed;

    public static SharedReport Create(Guid workspaceId, Guid createdByUserId, string token, SharedReportSettings settings)
    {
        var report = new SharedReport
        {
            WorkspaceId = Guard.Id(workspaceId),
            CreatedByUserId = Guard.Id(createdByUserId),
        };
        report.NewToken(token);
        report.Update(settings);
        return report;
    }

    public void Update(SharedReportSettings settings)
    {
        if (settings.Period == SharedPeriod.Fixed)
        {
            if (settings.From is not { } from || settings.To is not { } to || to < from || to.DayNumber - from.DayNumber >= MaxDays)
            {
                throw new DomainValidationException("to", $"Choose at most {MaxDays} days, ending on or after the first one.");
            }
        }

        if (settings.Filter.Rounding != RoundingMode.None && !DurationRounding.AllowedMinutes.Contains(settings.Filter.RoundingMinutes))
        {
            throw new DomainValidationException("roundingMinutes", "Round to 1, 5, 6, 10, 12, 15, 30 or 60 minutes.");
        }

        Title = Guard.Name(settings.Title, TitleMaxLength, "title");
        Sender = settings.Sender;
        SenderName = settings.Sender == SharedSender.Custom ? Guard.Name(settings.SenderName ?? string.Empty, SenderNameMaxLength, "senderName") : null;
        Period = settings.Period;
        From = settings.Period == SharedPeriod.Fixed ? settings.From : null;
        To = settings.Period == SharedPeriod.Fixed ? settings.To : null;
        TimeZone = settings.TimeZone;
        WeekStart = settings.WeekStart;
        var filter = settings.Filter;
        UserIds = [.. filter.UserIds.Distinct()];
        GroupIds = [.. filter.GroupIds.Distinct()];
        ProjectIds = [.. filter.ProjectIds.Distinct()];
        ClientIds = [.. filter.ClientIds.Distinct()];
        TaskIds = [.. filter.TaskIds.Distinct()];
        TagIds = [.. filter.TagIds.Distinct()];
        Description = string.IsNullOrWhiteSpace(filter.Description) ? null : filter.Description.Trim();
        Rounding = filter.Rounding;
        RoundingMinutes = filter.RoundingMinutes;
        ShowPeople = settings.ShowPeople;
        ShowAmounts = settings.ShowAmounts;
        ShowToday = settings.ShowToday;
        HourFormat = settings.HourFormat;
    }

    /// <summary>Gives the report a new link; the old one stops working.</summary>
    public void NewToken(string token) =>
        Token = string.IsNullOrWhiteSpace(token) ? throw new ArgumentException("A token is required.", nameof(token)) : token;

    /// <summary>
    /// The days it covers when opened on <paramref name="today"/>, stepped back by <paramref name="step"/> periods of
    /// the same length (0 or less), or null when it can't be stepped that far.
    /// </summary>
    public (DateOnly From, DateOnly To)? Days(DateOnly today, int step)
    {
        if (step > 0 || step < -MaxStepsBack || (!IsRelative && step != 0))
        {
            return null;
        }

        return Period switch
        {
            SharedPeriod.ThisWeek => Week(today.AddDays(7 * step)),
            SharedPeriod.LastWeek => Week(today.AddDays(7 * (step - 1))),
            SharedPeriod.ThisMonth => Month(today.AddMonths(step)),
            SharedPeriod.LastMonth => Month(today.AddMonths(step - 1)),
            _ => (From!.Value, To!.Value),
        };
    }

    private (DateOnly, DateOnly) Week(DateOnly day)
    {
        var from = day.AddDays(-(((int)day.DayOfWeek - (int)WeekStart + 7) % 7));
        return (from, from.AddDays(6));
    }

    private static (DateOnly, DateOnly) Month(DateOnly day)
    {
        var from = new DateOnly(day.Year, day.Month, 1);
        return (from, from.AddMonths(1).AddDays(-1));
    }
}
