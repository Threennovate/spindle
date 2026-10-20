using System.Globalization;
using Spindle.Core;

namespace Spindle.Windows.Model;

/// <summary>The part of an entry that falls on one day, in minutes of that day's wall clock.</summary>
public sealed record DaySegment(TimeEntryResponse Entry, DateOnly Day, double StartMinute, double EndMinute, bool ContinuesBefore = false, bool ContinuesAfter = false)
{
    /// <summary>Where it sits among overlapping segments: lane <see cref="Lane"/> of <see cref="Lanes"/>.</summary>
    public int Lane { get; init; }
    public int Lanes { get; init; } = 1;
}

/// <summary>Where entries go on a day or week calendar, the same as the Mac's and the web's.</summary>
public static class CalendarLayout
{
    public const double MinutesPerDay = 24 * 60;
    public const double Step = 15;

    /// <summary>The instant at a minute of a day; minute 1440 is the midnight after it.</summary>
    public static DateTimeOffset Instant(DateOnly day, double minute, UserTime time)
    {
        if (minute >= MinutesPerDay) return time.StartOf(day.AddDays(1));
        var whole = (int)Math.Floor(minute);
        return time.At(day, whole / 60, whole % 60);
    }

    public static double Snap(double minutes, double step = Step) => Math.Min(MinutesPerDay, Math.Max(0, Math.Round(minutes / step, MidpointRounding.AwayFromZero) * step));

    /// <summary>A segment per day an entry touches, overlapping ones side by side as calendars show double bookings.</summary>
    public static IReadOnlyList<DaySegment> Layout(IEnumerable<TimeEntryResponse> entries, IReadOnlyList<DateOnly> days, UserTime time, DateTimeOffset now)
    {
        var all = entries.ToArray();
        return days.SelectMany(day =>
        {
            var dayStart = time.StartOf(day);
            var dayEnd = time.StartOf(day.AddDays(1));
            var segments = all
                .Where(e => e.Start < dayEnd && (e.Stop ?? now) > dayStart)
                .Select(e =>
                {
                    var end = e.Stop ?? now;
                    return new DaySegment(e, day, e.Start <= dayStart ? 0 : time.MinuteOfDay(e.Start), end >= dayEnd ? MinutesPerDay : time.MinuteOfDay(end), e.Start < dayStart, end > dayEnd);
                });
            return AssignLanes(segments);
        }).ToArray();
    }

    private static IEnumerable<DaySegment> AssignLanes(IEnumerable<DaySegment> segments)
    {
        var placed = new List<DaySegment>();
        var cluster = new List<DaySegment>();
        var laneEnds = new List<double>();
        var clusterEnd = -1d;
        void Close()
        {
            placed.AddRange(cluster.Select(s => s with { Lanes = laneEnds.Count }));
            cluster.Clear();
            laneEnds.Clear();
        }
        foreach (var segment in segments.OrderBy(s => s.StartMinute).ThenByDescending(s => s.EndMinute))
        {
            if (segment.StartMinute >= clusterEnd && cluster.Count > 0) Close();
            // A segment of no length still needs a sliver of room.
            var end = Math.Max(segment.EndMinute, segment.StartMinute + 1);
            var lane = laneEnds.FindIndex(e => e <= segment.StartMinute);
            if (lane >= 0) laneEnds[lane] = end;
            else
            {
                lane = laneEnds.Count;
                laneEnds.Add(end);
            }
            cluster.Add(segment with { Lane = lane });
            clusterEnd = Math.Max(clusterEnd, end);
        }
        Close();
        return placed;
    }

    /// <summary>"22 – 28 Sep 2026", "29 Sep – 5 Oct 2026", or one day in full.</summary>
    public static string Label(IReadOnlyList<DateOnly> days)
    {
        if (days.Count == 0) return "";
        var english = CultureInfo.GetCultureInfo("en-GB");
        var (first, last) = (days[0], days[^1]);
        if (first == last) return first.ToString("dddd d MMMM yyyy", english);
        var sameMonth = first.Year == last.Year && first.Month == last.Month;
        var sameYear = first.Year == last.Year;
        return $"{first.ToString(sameMonth ? "%d" : sameYear ? "d MMM" : "d MMM yyyy", english)} – {last.ToString("d MMM yyyy", english)}";
    }
}

/// <summary>What dragging on the calendar does: create a stretch, move an entry, or move one of its edges.</summary>
public abstract record CalendarDrag
{
    public sealed record Create(DateOnly Day, double Anchor, double Current) : CalendarDrag;
    public sealed record Open(TimeEntryResponse Entry) : CalendarDrag;
    public sealed record Move(TimeEntryResponse Entry, double Offset, double Duration, DateOnly Day, double Start, bool Moved) : CalendarDrag;
    public sealed record Resize(bool StartEdge, TimeEntryResponse Entry, DateOnly Day, double Start, double End, bool Moved) : CalendarDrag;

    /// <summary>A finished entry on an open day can be moved and resized; others only open.</summary>
    public static bool CanChange(TimeEntryResponse entry, EntryRules rules, UserTime time) => entry.Stop is not null && !rules.IsLocked(time.Day(entry.Start));

    public static CalendarDrag? Begin(DaySegment? segment, bool? startEdge, DateOnly day, double minute, EntryRules rules, UserTime time)
    {
        if (segment is not null)
        {
            var entry = segment.Entry;
            if (entry.Stop is not { } stop || !CanChange(entry, rules, time)) return new Open(entry);
            if (startEdge is { } edge) return new Resize(edge, entry, segment.Day, segment.StartMinute, segment.EndMinute, false);
            if (segment.ContinuesBefore) return new Open(entry);
            return new Move(entry, minute - segment.StartMinute, (stop - entry.Start).TotalMinutes, segment.Day, segment.StartMinute, false);
        }
        if (rules.IsLocked(day)) return null;
        var start = CalendarLayout.Snap(minute - CalendarLayout.Step / 2);
        return new Create(day, start, start);
    }

    public CalendarDrag To(DateOnly day, double minute, double distance, EntryRules rules) => this switch
    {
        Create create => create with { Current = CalendarLayout.Snap(minute) },
        Move move => move with
        {
            Start = Math.Min(CalendarLayout.MinutesPerDay - CalendarLayout.Step, CalendarLayout.Snap(minute - move.Offset)),
            Day = rules.IsLocked(day) ? move.Day : day,
            Moved = move.Moved || distance > 4,
        },
        Resize { StartEdge: false } end => end with { End = Math.Max(end.Start + CalendarLayout.Step, CalendarLayout.Snap(minute)), Moved = true },
        Resize start => start with { Start = Math.Min(start.End - CalendarLayout.Step, CalendarLayout.Snap(minute)), Moved = true },
        _ => this,
    };

    /// <summary>What a drag that may have been a click shows while it lasts.</summary>
    public DaySegment? Preview() => this switch
    {
        Move { Moved: true } move => new DaySegment(move.Entry, move.Day, move.Start, Math.Min(move.Start + move.Duration, CalendarLayout.MinutesPerDay)),
        Resize { Moved: true } resize => new DaySegment(resize.Entry, resize.Day, resize.Start, resize.End),
        _ => null,
    };
}
