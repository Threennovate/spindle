using Spindle.Domain.TimeEntries;

namespace Spindle.Domain.AgentActivity;

// The rules here are the Mac's (SpindleKit, Activity/AgentActivity.swift and Activity/SplitSuggestion.swift),
// ported to work over an entry's own range rather than day by day. Both run the cases in
// tests/vectors/split-suggestions.json, so they suggest the same split. Times are Unix milliseconds.

/// <summary>
/// A prompt the person sent in a top-level session. <see cref="Repository"/> is null outside git. <see cref="Branches"/>
/// are the session's, which branch rules read.
/// </summary>
public readonly record struct AgentTouch(long At, string SessionId, AgentRepository? Repository, IReadOnlyList<string>? Branches = null);

/// <summary>An interval of the person's attention, attributed to the repository of the prompt that began it.</summary>
public sealed record AttentionSegment(AgentRepository? Repository, long Start, long End)
{
    public long Duration => End - Start;

    /// <summary>When the person prompted within it, in order: the prompt that began it, and those that kept it going.</summary>
    public IReadOnlyList<long> Prompts { get; init; } = [];

    /// <summary>The branches its sessions were on, distinct and in ordinal order, which branch rules read.</summary>
    public IReadOnlyList<string> Branches { get; init; } = [];

    public bool Equals(AttentionSegment? other) =>
        other is not null && Repository == other.Repository && Start == other.Start && End == other.End
        && Prompts.SequenceEqual(other.Prompts) && Branches.SequenceEqual(other.Branches);

    public override int GetHashCode() => HashCode.Combine(Repository, Start, End, Prompts.Count, Branches.Count);
}

public static class AttentionTimeline
{
    /// <summary>Attention after a prompt lasts until the next prompt anywhere, but no longer than this.</summary>
    public const long IdleCap = 15 * 60 * 1000;

    /// <summary>
    /// The person's attention within [<paramref name="from"/>, <paramref name="to"/>): each prompt until the next one
    /// anywhere, at most <see cref="IdleCap"/>, with a repository's consecutive stretches joined. Pass the touches
    /// from <see cref="IdleCap"/> before <paramref name="from"/> on, so attention carried in is counted.
    /// </summary>
    public static List<AttentionSegment> Build(IEnumerable<AgentTouch> touches, long from, long to)
    {
        var sorted = touches.OrderBy(t => t.At).ThenBy(t => t.SessionId, StringComparer.Ordinal).ToList();
        var attention = new List<AttentionSegment>();
        for (var index = 0; index < sorted.Count; index++)
        {
            var touch = sorted[index];
            var end = touch.At + IdleCap;
            if (index + 1 < sorted.Count)
            {
                end = Math.Min(end, sorted[index + 1].At);
            }

            var start = Math.Max(touch.At, from);
            end = Math.Min(end, to);
            if (start >= end)
            {
                continue;
            }

            // A prompt before the range only carries its attention in.
            long[] prompts = touch.At >= from ? [touch.At] : [];
            var branches = (touch.Branches ?? []).Distinct().Order(StringComparer.Ordinal).ToArray();
            // One repository's attention goes on across prompts, as long as its branches stay the same, so a branch
            // rule can tell two sessions on different branches apart.
            if (attention.Count > 0 && attention[^1].Repository == touch.Repository && attention[^1].Branches.SequenceEqual(branches)
                && attention[^1].End == start)
            {
                attention[^1] = attention[^1] with { End = end, Prompts = [.. attention[^1].Prompts, .. prompts] };
            }
            else
            {
                attention.Add(new AttentionSegment(touch.Repository, start, end) { Prompts = prompts, Branches = branches });
            }
        }

        return attention;
    }
}

/// <summary>A project the suggestion may name: active (not archived) and visible to the person.</summary>
public sealed record LinkableProject(Guid Id, string Name);

/// <summary>
/// Work on branches matching <paramref name="Pattern"/> in a repository goes to <paramref name="ProjectId"/>, and to
/// <paramref name="TaskId"/> when there is one: a monorepo, or maintenance and a feature billed separately. Rules
/// belong to a repository's link, in the order the person put them. As the Kit's BranchRule.
/// </summary>
/// <param name="Pattern">A branch name where <c>*</c> stands for any characters, <c>/</c> included: <c>acme/*</c>, <c>ACME-*</c>. Case doesn't matter.</param>
public sealed record BranchRule(string Pattern, Guid ProjectId, Guid? TaskId = null)
{
    /// <summary>The longest pattern a rule may have, as long as a branch name.</summary>
    public const int PatternMaxLength = 255;

