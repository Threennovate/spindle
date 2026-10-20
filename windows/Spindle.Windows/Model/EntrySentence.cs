using System.Globalization;
using System.Text.RegularExpressions;

namespace Spindle.Windows.Model;

/// <summary>
/// How one line in quick entry is read, the same as the Mac's quick entry and the web's command palette (they share
/// <c>tests/vectors/entry-sentence.json</c>): a description, then an explicit time at the end, such as
/// <c>since 9:40</c>, <c>9:00-10:30</c>, <c>yesterday for 2h</c> or <c>25m ago</c>. Projects, tasks, clients and tags
/// come only from chips the person picked, never from the text, so "Fix #482" stays as typed.
/// </summary>
public static partial class EntrySentence
{
    public abstract record Chip
    {
        public sealed record Project(string ProjectId, string? TaskId) : Chip;
        public sealed record Client(string ClientId) : Chip;
        public sealed record Tag(string TagId) : Chip;
        public sealed record Billable : Chip;
    }

    public sealed record Options(bool Hour12 = false, string DateFormat = "dd.MM.yyyy");

    public abstract record DayRef
    {
        public sealed record Today : DayRef;
        public sealed record Yesterday : DayRef;
        /// <summary>0 for Sunday.</summary>
        public sealed record Weekday(int Day) : DayRef;
        public sealed record Date(int? Year, int Month, int Day) : DayRef;
    }

    /// <summary>A clock time as typed; <see cref="Meridiem"/> when it said am or pm.</summary>
    public sealed record TypedClock(int Hours, int Minutes, bool Meridiem)
    {
        public int TotalMinutes => Hours * 60 + Minutes;
    }

    public abstract record Time
    {
        /// <summary>Running, started this long ago.</summary>
        public sealed record Ago(int Seconds) : Time;
        /// <summary>Running since a clock time.</summary>
        public sealed record Since(DayRef? Day, TypedClock At) : Time;
        /// <summary>Finished, from one clock time to another.</summary>
        public sealed record Range(DayRef? Day, TypedClock From, TypedClock To) : Time;
        /// <summary>Finished, this long.</summary>
        public sealed record Duration(DayRef? Day, int Seconds) : Time;

        public Time With(DayRef day) => this switch
        {
            Since since => since with { Day = day },
            Range range => range with { Day = day },
            Duration duration => duration with { Day = day },
            _ => this,
        };
    }

    public sealed record Reading(string Description, string? ProjectId, string? TaskId, string? ClientId, string[] TagIds, bool Billable, Time? Time, string TimeText);

    public static Reading Read(string text, IReadOnlyList<Chip>? chips = null, Options? options = null)
    {
        options ??= new Options();
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var found = ReadTime(tokens, options);
        var rest = found is { } time ? tokens[..time.Index] : tokens;
        (string, string?)? project = null;
        string? client = null;
        var tags = new List<string>();
        var billable = rest.Contains("$");
        foreach (var chip in chips ?? [])
        {
            switch (chip)
            {
                case Chip.Project p: project = (p.ProjectId, p.TaskId); break;
                case Chip.Client c: client = c.ClientId; break;
                case Chip.Tag t when !tags.Contains(t.TagId): tags.Add(t.TagId); break;
                case Chip.Billable: billable = true; break;
            }
        }
        return new Reading(
            string.Join(" ", rest.Where(t => t != "$")),
            project?.Item1,
            project?.Item2,
            client,
            tags.ToArray(),
            billable,
            found?.Time,
            found is { } at ? string.Join(" ", tokens[at.Index..]) : "");
    }

    // The time at the end

    private static readonly Dictionary<string, int> Weekdays = new()
    {
        ["sun"] = 0, ["sunday"] = 0, ["mon"] = 1, ["monday"] = 1, ["tue"] = 2, ["tues"] = 2, ["tuesday"] = 2, ["wed"] = 3, ["wednesday"] = 3,
        ["thu"] = 4, ["thur"] = 4, ["thurs"] = 4, ["thursday"] = 4, ["fri"] = 5, ["friday"] = 5, ["sat"] = 6, ["saturday"] = 6,
    };

