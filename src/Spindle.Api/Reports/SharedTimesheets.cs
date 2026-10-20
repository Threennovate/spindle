using System.Globalization;
using Spindle.Api.Reports.Export;
using Spindle.Domain.Billing;
using Spindle.Domain.Reports;

namespace Spindle.Api.Reports;

/// <summary>
/// What someone with a timesheet link sees: the billed time of the report it was made from, day by day. Amounts are
/// null unless the link shows them.
/// </summary>
/// <param name="Workspace">Who the timesheet is from: the workspace, the one person whose time it counts, or the name the link was given.</param>
/// <param name="CountedTo">The last day counted: <paramref name="To"/>, or yesterday when the link leaves today out.</param>
/// <param name="Step">How many periods back from the link's own period this is: 0, or less on a relative link.</param>
/// <param name="CanStepBack">Whether an earlier period of the same length can be shown.</param>
/// <param name="Seconds">All the time billed.</param>
/// <param name="Projects">Time per project, most first.</param>
/// <param name="Days">One row per day and project, and per person when people are shown, in order.</param>
public sealed record SharedTimesheet(
    string Title,
    string Workspace,
    DateOnly From,
    DateOnly To,
    DateOnly CountedTo,
    int Step,
    bool CanStepBack,
    HourFormat HourFormat,
    bool ShowPeople,
    string Currency,
    long Seconds,
    decimal? Amount,
    IReadOnlyList<SharedTimesheetProject> Projects,
    IReadOnlyList<SharedTimesheetDay> Days);

public sealed record SharedTimesheetProject(string Name, string? Client, string? Color, long Seconds, decimal? Amount);

/// <param name="Person">Who did the work; null unless people are shown, and for what agreements are paid.</param>
/// <param name="Descriptions">What was done, each once, in the order it was tracked.</param>
public sealed record SharedTimesheetDay(
    DateOnly Day,
    string Project,
    string? ProjectColor,
    string? Person,
    IReadOnlyList<string> Descriptions,
    long Seconds,
    decimal? Amount);

/// <summary>
/// Turns a report into a timesheet for a client. Only billed time counts: on projects whose billed time is entered
/// separately, the days as billed, never the time tracked. Each part of a split entry counts on its own project, and
/// nothing says where the rest of the entry went.
/// </summary>
internal static class SharedTimesheets
{