    /// <summary>The most rules one repository may have.</summary>
    public const int MaxRules = 20;

    /// <summary>
    /// Whether the rule covers work on these branches: there is at least one, and every one matches. A session that
    /// also ran on a branch outside the rule isn't the rule's, as the logs can't tell which branch a prompt was on.
    /// </summary>
    public bool Covers(IReadOnlyCollection<string> branches) => branches.Count > 0 && branches.All(Matches);

    public bool Matches(string branch) => Match(Pattern, branch);

    /// <summary><paramref name="branch"/> against <paramref name="pattern"/>, ignoring case, where <c>*</c> matches any run of characters, none included.</summary>
    public static bool Match(string pattern, string branch)
    {
        var p = pattern.Trim(' ').ToLowerInvariant();
        var text = branch.ToLowerInvariant();
        if (p.Length == 0)
        {
            return false;
        }

        int i = 0, t = 0, lastStar = -1, resume = 0;
        while (t < text.Length)
        {
            if (i < p.Length && p[i] == '*')
            {
                lastStar = i;
                resume = t;
                i++;
            }
            else if (i < p.Length && p[i] == text[t])
            {
                i++;
                t++;
            }
            else if (lastStar >= 0)
            {
                // Let the last * take one more character, and try again from there.
                i = lastStar + 1;
                resume++;
                t = resume;
            }
            else
            {
                return false;
            }
        }

        while (i < p.Length && p[i] == '*')
        {
            i++;
        }

        return i == p.Length;
    }
}

/// <summary>Where a stretch of attention's work goes: a project, and the task a branch rule names.</summary>
public sealed record SplitPlacement(Guid ProjectId, Guid? TaskId = null);