    private static (int Index, Time Time)? ReadTime(string[] tokens, Options options)
    {
        var lower = tokens.Select(t => t.ToLowerInvariant()).ToArray();
        var n = lower.Length;
        if (n == 0) return null;
        var last = lower[^1];
        // Times without a day: "25m ago", "-25".
        if (last == "ago")
        {
            foreach (var width in new[] { 2, 1 })
            {
                if (n - 1 - width < 0) continue;
                if (ReadDuration(lower[(n - 1 - width)..(n - 1)], needsUnit: false) is { } seconds) return (n - 1 - width, new Time.Ago(seconds));
            }
            return null;
        }
        if (Minus().IsMatch(last)) return ReadDuration([last[1..]], needsUnit: false) is { } ago ? (n - 1, new Time.Ago(ago)) : null;
        // Times that may have a day before them.
        if (ReadDayTime(lower, token => ReadDay(token, options) is not null) is not { } timed) return null;
        if (timed.Index > 0 && ReadDay(lower[timed.Index - 1], options) is { } day) return (timed.Index - 1, timed.Time.With(day));
        return timed;
    }

    private static (int Index, Time Time)? ReadDayTime(string[] lower, Func<string, bool> isDay)
    {
        var n = lower.Length;
        var last = lower[^1];
        // A day or "from" before a range says it is a time, so "yesterday 9-10" is one while "Sprint 1-2" stays text.
        bool SaysTime(int index) => index > 0 && (lower[index - 1] == "from" || isDay(lower[index - 1]));
        (int Index, TypedClock From, TypedClock To)? range = null;
        if (Dash().Match(last) is { Success: true } dash)
        {
            if (ReadRange(dash.Groups[1].Value, dash.Groups[2].Value, SaysTime(n - 1)) is { } found) range = (n - 1, found.From, found.To);
        }
        else if (n >= 3 && lower[n - 2] is "-" or "–")
        {
            if (ReadRange(lower[n - 3], last, SaysTime(n - 3)) is { } found) range = (n - 3, found.From, found.To);
        }
        else if (n >= 4 && lower[n - 2] == "to" && lower[n - 4] == "from")
        {
            if (ReadRange(lower[n - 3], last, saysTime: true) is { } found) range = (n - 4, found.From, found.To);
        }
        if (range is { } r)
        {
            var index = lower[r.Index] != "from" && r.Index > 0 && lower[r.Index - 1] == "from" ? r.Index - 1 : r.Index;
            return (index, new Time.Range(null, r.From, r.To));
        }
        // "since 9:40", "from 9:40"
        if (n >= 2 && lower[n - 2] is "since" or "from" && ReadClock(last) is { } at) return (n - 2, new Time.Since(null, at));
        // "for 1h30", "for 1h 30m", "for 90"
        foreach (var width in new[] { 2, 1 })
        {
            if (n - 1 - width < 0 || lower[n - 1 - width] != "for") continue;
            if (ReadDuration(lower[(n - width)..], needsUnit: false) is { } seconds) return (n - 1 - width, new Time.Duration(null, seconds));
        }
        // "45m", "1h30", "1h 30m": only with a unit, as a bare number is too often part of the description.
        foreach (var width in new[] { 2, 1 })
        {
            if (n - width < 0) continue;
            if (ReadDuration(lower[(n - width)..], needsUnit: true) is { } seconds) return (n - width, new Time.Duration(null, seconds));
        }
        return null;
    }

    private static TypedClock? ReadClock(string token) =>
        ClockTime.Parse(token) is { } clock ? new TypedClock(clock.Hours, clock.Minutes, MeridiemEnd().IsMatch(token)) : null;

