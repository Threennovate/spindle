namespace Spindle.Windows.Model;

/// <summary>The Pomodoro's arithmetic, the same as the Mac's.</summary>
public static class Pomodoro
{
    /// <summary>Whole focus intervals done after <paramref name="elapsedSeconds"/>; never below zero.</summary>
    public static int Intervals(double elapsedSeconds, int focusMinutes) =>
        focusMinutes <= 0 ? 0 : Math.Max(0, (int)Math.Floor(elapsedSeconds / (focusMinutes * 60d)));

    /// <summary>Seconds of focus left in the current interval.</summary>
    public static double FocusLeft(double elapsedSeconds, int focusMinutes)
    {
        var focus = Math.Max(1, focusMinutes) * 60d;
        var into = (elapsedSeconds % focus + focus) % focus;
        return focus - into;
    }

    /// <summary><c>m:ss</c>, rounding the seconds up and never below zero: 12:05, 0:01, 0:00.</summary>
    public static string Countdown(double seconds)
    {
        var whole = Math.Max(0, (long)Math.Ceiling(seconds));
        return $"{whole / 60}:{whole % 60:00}";
    }
}

/// <summary>When to remind a person to track, as on the Mac.</summary>
public sealed record ReminderSchedule(int[] Days, int FromMinute, int ToMinute, int EveryMinutes)
{
    /// <summary>Whether an instant falls in working time: a working day, from ≤ minute &lt; to, in the person's zone.</summary>
    public bool IsWorkingTime(DateTimeOffset now, UserTime time)
    {
        var local = time.Local(now);
        var minute = local.Hour * 60 + local.Minute;
        return Days.Contains((int)local.DayOfWeek) && minute >= FromMinute && minute < ToMinute;
    }

    /// <summary>How long after working hours end the day's review may still be offered.</summary>
    public static readonly TimeSpan DayEndWindow = TimeSpan.FromHours(2);

    /// <summary>
    /// The working day that just ended, when this moment is within <see cref="DayEndWindow"/> after its working hours:
    /// the moment to offer the day's review once.
    /// </summary>
    public DateOnly? EndedWorkingDay(DateTimeOffset now, UserTime time)
    {
        var local = time.Local(now);
        var minute = local.Hour * 60 + local.Minute;
        if (!Days.Contains((int)local.DayOfWeek) || minute < ToMinute || minute - ToMinute >= DayEndWindow.TotalMinutes) return null;
        return DateOnly.FromDateTime(local);
    }

    /// <summary>
    /// A reminder is due when nothing runs, it is working time, the person is at the PC (idle under five minutes),
    /// and the last reminder or stop was at least <see cref="EveryMinutes"/> ago.
    /// </summary>
    public bool ShouldRemind(DateTimeOffset now, UserTime time, bool running, double idleSeconds, DateTimeOffset? lastReminder, DateTimeOffset? lastStop)
    {
        if (running || !IsWorkingTime(now, time) || idleSeconds >= 300) return false;
        var last = new[] { lastReminder, lastStop }.Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty().Max();
        return last == default || (now - last).TotalMinutes >= EveryMinutes;
    }
}