/// <summary>
/// Which project each repository's work belongs to: a branch rule on the repository's link, the link the person
/// chose, links learned from how they tracked time while working in a repository, and repositories named like a
/// project, in that order. A repository the person said goes to no project has none. Keyed by identity.
/// </summary>
public sealed class RepositoryLinks(
    IReadOnlyDictionary<string, Guid> chosen,
    IReadOnlyDictionary<string, Guid> learned,
    IReadOnlyDictionary<string, IReadOnlyList<BranchRule>>? rules = null,
    IReadOnlySet<string>? noProject = null)
{
    public IReadOnlyDictionary<string, Guid> Chosen { get; } = chosen;

    public IReadOnlyDictionary<string, Guid> Learned { get; } = learned;

    /// <summary>Branch rules on the links the person chose, by repository identity, in their order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<BranchRule>> Rules { get; } = rules ?? new Dictionary<string, IReadOnlyList<BranchRule>>();

    /// <summary>Repositories the person said go to no project, by identity.</summary>
    public IReadOnlySet<string> NoProject { get; } = noProject ?? new HashSet<string>();

    /// <summary>The person's links as kept, with links learned from how they tracked time.</summary>
    public static RepositoryLinks Of(IReadOnlyCollection<AgentRepositoryLink> links, IReadOnlyDictionary<string, Guid> learned) => new(
        links.Where(l => l.ProjectId is not null).ToDictionary(l => l.Identity, l => l.ProjectId!.Value),
        learned,
        links.Where(l => l.BranchRules.Count > 0).ToDictionary(l => l.Identity, l => (IReadOnlyList<BranchRule>)l.BranchRules),
        links.Where(l => l.ProjectId is null).Select(l => l.Identity).ToHashSet());

    /// <summary>
    /// Where the work of a stretch of attention goes: the first branch rule of its repository that covers every branch
    /// its sessions were on, then the repository's project. A rule's project must be among <paramref name="projects"/> too.
    /// </summary>
    public SplitPlacement? Place(AttentionSegment segment, IReadOnlyList<LinkableProject> projects)
    {
        if (segment.Repository is not { } repository)
        {
            return null;
        }

        if (Rules.TryGetValue(repository.Identity, out var rules)
            && rules.FirstOrDefault(r => projects.Any(p => p.Id == r.ProjectId) && r.Covers(segment.Branches)) is { } rule)
        {
            return new SplitPlacement(rule.ProjectId, rule.TaskId);
        }

        return ProjectFor(repository, projects) is { } projectId ? new SplitPlacement(projectId) : null;
    }

    /// <summary>The project a repository's work goes to, if one is known. Branch rules need the branches: see <see cref="Place"/>.</summary>
    public Guid? ProjectFor(AgentRepository repository, IReadOnlyList<LinkableProject> projects)
    {
        if (NoProject.Contains(repository.Identity))
        {
            return null;
        }

        if (Chosen.TryGetValue(repository.Identity, out var chosenId) && projects.Any(p => p.Id == chosenId))
        {
            return chosenId;
        }

        if (Learned.TryGetValue(repository.Identity, out var learnedId) && projects.Any(p => p.Id == learnedId))
        {
            return learnedId;
        }

        return NamedLike(repository, projects);
    }

    /// <summary>
    /// A project whose name is the repository's, ignoring case and punctuation: "time-sheet" is "TimeSheet". One that
    /// contains it, or is contained in it, also counts when no other does.
    /// </summary>
    public static Guid? NamedLike(AgentRepository repository, IReadOnlyList<LinkableProject> projects)
    {
        var name = Normalized(repository.Name);
        if (name.Length < 3)
        {
            return null;
        }

        if (projects.FirstOrDefault(p => Normalized(p.Name) == name) is { } exact)
        {
            return exact.Id;
        }

        var close = projects.Where(p =>
        {
            var project = Normalized(p.Name);
            return project.Length >= 4 && (project.Contains(name, StringComparison.Ordinal) || name.Contains(project, StringComparison.Ordinal));
        }).ToList();
        return close.Count == 1 ? close[0].Id : null;
    }

    private static string Normalized(string text) =>
        new(text.ToLowerInvariant().Where(c => char.IsLetter(c) || char.IsNumber(c)).ToArray());

    /// <summary>An entry as learning reads it.</summary>
    public sealed record Entry(long Start, long? Stop, Guid? ProjectId, bool IsSplit);

    /// <summary>
    /// Learns links from entries on one project where attention mostly went to one repository: that repository goes
    /// with the project it was tracked on, when it was there for at least <paramref name="minimum"/> and mostly on
    /// that project. Entries whose attention was spread between repositories teach nothing; they may be the ones to split.
    /// </summary>
    public static Dictionary<string, Guid> Learn(
        IEnumerable<Entry> entries,
        IReadOnlyList<AttentionSegment> attention,
        long now,
        long minimum = 20 * 60 * 1000,
        double dominance = 0.6)
    {
        var segments = attention.Where(s => s.Repository is not null).ToList();
        var byIdentity = new Dictionary<string, Dictionary<Guid, long>>();
        foreach (var entry in entries)
        {
            if (entry.IsSplit || entry.ProjectId is not { } projectId)
            {
                continue;
            }

            var end = Math.Max(entry.Start, entry.Stop ?? now);
            var byRepository = new Dictionary<string, long>();
            foreach (var segment in segments)
            {
                var overlap = Math.Min(segment.End, end) - Math.Max(segment.Start, entry.Start);
                if (overlap > 0)
                {
                    byRepository[segment.Repository!.Identity] = byRepository.GetValueOrDefault(segment.Repository.Identity) + overlap;
                }
            }

            if (byRepository.Count == 0)
            {
                continue;
            }

            var total = byRepository.Values.Sum();
            var main = byRepository.MaxBy(r => r.Value);
            if ((double)main.Value / total < dominance)
            {
                continue;
            }

            var projects = byIdentity.TryGetValue(main.Key, out var existing) ? existing : byIdentity[main.Key] = [];
            projects[projectId] = projects.GetValueOrDefault(projectId) + main.Value;
        }

        var links = new Dictionary<string, Guid>();
        foreach (var (identity, projects) in byIdentity)
        {
            var total = projects.Values.Sum();
            var best = projects.MaxBy(p => p.Value);
            if (best.Value >= minimum && (double)best.Value / total >= dominance)
            {
                links[identity] = best.Key;
            }
        }

        return links;
    }
}

/// <summary>Where part of the time went: a project, or a repository not yet linked to one.</summary>
public sealed record SplitTarget(Guid? ProjectId, AgentRepository? Repository)
{
    public static SplitTarget Project(Guid projectId) => new(projectId, null);

