using System.Globalization;
using NodaTime;
using Spindle.Domain.Clients;
using Spindle.Domain.Projects;
using Spindle.Domain.Tags;
using Spindle.Domain.TimeEntries;

namespace Spindle.Api.Import;

/// <summary>How dates in the file are written, when they are not like 2026-09-14.</summary>
public enum DateOrder
{
    Auto,
    YearMonthDay,
    MonthDayYear,
    DayMonthYear,
}

/// <summary>One time entry from the file, still in the local time of whoever exported it.</summary>
/// <param name="Placed">Whether the file gave only the day and the hours, so the start was placed (see <see cref="ImportFile.PlacedFrom"/>).</param>
internal sealed record ImportRow(
    int Line,
    string? UserName,
    string? Email,
    string? Client,
    string? Project,
    string? Task,
    string Description,
    bool Billable,
    LocalDateTime Start,
    LocalDateTime End,
    IReadOnlyList<string> Tags,
    bool Placed = false);

public sealed record ImportProblem(int Line, string Message);

/// <param name="Format">Where the file comes from, as far as its columns tell: Toggl Track, Clockify, Harvest, Spindle or CSV.</param>
/// <param name="DateOrderGuessed">Whether every date could be read either way, e.g. 03/04/2026, so the order is a guess.</param>
internal sealed record ParsedFile(string Format, DateOrder DateOrder, bool DateOrderGuessed, IReadOnlyList<ImportRow> Rows, IReadOnlyList<ImportProblem> Problems);

/// <summary>
/// Reads the detailed report CSV that Toggl Track, Clockify and Harvest export, and Spindle's own: one time entry
/// per row, found by its column names in any order.
/// </summary>
internal static partial class ImportFile
{
    public const int MaxRows = 100_000;

    /// <summary>
    /// When the first of a day's entries starts, if the file has only hours. Harvest tracks durations unless an
    /// account switches to start and end times, so its export often has a date and hours but no time of day.
    /// </summary>
    public static readonly LocalTime PlacedFrom = new(9, 0);

    private static readonly string[] TimeFormats = ["H:mm:ss", "H:mm", "h:mm:ss tt", "h:mm tt", "H:mm:ss.fff", "h:mm:sstt", "h:mmtt"];

    public static ParsedFile Parse(string content, DateOrder order)
    {
        var table = Csv.Read(content);
        if (table.Count == 0)
        {
            return Empty("CSV", order, "The file is empty.");
        }

        var columns = new Columns(table[0]);
        var harvest = columns.Format == "Harvest";
        if (columns.StartDate is null || (columns.StartTime is null && !harvest) || (columns.EndTime is null && columns.Duration is null))
        {
            return Empty(
                "CSV",
                order,
                "This file does not have the start and end of each entry. Export the detailed report as CSV from Toggl Track, Clockify or Harvest, with dates and times.");
        }

        var data = table.Skip(1).ToList();
        if (data.Count > MaxRows)
        {
            return Empty(columns.Format, order, $"The file has {data.Count:N0} entries. Import at most {MaxRows:N0} at a time, e.g. a year per file.");
        }

        var (dateOrder, guessed) = order == DateOrder.Auto ? DetectDateOrder(data, columns) : (order, false);
        var rows = new List<ImportRow>();
        var problems = new List<ImportProblem>();

        for (var index = 0; index < data.Count; index++)
        {
            var line = index + 2;
            var cells = data[index];
            try
            {
                rows.Add(ReadRow(line, cells, columns, dateOrder));
            }
            catch (FormatException problem)
            {
                problems.Add(new ImportProblem(line, problem.Message));
            }
        }

        return new ParsedFile(columns.Format, dateOrder, guessed, Place(rows), problems);
    }

    /// <summary>
    /// Gives entries without a time of day a start: each person's on a day go one after another from
    /// <see cref="PlacedFrom"/>, in the file's order, or earlier on a day too full to end by midnight. Importing the
    /// same file again places them the same way.
    /// </summary>
    private static List<ImportRow> Place(List<ImportRow> rows)
    {
        static (string?, string?, LocalDate) Day(ImportRow row) => (row.Email, row.UserName?.ToUpperInvariant(), row.Start.Date);
        static long Seconds(ImportRow row) => Period.Between(row.Start, row.End, PeriodUnits.Seconds).Seconds;

        var totals = rows.Where(r => r.Placed).GroupBy(Day).ToDictionary(g => g.Key, g => g.Sum(Seconds));
        var next = new Dictionary<(string?, string?, LocalDate), LocalDateTime>();

        return rows.Select(row =>
        {
            if (!row.Placed)
            {
                return row;
            }

            var day = Day(row);
            if (!next.TryGetValue(day, out var start))
            {
                // Until it is placed, a row starts at midnight. A day of more than 15 hours starts early enough to end by the next.
                var early = row.Start.PlusSeconds(Math.Max(0, (24 * 3600) - totals[day]));
                var usual = row.Start.Date + PlacedFrom;
                start = early < usual ? early : usual;
            }

            var placed = row with { Start = start, End = start.PlusSeconds(Seconds(row)) };
            next[day] = placed.End;
            return placed;
        }).ToList();
    }