    private static (TypedClock From, TypedClock To)? ReadRange(string fromText, string toText, bool saysTime)
    {
        static bool LooksLikeClock(string text) => ClockMark().IsMatch(text) || MeridiemEnd().IsMatch(text);
        if (!(saysTime || LooksLikeClock(fromText) || LooksLikeClock(toText)) || ReadClock(fromText) is not { } first || ReadClock(toText) is not { } last) return null;
        // "9-10:30pm" is 21:00 to 22:30, and "11-1pm" 11:00 to 13:00.
        if (last.Meridiem && !first.Meridiem && first.Hours <= 12)
        {
            var sameHalf = new TypedClock(first.Hours % 12 + (last.Hours >= 12 ? 12 : 0), first.Minutes, true);
            first = sameHalf.TotalMinutes < last.TotalMinutes ? sameHalf : first with { Meridiem = true };
        }
        else if (first.Meridiem && !last.Meridiem && last.Hours <= 12)
        {
            var sameHalf = new TypedClock(last.Hours % 12 + (first.Hours >= 12 ? 12 : 0), last.Minutes, true);
            last = sameHalf.TotalMinutes > first.TotalMinutes ? sameHalf : last with { Meridiem = true };
        }
        return first.TotalMinutes == last.TotalMinutes ? null : (first, last);
    }

    private static readonly Dictionary<string, string> Units = new()
    {
        ["h"] = "h", ["hr"] = "h", ["hrs"] = "h", ["hour"] = "h", ["hours"] = "h", ["m"] = "m", ["min"] = "m", ["mins"] = "m", ["minute"] = "m", ["minutes"] = "m",
    };

    private static int? ReadDuration(string[] tokens, bool needsUnit)
    {
        string text;
        if (tokens.Length == 2)
        {
            var (first, second) = (tokens[0], tokens[1]);
            if (Units.TryGetValue(second, out var unit) && Number().IsMatch(first)) text = first + unit;
            else if (HoursToken().IsMatch(first) && MinutesToken().IsMatch(second)) text = first + second;
            else return null;
        }
        else
        {
            text = tokens[0];
            if (NumberWord().Match(text) is { Success: true } match && Units.TryGetValue(match.Groups[2].Value, out var unit)) text = match.Groups[1].Value + unit;
        }
        int? seconds;
        if (Hours().Match(text) is { Success: true } hours)
        {
            var whole = Durations.Decimal(hours.Groups[1].Value);
            seconds = (int)Math.Round(whole * 3600, MidpointRounding.AwayFromZero) + (hours.Groups[2].Success ? int.Parse(hours.Groups[2].Value, CultureInfo.InvariantCulture) : 0) * 60;
        }
        else if (Minutes().Match(text) is { Success: true } minutes)
        {
            seconds = int.Parse(minutes.Groups[1].Value, CultureInfo.InvariantCulture) * 60;
        }
        else
        {
            seconds = needsUnit ? null : Durations.Parse(text);
        }
        return seconds is > 0 ? seconds : null;
    }