    public static SplitTarget Unlinked(AgentRepository repository) => new(null, repository);
}

/// <param name="Attention">Time the agent logs place here: attention, and the time between two prompts here.</param>
/// <param name="Unattributed">Time without agent activity, which stays on the entry's own project.</param>
public sealed record SplitSuggestionPart(
    SplitTarget Target,
    IReadOnlyList<AgentRepository> Repositories,
    long Attention,
    long Unattributed,
    int Percent)
{
    /// <summary>The task a branch rule names for every piece of the part's time; null when none does, or they differ.</summary>
    public Guid? TaskId { get; init; }
}

/// <summary>One stretch of the entry, when the work moved from one project to the next rather than back and forth.</summary>
public sealed record SplitStretch(SplitTarget Target, long Start, long End);

/// <summary>Time the agent logs place with a target: an attention segment, or the time between two prompts for it.</summary>
public sealed record SplitPiece(SplitTarget Target, long Start, long End, AgentRepository Repository)
{
    /// <summary>The task a branch rule names for it.</summary>
    public Guid? TaskId { get; init; }
}

/// <summary>A prompt the person sent within the entry, and where it went.</summary>
public readonly record struct SplitPrompt(SplitTarget Target, long At);

/// <summary>How firmly the logs back a suggestion, which decides how loudly it is offered.</summary>
public enum SplitStrength
{
    /// <summary>
    /// The logs account for most of the time, and every part has prompts and time of its own: it may interrupt, with
    /// the card after a stop and a notification.
    /// </summary>
    Strong,

    /// <summary>Anything else waits quietly beside the entry.</summary>
    Weak,
}

/// <summary>How an entry's time could be split, from where agent attention went while it ran.</summary>
/// <param name="Coverage">How much of the entry the agent logs account for, from 0 to 1.</param>
/// <param name="Stretches">
/// The entry as consecutive stretches, one per part, when the work moved between projects once or a few times; null
/// when it went back and forth, so only sharing the time by percent describes it.
/// </param>
public sealed record SplitSuggestion(IReadOnlyList<SplitSuggestionPart> Parts, double Coverage, IReadOnlyList<SplitStretch>? Stretches)
{
    /// <summary>A part smaller than this share of the entry is left out as noise.</summary>
    public const double MinimumShare = 0.1;

    /// <summary>Less time placed by the agent logs inside an entry is too little to suggest anything.</summary>
    public const long MinimumAttention = 10 * 60 * 1000;

    /// <summary>A shorter stretch of one project between others is a detour, not a move to that project.</summary>
    public const long MinimumStretch = 10 * 60 * 1000;

    /// <summary>The logs must account for at least this share of the time for a suggestion to be strong.</summary>
    public const double StrongCoverage = 0.6;

    /// <summary>Each part of a strong suggestion has at least this many prompts.</summary>
    public const int StrongPrompts = 2;

    /// <summary>
    /// Each part of a strong suggestion has at least this much time; the entry's own project counts what the logs leave open.
    /// </summary>
    public const long StrongPartDuration = 10 * 60 * 1000;

    /// <summary>The time the suggestion divides.</summary>
    public long Start { get; init; }

    public long End { get; init; }

    /// <summary>What the logs place within it, in order, including targets too small to be a part.</summary>
    public IReadOnlyList<SplitPiece> Pieces { get; init; } = [];

    /// <summary>The time within it the logs don't account for, in order, which stays on the entry's own project.</summary>
    public IReadOnlyList<(long Start, long End)> Open { get; init; } = [];

    /// <summary>The person's prompts within it, in order.</summary>
    public IReadOnlyList<SplitPrompt> Prompts { get; init; } = [];

    /// <summary>The entry's own project, which keeps the time the logs leave open.</summary>
    public SplitTarget? Fallback { get; init; }

    /// <summary>How many prompts went to each target.</summary>
    public IReadOnlyDictionary<SplitTarget, int> Touches => Prompts.GroupBy(p => p.Target).ToDictionary(g => g.Key, g => g.Count());

    public SplitStrength Strength
    {
        get
        {
            var touches = Touches;
            return Coverage >= StrongCoverage && Parts.All(p =>
                touches.GetValueOrDefault(p.Target) >= StrongPrompts && p.Attention + p.Unattributed >= StrongPartDuration)
                ? SplitStrength.Strong
                : SplitStrength.Weak;
        }
    }