    private static ImportRow ReadRow(int line, string[] cells, Columns columns, DateOrder order)
    {
        string? Cell(int? column) => column is { } c && c < cells.Length && cells[c].Trim() is { Length: > 0 } value ? value : null;

        var startDate = ParseDate(Cell(columns.StartDate), order) ?? throw new FormatException($"The start date “{Cell(columns.StartDate)}” is not a date.");
        // Harvest has a time of day only for entries tracked with start and end times; the rest are placed later.
        var placed = columns.Format == "Harvest" && Cell(columns.StartTime) is null;
        var startTime = placed
            ? LocalTime.Midnight
            : ParseTime(Cell(columns.StartTime)) ?? throw new FormatException($"The start time “{Cell(columns.StartTime)}” is not a time.");
        var start = startDate + startTime;

        LocalDateTime end;
        if (!placed && ParseTime(Cell(columns.EndTime)) is { } endTime)
        {
            var endDate = Cell(columns.EndDate) is { } text ? ParseDate(text, order) ?? throw new FormatException($"The end date “{text}” is not a date.") : startDate;
            end = endDate + endTime;
            // Without an end date, an end before the start means the entry ran past midnight.
            if (end < start && columns.EndDate is null)
            {
                end = end.PlusDays(1);
            }
        }
        else if (ParseDuration(Cell(columns.Duration)) is { } duration)
        {
            end = start.Plus(Period.FromSeconds((long)duration.TotalSeconds));
        }
        else
        {
            throw new FormatException("The entry has neither an end time nor a duration.");
        }

        if (end < start)
        {
            throw new FormatException("The entry ends before it starts.");
        }

        if (Period.Between(start, end, PeriodUnits.Seconds).Seconds > TimeEntry.MaxDuration.TotalSeconds)
        {
            throw new FormatException($"The entry is longer than {TimeEntry.MaxDuration.TotalHours:0} hours.");
        }

        var description = Cell(columns.Description) ?? string.Empty;
        if (description.Length > TimeEntry.DescriptionMaxLength)
        {
            throw new FormatException($"The description is longer than {TimeEntry.DescriptionMaxLength} characters.");
        }

        var client = Name(Cell(columns.Client), Client.NameMaxLength, "client");
        var project = Name(Cell(columns.Project), Project.NameMaxLength, "project");
        var task = project is null ? null : Name(Cell(columns.Task), ProjectTask.NameMaxLength, "task");
        var tags = (Cell(columns.Tags) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(tag => Name(tag, Tag.NameMaxLength, "tag")!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var user = Cell(columns.User) ?? (string.Join(' ', new[] { Cell(columns.FirstName), Cell(columns.LastName) }.OfType<string>()) is { Length: > 0 } name ? name : null);

        return new ImportRow(
            line,
            user,
            Cell(columns.Email)?.ToLowerInvariant(),
            client,
            project,
            task,
            description,
            IsYes(Cell(columns.Billable)),
            start,
            end,
            tags,
            placed);
    }

    private static string? Name(string? value, int maxLength, string what) => value switch
    {
        null => null,
        { Length: var length } when length > maxLength => throw new FormatException($"The {what} name “{value[..20]}…” is longer than {maxLength} characters."),
        _ => value,
    };

    private static bool IsYes(string? value) =>
        value is not null && (value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value == "1"
            || value.Equals("billable", StringComparison.OrdinalIgnoreCase));

    public static LocalDate? ParseDate(string? text, DateOrder order)
    {
        if (text is null)
        {
            return null;
        }

        var formats = order switch
        {
            DateOrder.MonthDayYear => new[] { "M/d/yyyy", "M-d-yyyy", "M.d.yyyy", "M/d/yy", "yyyy-M-d" },
            DateOrder.DayMonthYear => new[] { "d/M/yyyy", "d.M.yyyy", "d-M-yyyy", "d.M.yyyy.", "d/M/yy", "yyyy-M-d" },
            _ => new[] { "yyyy-M-d", "yyyy/M/d", "yyyy.M.d" },
        };

        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? LocalDate.FromDateTime(date)
            : null;
    }

    public static LocalTime? ParseTime(string? text) =>
        text is not null && DateTime.TryParseExact(text.ToUpperInvariant(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? LocalTime.FromTicksSinceMidnight(time.TimeOfDay.Ticks)
            : null;

    /// <summary>A duration such as 1:30:00, 1:30 or 1.5 (hours).</summary>
    public static TimeSpan? ParseDuration(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var parts = text.Split(':');
        if (parts.Length is 2 or 3 && parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            var numbers = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            return new TimeSpan(numbers[0], numbers[1], parts.Length == 3 ? numbers[2] : 0);
        }

        // Whole seconds, so 1.1 hours is 3960 seconds and not a hair under.
        return decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var hours) && hours >= 0
            ? TimeSpan.FromSeconds((double)Math.Round(hours * 3600))
            : null;
    }

    /// <summary>
    /// Works out how the file writes dates. ISO dates are clear; with slashes a first part over 12 means day first
    /// and a second part over 12 means month first; dots mean day first. If every date reads both ways, month first
    /// is the guess, as in Clockify's and Toggl's US formats.
    /// </summary>
    private static (DateOrder Order, bool Guessed) DetectDateOrder(List<string[]> data, Columns columns)
    {
        var dates = data
            .SelectMany(cells => new[] { columns.StartDate, columns.EndDate }.Select(c => c is { } i && i < cells.Length ? cells[i].Trim() : null))
            // Only what looks like a date counts; a mistyped one is reported on its own row.
            .Where(date => date is not null && DateLike().IsMatch(date))
            .Select(date => date!)
            .ToList();

        if (dates.Count == 0 || dates.All(d => ParseDate(d, DateOrder.YearMonthDay) is not null))
        {
            return (DateOrder.YearMonthDay, false);
        }

        var dayFirst = false;
        var monthFirst = false;
        foreach (var date in dates)
        {
            var parts = date.Split('/', '.', '-');
            if (parts.Length < 3 || parts[0].Length == 4)
            {
                continue;
            }

            if (date.Contains('.') || (int.TryParse(parts[0], out var first) && first > 12))
            {
                dayFirst = true;
            }
            else if (int.TryParse(parts[1], out var second) && second > 12)
            {
                monthFirst = true;
            }
        }

        return dayFirst ? (DateOrder.DayMonthYear, false) : (DateOrder.MonthDayYear, !monthFirst);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{1,4}[./-]\d{1,2}[./-]\d{1,4}\.?$")]
    private static partial System.Text.RegularExpressions.Regex DateLike();

    private static ParsedFile Empty(string format, DateOrder order, string problem) =>
        new(format, order == DateOrder.Auto ? DateOrder.YearMonthDay : order, false, [], [new ImportProblem(1, problem)]);

    /// <summary>Where each field is, by column name, whichever app wrote the file.</summary>
    private sealed class Columns
    {
        public Columns(string[] header)
        {
            var names = header.Select((name, index) => (Name: name.Trim().ToLowerInvariant(), index)).ToList();
            int? Find(params string[] candidates) =>
                candidates.Select(c => names.FirstOrDefault(n => n.Name == c)).FirstOrDefault(n => n.Name is not null) is { Name: not null } match ? match.index : null;

            User = Find("user", "member");
            FirstName = Find("first name");
            LastName = Find("last name");
            Email = Find("email");
            Client = Find("client");
            Project = Find("project");
            Task = Find("task");
            Description = Find("description", "notes");
            Billable = Find("billable", "billable?");
            StartDate = Find("start date", "date");
            // Harvest's names for these are not documented; its export has them when an account tracks start and end times.
            StartTime = Find("start time", "start", "started at", "started");
            EndDate = Find("end date");
            EndTime = Find("end time", "end", "ended at", "ended");
            // Harvest's hours are decimal and unrounded; "hours rounded" is the rounded copy.
            Duration = Find("duration", "duration (h)", "hours");
            Tags = Find("tags", "tag");

            Format = names.Any(n => n.Name.StartsWith("duration (decimal)", StringComparison.Ordinal)) || names.Any(n => n.Name == "duration (h)")
                ? "Clockify"
                : names.Any(n => n.Name == "member") && names.Any(n => n.Name == "hours")
                    ? "Spindle"
                    : FirstName is not null && LastName is not null && names.Any(n => n.Name == "hours")
                        ? "Harvest"
                        : names.Any(n => n.Name == "start date") && names.Any(n => n.Name == "duration")
                            ? "Toggl Track"
                            : "CSV";
        }

        public string Format { get; }

        public int? User { get; }

        public int? FirstName { get; }

        public int? LastName { get; }

        public int? Email { get; }

        public int? Client { get; }

        public int? Project { get; }

        public int? Task { get; }

        public int? Description { get; }

        public int? Billable { get; }

        public int? StartDate { get; }

        public int? StartTime { get; }

        public int? EndDate { get; }

        public int? EndTime { get; }

        public int? Duration { get; }

        public int? Tags { get; }
    }
}
