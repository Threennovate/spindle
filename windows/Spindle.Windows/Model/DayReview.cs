using Spindle.Activity;
using Spindle.Core;
using Spindle.Platform;
using Spindle.Windows.Desktop;
using Spindle.Windows.Views;

namespace Spindle.Windows.Model;

/// <summary>"Start at 9:12?": agents were at work right before a timer started, with nothing tracked.</summary>
/// <param name="ProjectId">The project the agents' attention went to most, if any.</param>
public sealed record EarlierStart(TimeEntryResponse Entry, DateTimeOffset Start, Guid? ProjectId)
{
    /// <summary>The agents' project, when the timer has none: taking the offer sets it too, as it replaces nothing the person chose.</summary>
    public Guid? FillsProjectId => ProjectId is { } id && Entry.ProjectId is null && !Entry.IsSplit() ? id : null;

    /// <summary>The agents' project, when it isn't the timer's: the start moves, the project stays the person's.</summary>
    public Guid? OtherProjectId => FillsProjectId is null && ProjectId is { } id && !Entry.Parts().Any(p => p.ProjectId == id) ? id : null;
}

/// <summary>Agent attention nobody tracked, as the calendar and the day's review show it.</summary>
/// <param name="Minute">The minute of the day it starts at, which dismissing it remembers.</param>
public sealed record UntrackedBlock(DateOnly Day, UntrackedStretch Stretch, int Minute)
{
    public DateTimeOffset Start => DateTimeOffset.FromUnixTimeMilliseconds(Stretch.Range.Start);
    public DateTimeOffset End => DateTimeOffset.FromUnixTimeMilliseconds(Stretch.Range.End);
    public string Id => $"{Day:yyyy-MM-dd}|{Minute}";
}

/// <summary>
/// One thing the day's review offers, in its place on the day: everything the app would otherwise offer one by one,
/// with a chip, a card or a notification, so a day can be closed in one go. As the Mac's ReviewItem.
/// </summary>
public abstract record ReviewItem
{
    // What agents proposed (ProposalInbox) joins here later, so the day has one inbox; it has its own review today.

    /// <summary>A finished entry on one project whose time agent activity suggests splitting (with its evidence).</summary>
    public sealed record Split(SplitOffer Offer) : ReviewItem;

    /// <summary>A finished split entry whose work changed after its split was set.</summary>
    public sealed record Drift(DriftOffer Offer) : ReviewItem;

    /// <summary>Agent attention with nothing tracked.</summary>
    public sealed record Untracked(UntrackedBlock Block) : ReviewItem;

    public string Id => this switch
    {
        Split split => $"split|{split.Offer.Entry.Id}",
        Drift drift => $"drift|{drift.Offer.Entry.Id}|{drift.Offer.Statement}",
        Untracked untracked => $"untracked|{untracked.Block.Id}",
        _ => "",
    };

    /// <summary>When the time it is about starts, where it sits on the day.</summary>
    public DateTimeOffset Start => this switch
    {
        Split split => DateTimeOffset.FromUnixTimeMilliseconds(split.Offer.Suggestion.Start),
        Drift drift => drift.Offer.Entry.Start,
        Untracked untracked => untracked.Block.Start,
        _ => default,
    };

    public DateTimeOffset End => this switch
    {
        Split split => DateTimeOffset.FromUnixTimeMilliseconds(split.Offer.Suggestion.End),
        Drift drift => drift.Offer.Entry.Stop is { } stop && stop > drift.Offer.Entry.Start ? stop : drift.Offer.Entry.Start,
        Untracked untracked => untracked.Block.End,
        _ => default,
    };

    /// <summary>The entry it changes; null for time nobody tracked.</summary>
    public TimeEntryResponse? Entry => this switch
    {
        Split split => split.Offer.Entry,
        Drift drift => drift.Offer.Entry,
        _ => null,
    };

    /// <summary>Whether one click can take it, as "Take all" does: every repository it names has a project.</summary>
    public bool IsTakeable => this switch
    {
        Split split => split.Offer.Suggestion.Parts.All(p => p.Target.ProjectId is not null),
        Drift => true,
        Untracked untracked => untracked.Block.Stretch.Projects.Count > 0,
        _ => false,
    };

