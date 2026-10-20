namespace Spindle.Activity;

// The Mac's rules (SpindleKit, Activity/UntrackedActivity.swift), the same as the server's
// (src/Spindle.Domain/AgentActivity/UntrackedActivity.cs). Both apps run the cases in
// tests/vectors/untracked-activity.json. Times are Unix milliseconds.

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

    public bool Contains(long at) => at >= Start && at < End;
}

/// <summary>A repository's run of attention within a stretch; <see cref="Repository"/> is null outside git.</summary>
public sealed record UntrackedRun(AgentRepository? Repository, TimeRange Range);

/// <summary>
/// A stretch of agent attention nobody tracked, and where it went: its <see cref="Projects"/>, most attention first,
/// and the attention in repositories without a project (<see cref="Unlinked"/>), which no project can be filled in for.
/// </summary>
public sealed record UntrackedStretch(TimeRange Range, IReadOnlyList<Guid> Projects, long Unlinked)
{
    public bool Equals(UntrackedStretch? other) =>
        other is not null && Range == other.Range && Projects.SequenceEqual(other.Projects) && Unlinked == other.Unlinked;

    public override int GetHashCode() => HashCode.Combine(Range, Projects.Count, Unlinked);
}

/// <summary>
/// When agents were busy and no entry covers it: the modest hint the vision allows ("agents were active 14:00–15:30
/// with no timer running"). It never says where attention "probably" was outside the logs; meetings, calls and
/// reading stay the person's to tell.
/// </summary>
public static class UntrackedActivity
{
    /// <summary>Stretches closer than this are one stretch of work.</summary>
    public const long JoinGap = 20 * 60 * 1000;
    /// <summary>Shorter stretches are left out.</summary>
    public const long Shortest = 5 * 60 * 1000;
    /// <summary>Pauses in one repository's work shorter than this don't split its run.</summary>
    public const long RunGap = 5 * 60 * 1000;
    /// <summary>How close to a timer's start, or to now, untracked attention has to reach to belong to it.</summary>
    public const long Reach = 5 * 60 * 1000;
    /// <summary>How much attention in one project with no timer running makes the reminder to track name it.</summary>
    public const long OngoingAttention = 15 * 60 * 1000;

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

    // What the apps offer for it

    /// <summary>The untracked stretches within <paramref name="range"/>, up to <paramref name="now"/>, with the projects their attention went to.</summary>
    public static List<UntrackedStretch> Stretches(IReadOnlyList<AttentionSegment> attention, IReadOnlyList<TimeRange> covered, TimeRange range, long now,
        Func<AgentRepository, Guid?> project) =>
        Stretches(attention, covered, range, now, Placing(project));

    /// <summary>The same, with each stretch of attention's project from <paramref name="place"/>, which reads branch rules.</summary>
    public static List<UntrackedStretch> Stretches(IReadOnlyList<AttentionSegment> attention, IReadOnlyList<TimeRange> covered, TimeRange range, long now,
        Func<AttentionSegment, Guid?> place) =>
        Stretches(attention, covered, range, now).Select(stretch =>
        {
            var (byProject, unlinked) = Attended(attention, covered, stretch, place);
            var projects = byProject.OrderByDescending(a => a.Value).ThenBy(a => a.Key.ToString(), StringComparer.Ordinal).Select(a => a.Key).ToList();
            return new UntrackedStretch(stretch, projects, unlinked);
        }).ToList();

    /// <summary>
    /// Each project's attention within <paramref name="range"/> outside <paramref name="covered"/>, and apart from it
    /// the attention in repositories without a project (or outside git).
    /// </summary>
    internal static (Dictionary<Guid, long> ByProject, long Unlinked) Attended(IReadOnlyList<AttentionSegment> attention, IReadOnlyList<TimeRange> covered,
        TimeRange range, Func<AttentionSegment, Guid?> place)
    {
        var blocks = Merge(covered.Select(c => c.Intersect(range)).OfType<TimeRange>(), 0);
        var byProject = new Dictionary<Guid, long>();
        var unlinked = 0L;
        foreach (var segment in attention)
        {
            if (new TimeRange(segment.Start, segment.End).Intersect(range) is not { Duration: > 0 } clipped) continue;
            var duration = Subtract(blocks, clipped).Sum(r => r.Duration);
            if (segment.Repository is not null && place(segment) is { } id) byProject[id] = byProject.GetValueOrDefault(id) + duration;
            else unlinked += duration;
        }
        return (byProject, unlinked);
    }

    /// <summary>
    /// Where a timer started at <paramref name="start"/> could have started instead: the untracked stretch just before
    /// it, when that reaches within <see cref="Reach"/> of the start. Never before <paramref name="notBefore"/> (the
    /// previous entry's stop, the day's start, a locked day), and on a whole minute.
    /// </summary>
    public static UntrackedStretch? EarlierStart(long start, IReadOnlyList<AttentionSegment> attention, IReadOnlyList<TimeRange> covered, long notBefore,
        Func<AgentRepository, Guid?> project) =>
        EarlierStart(start, attention, covered, notBefore, Placing(project));

    public static UntrackedStretch? EarlierStart(long start, IReadOnlyList<AttentionSegment> attention, IReadOnlyList<TimeRange> covered, long notBefore,
        Func<AttentionSegment, Guid?> place)
    {
        if (notBefore >= start) return null;
        if (Stretches(attention, covered, new TimeRange(notBefore, start), start, place).LastOrDefault() is not { } last || start - last.Range.End > Reach) return null;
        var from = Math.Max(notBefore, WholeMinute(last.Range.Start));
        if (start - from < 60_000) return null;
        return last with { Range = new TimeRange(from, last.Range.End) };
    }

    /// <summary>
    /// The untracked work going on now, with no timer running: the last stretch, when it reaches within
    /// <see cref="Reach"/> of <paramref name="now"/> and one project has had at least <paramref name="least"/> of its
    /// attention. Starts on a whole minute, never before <paramref name="notBefore"/>.
    /// </summary>
    public static UntrackedStretch? Ongoing(IReadOnlyList<AttentionSegment> attention, IReadOnlyList<TimeRange> covered, long notBefore, long now,
        Func<AgentRepository, Guid?> project, long least = OngoingAttention) =>
        Ongoing(attention, covered, notBefore, now, Placing(project), least);

    public static UntrackedStretch? Ongoing(IReadOnlyList<AttentionSegment> attention, IReadOnlyList<TimeRange> covered, long notBefore, long now,
        Func<AttentionSegment, Guid?> place, long least = OngoingAttention)
    {
        if (notBefore >= now) return null;
        if (Stretches(attention, covered, new TimeRange(notBefore, now), now, place).LastOrDefault() is not { } last
            || now - last.Range.End > Reach || last.Projects.Count == 0) return null;
        if (Attended(attention, covered, last.Range, place).ByProject.GetValueOrDefault(last.Projects[0]) < least) return null;
        return last with { Range = new TimeRange(Math.Max(notBefore, WholeMinute(last.Range.Start)), last.Range.End) };
    }

    /// <summary>A repository's project as the place of each stretch of its attention, for callers without branch rules.</summary>
    private static Func<AttentionSegment, Guid?> Placing(Func<AgentRepository, Guid?> project) =>
        segment => segment.Repository is { } repository ? project(repository) : null;

    private static long WholeMinute(long at) => (long)Math.Floor(at / 60_000d) * 60_000;
}
