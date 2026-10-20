using Spindle.Domain.Common;

namespace Spindle.Domain.TimeEntries;

/// <summary>
/// The part of an entry's time that went to one project (and task), when someone worked on several things at once.
/// The parts of a split entry add up to 100 percent, so a split never makes a day longer than it was.
/// </summary>
/// <param name="Description">
/// What was done on this part, when each part of a split has its own description; null when the entry's one
/// description covers all of them. Reports and timesheets of a project then show only its own part's description.
/// </param>
public sealed record TimeShare(Guid? ProjectId, Guid? TaskId, bool IsBillable, int Percent, string? Description = null)
{
    public const int MaxShares = 6;

    /// <summary>What this part shows in reports: its own description, or the entry's when the parts share one.</summary>
    public string DescriptionOr(string entryDescription) => Description ?? entryDescription;

    /// <summary>Whether each part has its own description, rather than all of them sharing the entry's.</summary>
    public static bool AreDescribed(IEnumerable<TimeShare> shares) => shares.Any(s => s.Description is not null);

    /// <summary>The parts' own descriptions as one, in their order, for where an entry shows a single description.</summary>
    public static string JoinDescriptions(IEnumerable<TimeShare> shares) =>
        string.Join("; ", shares.Select(s => s.Description).Where(d => !string.IsNullOrEmpty(d)));

    /// <summary>
    /// Divides whole seconds between parts in proportion to their percentages, so that they add up exactly:
    /// what rounding leaves over goes to the parts with the largest remainders, the earlier one on a tie.
    /// </summary>
    public static long[] Allocate(long seconds, IReadOnlyList<int> percents)
    {
        var parts = new long[percents.Count];
        if (percents.Count == 0)
        {
            return parts;
        }

        var total = percents.Sum();
        var remainders = new long[percents.Count];
        long given = 0;
        for (var i = 0; i < percents.Count; i++)
        {
            parts[i] = Math.DivRem(seconds * percents[i], total, out remainders[i]);
            given += parts[i];
        }

        foreach (var i in Enumerable.Range(0, percents.Count).OrderByDescending(i => remainders[i]).ThenBy(i => i).Take((int)(seconds - given)))
        {
            parts[i]++;
        }

        return parts;
    }

    /// <summary>Where an entry's time went: its parts when split, or all of it on its one project.</summary>
    public static IReadOnlyList<TimeShare> PartsOf(TimeShare[] shares, Guid? projectId, Guid? taskId, bool isBillable) =>
        shares.Length > 1 ? shares : [new TimeShare(projectId, taskId, isBillable, 100)];

    /// <summary>Divides an entry's seconds, e.g. its rounded duration, between its parts.</summary>
    public static IEnumerable<(TimeShare Part, long Seconds)> Divide(IReadOnlyList<TimeShare> parts, long seconds) =>
        parts.Zip(Allocate(seconds, parts.Select(p => p.Percent).ToList()));

    /// <summary>Two to <see cref="MaxShares"/> parts of different projects or tasks that add up to 100 percent.</summary>
    internal static TimeShare[] Check(IReadOnlyList<TimeShare> shares)
    {
        if (shares.Count > MaxShares)
        {
            throw new DomainValidationException("shares", $"Split time between at most {MaxShares} projects.");
        }

        if (shares.Any(s => s.Percent is < 1 or > 99))
        {
            throw new DomainValidationException("shares", "Each part needs from 1 to 99 percent.");
        }

        if (shares.Sum(s => s.Percent) != 100)
        {
            throw new DomainValidationException("shares", "The parts must add up to 100 percent.");
        }

        if (shares.DistinctBy(s => (s.ProjectId, s.TaskId)).Count() != shares.Count)
        {
            throw new DomainValidationException("shares", "Each part needs a different project or task.");
        }

        return [.. shares];
    }

    /// <summary>
    /// The parts once a project or task is gone: its parts lose it, and parts that became the same are joined, along with
    /// their descriptions. A single part left is no split at all.
    /// </summary>
    internal static TimeShare[] Without(IEnumerable<TimeShare> shares, Func<TimeShare, TimeShare> remove) =>
    [
        .. shares
            .Select(remove)
            .GroupBy(s => (s.ProjectId, s.TaskId))
            .Select(g => g.First() with
            {
                Percent = g.Sum(s => s.Percent),
                IsBillable = g.Any(s => s.IsBillable),
                Description = AreDescribed(g) ? JoinDescriptions(g) : null,
            }),
    ];
}