    /// <summary>What a set of items is, counted by kind: "2 splits, 1 split change, 3 untracked stretches".</summary>
    public static string Breakdown(IReadOnlyList<ReviewItem> items)
    {
        var splits = items.Count(i => i is Split);
        var changes = items.Count(i => i is Drift);
        var untracked = items.Count(i => i is Untracked);
        return string.Join(", ", new[]
        {
            splits == 0 ? null : splits == 1 ? "1 split" : $"{splits} splits",
            changes == 0 ? null : changes == 1 ? "1 split change" : $"{changes} split changes",
            untracked == 0 ? null : untracked == 1 ? "1 untracked stretch" : $"{untracked} untracked stretches",
        }.OfType<string>());
    }
}

/// <summary>Why one click can't take a review item, said in its row; <see cref="Reason"/> is null where the row says it otherwise.</summary>
public sealed record ReviewHold(string? Reason);

/// <summary>What became of an item while the review was open.</summary>
public enum ReviewOutcome { Taken, Changed, NotNow }

/// <summary>An item dealt with: what became of it and, for a take, what changed, to undo it.</summary>
/// <param name="Order">When it was dealt with, so takes are undone newest first.</param>
public sealed record ReviewDone(ReviewItem Item, ReviewOutcome Outcome, string Title, IReadOnlyList<SavedChange> Changes, int Order);

/// <summary>The last thing done, said in the review's footer, with Undo for the items it covers.</summary>
public sealed record ReviewNotice(string Text, IReadOnlyList<string> Ids, bool IsError = false);

/// <summary>
/// One opening of the day's review, which lasts while the editor is open for Change… and ends when it closes: items
/// dealt with stay in their place with their outcome, and every one can be undone until then.
/// </summary>
public sealed class ReviewSession
{
    private int order;

    public Dictionary<string, ReviewDone> Done { get; } = [];
    public ReviewNotice? Notice { get; set; }

    /// <summary>The selected item, and where it was, to keep the place across Change… and after an item is dealt with.</summary>
    public string? Selected { get; set; }
    public int SelectedIndex { get; set; }

    public void Mark(ReviewItem item, ReviewOutcome outcome, string title, IReadOnlyList<SavedChange>? changes = null) =>
        Done[item.Id] = new ReviewDone(item, outcome, title, changes ?? [], ++order);
}

/// <summary>The days a review covers: one day, or a week (or the last seven days, after turning agent logs on).</summary>
public sealed record ReviewScope(DateOnly[] Days)
{
    public bool IsWeek => Days.Length > 1;

    public static ReviewScope Day(DateOnly day) => new([day]);

    /// <summary>The calendar week the day is in.</summary>
    public static ReviewScope Week(DateOnly day, UserTime time) => new(time.Week(day));

    /// <summary>The seven days up to and including <paramref name="day"/>.</summary>
    public static ReviewScope LastSevenDays(DateOnly day) => new(Enumerable.Range(0, 7).Select(i => day.AddDays(i - 6)).ToArray());

    public bool Equals(ReviewScope? other) => other is not null && Days.SequenceEqual(other.Days);

    public override int GetHashCode() => HashCode.Combine(Days.Length, Days.FirstOrDefault());
}

// Time nobody tracked, and the day's review, as the Mac's SplitAdvisor and DayReview.swift.
public sealed partial class SplitAdvisor
{
    /// <summary>How long after a timer starts the timer bar offers to move its start back.</summary>
    public static readonly TimeSpan EarlierStartWindow = TimeSpan.FromMinutes(10);

    /// <summary>The moment the timer bar asks about, e.g. a day of synthetic logs in snapshots.</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// For the first minutes of a running entry: the start agents' untracked work right before it suggests. Never
    /// before the previous entry's stop, the day's start, or into a locked day.
    /// </summary>
    public EarlierStart? StartEarlier(TimeEntryResponse running, DateTimeOffset? at = null)
    {
        var now = at ?? Now();
        var time = owner.Time;
        if (!Enabled || running.Stop is not null || now - running.Start >= EarlierStartWindow || !days.TryGetValue(time.Day(running.Start), out var read)) return null;
        var others = owner.Entries.Where(e => e.Id != running.Id).ToList();
        var previousStop = others.Where(e => e.Stop is { } stop && stop <= running.Start).Select(e => e.Stop!.Value).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var dayStart = time.StartOf(time.Day(running.Start));
        var floor = previousStop > dayStart ? previousStop : dayStart;
        var projects = Linkable();
        var links = Links();
        if (UntrackedActivity.EarlierStart(running.Start.ToUnixTimeMilliseconds(), read.Snapshot.Attention, Covered(others, now), floor.ToUnixTimeMilliseconds(),
                segment => links.Place(segment, projects)?.ProjectId) is not { } stretch) return null;
        var start = DateTimeOffset.FromUnixTimeMilliseconds(stretch.Range.Start);
        if (owner.Rules.IsLocked(time.Day(start))) return null;
        return new EarlierStart(running, start, stretch.Projects.Count > 0 ? stretch.Projects[0] : null);
    }

