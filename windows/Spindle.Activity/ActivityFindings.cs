namespace Spindle.Activity;

// The Mac's rules (SpindleKit, Activity/ActivityFindings.swift). Times are Unix milliseconds.

/// <summary>Tracked time that went somewhere other than the project on the timer.</summary>
/// <param name="OtherProjects">Time in projects other than the entry's own.</param>
/// <param name="Unlinked">Time in repositories without a project yet, which may still turn out to be the entry's own.</param>
/// <param name="Entries">The entries a split is suggested for.</param>
public sealed record ElsewhereFinding(long OtherProjects, long Unlinked, int Entries);

/// <summary>A repository agents worked in, and for how long.</summary>
public sealed record RepositoryFinding(AgentRepository Repository, long Attention);

/// <summary>
/// What agent logs say about time already tracked, in a sentence or two: what turning them on shows first, so the
/// point of reading them is seen before the first parallel entry stops. Worked out by the same rules as the
/// suggestions, so every minute it counts is one a suggestion offers.
/// </summary>
public static class ActivityFindings
{
    /// <summary>A finished entry as the findings read it.</summary>
    public sealed record Entry(long Start, long Stop, Guid? ProjectId, bool IsSplit);

    /// <summary>
    /// How much of the finished entries' time on one project the logs place in other projects, by their suggestions.
    /// Entries the person split already, and those without a project, aren't counted: there is nothing to compare.
    /// </summary>
    public static ElsewhereFinding Elsewhere(IEnumerable<Entry> entries, IReadOnlyList<AttentionSegment> attention, Func<AgentRepository, Guid?> project) =>
        Elsewhere(entries, attention, SplitSuggestion.Placing(project));

    /// <summary>The same, with each stretch of attention placed by <paramref name="place"/>, which reads branch rules.</summary>
    public static ElsewhereFinding Elsewhere(IEnumerable<Entry> entries, IReadOnlyList<AttentionSegment> attention, Func<AttentionSegment, SplitPlacement?> place)
    {
        long other = 0, unlinked = 0;
        var count = 0;
        foreach (var entry in entries)
        {
            if (entry.IsSplit || entry.ProjectId is not { } own) continue;
            if (SplitSuggestion.Suggest(entry.Start, entry.Stop, attention, own, place) is not { } suggestion) continue;
            count++;
            foreach (var part in suggestion.Parts)
            {
                if (part.Target.ProjectId == own) continue;
                if (part.Target.ProjectId is not null) other += part.Attention + part.Unattributed;
                else unlinked += part.Attention + part.Unattributed;
            }
        }
        return new ElsewhereFinding(other, unlinked, count);
    }

    /// <summary>
    /// The repositories with agent attention, most first. Folders outside git don't count, as suggestions leave them
    /// out too; worktrees are their repository already.
    /// </summary>
    public static List<RepositoryFinding> Repositories(IEnumerable<AttentionSegment> attention) =>
        attention.Where(a => a.Repository is not null).GroupBy(a => a.Repository!.Identity)
            .Select(g => new RepositoryFinding(g.First().Repository!, g.Sum(a => a.Duration)))
            .OrderByDescending(r => r.Attention).ThenBy(r => r.Repository.Name, StringComparer.Ordinal).ToList();
}
