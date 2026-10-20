using System.Globalization;
using System.Text.RegularExpressions;
using Spindle.Core;

namespace Spindle.Windows.Model;

/// <summary>How a person likes finished durations written, from the account's preferences.</summary>
public enum DurationFormat { Hms, Hm, Decimal }

/// <summary>Durations as Spindle shows and reads them, the same as the Mac and web apps.</summary>
public static partial class Durations
{
    /// <summary><c>h:mm:ss</c>: 0:05:09, 12:30:00, 134:00:00.</summary>
    public static string Clock(double totalSeconds)
    {
        var seconds = Math.Max(0, (long)Math.Floor(totalSeconds));
        return $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }

    /// <summary>A finished duration in the person's format.</summary>
    public static string Format(double totalSeconds, DurationFormat format)
    {
        var seconds = Math.Max(0, (long)Math.Floor(totalSeconds));
        return format switch
        {
            DurationFormat.Hm => $"{seconds / 3600}:{seconds / 60 % 60:00}",
            DurationFormat.Decimal => (seconds / 3600d).ToString("0.00", CultureInfo.InvariantCulture) + " h",
            _ => Clock(seconds),
        };
    }

    /// <summary><c>12h 05m</c>, <c>45m</c>, <c>0m</c>, for totals and estimates.</summary>
    public static string HoursMinutes(double totalSeconds)
    {
        var minutes = Math.Max(0, (long)Math.Floor(totalSeconds)) / 60;
        return minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes}m";
    }

    /// <summary><c>1:05</c>: hours and minutes, for the time of a split part.</summary>
    public static string ShortClock(double totalSeconds)
    {
        var minutes = Math.Max(0, (long)Math.Floor(totalSeconds)) / 60;
        return $"{minutes / 60}:{minutes % 60:00}";
    }

    /// <summary>
    /// Reads a duration a person typed, in seconds: <c>1:30</c>, <c>1:30:15</c>, <c>1h 30m</c>, <c>90m</c>, <c>45s</c>,
    /// <c>1.5</c> or <c>1,5</c> hours, or a whole number of minutes such as <c>90</c>. Null if it is not one.
    /// </summary>
    public static int? Parse(string text)
    {
        var value = text.Trim().ToLowerInvariant();
        if (value.Length == 0) return null;
        if (ClockPattern().Match(value) is { Success: true } clock)
        {
            var seconds = clock.Groups[3].Success ? int.Parse(clock.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            return int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture) * 60 + seconds;
        }
        if (UnitPattern().Match(value) is { Success: true } units && (units.Groups[1].Success || units.Groups[2].Success || units.Groups[3].Success))
        {
            var hours = Decimal(units.Groups[1].Success ? units.Groups[1].Value : null);
            var minutes = units.Groups[2].Success ? int.Parse(units.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            var seconds = units.Groups[3].Success ? int.Parse(units.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            return (int)Math.Round(hours * 3600, MidpointRounding.AwayFromZero) + minutes * 60 + seconds;
        }
        if (DecimalPattern().IsMatch(value)) return (int)Math.Round(Decimal(value) * 3600, MidpointRounding.AwayFromZero);
        if (WholePattern().IsMatch(value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var whole)) return whole * 60;
        return null;
    }

    internal static double Decimal(string? text) =>
        text is null ? 0 : double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    [GeneratedRegex(@"^(\d+):([0-5]?\d)(?::([0-5]?\d))?$")]
    private static partial Regex ClockPattern();
    [GeneratedRegex(@"^(?:(\d+(?:[.,]\d+)?)\s*h)?\s*(?:(\d+)\s*m(?:in)?)?\s*(?:(\d+)\s*s)?$")]
    private static partial Regex UnitPattern();
    [GeneratedRegex(@"^\d+[.,]\d+$")]
    private static partial Regex DecimalPattern();
    [GeneratedRegex(@"^\d+$")]
    private static partial Regex WholePattern();
}

/// <summary>A clock time: hours 0–23 and minutes.</summary>
public readonly record struct ClockTime(int Hours, int Minutes)
{
    public int TotalMinutes => Hours * 60 + Minutes;

    /// <summary>
    /// Reads a clock time. Digits alone need no separator: <c>9</c> is 9:00, <c>930</c> is 9:30 and <c>2130</c> is 21:30.
    /// With a separator (<c>:</c>, <c>.</c>, <c>,</c>, <c>h</c> or a space) minutes may be short: <c>9:5</c> is 9:05.
    /// <c>am</c>/<c>pm</c> (also <c>a</c>, <c>p</c>, <c>a.m.</c>) read the hours on a 12-hour clock.
    /// </summary>
    public static ClockTime? Parse(string text)
    {
        var match = Pattern.Match(text.Trim().ToLowerInvariant());
        if (!match.Success) return null;
        var digits = match.Groups[1].Value;
        int hours;
        int minutes;
        if (match.Groups[2].Success)
        {
            if (digits.Length > 2) return null;
            hours = int.Parse(digits, CultureInfo.InvariantCulture);
            minutes = match.Groups[2].Value.Length == 0 ? 0 : int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else if (digits.Length <= 2)
        {
            hours = int.Parse(digits, CultureInfo.InvariantCulture);
            minutes = 0;
        }
        else
        {
            hours = int.Parse(digits[..^2], CultureInfo.InvariantCulture);
            minutes = int.Parse(digits[^2..], CultureInfo.InvariantCulture);
        }
        if (minutes > 59) return null;
        if (match.Groups[3].Success)
        {
            if (hours is < 1 or > 12) return null;
            hours = hours % 12 + (match.Groups[3].Value == "p" ? 12 : 0);
        }
        else if (hours > 23)
        {
            return null;
        }
        return new ClockTime(hours, minutes);
    }

    /// <summary>For a field: <c>09:05</c>, or <c>9:05 am</c> with a 12-hour clock.</summary>
    public string Field(bool hour12)
    {
        if (!hour12) return $"{Hours:00}:{Minutes:00}";
        var hours = Hours % 12 == 0 ? 12 : Hours % 12;
        return $"{hours}:{Minutes:00} {(Hours < 12 ? "am" : "pm")}";
    }

    /// <summary>The clock time <paramref name="minutes"/> later, or earlier when negative, going round midnight.</summary>
    public ClockTime Adding(int minutes)
    {
        var total = ((TotalMinutes + minutes) % 1440 + 1440) % 1440;
        return new ClockTime(total / 60, total % 60);
    }

    private static readonly Regex Pattern = new(@"^(\d{1,4})(?:\s*[:.,h\s]\s*(\d{0,2}))?\s*(?:([ap])\.?(?:m\.?)?)?$", RegexOptions.Compiled);
}

/// <summary>
/// Time in the person's own time zone and preferences, not the PC's: a day, a week and a clock time mean the same on
/// every device they sign in from, as in the Mac and web apps.
/// </summary>
public sealed class UserTime
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    public UserTime(TimeZoneInfo zone, int weekStart = 1, bool hour12 = false, DurationFormat durationFormat = DurationFormat.Hms)
    {
        Zone = zone;
        WeekStart = weekStart;
        Hour12 = hour12;
        DurationFormat = durationFormat;
    }

    public static UserTime For(MeResponse me)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(me.TimeZone);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            zone = TimeZoneInfo.Local;
        }
        var format = me.Preferences.DurationFormat switch
        {
            "hm" => DurationFormat.Hm,
            "decimal" => DurationFormat.Decimal,
            _ => DurationFormat.Hms,
        };
        return new UserTime(zone, me.Preferences.WeekStart, me.Preferences.TimeFormat == "12h", format);
    }

    public TimeZoneInfo Zone { get; }
    /// <summary>0 for Sunday, 1 for Monday, and so on.</summary>
    public int WeekStart { get; }
    public bool Hour12 { get; }
    public DurationFormat DurationFormat { get; }

    // Days

    public DateTime Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone).DateTime;
    public DateOnly Day(DateTimeOffset instant) => DateOnly.FromDateTime(Local(instant));
    public DateOnly Today(DateTimeOffset? now = null) => Day(now ?? DateTimeOffset.UtcNow);

    /// <summary>The instant for a day and a clock time. A time daylight saving skips moves forward.</summary>
    public DateTimeOffset At(DateOnly day, int hours, int minutes)
    {
        var local = day.ToDateTime(new TimeOnly(0, 0)).AddHours(hours).AddMinutes(minutes);
        if (Zone.IsInvalidTime(local)) local = local.AddHours(1);
        var offset = Zone.IsAmbiguousTime(local) ? Zone.GetAmbiguousTimeOffsets(local).Max() : Zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    public DateTimeOffset At(DateOnly day, ClockTime clock) => At(day, clock.Hours, clock.Minutes);
    public DateTimeOffset StartOf(DateOnly day) => At(day, 0, 0);

    /// <summary>Minutes since the day's midnight, counted on the wall clock.</summary>
    public double MinuteOfDay(DateTimeOffset instant) => Local(instant).TimeOfDay.TotalMinutes;

    public ClockTime ClockOf(DateTimeOffset instant)
    {
        var local = Local(instant);
        return new ClockTime(local.Hour, local.Minute);
    }

    // Weeks

    public DateOnly WeekStartDay(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek - WeekStart + 7) % 7));
    public DateOnly[] Week(DateOnly containing)
    {
        var first = WeekStartDay(containing);
        return Enumerable.Range(0, 7).Select(first.AddDays).ToArray();
    }

    // Writing

    /// <summary><c>09:05</c>, or <c>9:05 am</c> with a 12-hour clock.</summary>
    public string Clock(DateTimeOffset instant) => Local(instant).ToString(Hour12 ? "h:mm tt" : "HH:mm", English).Replace("AM", "am").Replace("PM", "pm");
    public string ClockField(DateTimeOffset instant) => ClockOf(instant).Field(Hour12);
    public string Span(DateTimeOffset start, DateTimeOffset? stop) => $"{Clock(start)} – {(stop is { } end ? Clock(end) : "now")}";

    /// <summary>A finished duration in the person's format; a running timer always counts in seconds.</summary>
    public string Duration(double seconds, bool running = false) => running ? Durations.Clock(seconds) : Durations.Format(seconds, DurationFormat);

    /// <summary><c>Today</c>, <c>Yesterday</c>, <c>Mon, 22 Sep</c>, or with the year when it isn't this year.</summary>
    public string DayHeading(DateOnly day, DateTimeOffset? now = null)
    {
        var today = Today(now);
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.ToString(day.Year == today.Year ? "ddd, d MMM" : "ddd, d MMM yyyy", English);
    }

    /// <summary><c>1 September 2026</c>.</summary>
    public string LongDay(DateOnly day) => day.ToString("d MMMM yyyy", English);
    public string ShortDay(DateOnly day) => day.ToString("ddd, d MMM yyyy", English);
    public static string Weekday(DateOnly day) => day.ToString("ddd", English);
    public static string Format(DateOnly day, string pattern) => day.ToString(pattern, English);

    // Reading

    /// <summary>
    /// What was typed in a clock field: a clock time such as <c>930</c> or <c>9:30</c>, <c>now</c>, or how long ago
    /// after a minus: <c>-15</c> is 15 minutes ago, <c>-1:30</c> an hour and a half ago.
    /// </summary>
    public ClockTime? ReadClockField(string text, DateTimeOffset? now = null)
    {
        var value = text.Trim().ToLowerInvariant();
        var at = now ?? DateTimeOffset.UtcNow;
        if (value == "now") return ClockOf(at);
        if (value.StartsWith('-')) return Durations.Parse(value[1..]) is { } seconds ? ClockOf(at.AddSeconds(-seconds)) : null;
        return ClockTime.Parse(value);
    }

    /// <summary>When something started: <c>-15</c> is 15 minutes before now; a clock time is the latest such instant not after now.</summary>
    public DateTimeOffset? ReadStartField(string text, DateTimeOffset? now = null)
    {
        var value = text.Trim();
        var at = now ?? DateTimeOffset.UtcNow;
        if (value.StartsWith('-'))
        {
            if (Durations.Parse(value[1..]) is not { } seconds) return null;
            var instant = at.AddSeconds(-seconds);
            return new DateTimeOffset(instant.Ticks - instant.Ticks % TimeSpan.TicksPerMinute, instant.Offset);
        }
        return ReadClockField(value, at) is { } clock ? Latest(clock, at) : null;
    }

    /// <summary>The latest instant at a clock time that isn't after now: today's, or yesterday's if today's is to come.</summary>
    public DateTimeOffset Latest(ClockTime clock, DateTimeOffset now)
    {
        var today = Day(now);
        var start = At(today, clock);
        return start > now ? At(today.AddDays(-1), clock) : start;
    }

    /// <summary>A day for a field, as the PC's region writes dates.</summary>
    public static string DayField(DateOnly day) => day.ToString("d", CultureInfo.CurrentCulture);

    /// <summary>A typed day: the region's order, or year first; day and month in <paramref name="near"/>'s year; or a day alone.</summary>
    public static DateOnly? ParseDayField(string text, DateOnly near, CultureInfo? culture = null)
    {
        var numbers = Regex.Matches(text, @"\d+").Select(m => (Value: int.Parse(m.Value, CultureInfo.InvariantCulture), Digits: m.Value.Length)).ToArray();
        var pattern = (culture ?? CultureInfo.CurrentCulture).DateTimeFormat.ShortDatePattern;
        var order = new[] { ('d', pattern.IndexOf('d')), ('M', pattern.IndexOf('M')), ('y', pattern.IndexOf('y')) }
            .Where(p => p.Item2 >= 0).OrderBy(p => p.Item2).Select(p => p.Item1).ToArray();
        if (order.Length != 3) order = ['d', 'M', 'y'];
        if (numbers.FirstOrDefault().Digits == 4) order = ['y', 'M', 'd'];
        var (year, month, dayOfMonth) = (near.Year, near.Month, near.Day);
        switch (numbers.Length)
        {
            case 1:
                dayOfMonth = numbers[0].Value;
                break;
            case 2:
            case 3:
                var fields = numbers.Length == 3 ? order : order.Where(f => f != 'y').ToArray();
                foreach (var (field, number) in fields.Zip(numbers))
                {
                    if (field == 'y') year = number.Digits <= 2 ? 2000 + number.Value : number.Value;
                    else if (field == 'M') month = number.Value;
                    else dayOfMonth = number.Value;
                }
                break;
            default:
                return null;
        }
        if (month is < 1 or > 12 || year is < 1 or > 9999 || dayOfMonth < 1 || dayOfMonth > DateTime.DaysInMonth(year, month)) return null;
        return new DateOnly(year, month, dayOfMonth);
    }
}