    /// <summary>
    /// With no timer running: agents at work in a linked project, for long enough that the reminder to track names it.
    /// Repositories without a project don't count.
    /// </summary>
    public UntrackedStretch? OngoingUntracked(DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        var time = owner.Time;
        var entries = owner.Entries;
        if (!Enabled || entries.Any(e => e.IsRunning()) || !days.TryGetValue(time.Day(now), out var read)) return null;
        var lastStop = entries.Where(e => e.Stop is { } stop && stop <= now).Select(e => e.Stop!.Value).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var dayStart = time.StartOf(time.Day(now));
        var floor = lastStop > dayStart ? lastStop : dayStart;
        var projects = Linkable();
        var links = Links();
        return UntrackedActivity.Ongoing(read.Snapshot.Attention, Covered(entries, now), floor.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(),
            segment => links.Place(segment, projects)?.ProjectId);
    }

    /// <summary>
    /// Past agent attention no entry covers on these days, for the calendar and the day's review, without what the
    /// person dismissed and without locked days. A stretch needs a prompt of its own: the minutes the logs assume after
    /// the last prompt before a stop are no sign of work nobody tracked. <paramref name="covered"/> adds time that is
    /// spoken for otherwise, e.g. agents' proposals.
    /// </summary>
    public List<UntrackedBlock> Untracked(IEnumerable<DateOnly> wanted, IEnumerable<TimeRange>? covered = null, DateTimeOffset? at = null)
    {
        if (!Enabled) return [];
        var now = at ?? DateTimeOffset.UtcNow;
        var time = owner.Time;
        var spoken = Covered(owner.Entries, now).Concat(covered ?? []).ToList();
        var projects = Linkable();
        var links = Links();
        var dismissedTime = owner.Preferences.DismissedUntrackedTime;
        var blocks = new List<UntrackedBlock>();
        foreach (var day in wanted.Distinct().Where(d => !owner.Rules.IsLocked(d)))
        {
            if (!days.TryGetValue(day, out var read)) continue;
            var dayStart = time.StartOf(day);
            var bounds = new TimeRange(dayStart.ToUnixTimeMilliseconds(), time.StartOf(day.AddDays(1)).ToUnixTimeMilliseconds());
            var prompts = read.Snapshot.Attention.SelectMany(a => a.Prompts).ToList();
            foreach (var stretch in UntrackedActivity.Stretches(read.Snapshot.Attention, spoken, bounds, now.ToUnixTimeMilliseconds(), segment => links.Place(segment, projects)?.ProjectId))
            {
                if (!prompts.Any(stretch.Range.Contains)) continue;
                var block = new UntrackedBlock(day, stretch, (int)((stretch.Range.Start - bounds.Start) / 60_000));
                if (!dismissedTime.Contains(block.Id)) blocks.Add(block);
            }
        }
        return blocks;
    }

    /// <summary>Hides untracked time in the calendar, on this PC.</summary>
    public void Dismiss(UntrackedBlock block)
    {
        owner.SavePreferences(owner.Preferences with { DismissedUntrackedTime = owner.Preferences.DismissedUntrackedTime.Adding(block.Id) });
        Revision++;
        Changed?.Invoke();
    }

    /// <summary>What the editor starts with for untracked time: its project, or the split its attention suggests.</summary>
    public EditorStart Editing(UntrackedBlock block)
    {
        Guid? projectId = block.Stretch.Projects.Count > 0 ? block.Stretch.Projects[0] : null;
        var details = new EntryDraft { ProjectId = projectId, IsBillable = owner.Catalog.Project(projectId)?.IsBillable };
        var take = Suggestion(block.Start, block.End, projectId) is { } suggestion ? Take(suggestion, trackedProject: projectId) : null;
        return new EditorStart(Details: details, Split: take?.Stretches is null ? take?.Parts : null, Stretches: take?.Stretches);
    }