    private sealed class Tally(long first)
    {
        public long Attention;
        public long Unattributed;
        public long First = first;
        public readonly List<AgentRepository> Repositories = [];
        public readonly HashSet<Guid?> Tasks = [];
    }

    /// <summary>
    /// The split that agent activity suggests for the time from <paramref name="start"/> to <paramref name="end"/>,
    /// when it went to two or more projects (or unlinked repositories); null when it all went to one, or there was too
    /// little to tell. Attention goes where each prompt was. The time between two prompts for the same project goes to
    /// that project however long it was. Time the logs don't account for goes to <paramref name="fallback"/>, the
    /// project the entry was tracked on, when it has one.
    /// </summary>
    /// <param name="attention">The attention of the days the entry starts and ends on, in the person's time zone.</param>
    public static SplitSuggestion? Suggest(
        long start,
        long end,
        IReadOnlyList<AttentionSegment> attention,
        Guid? fallback,
        Func<AgentRepository, Guid?> project) =>
        Suggest(start, end, attention, fallback, Placing(project));

    /// <summary>
    /// The split for the time from <paramref name="start"/> to <paramref name="end"/>, with each stretch of attention
    /// placed by <paramref name="place"/>, which may read its branches (<see cref="RepositoryLinks.Place"/>). A part
    /// gets the task its pieces all have.
    /// </summary>
    public static SplitSuggestion? Suggest(
        long start,
        long end,
        IReadOnlyList<AttentionSegment> attention,
        Guid? fallback,
        Func<AttentionSegment, SplitPlacement?> place)
    {
        end = Math.Max(start, end);
        if (end <= start)
        {
            return null;
        }

        var pieces = PiecesWithin(start, end, attention, place);
        var attributed = pieces.Sum(p => p.End - p.Start);
        if (attributed < MinimumAttention)
        {
            return null;
        }

        var byTarget = new Dictionary<SplitTarget, Tally>();
        foreach (var piece in pieces)
        {
            if (!byTarget.TryGetValue(piece.Target, out var tally))
            {
                byTarget[piece.Target] = tally = new Tally(piece.Start);
            }

            tally.Attention += piece.End - piece.Start;
            if (!tally.Repositories.Contains(piece.Repository))
            {
                tally.Repositories.Add(piece.Repository);
            }

            tally.First = Math.Min(tally.First, piece.Start);
            tally.Tasks.Add(piece.TaskId);
        }

        // What the logs leave open, in order, to stay on the entry's project.
        var open = new List<(long Start, long End)>();
        var cursor = start;
        foreach (var piece in pieces)
        {
            if (piece.Start > cursor)
            {
                open.Add((cursor, piece.Start));
            }

            cursor = Math.Max(cursor, piece.End);
        }

        if (cursor < end)
        {
            open.Add((cursor, end));
        }

        var fallbackTarget = fallback is { } fallbackId ? SplitTarget.Project(fallbackId) : null;
        if (fallbackTarget is not null && open.Count > 0)
        {
            // Its work began with the first time left open, unless a prompt for it came earlier.
            if (!byTarget.TryGetValue(fallbackTarget, out var tally))
            {
                byTarget[fallbackTarget] = tally = new Tally(open[0].Start);
            }

            tally.Unattributed = open.Sum(o => o.End - o.Start);
            tally.First = Math.Min(tally.First, open[0].Start);
        }

        double total = byTarget.Values.Sum(t => t.Attention + t.Unattributed);
        // In the order work on each began, as the focus lane shows it.
        var kept = byTarget
            .Where(t => (t.Value.Attention + t.Value.Unattributed) / total >= MinimumShare)
            .OrderBy(t => t.Value.First)
            .ToList();
        if (kept.Count < 2 || kept.Count > TimeShare.MaxShares)
        {
            return null;
        }

        var percents = Apportion(
            kept.Select(t => (long)Math.Round((t.Value.Attention + t.Value.Unattributed) / 1000.0, MidpointRounding.AwayFromZero)).ToList(),
            100);
        var parts = kept.Select((t, i) => new SplitSuggestionPart(t.Key, t.Value.Repositories, t.Value.Attention, t.Value.Unattributed, percents[i])
        {
            TaskId = t.Value.Tasks.Count == 1 ? t.Value.Tasks.Single() : null,
        }).ToList();

        // As stretches, the time before any prompt is the entry's project, where the person said they began. Later
        // time the logs leave open continues whatever came before it, until a prompt elsewhere.
        var timeline = pieces.Select(p => ((SplitTarget?)p.Target, p.Start, p.End))
            .Concat(open.Select(o => (o.Start == start ? fallbackTarget : null, o.Start, o.End)))
            .OrderBy(t => t.Item2)
            .ToList();
        var stretches = StretchesOf(
            timeline,
            kept.Select(t => t.Key).ToHashSet(),
            kept.Where(t => t.Value.Attention > 0).Select(t => t.Key).ToHashSet(),
            start,
            end);

        return new SplitSuggestion(parts, Math.Min(1, (double)attributed / (end - start)), stretches)
        {
            Start = start,
            End = end,
            Pieces = pieces,
            Open = open,
            Prompts = PromptsWithin(start, end, attention, place),
            Fallback = fallbackTarget,
        };
    }

