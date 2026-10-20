namespace Spindle.Activity;

// The Mac's rules (SpindleKit, Activity/SplitDrift.swift). Both run the cases in tests/vectors/split-drift.json.
// Times are Unix milliseconds.

/// <summary>
/// How a split entry's work changed after its split was set: <see cref="Quiet"/> projects had agent attention earlier
/// in the entry and none since <see cref="At"/>, or the <see cref="Joined"/> project, outside the split, has had it
/// since <see cref="At"/>. <see cref="At"/> is where the entry would end and the next begin, at a whole minute.
/// </summary>
public sealed record SplitDrift(IReadOnlyList<Guid> Quiet, Guid? Joined, long At)
{
    /// <summary>A project of the split with no agent attention for this long has gone quiet...</summary>
    public const long QuietAfter = 30 * 60 * 1000;

    /// <summary>
    /// ...when the split's other projects had at least this much in that time, some of it after the first
    /// <see cref="AttentionTimeline.IdleCap"/> of it.
    /// </summary>
    public const long OthersActive = 10 * 60 * 1000;

    /// <summary>A project outside the split joined it once it had this much attention since the statement time.</summary>
    public const long JoinedAfter = 15 * 60 * 1000;

    public bool IsQuiet => Joined is null;

    public override string ToString() => IsQuiet ? $"quiet {string.Join(",", Quiet)} at {At}" : $"joined {Joined} at {At}";

    public bool Equals(SplitDrift? other) =>
        other is not null && Quiet.SequenceEqual(other.Quiet) && Joined == other.Joined && At == other.At;

    public override int GetHashCode() => HashCode.Combine(Quiet.Count, Joined, At);

    /// <summary>
    /// How the work changed since <paramref name="statedAt"/>, when the split on <paramref name="parts"/> (project IDs)
    /// was set, in an entry that began at <paramref name="start"/> and runs until <paramref name="now"/>. Attention and
    /// repository links are those of <see cref="SplitSuggestion.Suggest"/>. A part that never had agent attention in
    /// the entry is never judged: it may be work the logs can't see. When both a project went quiet and another
    /// joined, the earlier change is the one to offer.
    /// </summary>
    public static SplitDrift? Detect(
        IReadOnlyList<Guid> parts,
        long start,
        long statedAt,
        long now,
        IReadOnlyList<AttentionSegment> attention,
        Func<AgentRepository, Guid?> project) =>
        Detect(parts, start, statedAt, now, attention, SplitSuggestion.Placing(project));

    /// <summary>The same, with each stretch of attention placed by <paramref name="place"/>, which reads branch rules.</summary>
    public static SplitDrift? Detect(
        IReadOnlyList<Guid> parts,
        long start,
        long statedAt,
        long now,
        IReadOnlyList<AttentionSegment> attention,
        Func<AttentionSegment, SplitPlacement?> place)
    {
        if (now <= start || parts.Count == 0)
        {
            return null;
        }

        var stated = Math.Min(Math.Max(statedAt, start), now);
        var pieces = SplitSuggestion.PiecesWithin(start, now, attention, place)
            .Where(p => p.Target.ProjectId is not null)
            .Select(p => (Project: p.Target.ProjectId!.Value, p.Start, p.End))
            .ToList();
        long Attention(IReadOnlySet<Guid> projects, long from, long to) =>
            pieces.Where(p => projects.Contains(p.Project)).Sum(p => Math.Max(0, Math.Min(p.End, to) - Math.Max(p.Start, from)));
        var ordered = parts.Distinct().ToList();
        var split = ordered.ToHashSet();

        var quiet = new List<(Guid Project, long Since)>();
        foreach (var id in ordered)
        {
            var own = pieces.Where(p => p.Project == id).ToList();
            if (own.Count == 0)
            {
                continue;
            }

            var since = Math.Max(own.Max(p => p.End), stated);
            var others = split.Where(p => p != id).ToHashSet();
            // The others went on: one last prompt before everyone stopped leaves up to IdleCap of attention, so some
            // of theirs must come later than that.
            if (now - since >= QuietAfter
                && Attention(others, since, since + QuietAfter) >= OthersActive
                && Attention(others, since + AttentionTimeline.IdleCap, since + QuietAfter) > 0)
            {
                quiet.Add((id, since));
            }
        }

        var found = new List<SplitDrift>();
        // The latest of them, so no quiet project's work after its own last prompt is cut off from it.
        if (quiet.Count > 0 && quiet.Count < ordered.Count && Cut(quiet.Max(q => q.Since), start, now) is { } quietAt)
        {
            found.Add(new SplitDrift(quiet.Select(q => q.Project).ToList(), null, quietAt));
        }

        if (split.Count < 6)
        {
            var joined = pieces
                .Where(p => !split.Contains(p.Project) && Math.Min(p.End, now) > Math.Max(p.Start, stated))
                .GroupBy(p => p.Project)
                .Select(g => (Project: g.Key, Attention: g.Sum(p => Math.Min(p.End, now) - Math.Max(p.Start, stated)), First: g.Min(p => Math.Max(p.Start, stated))))
                .Where(g => g.Attention >= JoinedAfter)
                .OrderBy(g => g.First)
                .ThenByDescending(g => g.Attention)
                .FirstOrDefault();
            if (joined.Attention > 0 && Cut(joined.First, start, now) is { } joinedAt)
            {
                found.Add(new SplitDrift([], joined.Project, joinedAt));
            }
        }

        return found.OrderBy(d => d.At).FirstOrDefault();
    }

    /// <summary>At the nearest whole minute, which is how the entries will read, strictly inside the entry.</summary>
    private static long? Cut(long at, long start, long end)
    {
        var minute = (long)Math.Round(at / 60_000.0, MidpointRounding.AwayFromZero) * 60_000;
        return minute > start && minute < end ? minute : null;
    }

    /// <summary>
    /// The percentages the entry continues with after this change, for the projects <see cref="ContinuingProjects"/>
    /// names: the remaining parts keep their ratio as the person set it, or the project that joined gets an equal
    /// share, as <c>@Project</c> mentions add one.
    /// </summary>
    public int[] ContinuingPercents(IReadOnlyList<Guid?> parts, IReadOnlyList<int> percents)
    {
        if (IsQuiet)
        {
            var rest = percents.Where((_, i) => !(parts[i] is { } id && Quiet.Contains(id))).Select(p => (long)p).ToList();
            return SplitSuggestion.Apportion(rest, 100);
        }

        var share = (int)Math.Round(100d / (percents.Count + 1), MidpointRounding.AwayFromZero);
        var free = 100 - share - percents.Count;
        return [.. SplitSuggestion.Apportion(percents.Select(p => (long)p).ToList(), free).Select(p => p + 1), share];
    }

    /// <summary>The projects the entry continues with, in order: without the quiet ones, or with the one that joined last.</summary>
    public IReadOnlyList<Guid?> ContinuingProjects(IReadOnlyList<Guid?> parts) =>
        IsQuiet ? parts.Where(p => !(p is { } id && Quiet.Contains(id))).ToList() : [.. parts, Joined];
}