    /// <summary>Opens a new entry for untracked time in the editor, with what the agents worked on filled in.</summary>
    public Task<bool> EditAsync(UntrackedBlock block) => owner.EditAsync(null, block.Start, block.End, start: Editing(block));

    /// <summary>
    /// What one click saves for untracked time: the stretch on its project, or as the split or separate entries its
    /// attention suggests. Null when the workspace needs details one click can't fill in, or no project is known.
    /// </summary>
    public CreateTimeEntryRequest[]? Requests(UntrackedBlock block) =>
        Build(block) is { } built && built.Needs is null ? built.Requests : null;

    /// <summary>What untracked time still needs that one click can't fill in, e.g. "Needs a task on Beacon"; null when nothing.</summary>
    public string? Needs(UntrackedBlock block) => Build(block)?.Needs;

    /// <summary>The entries one click would add for untracked time, and what the first still needs; null without a project.</summary>
    private (CreateTimeEntryRequest[] Requests, string? Needs)? Build(UntrackedBlock block)
    {
        if (owner.Rules.IsLocked(block.Day) || block.Stretch.Projects.Count == 0) return null;
        var catalog = owner.Catalog;
        var projectId = block.Stretch.Projects[0];
        bool Billable(Guid id) => catalog.Project(id)?.IsBillable ?? false;
        var take = Suggestion(block.Start, block.End, projectId) is { } suggestion ? Take(suggestion, trackedProject: projectId) : null;
        // A branch rule's task goes with its project's time.
        Guid? TaskOn(Guid id) => take?.Parts?.FirstOrDefault(p => p.ProjectId == id)?.TaskId;
        if (take?.Stretches is { Length: > 1 } stretches)
        {
            var requests = stretches.Select((s, i) => new CreateTimeEntryRequest
            {
                Id = Guid.NewGuid(), ProjectId = s.ProjectId, TaskId = TaskOn(s.ProjectId), IsBillable = Billable(s.ProjectId), Start = i == 0 ? block.Start : s.Start,
                Stop = i + 1 < stretches.Length ? stretches[i + 1].Start : block.End,
            }).ToArray();
            return (requests, requests.Select(r => Needs("", r.ProjectId, r.TaskId, [], null)).FirstOrDefault(n => n is not null));
        }
        if (take?.Parts is { Length: > 1 } parts)
        {
            var main = parts.First(p => p.Percent == parts.Max(q => q.Percent));
            return ([new CreateTimeEntryRequest
            {
                Id = Guid.NewGuid(), ProjectId = main.ProjectId, TaskId = main.TaskId, IsBillable = main.IsBillable, Start = block.Start, Stop = block.End,
                Shares = parts.Select(p => p.Request()).ToArray(),
            }], Needs("", null, null, [], parts));
        }
        return ([new CreateTimeEntryRequest { Id = Guid.NewGuid(), ProjectId = projectId, IsBillable = Billable(projectId), Start = block.Start, Stop = block.End }],
            Needs("", projectId, null, [], null));
    }

    /// <summary>
    /// What an entry still needs that the workspace requires, in the review's words: "Needs a description", "Needs a
    /// task on Beacon"; null when nothing. A split (<paramref name="parts"/>) is checked part by part.
    /// </summary>
    public string? Needs(string description, Guid? projectId, Guid? taskId, Guid[] tagIds, IReadOnlyList<SplitPart>? parts, bool describesParts = false)
    {
        var catalog = owner.Catalog;
        var split = parts is { Count: > 1 };
        var missing = owner.Rules.Missing(description, projectId, taskId, tagIds, split, catalog.Project(projectId), describesParts);
        if (missing.Count > 0) return NeedsWords(missing[0].Field, catalog.Project(projectId));
        if (!split) return null;
        if (owner.Rules.MissingInParts(parts!, catalog).OrderBy(m => m.Key).Select(m => (Index: m.Key, Message: m.Value)).FirstOrDefault() is not { Message: { } message } first) return null;
        var project = catalog.Project(parts![first.Index].ProjectId);
        return message == EntryRules.DescriptionRequired ? NeedsWords("description", project) : parts[first.Index].ProjectId is null ? NeedsWords("projectId", project) : NeedsWords("taskId", project);
    }