    /// <summary>Places attention by its repository's project alone, as before branch rules.</summary>
    internal static Func<AttentionSegment, SplitPlacement?> Placing(Func<AgentRepository, Guid?> project) =>
        segment => segment.Repository is { } repository && project(repository) is { } projectId ? new SplitPlacement(projectId) : null;

    /// <summary>
    /// The target and task of each stretch of attention in a repository, looked up once for each repository and set of
    /// branches. A folder outside a repository, e.g. a scratch folder, says nothing about which project it was for.
    /// </summary>
    private sealed class Targets(Func<AttentionSegment, SplitPlacement?> place)
    {
        private readonly Dictionary<(AgentRepository, string), (SplitTarget Target, Guid? TaskId)> found = [];

        public (SplitTarget Target, Guid? TaskId)? Of(AttentionSegment segment)
        {
            if (segment.Repository is not { } repository)
            {
                return null;
            }

            var key = (repository, string.Join('\n', segment.Branches));
            if (!found.TryGetValue(key, out var value))
            {
                var placement = place(segment);
                found[key] = value = (placement is null ? SplitTarget.Unlinked(repository) : SplitTarget.Project(placement.ProjectId), placement?.TaskId);
            }

            return value;
        }
    }

    /// <summary>The prompts within [<paramref name="start"/>, <paramref name="end"/>) sent in a repository, and where each went.</summary>
    internal static List<SplitPrompt> PromptsWithin(long start, long end, IReadOnlyList<AttentionSegment> attention, Func<AttentionSegment, SplitPlacement?> place)
    {
        var targets = new Targets(place);
        var prompts = new List<SplitPrompt>();
        foreach (var segment in attention)
        {
            var inside = segment.Prompts.Where(at => at >= start && at < end).ToList();
            if (inside.Count == 0 || targets.Of(segment) is not { } placed)
            {
                continue;
            }

            prompts.AddRange(inside.Select(at => new SplitPrompt(placed.Target, at)));
        }

        return prompts.OrderBy(p => p.At).ToList();
    }

    internal static List<SplitPiece> PiecesWithin(long start, long end, IReadOnlyList<AttentionSegment> attention, Func<AgentRepository, Guid?> project) =>
        PiecesWithin(start, end, attention, Placing(project));

    /// <summary>
    /// The time within [<paramref name="start"/>, <paramref name="end"/>) the agent logs place with a target: each
    /// attention segment, and the time between two prompts for the same target, in order.
    /// </summary>
    internal static List<SplitPiece> PiecesWithin(
        long start,
        long end,
        IReadOnlyList<AttentionSegment> attention,
        Func<AttentionSegment, SplitPlacement?> place)
    {
        var targets = new Targets(place);
        var segments = attention.OrderBy(s => s.Start).ToList();
        var placed = segments.Select(targets.Of).ToList();
        var pieces = new List<SplitPiece>();
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (placed[index] is not (SplitTarget target, var taskId))
            {
                continue;
            }

            var (overlapStart, overlapEnd) = (Math.Max(segment.Start, start), Math.Min(segment.End, end));
            if (overlapEnd > overlapStart)
            {
                pieces.Add(new SplitPiece(target, overlapStart, overlapEnd, segment.Repository!) { TaskId = taskId });
            }

            // The time between two prompts for the same target is its; its task only when both name the same.
            if (index + 1 < segments.Count && placed[index + 1] is (SplitTarget nextTarget, var nextTaskId) && nextTarget == target
                && segment.End < segments[index + 1].Start)
            {
                var (gapStart, gapEnd) = (Math.Max(segment.End, start), Math.Min(segments[index + 1].Start, end));
                if (gapEnd > gapStart)
                {
                    pieces.Add(new SplitPiece(target, gapStart, gapEnd, segment.Repository!) { TaskId = nextTaskId == taskId ? taskId : null });
                }
            }
        }

