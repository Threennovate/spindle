using Spindle.Activity;
using Spindle.Core;

namespace Spindle.Windows.Model;

/// <summary>
/// One of the entries an item of the day's review leaves behind, as its row shows it: its time, and each project's (or
/// repository's) share of it; one part for an entry on one project.
/// </summary>
public sealed record ReviewResult(DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<(SplitTarget Target, long Seconds)> Parts);

// What the time becomes once a review item is taken, as the web's review rows say it.
public sealed partial class SplitAdvisor
{
    /// <summary>A target for "No project", e.g. untracked time only in repositories without one.</summary>
    public static readonly SplitTarget NoProject = new(null, null);

    /// <summary>
    /// What the time becomes once the item is taken: one entry split between projects, separate entries cut where the
    /// work moved, the split ended and continued, or a new entry for untracked time, on one project or split.
    /// Repositories without a project show as they are.
    /// </summary>
    public List<ReviewResult> Outcomes(ReviewItem item)
    {
        switch (item)
        {
            case ReviewItem.Split split:
            {
                var suggestion = split.Offer.Suggestion;
                if (suggestion.Stretches is { Count: > 1 } stretches) return Cut(stretches, item.Start, item.End);
                return [new ReviewResult(item.Start, item.End, Shares(suggestion.Parts.Select(p => (p.Target, p.Percent)), item.End - item.Start))];
            }
            case ReviewItem.Drift drift:
            {
                var entry = drift.Offer.Entry;
                var at = DateTimeOffset.FromUnixTimeMilliseconds(drift.Offer.Drift.At);
                static SplitTarget Target(SplitPart part) => part.ProjectId is { } id ? SplitTarget.Project(id) : NoProject;
                return
                [
                    new ReviewResult(entry.Start, at, Shares(entry.Parts().Select(p => (Target(p), p.Percent)), at - entry.Start)),
                    new ReviewResult(at, item.End, Shares(Continuing(entry, drift.Offer.Drift).Select(p => (Target(p), p.Percent)), item.End - at)),
                ];
            }
            case ReviewItem.Untracked untracked:
            {
                var block = untracked.Block;
                if (block.Stretch.Projects.Count == 0) return [new ReviewResult(item.Start, item.End, [(NoProject, (long)(item.End - item.Start).TotalSeconds)])];
                var projectId = block.Stretch.Projects[0];
                // As one click saves it (Requests): its stretches or its split once every repository has a project.
                if (Suggestion(block.Start, block.End, projectId) is { } suggestion && Take(suggestion, trackedProject: projectId) is { } take)
                {
                    if (take.Stretches is { Length: > 1 } && suggestion.Stretches is { } stretches) return Cut(stretches, item.Start, item.End);
                    if (take.Parts is { Length: > 1 }) return [new ReviewResult(item.Start, item.End, Shares(suggestion.Parts.Select(p => (p.Target, p.Percent)), item.End - item.Start))];
                }
                return [new ReviewResult(item.Start, item.End, [(SplitTarget.Project(projectId), (long)(item.End - item.Start).TotalSeconds)])];
            }
        }
        return [];
    }

    /// <summary>Separate entries, cut where the work moved: the first from the start, the last to the end.</summary>
    private static List<ReviewResult> Cut(IReadOnlyList<SplitStretch> stretches, DateTimeOffset start, DateTimeOffset end) =>
        stretches.Select((stretch, index) =>
        {
            var from = index == 0 ? start : DateTimeOffset.FromUnixTimeMilliseconds(stretch.Start);
            var to = index == stretches.Count - 1 ? end : DateTimeOffset.FromUnixTimeMilliseconds(stretches[index + 1].Start);
            return new ReviewResult(from, to, [(stretch.Target, (long)(to - from).TotalSeconds)]);
        }).ToList();

    /// <summary>Each part's time of <paramref name="span"/>, in whole minutes that add up to the whole.</summary>
    private static List<(SplitTarget, long)> Shares(IEnumerable<(SplitTarget Target, int Percent)> parts, TimeSpan span)
    {
        var list = parts.ToList();
        var minutes = SplitMath.Allocate((long)Math.Round(Math.Max(0, span.TotalMinutes)), list.Select(p => p.Percent).ToArray());
        return list.Select((p, i) => (p.Target, minutes[i] * 60)).ToList();
    }
}
