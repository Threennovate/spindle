namespace Spindle.Domain.AgentActivity;

/// <summary>A span of time in Unix milliseconds, [Start, End).</summary>
public readonly record struct TimeRange(long Start, long End)
{
    public long Duration => End - Start;

    public TimeRange? Intersect(TimeRange other)
    {
        var start = Math.Max(Start, other.Start);
        var end = Math.Min(End, other.End);
        return end >= start ? new TimeRange(start, end) : null;
    }
}

/// <summary>A repository's run of attention within a stretch; <see cref="Repository"/> is null outside git.</summary>
public sealed record UntrackedRun(AgentRepository? Repository, TimeRange Range);

/// <summary>
/// When agents were busy and no entry covers it: the modest hint the vision allows ("agents were active 14:00–15:30
/// with no timer running"), for an agent to propose entries from when the person asks it to fill in a day. It never
/// says where attention "probably" was outside the logs; meetings, calls and reading stay the person's to tell. The
/// Mac's rules (SpindleKit, Activity/UntrackedActivity.swift), as Windows has them (Spindle.Activity), for the server's
/// MCP, which reads the attention from synced agent activity.
/// </summary>
public static class UntrackedActivity
{
    /// <summary>Stretches closer than this are one stretch of work.</summary>
    public const long JoinGap = 20 * 60 * 1000;
    /// <summary>Shorter stretches are left out.</summary>
    public const long Shortest = 5 * 60 * 1000;
    /// <summary>Pauses in one repository's work shorter than this don't split its run.</summary>
    public const long RunGap = 5 * 60 * 1000;

    /// <summary>
    /// The stretches of agent attention within <paramref name="range"/> that nothing in <paramref name="covered"/>
    /// (entries, proposals waiting) overlaps, up to <paramref name="now"/>, joined when close and left out when short.
    /// </summary>
    public static List<TimeRange> Stretches(IEnumerable<AttentionSegment> attention, IEnumerable<TimeRange> covered, TimeRange range, long now)
    {
        var end = Math.Min(range.End, now);
        if (end <= range.Start) return [];
        var bounds = new TimeRange(range.Start, end);
        var blocks = Merge(covered.Select(c => c.Intersect(bounds)).OfType<TimeRange>(), 0);
        var pieces = new List<TimeRange>();
        foreach (var segment in attention)
        {
            if (new TimeRange(segment.Start, segment.End).Intersect(bounds) is not { Duration: > 0 } clipped) continue;
            pieces.AddRange(Subtract(blocks, clipped));
        }
        return Merge(pieces, JoinGap).Where(s => s.Duration >= Shortest).ToList();
    }

    /// <summary>
    /// Where a stretch's attention went, repository by repository in time order, each run joined across short pauses,
    /// so parallel work shows as overlapping runs and sequential work as one after another.
    /// </summary>
    public static List<UntrackedRun> Runs(IEnumerable<AttentionSegment> attention, IEnumerable<TimeRange> covered, TimeRange stretch)
    {
        var blocks = Merge(covered.Select(c => c.Intersect(stretch)).OfType<TimeRange>(), 0);
        var byRepository = new Dictionary<string, (AgentRepository? Repository, List<TimeRange> Ranges)>();
        foreach (var segment in attention)
        {
            if (new TimeRange(segment.Start, segment.End).Intersect(stretch) is not { Duration: > 0 } clipped) continue;
            var key = segment.Repository?.Identity ?? "";
            if (!byRepository.TryGetValue(key, out var runs)) byRepository[key] = runs = (segment.Repository, []);
            runs.Ranges.AddRange(Subtract(blocks, clipped));
        }
        return byRepository.Values.SelectMany(r => Merge(r.Ranges, RunGap).Select(range => new UntrackedRun(r.Repository, range)))
            .OrderBy(r => r.Range.Start).ThenBy(r => r.Repository?.Name ?? "", StringComparer.Ordinal).ToList();
    }

    /// <summary>Ranges joined where they overlap or lie within <paramref name="gap"/> of each other, in order.</summary>
    public static List<TimeRange> Merge(IEnumerable<TimeRange> ranges, long gap)
    {
        var result = new List<TimeRange>();
        foreach (var range in ranges.OrderBy(r => r.Start))
        {
            if (result.Count > 0 && range.Start - result[^1].End <= gap)
                result[^1] = new TimeRange(result[^1].Start, Math.Max(result[^1].End, range.End));
            else
                result.Add(range);
        }
        return result;
    }

    /// <summary>What is left of <paramref name="range"/> outside <paramref name="blocks"/>, which are sorted and don't overlap.</summary>
    public static List<TimeRange> Subtract(IReadOnlyList<TimeRange> blocks, TimeRange range)
    {
        var left = new List<TimeRange>();
        var start = range.Start;
        foreach (var block in blocks.Where(b => b.End > range.Start && b.Start < range.End))
        {
            if (block.End <= start) continue;
            if (block.Start > start) left.Add(new TimeRange(start, block.Start));
            start = Math.Max(start, block.End);
        }
        if (start < range.End) left.Add(new TimeRange(start, range.End));
        return left.Where(r => r.Duration > 0).ToList();
    }
}