    private static string NeedsWords(string field, ProjectResponse? project) => field switch
    {
        "description" => "Needs a description",
        "projectId" => "Needs a project",
        "taskId" => project is null ? "Needs a task" : $"Needs a task on {project.Name}",
        _ => "Needs a tag",
    };

    /// <summary>
    /// What the review offers on these days, in order: splits of finished entries on one project, changes after a split,
    /// and agent time nobody tracked. Leaves out running entries (the timer bar offers theirs), locked days, what the
    /// person kept as it is, and what they put off with "Not now". Weak suggestions are here too; this is where they wait.
    /// </summary>
    public List<ReviewItem> Review(IEnumerable<DateOnly> wanted, IEnumerable<TimeRange>? covered = null, DateTimeOffset? at = null)
    {
        if (!Enabled) return [];
        var time = owner.Time;
        var open = wanted.Where(d => !owner.Rules.IsLocked(d)).ToHashSet();
        var items = new List<ReviewItem>();
        foreach (var entry in owner.Entries.Where(e => e.Stop is not null && open.Contains(time.Day(e.Start))))
        {
            if (entry.IsSplit())
            {
                if (Drift(entry, at) is { } drift) items.Add(new ReviewItem.Drift(drift));
            }
            else if (Suggestion(entry) is { } suggestion)
            {
                items.Add(new ReviewItem.Split(new SplitOffer(entry, suggestion)));
            }
        }
        items.AddRange(Untracked(open, covered, at).Select(b => new ReviewItem.Untracked(b)));
        var putOff = owner.Preferences.ReviewPutOff;
        return items.Where(i => !putOff.Contains(i.Id)).OrderBy(i => i.Start).ToList();
    }

    /// <summary>Leaves an item out of the day's review: "Not now". Nothing else changes.</summary>
    public void PutOff(ReviewItem item)
    {
        owner.SavePreferences(owner.Preferences with { ReviewPutOff = owner.Preferences.ReviewPutOff.Adding(item.Id) });
        Revision++;
        Changed?.Invoke();
    }

    /// <summary>Takes back a "Not now": the item is in the review again.</summary>
    public void Restore(ReviewItem item)
    {
        owner.SavePreferences(owner.Preferences with { ReviewPutOff = owner.Preferences.ReviewPutOff.Removing(item.Id) });
        Revision++;
        Changed?.Invoke();
    }

    /// <summary>"Split", "Split at 10:42", "End split at 11:05" or "Add entry".</summary>
    public string Action(ReviewItem item) => item switch
    {
        ReviewItem.Split split => Title(split.Offer.Suggestion),
        ReviewItem.Drift drift => Title(drift.Offer.Drift),
        _ => "Add entry",
    };

    /// <summary>The activity read for these days, for the days a review covers.</summary>
    public IReadOnlyList<AttentionSegment> Attention(IEnumerable<DateOnly> wanted) =>
        wanted.Distinct().SelectMany(d => days.TryGetValue(d, out var read) ? read.Snapshot.Attention : []).Distinct().ToList();

    /// <summary>Where a stretch of attention's work goes: a branch rule's project and task, else its repository's project.</summary>
    public Func<AttentionSegment, SplitPlacement?> Placing()
    {
        var projects = Linkable();
        var links = Links();
        return segment => links.Place(segment, projects);
    }

    /// <summary>The project a repository's work goes to, by the person's links, learned links and names.</summary>
    public Func<AgentRepository, Guid?> Matcher()
    {
        var projects = Linkable();
        var links = Links();
        return repository => links.ProjectFor(repository, projects);
    }

    /// <summary>The project the person chose for a repository, if any.</summary>
    public Guid? Chosen(AgentRepository repository) => chosen.TryGetValue(repository.Identity, out var id) ? id : null;

    /// <summary>The time entries take, a running one up to now.</summary>
    private static List<TimeRange> Covered(IEnumerable<TimeEntryResponse> entries, DateTimeOffset now) =>
        entries.Select(e => new TimeRange(e.Start.ToUnixTimeMilliseconds(), Math.Max(e.Start.ToUnixTimeMilliseconds(), (e.Stop ?? now).ToUnixTimeMilliseconds()))).ToList();
}