        return pieces.OrderBy(p => p.Start).ToList();
    }

    /// <summary>
    /// The timeline as one stretch per target, when each target's time is in one place once detours shorter than
    /// <see cref="MinimumStretch"/>, and time without a kept target, join a neighbouring stretch. Every target the logs
    /// place time in (<paramref name="required"/>) needs its stretch. Null when the work alternates between them.
    /// </summary>
    private static List<SplitStretch>? StretchesOf(
        List<(SplitTarget? Target, long Start, long End)> timeline,
        HashSet<SplitTarget> kept,
        HashSet<SplitTarget> required,
        long start,
        long end)
    {
        var runs = timeline.Select(t => (Target: t.Target is not null && kept.Contains(t.Target) ? t.Target : null, t.Start, t.End)).ToList();
        void Merge()
        {
            var merged = new List<(SplitTarget? Target, long Start, long End)>();
            foreach (var run in runs)
            {
                if (merged.Count > 0 && merged[^1].Target == run.Target)
                {
                    merged[^1] = merged[^1] with { End = run.End };
                }
                else
                {
                    merged.Add(run);
                }
            }

            runs = merged;
        }

        Merge();
        // Absorb unknown runs first, then the shortest detour, into a neighbour, until every run is a kept target
        // long enough to be a stretch of its own.
        while (runs.Count > 1)
        {
            var candidates = Enumerable.Range(0, runs.Count)
                .Where(i => runs[i].Target is null || runs[i].End - runs[i].Start < MinimumStretch)
                .ToList();
            if (candidates.Count == 0)
            {
                break;
            }

            var index = candidates.OrderBy(i => runs[i].Target is null ? 0 : 1).ThenBy(i => runs[i].End - runs[i].Start).First();
            var neighbour = index > 0 ? index - 1 : index + 1;
            runs[neighbour] = neighbour < index ? runs[neighbour] with { End = runs[index].End } : runs[neighbour] with { Start = runs[index].Start };
            runs.RemoveAt(index);
            Merge();
        }

        var targets = runs.Where(r => r.Target is not null).Select(r => r.Target!).ToList();
        if (runs.Count < 2 || targets.Count != runs.Count || targets.Distinct().Count() != targets.Count || !required.IsSubsetOf(targets))
        {
            return null;
        }

        // Cut at whole minutes, which is how the entries will read.
        var bounds = new List<long> { start };
        bounds.AddRange(runs.Skip(1).Select(r => Math.Clamp((long)Math.Round(r.Start / 60_000.0, MidpointRounding.AwayFromZero) * 60_000, start, end)));
        bounds.Add(end);
        if (bounds.Zip(bounds.Skip(1)).Any(pair => pair.First >= pair.Second))
        {
            return null;
        }

        return runs.Select((r, i) => new SplitStretch(r.Target!, bounds[i], bounds[i + 1])).ToList();
    }

    /// <summary>
    /// Divides <paramref name="total"/> in proportion to <paramref name="weights"/>, whole numbers that add up exactly:
    /// what rounding leaves over goes to the largest remainders, the earlier one on a tie. As the Kit's SplitMath.apportion.
    /// </summary>
    internal static int[] Apportion(IReadOnlyList<long> weights, int total)
    {
        if (weights.Count == 0)
        {
            return [];
        }

        var positive = weights.Select(w => Math.Max(0, w)).ToList();
        var sum = positive.Sum();
        var shares = sum > 0 ? positive : positive.Select(_ => 1L).ToList();
        var divisor = sum > 0 ? sum : weights.Count;
        var result = shares.Select(s => (int)(s * total / divisor)).ToArray();
        var rests = shares.Select(s => s * total % divisor).ToList();
        var left = total - result.Sum();
        foreach (var index in Enumerable.Range(0, rests.Count).OrderByDescending(i => rests[i]).ThenBy(i => i).Take(left))
        {
            result[index]++;
        }

        return result;
    }
}
