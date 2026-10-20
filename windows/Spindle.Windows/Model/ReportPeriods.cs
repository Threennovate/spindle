using System.Globalization;

namespace Spindle.Windows.Model;

public enum ReportKind { Summary, Detailed, Weekly }

public enum ReportPeriod { Today, Yesterday, ThisWeek, LastWeek, ThisMonth, LastMonth, ThisYear, LastYear, Custom }

/// <summary>A report's days, first and last included, as on the Mac and the web.</summary>
public readonly record struct DayRange(DateOnly From, DateOnly To)
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    public static string Label(ReportPeriod period) => period switch
    {
        ReportPeriod.Today => "Today",
        ReportPeriod.Yesterday => "Yesterday",
        ReportPeriod.ThisWeek => "This week",
        ReportPeriod.LastWeek => "Last week",
        ReportPeriod.ThisMonth => "This month",
        ReportPeriod.LastMonth => "Last month",
        ReportPeriod.ThisYear => "This year",
        ReportPeriod.LastYear => "Last year",
        _ => "Custom",
    };

    public static DayRange Resolve(ReportPeriod period, DayRange? custom, DateOnly today, int weekStart) => period switch
    {
        ReportPeriod.Today => new(today, today),
        ReportPeriod.Yesterday => new(today.AddDays(-1), today.AddDays(-1)),
        ReportPeriod.ThisWeek => Week(today, weekStart),
        ReportPeriod.LastWeek => Week(today.AddDays(-7), weekStart),
        ReportPeriod.ThisMonth => Month(today),
        ReportPeriod.LastMonth => Month(today.AddMonths(-1)),
        ReportPeriod.ThisYear => new(new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
        ReportPeriod.LastYear => new(new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
        _ => custom ?? Week(today, weekStart),
    };

    public static DayRange Week(DateOnly day, int weekStart)
    {
        var from = day.AddDays(-(((int)day.DayOfWeek - weekStart + 7) % 7));
        return new(from, from.AddDays(6));
    }

    public static DayRange Month(DateOnly day)
    {
        var from = new DateOnly(day.Year, day.Month, 1);
        return new(from, from.AddMonths(1).AddDays(-1));
    }

    /// <summary>The period before (-1) or after (1): whole months move by months, anything else by its own length.</summary>
    public DayRange Shifted(int direction)
    {
        var next = To.AddDays(1);
        if (From.Day == 1 && next.Day == 1)
        {
            var months = (next.Year - From.Year) * 12 + next.Month - From.Month;
            var start = From.AddMonths(months * direction);
            return new(start, start.AddMonths(months).AddDays(-1));
        }
        var length = To.DayNumber - From.DayNumber + 1;
        return new(From.AddDays(length * direction), To.AddDays(length * direction));
    }

    /// <summary>"Wed, 30 Sep 2026", "14 – 20 Sep 2026", "28 Sep – 4 Oct 2026", "28 Dec 2026 – 3 Jan 2027".</summary>
    public string Text()
    {
        if (From == To) return From.ToString("ddd, d MMM yyyy", English);
        if (From.Year == To.Year && From.Month == To.Month) return $"{From.Day} – {To.ToString("d MMM yyyy", English)}";
        if (From.Year == To.Year) return $"{From.ToString("d MMM", English)} – {To.ToString("d MMM yyyy", English)}";
        return $"{From.ToString("d MMM yyyy", English)} – {To.ToString("d MMM yyyy", English)}";
    }
}

/// <summary>How reports write numbers, the same as the Mac's.</summary>
public static class ReportFormat
{
    /// <summary>Hours on a chart's axis: steps that give about four lines up to the highest bar.</summary>
    public static double[] HourTicks(double maxHours)
    {
        double[] steps = [0.25, 0.5, 1, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 30, 40, 50, 60, 80, 100, 150, 200, 250, 300, 400, 500, 750, 1000, 1500, 2000, 2500, 5000];
        var step = steps.FirstOrDefault(s => s * 4 >= maxHours);
        if (step == 0) step = Math.Ceiling(maxHours / 4 / 1000) * 1000;
        var count = Math.Max(1, (int)Math.Ceiling(maxHours / step));
        return Enumerable.Range(0, count + 1).Select(i => i * step).ToArray();
    }

    /// <summary>"0", "30m", "1h 30m", "8h".</summary>
    public static string HourTick(double hours)
    {
        if (hours == 0) return "0";
        var whole = (int)Math.Floor(hours);
        var minutes = (int)Math.Round((hours - whole) * 60);
        if (whole == 0) return $"{minutes}m";
        return minutes > 0 ? $"{whole}h {minutes}m" : $"{whole}h";
    }

    /// <summary>"33%", "3.3%", "0%".</summary>
    public static string Percent(double part, double whole)
    {
        if (whole <= 0) return "0%";
        var value = part / whole * 100;
        return value >= 10 || value == 0 ? $"{Math.Round(value, MidpointRounding.AwayFromZero):0}%" : value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>An amount in the workspace's currency with its symbol, e.g. €1,234.50; a dash without one.</summary>
    public static string Money(decimal? amount, string currency)
    {
        if (amount is not { } value) return "—";
        var format = (NumberFormatInfo)CultureInfo.GetCultureInfo("en-GB").NumberFormat.Clone();
        format.CurrencySymbol = Symbol(currency);
        return value.ToString("C2", format);
    }

    private static string Symbol(string currency) => currency.ToUpperInvariant() switch
    {
        "EUR" => "€",
        "USD" => "$",
        "GBP" => "£",
        "JPY" => "¥",
        "INR" => "₹",
        "" => "",
        var code => code + " ",
    };

    /// <summary>A group keeps its project's colour; the rest get one from the palette by position, and "none" none.</summary>
    public static string? Color(string key, string? color, int index) => color ?? (key == "none" ? null : Catalog.Colors[index % Catalog.Colors.Length]);
}
