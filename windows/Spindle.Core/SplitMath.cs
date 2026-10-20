namespace Spindle.Core;

public sealed record TimeAllocation(Guid? ProjectId, Guid? TaskId, bool IsBillable, long Seconds);

/// <summary>Mirrors Domain TimeShare.Divide: largest remainder, with stable ties. A split never adds time.</summary>
public static class SplitMath
{
    public static long[] Allocate(long seconds, IReadOnlyList<int> percents)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        if (percents.Count == 0) return [];
        if (percents.Any(p => p <= 0) || percents.Sum() != 100)
            throw new ArgumentException("Split percentages must be positive and add up to 100.", nameof(percents));
        var parts = new long[percents.Count];
        var remainders = new long[percents.Count];
        long given = 0;
        for (var i = 0; i < percents.Count; i++)
        {
            // Divide before multiplication to avoid overflow for long durations.
            var whole = Math.DivRem(seconds, 100, out var remainder);
            parts[i] = whole * percents[i] + Math.DivRem(remainder * percents[i], 100, out remainders[i]);
            given += parts[i];
        }
        foreach (var i in Enumerable.Range(0, parts.Length).OrderByDescending(i => remainders[i]).ThenBy(i => i).Take((int)(seconds - given)))
            parts[i]++;
        return parts;
    }

    public static IReadOnlyList<TimeAllocation> Divide(TimeEntryResponse entry, long seconds)
    {
        if (entry.Shares.Length < 2) return [new(entry.ProjectId, entry.TaskId, entry.IsBillable, seconds)];
        var values = Allocate(seconds, entry.Shares.Select(s => s.Percent).ToArray());
        return entry.Shares.Select((s, i) => new TimeAllocation(s.ProjectId, s.TaskId, s.IsBillable, values[i])).ToArray();
    }

    /// <summary>Whether each part of a split entry has a description of its own; the entry's description is then made of them.</summary>
    public static bool DescribesParts(TimeEntryResponse entry) => entry.Shares.Length > 1 && entry.Shares.Any(s => s.Description is not null);

    /// <summary>The parts' own descriptions as one, in their order, as the server makes an entry's description of them.</summary>
    public static string JoinDescriptions(IEnumerable<string?> descriptions) =>
        string.Join("; ", descriptions.Select(d => d?.Trim()).Where(d => !string.IsNullOrEmpty(d)));

    /// <summary>
    /// The parts and description the server keeps for a save, as Domain TimeEntry and the API's part description rule:
    /// true saves the parts' own descriptions, false one description for all; null (apps that predate them) keeps the
    /// parts' descriptions, by project and task, while the entry's description is sent unchanged, and a new part starts
    /// without one. Parts with descriptions make the entry's description of theirs.
    /// </summary>
    public static (TimeShareResponse[] Shares, string Description) Described(TimeShareResponse[] shares, string description, bool? describeParts, TimeEntryResponse? existing)
    {
        description = description.Trim();
        if (shares.Length < 2) return (shares.Select(s => s with { Description = null }).ToArray(), description);
        Dictionary<(Guid?, Guid?), string?>? kept = describeParts is null && existing is not null && DescribesParts(existing) && description == existing.Description
            ? existing.Shares.GroupBy(s => (s.ProjectId, s.TaskId)).ToDictionary(g => g.Key, g => g.First().Description)
            : null;
        var described = shares.Select(s => s with
        {
            Description = describeParts switch
            {
                true => s.Description?.Trim() ?? "",
                false => null,
                null => kept is null ? null : kept.GetValueOrDefault((s.ProjectId, s.TaskId))?.Trim() ?? "",
            },
        }).ToArray();
        return described.Any(s => s.Description is not null) ? (described, JoinDescriptions(described.Select(s => s.Description))) : (described, description);
    }
}