    /// <summary>The descriptions of a day's entries as a timesheet lists them: each once, in order, none empty.</summary>
    public static IReadOnlyList<string> Describe(IEnumerable<string> descriptions) =>
        descriptions.Select(d => d.Trim()).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <param name="described">What days were described as by hand, by project, person and day.</param>
    /// <param name="today">Today in the link's time zone.</param>
    public static SharedTimesheet Build(
        SharedReport report,
        ReportData data,
        IReadOnlyDictionary<(Guid ProjectId, Guid UserId, DateOnly Day), string> described,
        int step,
        DateOnly today)
    {
        var countedTo = !report.ShowToday && data.To >= today ? today.AddDays(-1) : data.To;
        var counted = data.Entries.Where(e => e.Day <= countedTo).ToList();

        // Time entries and billing adjustments add up to the time billed; what agreements are paid is money only.
        var rows = counted.Where(e => e.Kind != ReportEntryKind.Agreement || (report.ShowAmounts && e.Amount is not (null or 0)));

        var grouped = rows
            .GroupBy(e => (e.Day, e.ProjectId, UserId: report.ShowPeople ? e.UserId : Guid.Empty))
            .Select(group =>
            {
                var project = group.Key.ProjectId is { } projectId ? data.Projects.GetValueOrDefault(projectId) : null;
                var day = new SharedTimesheetDay(
                    group.Key.Day,
                    project?.Name ?? "No project",
                    project?.Color,
                    report.ShowPeople && group.Key.UserId != Guid.Empty ? data.Users.GetValueOrDefault(group.Key.UserId) ?? "Former member" : null,
                    Descriptions(data, group.ToList(), described),
                    group.Sum(e => e.BilledSeconds),
                    report.ShowAmounts ? group.Sum(e => e.Amount ?? 0) : null);
                return (group.Key.ProjectId, Project: project, Day: day);
            })
            // A day billed as nothing isn't on the timesheet.
            .Where(row => row.Day.Seconds > 0 || row.Day.Amount is not (null or 0))
            .OrderBy(row => row.Day.Day)
            .ThenBy(row => row.Day.Project, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(row => row.Day.Person, StringComparer.InvariantCultureIgnoreCase)
            .ToList();
        var days = grouped.Select(row => row.Day).ToList();

        var projects = grouped
            .GroupBy(row => row.ProjectId)
            .Select(group => new SharedTimesheetProject(
                group.First().Day.Project,
                group.First().Project?.ClientId is { } clientId ? data.Clients.GetValueOrDefault(clientId) : null,
                group.First().Project?.Color,
                group.Sum(row => row.Day.Seconds),
                report.ShowAmounts ? group.Sum(row => row.Day.Amount ?? 0) : null))
            .OrderByDescending(project => project.Seconds)
            .ThenBy(project => project.Name, StringComparer.InvariantCultureIgnoreCase)
            .ToList();

        return new SharedTimesheet(
            report.Title,
            Sender(report, data, counted),
            data.From,
            data.To,
            countedTo,
            step,
            report.IsRelative && step > -SharedReport.MaxStepsBack,
            report.HourFormat,
            report.ShowPeople,
            data.Currency,
            days.Sum(day => day.Seconds),
            report.ShowAmounts ? days.Sum(day => day.Amount ?? 0) : null,
            projects,
            days);
    }

    /// <summary>
    /// The name the timesheet is from. A link from the person who did the work names them only while the period holds
    /// one person's time; with several, or none, it is the workspace's.
    /// </summary>
    private static string Sender(SharedReport report, ReportData data, IEnumerable<ReportEntry> counted)
    {
        switch (report.Sender)
        {
            case SharedSender.Custom when report.SenderName is { } name:
                return name;
            case SharedSender.Person:
                var people = counted.Where(e => e.Kind != ReportEntryKind.Agreement).Select(e => e.UserId).Distinct().Take(2).ToList();
                return people.Count == 1 && data.Users.GetValueOrDefault(people[0]) is { } person ? person : data.WorkspaceName;
            default:
                return data.WorkspaceName;
        }
    }

    /// <summary>The timesheet as a table, for CSV, Excel and PDF.</summary>
    public static ReportTable Table(SharedTimesheet timesheet)
    {
        var hours = timesheet.HourFormat == HourFormat.Decimal
            ? new TableColumn("Hours", CellKind.Hours, 8)
            : new TableColumn("Time", CellKind.Duration, 9);
        var showProject = timesheet.Projects.Count > 1;
        var columns = new List<TableColumn> { new("Date", CellKind.Date, 13) };
        if (showProject)
        {
            columns.Add(new TableColumn("Project", CellKind.Text, 18));
        }

        if (timesheet.ShowPeople)
        {
            columns.Add(new TableColumn("Person", CellKind.Text, 16));
        }

        columns.Add(new TableColumn("Description", CellKind.Text, 48));
        columns.Add(hours);
        var showAmount = timesheet.Amount is not null;
        if (showAmount)
        {
            columns.Add(new TableColumn($"Amount ({timesheet.Currency})", CellKind.Money, 11));
        }

        // Boxed apiece: a plain conditional would turn the seconds into a decimal, which files write as a number.
        object Time(long seconds) => hours.Kind == CellKind.Hours ? ReportTables.Hours(seconds) : (object)seconds;

        var rows = timesheet.Days.Select(day =>
        {
            var cells = new List<object?> { day.Day };
            if (showProject)
            {
                cells.Add(day.Project);
            }

            if (timesheet.ShowPeople)
            {
                cells.Add(day.Person ?? string.Empty);
            }

            cells.Add(string.Join("; ", day.Descriptions));
            cells.Add(Time(day.Seconds));
            if (showAmount)
            {
                cells.Add(day.Amount);
            }

            return new TableRow(cells);
        }).ToList();

        var footer = new List<object?> { "Total" };
        footer.AddRange(Enumerable.Repeat<object?>(string.Empty, columns.Count - (showAmount ? 3 : 2)));
        footer.Add(Time(timesheet.Seconds));
        if (showAmount)
        {
            footer.Add(timesheet.Amount);
        }

        var totals = new List<(string, string)>
        {
            ("Total", timesheet.HourFormat == HourFormat.Decimal
                ? string.Create(CultureInfo.InvariantCulture, $"{ReportTables.Hours(timesheet.Seconds):0.00} h")
                : ReportTables.FormatMinutes(timesheet.Seconds)),
        };
        if (timesheet.Amount is { } amount)
        {
            totals.Add(("Amount", ReportTables.FormatMoney(amount, timesheet.Currency)));
        }

        return new ReportTable(
            timesheet.Title,
            timesheet.Workspace,
            ReportTables.FormatPeriod(timesheet.From, timesheet.To),
            timesheet.Currency,
            totals,
            columns,
            rows,
            new TableRow(footer, IsGroup: true),
            $"timesheet-{FileSlug(timesheet.Title)}-{timesheet.From:yyyy-MM-dd}-to-{timesheet.To:yyyy-MM-dd}",
            "Timesheet",
            Minutes: true);
    }

    /// <summary>
    /// What a row says was done: for each person, what their day was described as by hand, or else their entries'
    /// descriptions; then what agreements are paid.
    /// </summary>
    private static IReadOnlyList<string> Descriptions(
        ReportData data,
        IReadOnlyList<ReportEntry> entries,
        IReadOnlyDictionary<(Guid ProjectId, Guid UserId, DateOnly Day), string> described)
    {
        var lines = new List<string>();
        foreach (var person in entries.Where(e => e.Kind != ReportEntryKind.Agreement).OrderBy(e => e.Start).GroupBy(e => e.UserId))
        {
            var first = person.First();
            if (first.ProjectId is { } projectId && described.TryGetValue((projectId, person.Key, first.Day), out var text))
            {
                lines.Add(text);
            }
            else
            {
                lines.AddRange(person.Where(e => e.Kind == ReportEntryKind.Time).Select(e => e.Description));
            }
        }

        foreach (var payment in entries.Where(e => e.Kind == ReportEntryKind.Agreement))
        {
            if (data.Agreements.Find(payment.AgreementId) is { } agreement)
            {
                lines.Add(agreement.Kind == AgreementKind.Retainer ? $"Retainer: {agreement.Name}" : $"Fixed price: {agreement.Name}");
            }
        }

        return Describe(lines);
    }

    /// <summary>A file name's worth of the title: lowercase letters and digits, joined by dashes.</summary>
    private static string FileSlug(string title)
    {
        var words = new string(title.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? "report" : string.Join('-', words);
    }
}
