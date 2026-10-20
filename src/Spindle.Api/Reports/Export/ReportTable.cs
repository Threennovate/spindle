using System.Globalization;
using NodaTime;

namespace Spindle.Api.Reports.Export;

/// <summary>What a column holds, which decides how each file format writes it.</summary>
internal enum CellKind
{
    Text,

    /// <summary>Seconds, written as a duration such as 1:30:00.</summary>
    Duration,

    /// <summary>Hours as a decimal number, such as 1.50, for invoices and spreadsheets.</summary>
    Hours,

    Money,
    Date,
    Time,
}

/// <summary>Which of a report's figures a number column shows, so that rows and totals fill it alike.</summary>
internal enum Figure
{
    None,
    Duration,
    Hours,
    BilledHours,
    Amount,
    LaborCost,
}

/// <param name="Width">Relative width, roughly in characters.</param>
internal sealed record TableColumn(string Header, CellKind Kind, double Width, Figure Figure = Figure.None);

/// <summary>The figures of a group, an entry or the totals.</summary>
internal readonly record struct Figures(long Seconds, long? BilledSeconds, decimal? Amount, decimal? LaborCost)
{
    public static Figures Of(ReportTotals totals) => new(totals.Seconds, totals.BilledSeconds, totals.Amount, totals.LaborCost);

    public object? Value(Figure figure) => figure switch
    {
        // Durations stay whole seconds, which the file formats write as durations.
        Figure.Duration => (object)Seconds,
        Figure.Hours => ReportTables.Hours(Seconds),
        Figure.BilledHours => BilledSeconds is { } billed ? ReportTables.Hours(billed) : null,
        Figure.Amount => Amount,
        Figure.LaborCost => LaborCost,
        _ => null,
    };
}

/// <param name="Level">0 for a row of its own or a group; 1 for a sub-group, which repeats its group in the first cell.</param>
internal sealed record TableRow(IReadOnlyList<object?> Cells, bool IsGroup = false, int Level = 0);

/// <summary>A report as a table, the one shape that CSV, Excel and PDF exports are all written from.</summary>
/// <param name="Kind">What the document is, when the title names something else, such as "Timesheet" for a client's link.</param>
/// <param name="Minutes">Durations on paper to the minute (3:40), as a timesheet link shows them, rather than the second.</param>
internal sealed record ReportTable(
    string Title,
    string Workspace,
    string Period,
    string Currency,
    IReadOnlyList<(string Label, string Value)> Totals,
    IReadOnlyList<TableColumn> Columns,
    IReadOnlyList<TableRow> Rows,
    TableRow Footer,
    string FileName,
    string? Kind = null,
    bool Minutes = false);

