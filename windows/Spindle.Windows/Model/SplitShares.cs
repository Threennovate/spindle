namespace Spindle.Windows.Model;

/// <summary>
/// Editing a split's percentages, the same as the Mac's SplitMath: whole percentages that always add up to 100.
/// Time itself is divided by <see cref="Spindle.Core.SplitMath.Allocate"/>, the rule the server uses.
/// </summary>
public static class SplitShares
{
    /// <summary>The most parts the server allows.</summary>
    public const int MaxParts = 6;

    /// <summary>The smallest part, in percent.</summary>
    public const int MinPercent = 1;

    /// <summary>
    /// Whole numbers in proportion to <paramref name="weights"/> that add up to <paramref name="total"/>, each at least
    /// <paramref name="minimum"/>: what rounding leaves over goes to the largest remainders, the earlier one on a tie.
    /// </summary>
    public static int[] Apportion(IReadOnlyList<int> weights, int total, int minimum = 0)
    {
        if (weights.Count == 0) return [];
        var free = total - minimum * weights.Count;
        var positive = weights.Select(w => Math.Max(0, w)).ToArray();
        var sum = positive.Sum();
        var shares = sum > 0 ? positive : positive.Select(_ => 1).ToArray();
        var divisor = sum > 0 ? sum : weights.Count;
        var result = shares.Select(s => s * free / divisor).ToArray();
        var rests = shares.Select(s => s * free % divisor).ToArray();
        var left = free - result.Sum();
        foreach (var index in Enumerable.Range(0, rests.Length).OrderByDescending(i => rests[i]).ThenBy(i => i))
        {
            if (left <= 0) break;
            result[index]++;
            left--;
        }
        return result.Select(r => r + minimum).ToArray();
    }

    /// <summary><paramref name="count"/> equal parts; the first ones get what does not divide evenly.</summary>
    public static int[] Equal(int count) => Apportion(Enumerable.Repeat(1, count).ToArray(), 100, MinPercent);

    /// <summary>Moves the boundary after part <paramref name="index"/> to <paramref name="position"/> percent; only its two neighbours change.</summary>
    public static int[] MoveBoundary(IReadOnlyList<int> percents, int index, double position, int step = 1)
    {
        var before = percents.Take(index).Sum();
        var pair = percents[index] + percents[index + 1];
        var snapped = (int)Math.Round(position / step, MidpointRounding.AwayFromZero) * step;
        var at = Math.Min(before + pair - MinPercent, Math.Max(before + MinPercent, snapped));
        var next = percents.ToArray();
        next[index] = at - before;
        next[index + 1] = pair - next[index];
        return next;
    }

    /// <summary>Gives part <paramref name="index"/> the percentage typed for it; the others make room in proportion.</summary>
    public static int[] Setting(IReadOnlyList<int> percents, int index, int value)
    {
        var clamped = Math.Min(100 - (percents.Count - 1) * MinPercent, Math.Max(MinPercent, value));
        var others = percents.Where((_, i) => i != index).ToArray();
        var result = Apportion(others, 100 - clamped, MinPercent).ToList();
        result.Insert(index, clamped);
        return result.ToArray();
    }

    /// <summary>Adds a part with an equal share; the others shrink in proportion.</summary>
    public static int[] Adding(IReadOnlyList<int> percents)
    {
        var share = (int)Math.Round(100d / (percents.Count + 1), MidpointRounding.AwayFromZero);
        return [.. Apportion(percents, 100 - share, MinPercent), share];
    }

    /// <summary>Removes part <paramref name="index"/>; its time goes to the others in proportion.</summary>
    public static int[] Removing(IReadOnlyList<int> percents, int index)
    {
        var rest = percents.Where((_, i) => i != index).ToArray();
        return Apportion(rest, 100, rest.Length > 1 ? MinPercent : 0);
    }

    /// <summary>The percentage closest to <paramref name="seconds"/> of an entry <paramref name="total"/> seconds long.</summary>
    public static int PercentOf(long seconds, long total) => total > 0 ? (int)Math.Round(seconds * 100d / total, MidpointRounding.AwayFromZero) : 0;

    /// <summary>The minutes each part gets, by the server's rule.</summary>
    public static long[] Minutes(long totalMinutes, IReadOnlyList<int> percents) =>
        percents.Sum() == 100 && percents.All(p => p > 0) ? Spindle.Core.SplitMath.Allocate(Math.Max(0, totalMinutes), percents) : percents.Select(_ => 0L).ToArray();

    /// <summary>Whether each part has a description of its own, which its project's reports and timesheet links show.</summary>
    public static bool DescribesEach(IReadOnlyList<SplitPart> parts) => parts.Count > 1 && parts.Any(p => p.Description is not null);

    /// <summary>The parts' own descriptions as one, as the server makes an entry's description of them.</summary>
    public static string JoinedDescriptions(IEnumerable<SplitPart> parts) => Spindle.Core.SplitMath.JoinDescriptions(parts.Select(p => p.Description));

    /// <summary>
    /// Gives each part a description of its own. The one description so far goes to the largest part, as it most likely
    /// describes the project the timer was on; the others start empty rather than repeat it on other projects.
    /// </summary>
    public static SplitPart[] DescribingEach(IReadOnlyList<SplitPart> parts, string description)
    {
        // The first of equal largest, as the server and the other apps choose it.
        var largest = Enumerable.Range(0, parts.Count).Aggregate(0, (best, i) => parts[i].Percent > parts[best].Percent ? i : best);
        return parts.Select((p, i) => p with { Description = i == largest ? description.Trim() : "" }).ToArray();
    }

    /// <summary>One description for all parts again: theirs joined, and none of their own.</summary>
    public static (SplitPart[] Parts, string Description) SharingOneDescription(IReadOnlyList<SplitPart> parts) =>
        (parts.Select(p => p with { Description = null }).ToArray(), JoinedDescriptions(parts));
}