    /// <summary>A day as typed: today, yesterday, a weekday, 2026-10-03, 3.10. (day first) or 10/3 (as the date format orders it).</summary>
    public static DayRef? ReadDay(string token, Options? options = null)
    {
        options ??= new Options();
        if (token == "today") return new DayRef.Today();
        if (token == "yesterday") return new DayRef.Yesterday();
        if (Weekdays.TryGetValue(token, out var weekday)) return new DayRef.Weekday(weekday);
        int? year = null;
        int month;
        int day;
        if (Iso().Match(token) is { Success: true } iso)
        {
            (year, month, day) = (Int(iso.Groups[1].Value), Int(iso.Groups[2].Value), Int(iso.Groups[3].Value));
        }
        else if (Dotted().Match(token) is { Success: true } dotted)
        {
            // Dots are always day first: 3.10. is 3 October.
            (day, month) = (Int(dotted.Groups[1].Value), Int(dotted.Groups[2].Value));
            year = dotted.Groups[3].Success ? FullYear(dotted.Groups[3].Value) : null;
        }
        else if (Slashed().Match(token) is { Success: true } slashed)
        {
            var monthFirst = options.DateFormat.StartsWith('M');
            (month, day) = monthFirst ? (Int(slashed.Groups[1].Value), Int(slashed.Groups[2].Value)) : (Int(slashed.Groups[2].Value), Int(slashed.Groups[1].Value));
            year = slashed.Groups[3].Success ? FullYear(slashed.Groups[3].Value) : null;
        }
        else
        {
            return null;
        }
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year ?? 2000, month)) return null;
        return new DayRef.Date(year, month, day);
    }

    private static int Int(string text) => int.Parse(text, CultureInfo.InvariantCulture);
    private static int FullYear(string text) => text.Length == 2 ? 2000 + Int(text) : Int(text);

    // Resolving to instants

    public sealed record Span(DateTimeOffset Start, DateTimeOffset? Stop);

    private const int DefaultStartHour = 9;

    public static DateOnly Resolve(DayRef day, DateOnly today) => day switch
    {
        DayRef.Today => today,
        DayRef.Yesterday => today.AddDays(-1),
        DayRef.Weekday weekday => today.AddDays(-(((int)today.DayOfWeek - weekday.Day + 7) % 7)),
        DayRef.Date { Year: { } year } date => new DateOnly(year, date.Month, date.Day),
        DayRef.Date date => new DateOnly(today.Year, date.Month, date.Day) is var thisYear && thisYear <= today ? thisYear : new DateOnly(today.Year - 1, date.Month, date.Day),
        _ => today,
    };

    /// <summary>The instants a reading means, now; <paramref name="dayEnds"/> gives when a day's entries ended, to add time after them.</summary>
    public static Span Resolve(Time? time, DateTimeOffset now, UserTime userTime, Func<DateOnly, IEnumerable<DateTimeOffset>>? dayEnds = null)
    {
        var today = userTime.Day(now);
        var thisMinute = Floor(now);
        switch (time)
        {
            case null:
                return new Span(now, null);
            case Time.Ago ago:
                return new Span(Floor(now.AddSeconds(-ago.Seconds)), null);
            case Time.Since since:
                return new Span(since.Day is { } sinceDay ? AtClock(Resolve(sinceDay, today), since.At, now, userTime) : LatestStart(since.At, now, userTime), null);
            case Time.Range range:
                var start = range.Day is { } rangeDay ? AtClock(Resolve(rangeDay, today), range.From, now, userTime) : LatestStart(range.From, now, userTime);
                var startDay = userTime.Day(start);
                var stop = userTime.At(startDay, range.To.Hours, range.To.Minutes);
                if (stop <= start)
                {
                    // "9-5" on a 12-hour clock ends at 17:00; otherwise the range goes past midnight.
                    DateTimeOffset? afternoon = userTime.Hour12 && !range.To.Meridiem && range.To.Hours < 12 ? userTime.At(startDay, range.To.Hours + 12, range.To.Minutes) : null;
                    stop = afternoon is { } later && later > start ? later : userTime.At(startDay.AddDays(1), range.To.Hours, range.To.Minutes);
                }
                return new Span(start, stop);
            case Time.Duration duration:
                var length = TimeSpan.FromSeconds(duration.Seconds);
                if (duration.Day is not { } durationDay || Resolve(durationDay, today) == today) return new Span(thisMinute - length, thisMinute);
                // Another day: after that day's last entry, or at 9:00, as the timesheet adds time.
                var day = Resolve(durationDay, today);
                var morning = userTime.At(day, DefaultStartHour, 0);
                var dayEnd = userTime.StartOf(day.AddDays(1));
                var latest = new[] { morning }.Concat(dayEnds?.Invoke(day) ?? []).Max();
                var begin = latest < dayEnd - length ? latest : dayEnd - length;
                return new Span(begin, begin + length);
            default:
                return new Span(now, null);
        }
    }

    private static DateTimeOffset Floor(DateTimeOffset value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMinute, value.Offset);

    private static DateTimeOffset AtClock(DateOnly day, TypedClock at, DateTimeOffset now, UserTime time)
    {
        var morning = time.At(day, at.Hours, at.Minutes);
        if (!time.Hour12 || at.Meridiem || at.Hours >= 12) return morning;
        var afternoon = time.At(day, at.Hours + 12, at.Minutes);
        return afternoon <= now ? afternoon : morning;
    }

    private static DateTimeOffset LatestStart(TypedClock at, DateTimeOffset now, UserTime time)
    {
        var latest = time.Latest(new ClockTime(at.Hours, at.Minutes), now);
        if (!time.Hour12 || at.Meridiem || at.Hours >= 12) return latest;
        var later = time.Latest(new ClockTime(at.Hours + 12, at.Minutes), now);
        return later > latest ? later : latest;
    }

    // What is typed at the caret

    /// <summary>A word starting with @ or # around the caret: a project or tag being picked.</summary>
    public sealed record Mention(char Sign, string Query, int Start, int End);

    public static Mention? MentionAt(string text, int caret)
    {
        var start = Math.Min(caret, text.Length);
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var end = Math.Min(caret, text.Length);
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        if (start >= text.Length || text[start] is not ('@' or '#')) return null;
        return new Mention(text[start], text[(start + 1)..end], start, end);
    }

    /// <summary>The text without a mention, once it became a chip, and where the caret goes.</summary>
    public static (string Text, int Caret) Removing(Mention mention, string text)
    {
        var before = text[..mention.Start].TrimEnd();
        var after = text[mention.End..].TrimStart();
        var joined = before.Length > 0 && after.Length > 0 ? $"{before} {after}" : before.Length == 0 ? after : before;
        var result = before.Length > 0 && after.Length == 0 ? joined + " " : joined;
        return (result, before.Length == 0 ? 0 : before.Length + 1);
    }

    [GeneratedRegex(@"^[-−]\d.*$")]
    private static partial Regex Minus();
    [GeneratedRegex(@"^([^-–]+)[-–]([^-–]+)$")]
    private static partial Regex Dash();
    [GeneratedRegex(@"[ap]\.?m?\.?$")]
    private static partial Regex MeridiemEnd();
    [GeneratedRegex(@"[:.h]")]
    private static partial Regex ClockMark();
    [GeneratedRegex(@"^\d+(?:[.,]\d+)?$")]
    private static partial Regex Number();
    [GeneratedRegex(@"^\d+(?:[.,]\d+)?h$")]
    private static partial Regex HoursToken();
    [GeneratedRegex(@"^\d{1,2}(?:m|min)?$")]
    private static partial Regex MinutesToken();
    [GeneratedRegex(@"^(\d+(?:[.,]\d+)?)([a-z]+)$")]
    private static partial Regex NumberWord();
    [GeneratedRegex(@"^(\d+(?:[.,]\d+)?)h(?:(\d{1,2})(?:m|min)?)?$")]
    private static partial Regex Hours();
    [GeneratedRegex(@"^(\d+)(?:m|min)$")]
    private static partial Regex Minutes();
    [GeneratedRegex(@"^(\d{4})-(\d{1,2})-(\d{1,2})$")]
    private static partial Regex Iso();
    [GeneratedRegex(@"^(\d{1,2})\.(\d{1,2})\.(\d{2}|\d{4})?$")]
    private static partial Regex Dotted();
    [GeneratedRegex(@"^(\d{1,2})/(\d{1,2})(?:/(\d{2}|\d{4}))?$")]
    private static partial Regex Slashed();
}