/// <summary>Lays out the summary, detailed and weekly reports as tables.</summary>
internal static class ReportTables
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    public static ReportTable Summary(ReportData data, SummaryReport report, ReportGrouping groupBy, ReportGrouping? subGroupBy)
    {
        var sub = subGroupBy is { } s && s != groupBy ? s : (ReportGrouping?)null;
        var detail = DetailHeader(groupBy);

        var columns = new List<TableColumn> { new(GroupingName(groupBy), CellKind.Text, 28) };
        if (detail is not null)
        {
            columns.Add(new TableColumn(detail, CellKind.Text, 18));
        }

        if (sub is { } subGrouping)
        {
            columns.Add(new TableColumn(GroupingName(subGrouping), CellKind.Text, 32));
        }

        columns.AddRange(NumberColumns(data, report.Totals));

        var rows = new List<TableRow>();
        foreach (var group in report.Groups)
        {
            rows.Add(Row(columns, [group.Name, .. Detail(detail, group), .. Empty(sub)], Of(group), isGroup: sub is not null));
            foreach (var subGroup in group.SubGroups)
            {
                rows.Add(Row(columns, [group.Name, .. Detail(detail, group), subGroup.Name], Of(subGroup), level: 1));
            }
        }

        return Table(data, "Summary report", "summary", report.Totals, columns, rows);
    }

    public static ReportTable Detailed(ReportData data, IEnumerable<DetailedEntry> entries)
    {
        var columns = new List<TableColumn>
        {
            new("Date", CellKind.Date, 13),
            new("Start", CellKind.Time, 7),
            new("End", CellKind.Time, 7),
            new("Duration", CellKind.Duration, 9, Figure.Duration),
            new("Hours", CellKind.Hours, 7, Figure.Hours),
            new("Member", CellKind.Text, 16),
            new("Project", CellKind.Text, 18),
            new("Client", CellKind.Text, 14),
            new("Task", CellKind.Text, 14),
            new("Description", CellKind.Text, 32),
            new("Tags", CellKind.Text, 14),
            new("Billable", CellKind.Text, 8),
        };
        var totals = data.Totals();
        if (data.ShowMoney)
        {
            columns.Add(new TableColumn($"Amount ({data.Currency})", CellKind.Money, 11, Figure.Amount));
        }

        var withCost = ShowsLaborCost(data, totals);
        if (withCost)
        {
            columns.Add(new TableColumn($"Labor cost ({data.Currency})", CellKind.Money, 11, Figure.LaborCost));
        }

        var rows = entries.Select(entry =>
        {
            var start = Instant.FromDateTimeOffset(entry.Start).InZone(data.Zone);
            var stop = Instant.FromDateTimeOffset(entry.Stop).InZone(data.Zone);
            var cells = new List<object?>
            {
                start.Date.ToDateOnly(),
                start.TimeOfDay.ToTimeOnly(),
                stop.TimeOfDay.ToTimeOnly(),
                entry.Seconds,
                Hours(entry.Seconds),
                entry.UserName,
                entry.Percent < 100 ? $"{entry.ProjectName ?? "No project"} ({entry.Percent}%)" : entry.ProjectName ?? string.Empty,
                entry.ClientName ?? string.Empty,
                entry.TaskName ?? string.Empty,
                entry.Description,
                string.Join(", ", entry.Tags),
                entry.IsBillable ? "Yes" : "No",
            };
            if (data.ShowMoney)
            {
                cells.Add(entry.Amount);
            }

            if (withCost)
            {
                cells.Add(entry.LaborCost);
            }

            return new TableRow(cells);
        }).ToList();

        return Table(data, "Detailed report", "detailed", totals, columns, rows);
    }

    public static ReportTable Weekly(ReportData data, WeeklyReport report, ReportGrouping groupBy)
    {
        var detail = DetailHeader(groupBy);
        var columns = new List<TableColumn> { new(GroupingName(groupBy), CellKind.Text, 26) };
        if (detail is not null)
        {
            columns.Add(new TableColumn(detail, CellKind.Text, 16));
        }

        columns.AddRange(report.Days.Select(day => new TableColumn(day.ToString("ddd d MMM", English), CellKind.Duration, 9)));
        columns.Add(new TableColumn("Total", CellKind.Duration, 9, Figure.Duration));
        if (data.ShowMoney)
        {
            columns.Add(new TableColumn($"Amount ({data.Currency})", CellKind.Money, 11, Figure.Amount));
        }

        var rows = report.Rows.Select(row =>
        {
            var cells = new List<object?> { row.Name };
            if (detail is not null)
            {
                cells.Add(row.Detail ?? string.Empty);
            }

            cells.AddRange(row.Days.Cast<object?>());
            cells.Add(row.Seconds);
            if (data.ShowMoney)
            {
                cells.Add(row.Amount);
            }

            return new TableRow(cells);
        }).ToList();

        var footer = new List<object?> { "Total" };
        if (detail is not null)
        {
            footer.Add(string.Empty);
        }

        footer.AddRange(report.DayTotals.Cast<object?>());
        footer.Add(report.Totals.Seconds);
        if (data.ShowMoney)
        {
            footer.Add(report.Totals.Amount);
        }

        return Table(data, "Weekly report", "weekly", report.Totals, columns, rows, new TableRow(footer, IsGroup: true));
    }

    public static string FormatDuration(long seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}");

    /// <summary>Hours and whole minutes, such as 30:41.</summary>
    public static string FormatMinutes(long seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}");

    /// <summary>"September 2026" for a whole month, "14 Sept 2026" for a day, otherwise the range.</summary>
    public static string FormatPeriod(DateOnly from, DateOnly to)
    {
        if (from == to)
        {
            return from.ToString("d MMM yyyy", English);
        }

        if (from.Day == 1 && to == from.AddMonths(1).AddDays(-1))
        {
            return from.ToString("MMMM yyyy", English);
        }

        return $"{from.ToString("d MMM yyyy", English)} – {to.ToString("d MMM yyyy", English)}";
    }

    public static decimal Hours(long seconds) => decimal.Round(seconds / 3600m, 2, MidpointRounding.AwayFromZero);

    public static string FormatMoney(decimal amount, string currency) =>
        string.Create(CultureInfo.InvariantCulture, $"{amount:#,##0.00} {currency}");

    private static ReportTable Table(
        ReportData data,
        string title,
        string kind,
        ReportTotals totals,
        IReadOnlyList<TableColumn> columns,
        IReadOnlyList<TableRow> rows,
        TableRow? footer = null)
    {
        var summary = new List<(string, string)>
        {
            ("Total", FormatDuration(totals.Seconds)),
            ("Billable", FormatDuration(totals.BillableSeconds)),
        };
        if (data.HasBilledTime && totals.BilledSeconds is { } billed)
        {
            summary.Add(("Billed", FormatDuration(billed)));
        }

        if (totals.Amount is { } amount)
        {
            summary.Add(("Amount", FormatMoney(amount, data.Currency)));
        }

        if (ShowsLaborCost(data, totals))
        {
            summary.Add(("Labor cost", FormatMoney(totals.LaborCost!.Value, data.Currency)));
        }

        return new ReportTable(
            title,
            data.WorkspaceName,
            FormatPeriod(data.From, data.To),
            data.Currency,
            summary,
            columns,
            rows,
            footer ?? TotalRow(columns, totals),
            $"spindle-{kind}-{data.From:yyyy-MM-dd}-to-{data.To:yyyy-MM-dd}");
    }

    /// <summary>The totals under a table: "Total" in the first column, the sums under the number columns.</summary>
    private static TableRow TotalRow(IReadOnlyList<TableColumn> columns, ReportTotals totals)
    {
        var figures = Figures.Of(totals);
        var cells = columns
            .Select((column, index) => index == 0 ? "Total" : column.Figure == Figure.None ? string.Empty : figures.Value(column.Figure))
            .ToList();
        return new TableRow(cells, IsGroup: true);
    }

    /// <summary>Time, and for admins money: billed hours where some were entered separately, and labor costs where set.</summary>
    private static IEnumerable<TableColumn> NumberColumns(ReportData data, ReportTotals totals)
    {
        yield return new TableColumn("Duration", CellKind.Duration, 10, Figure.Duration);
        yield return new TableColumn("Hours", CellKind.Hours, 8, Figure.Hours);
        if (data.ShowMoney && data.HasBilledTime)
        {
            yield return new TableColumn("Billed hours", CellKind.Hours, 8, Figure.BilledHours);
        }

        if (data.ShowMoney)
        {
            yield return new TableColumn($"Amount ({data.Currency})", CellKind.Money, 12, Figure.Amount);
        }

        if (ShowsLaborCost(data, totals))
        {
            yield return new TableColumn($"Labor cost ({data.Currency})", CellKind.Money, 12, Figure.LaborCost);
        }
    }

    private static bool ShowsLaborCost(ReportData data, ReportTotals totals) => data.ShowMoney && totals.LaborCost > 0;

    private static Figures Of(SummaryGroup group) => new(group.Seconds, group.BilledSeconds, group.Amount, group.LaborCost);

    private static TableRow Row(IReadOnlyList<TableColumn> columns, IReadOnlyList<object?> labels, Figures figures, bool isGroup = false, int level = 0)
    {
        var cells = new List<object?>(labels);
        cells.AddRange(columns.Skip(labels.Count).Select(column => figures.Value(column.Figure)));
        return new TableRow(cells, isGroup, level);
    }

    private static IEnumerable<object?> Detail(string? header, SummaryGroup group) =>
        header is null ? [] : [group.Detail ?? string.Empty];

    private static IEnumerable<object?> Empty(ReportGrouping? sub) => sub is null ? [] : [string.Empty];

    private static string? DetailHeader(ReportGrouping grouping) => grouping switch
    {
        ReportGrouping.Project => "Client",
        ReportGrouping.Task => "Project",
        _ => null,
    };

    private static string GroupingName(ReportGrouping grouping) => grouping switch
    {
        ReportGrouping.User => "Member",
        _ => grouping.ToString(),
    };
}
