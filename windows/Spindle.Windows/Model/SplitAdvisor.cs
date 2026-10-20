using Spindle.Activity;
using Spindle.Core;
using Spindle.Platform;
using Spindle.Windows.Views;

namespace Spindle.Windows.Model;

/// <summary>A suggestion turned into what the editor applies: shares by percent, or consecutive stretches.</summary>
public sealed record SplitTake(SplitPart[]? Parts, (Guid ProjectId, DateTimeOffset Start)[]? Stretches);

/// <summary>
/// What one click changed, so Undo can put it back: the entry as it was (null when only entries were added), and the
/// entries added beside it.
/// </summary>
public sealed record SavedChange(TimeEntryResponse? Before, IReadOnlyList<Guid> Added)
{
    /// <summary>
    /// What puts the entry back. One that was running keeps running, unless the timer stopped since (on it or on an
    /// entry the change added): then it keeps that stop, so Undo doesn't start the timer again.
    /// </summary>
    public TimeEntryRequest? Restoring(IEnumerable<TimeEntryResponse> now)
    {
        if (Before is not { } before) return null;
        var request = before.Request();
        var since = now.Where(e => e.Id == before.Id || Added.Contains(e.Id)).ToList();
        if (before.Stop is null && since.Count > 0 && since.All(e => e.Stop is not null))
            request = request with { Stop = since.Max(e => e.Stop!.Value) };
        return request;
    }
}

/// <summary>A suggestion taken with one click: what to say about it, and what changed, to undo it.</summary>
public sealed record SplitTaken(string Notice, SavedChange Change);

/// <summary>A split entry's work changed after its split was set (<see cref="Statement"/>, its parts then).</summary>
public sealed record DriftOffer(TimeEntryResponse Entry, SplitDrift Drift, string Statement);

/// <summary>
/// Offers to split entries whose time went to several projects at once, from where agent attention went while they
/// ran, as the Mac's SplitAdvisor. Repositories are linked to projects as the person chose, as learned from how they
/// tracked, or by name. The logs stay on this PC; the links chosen and the entries kept as they are go to the account.
/// Nothing changes until the person says so.
/// </summary>
public sealed partial class SplitAdvisor
{
    private static readonly TimeSpan TodayRereadAfter = TimeSpan.FromMinutes(3);
    private readonly MainWindow owner;
    private readonly Dictionary<DateOnly, (ActivitySnapshot Snapshot, DateTimeOffset ReadAt)> days = [];
    private readonly Dictionary<string, Guid> chosen = [];
    private readonly Dictionary<string, IReadOnlyList<BranchRule>> rules = [];
    /// <summary>Repositories the person said go to no project, by identity.</summary>
    private readonly HashSet<string> noProject = [];
    private readonly HashSet<Guid> dismissed = [];
    private Guid? accountFor;
    private (int Revision, Dictionary<string, Guid> Links)? learned;

    public SplitAdvisor(MainWindow owner) => this.owner = owner;

    /// <summary>When the app started: a running entry first seen after this was seen from its start.</summary>
    public DateTimeOffset Launched { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Changes when newly read activity or a new link may change the suggestions.</summary>
    public int Revision { get; private set; }
    public event Action? Changed;

    private bool Enabled => owner.AgentLogsEnabled;

    public void Clear()
    {
        days.Clear();
        chosen.Clear();
        rules.Clear();
        noProject.Clear();
        unsent.Clear();
        names.Clear();
        dismissed.Clear();
        accountFor = null;
        learned = null;
        Revision++;
    }

    /// <summary>Drops the activity read so far, e.g. once other agents' logs are read; links and dismissals stay.</summary>
    public void ForgetActivity()
    {
        days.Clear();
        learned = null;
        Revision++;
        Changed?.Invoke();
    }

    /// <summary>Reads the activity of these days unless it was read already; today again every few minutes.</summary>
    public async Task LoadAsync(IEnumerable<DateOnly> wanted)
    {
        if (!Enabled || owner.Api is not { } api) return;
        var changed = false;
        if (accountFor != owner.WorkspaceId)
        {
            accountFor = owner.WorkspaceId;
            try
            {
                Absorb((await api.GetRepositoryLinksAsync()).Where(l => l.WorkspaceId == owner.WorkspaceId).ToList());
                foreach (var id in await api.GetSplitSuggestionDismissalsAsync()) dismissed.Add(id);
                await SendUnsentAsync();
            }
            catch (Exception)
            {
                // Without the account's links, the learned ones and names still suggest.
            }
            changed = true;
        }
        var today = owner.Time.Today();
        foreach (var day in wanted.Distinct())
        {
            if (days.TryGetValue(day, out var read) && (day != today || DateTimeOffset.UtcNow - read.ReadAt < TodayRereadAfter)) continue;
            try
            {
                var snapshot = await owner.Activity.ReadAsync(owner.Time.StartOf(day), owner.Time.StartOf(day.AddDays(1)));
                days[day] = (snapshot, DateTimeOffset.UtcNow);
                changed = true;
            }
            catch (Exception)
            {
                days[day] = (ActivitySnapshot.Empty, DateTimeOffset.UtcNow);
            }
        }
        if (!changed) return;
        Revision++;
        Changed?.Invoke();
    }

    /// <summary>The activity read for a day, for the calendar's lane.</summary>
    public ActivitySnapshot? Day(DateOnly day) => days.TryGetValue(day, out var read) ? read.Snapshot : null;

    /// <summary>The days whose activity these entries' suggestions need.</summary>
    public IEnumerable<DateOnly> DaysOf(IEnumerable<TimeEntryResponse> entries)
    {
        var time = owner.Time;
        var since = time.Today().AddDays(-14);
        return entries.SelectMany(e => new[] { time.Day(e.Start), time.Day(e.Stop ?? DateTimeOffset.UtcNow) }).Where(d => d >= since).Distinct();
    }

    public bool IsDismissed(Guid entryId) => dismissed.Contains(entryId);

    /// <summary>The split agent activity suggests for an entry on one project, unless the person said no to it.</summary>
    public SplitSuggestion? Suggestion(TimeEntryResponse entry)
    {
        if (!Enabled || entry.IsSplit() || dismissed.Contains(entry.Id)) return null;
        return Suggestion(entry.Start, entry.Stop ?? DateTimeOffset.UtcNow, entry.ProjectId);
    }

    /// <summary>The split for a stretch of time tracked on <paramref name="projectId"/>, which keeps what the logs don't account for.</summary>
    public SplitSuggestion? Suggestion(DateTimeOffset start, DateTimeOffset end, Guid? projectId)
    {
        if (!Enabled) return null;
        var time = owner.Time;
        var first = time.Day(start);
        var last = time.Day(end);
        var segments = new List<AttentionSegment>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            if (!days.TryGetValue(day, out var read)) return null;
            segments.AddRange(read.Snapshot.Attention);
        }
        var projects = Linkable();
        var links = Links();
        return SplitSuggestion.Suggest(start.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds(), segments.Distinct().ToArray(), projectId,
            segment => links.Place(segment, projects));
    }

    private LinkableProject[] Linkable() => owner.Catalog.Projects.Where(p => p.ArchivedAt is null).Select(p => new LinkableProject(p.Id, p.Name)).ToArray();

    private RepositoryLinks Links()
    {
        if (learned is not { } cache || cache.Revision != Revision)
        {
            var attention = days.Values.SelectMany(d => d.Snapshot.Attention).Distinct().ToArray();
            var entries = owner.Entries.Select(e => new RepositoryLinks.Entry(e.Start.ToUnixTimeMilliseconds(), e.Stop?.ToUnixTimeMilliseconds(), e.ProjectId, e.IsSplit()));
            learned = cache = (Revision, RepositoryLinks.Learn(entries, attention, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
        return new RepositoryLinks(chosen, cache.Links, rules, noProject);
    }

    // When the work changes after a split

    /// <summary>The most entries whose statements are remembered, the newest kept.</summary>
    private const int StatementLimit = 20;

    /// <summary>The parts as a statement remembers them; another split starts over.</summary>
    public static string PartsKey(IEnumerable<SplitPart> parts) => string.Join(",", parts.Select(p => $"{p.ProjectId}/{p.TaskId}/{p.Percent}"));

    /// <summary>
    /// When the running entry's current split was set. A split there when the timer started counts from the start; one
    /// this PC first sees later counts from then, which only makes an offer later, never wrong. A stopped entry has the
    /// statement it had while it ran, if this PC saw it run.
    /// </summary>
    public SplitStatement? Statement(TimeEntryResponse entry, DateTimeOffset now)
    {
        var parts = PartsKey(entry.Parts());
        var statements = owner.Preferences.SplitStatements;
        if (statements.TryGetValue(entry.Id, out var known) && known.Parts == parts) return known;
        if (entry.Stop is not null) return null;
        var seen = statements.ContainsKey(entry.Id) || entry.Start < Launched ? now : entry.Start;
        var statement = new SplitStatement(parts, seen < entry.Start ? entry.Start : seen > now ? now : seen);
        State(entry.Id, statement);
        return statement;
    }

    /// <summary>Remembers when an entry's split was set, e.g. for the entry a change just started, from where it begins.</summary>
    public void State(Guid entryId, SplitStatement statement)
    {
        var next = new Dictionary<Guid, SplitStatement>(owner.Preferences.SplitStatements) { [entryId] = statement };
        owner.SaveSplitStatements(new SplitStatements(next.OrderByDescending(s => s.Value.At).Take(StatementLimit)));
    }

    /// <summary>The change was offered in a notification, once per split.</summary>
    public void MarkNotified(DriftOffer offer) => Mark(offer, s => s with { Notified = true });

    /// <summary>Nothing more is offered for this split: it was taken, looked at, or put off with "Not now".</summary>
    public void Answer(DriftOffer offer)
    {
        Mark(offer, s => s with { Answered = true });
        Revision++;
        Changed?.Invoke();
    }

    private void Mark(DriftOffer offer, Func<SplitStatement, SplitStatement> change)
    {
        if (owner.Preferences.SplitStatements.TryGetValue(offer.Entry.Id, out var statement) && statement.Parts == offer.Statement)
            State(offer.Entry.Id, change(statement));
    }

    /// <summary>How the work changed since a split entry's split was set, while nobody answered that.</summary>
    public DriftOffer? Drift(TimeEntryResponse entry, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        if (!Enabled || !entry.IsSplit() || dismissed.Contains(entry.Id) || Statement(entry, now) is not { Answered: false } statement) return null;
        var end = entry.Stop ?? now;
        var segments = new List<AttentionSegment>();
        for (var day = owner.Time.Day(entry.Start); day <= owner.Time.Day(end); day = day.AddDays(1))
        {
            if (!days.TryGetValue(day, out var read)) return null;
            segments.AddRange(read.Snapshot.Attention);
        }
        var projects = Linkable();
        var links = Links();
        var drift = SplitDrift.Detect(entry.Parts().Where(p => p.ProjectId is not null).Select(p => p.ProjectId!.Value).ToList(),
            entry.Start.ToUnixTimeMilliseconds(), statement.At.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds(),
            segments.Distinct().ToArray(), segment => links.Place(segment, projects));
        return drift is null ? null : new DriftOffer(entry, drift, statement.Parts);
    }

    /// <summary>The parts the entry continues with: without the quiet projects, or with the one that joined.</summary>
    public SplitPart[] Continuing(TimeEntryResponse entry, SplitDrift drift)
    {
        var parts = entry.Parts();
        var ids = parts.Select(p => p.ProjectId).ToList();
        var projects = drift.ContinuingProjects(ids);
        var percents = drift.ContinuingPercents(ids, parts.Select(p => p.Percent).ToList());
        var described = SplitMath.DescribesParts(entry);
        return projects.Select((id, i) => (parts.FirstOrDefault(p => p.ProjectId == id)
            ?? new SplitPart(id, null, owner.Catalog.Project(id!.Value)?.IsBillable ?? false, 0, described ? "" : null)) with { Percent = percents[i] }).ToArray();
    }

    /// <summary>
    /// Ends a split entry at the change, with its split as it is, and continues from there in a new entry on the parts
    /// that remain or with the project that joined: running when the entry was, otherwise until its stop. The new entry
    /// keeps the description, tags and the parts' own descriptions. Returns what was done, for a notice with Undo.
    /// </summary>
    public async Task<SplitTaken> EndAndContinueAsync(TimeEntryResponse entry, SplitDrift drift)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(drift.At);
        var parts = Continuing(entry, drift);
        var described = SplitMath.DescribesParts(entry);
        var main = parts.First(p => p.Percent == parts.Max(q => q.Percent));
        // One part left keeps its own text, even none: the entry's description is all the parts' joined.
        var description = parts.Length == 1 && described ? parts[0].Description?.Trim() ?? "" : entry.Description;
        var next = Guid.NewGuid();
        await owner.MutateAsync(async () =>
        {
            await owner.UpdateEntryAsync(entry, entry.Request() with { Stop = at });
            await owner.CreateEntryAsync(new CreateTimeEntryRequest
            {
                Id = next, Description = description, ProjectId = main.ProjectId, TaskId = main.TaskId, TagIds = entry.TagIds, IsBillable = main.IsBillable,
                Start = at, Stop = entry.Stop, Shares = parts.Length > 1 ? parts.Select(p => p.Request()).ToArray() : null,
                DescribeParts = parts.Length > 1 && described ? true : null,
            });
        });
        // The new entry's split was set where it begins, and nothing about it was offered yet.
        if (entry.IsRunning()) State(next, new SplitStatement(PartsKey(parts), at));
        var notice = drift.Joined is { } joined ? $"Added {owner.Catalog.Project(joined)?.Name ?? "the project"} from {owner.Time.Clock(at)}" : $"Split ended at {owner.Time.Clock(at)}";
        return new(notice, new SavedChange(entry, [next]));
    }

    /// <summary>"End split at 11:05", or "Add Comet from 11:20".</summary>
    public string Title(SplitDrift drift) => drift.Joined is { } joined
        ? $"Add {owner.Catalog.Project(joined)?.Name ?? "project"} from {Clock(drift.At)}"
        : $"End split at {Clock(drift.At)}";

    /// <summary>"Beacon has been quiet since 11:05", or "Agents have been in Comet since 11:20".</summary>
    public string Headline(SplitDrift drift) => drift.Joined is { } joined
        ? $"Agents have been in {owner.Catalog.Project(joined)?.Name ?? "another project"} since {Clock(drift.At)}"
        : $"{Names(drift.Quiet)} {(drift.Quiet.Count == 1 ? "has" : "have")} been quiet since {Clock(drift.At)}";

    /// <summary>"End the split then and continue on Atlas?", or "Add Comet to the split from then?", after the headline.</summary>
    public string Question(DriftOffer offer) => offer.Drift.Joined is { } joined
        ? $"Add {owner.Catalog.Project(joined)?.Name ?? "the project"} to the split from then?"
        : $"End the split then and continue on {Names(offer.Entry.Parts().Where(p => p.ProjectId is { } id && !offer.Drift.Quiet.Contains(id)).Select(p => p.ProjectId!.Value).ToList())}?";

    private string Clock(long at) => owner.Time.Clock(DateTimeOffset.FromUnixTimeMilliseconds(at));

    private string Names(IReadOnlyList<Guid> projects)
    {
        var names = projects.Select(id => owner.Catalog.Project(id)?.Name ?? "a project").ToList();
        return names.Count > 1 ? string.Join(", ", names[..^1]) + " and " + names[^1] : names.FirstOrDefault() ?? "";
    }

    /// <summary>The suggestion as the editor takes it: separate entries when the work moved once or a few times, otherwise shares.</summary>
    public SplitTake? Take(SplitSuggestion suggestion, Guid? taskId = null, bool? billable = null, Guid? trackedProject = null)
    {
        if (suggestion.Parts.Any(p => p.Target.ProjectId is null)) return null;
        var parts = suggestion.Parts.Select(p =>
        {
            var projectId = p.Target.ProjectId!.Value;
            var keeps = projectId == trackedProject;
            // The entry's own task stays on its project; a branch rule's task goes with the rest.
            var task = keeps && taskId is not null ? taskId : RuleTask(p);
            return new SplitPart(projectId, task, keeps && billable is { } value ? value : owner.Catalog.Project(projectId)?.IsBillable ?? false, p.Percent);
        }).ToArray();
        var stretches = suggestion.Stretches?.Select(s => (s.Target.ProjectId ?? Guid.Empty, DateTimeOffset.FromUnixTimeMilliseconds(s.Start))).ToArray();
        if (stretches is not null && stretches.Any(s => s.Item1 == Guid.Empty)) stretches = null;
        return new SplitTake(parts, stretches);
    }

    /// <summary>The task a branch rule names for a part, while it is an open task of the part's project.</summary>
    private Guid? RuleTask(SplitSuggestionPart? part) =>
        part?.TaskId is { } id && owner.Catalog.Task(id) is { IsDone: false } task && task.ProjectId == part.Target.ProjectId ? id : null;

    /// <summary>Opens the entry in the editor with the split agent activity suggests, ready to save.</summary>
    public Task ReviewAsync(TimeEntryResponse entry)
    {
        var suggestion = Suggestion(entry);
        var take = suggestion is null ? null : Take(suggestion, entry.TaskId, entry.IsBillable, entry.ProjectId);
        return owner.EditAsync(entry, start: new EditorStart(Split: take?.Stretches is null ? take?.Parts : null, Stretches: take?.Stretches));
    }

    /// <summary>
    /// Takes a suggestion with one click: separate entries cut where the work moved, the running timer switched to
    /// the project the work moved to, or one entry shared by percent. Returns what was done, for a notice with Undo.
    /// </summary>
    public async Task<SplitTaken> ApplyAsync(TimeEntryResponse entry, SplitSuggestion suggestion)
    {
        var take = Take(suggestion, entry.TaskId, entry.IsBillable, entry.ProjectId) ?? throw new InvalidOperationException("Choose a project for each repository first.");
        var catalog = owner.Catalog;
        bool Billable(Guid id) => catalog.Project(id)?.IsBillable ?? false;
        Guid? TaskOn(Guid id) => (id == entry.ProjectId ? entry.TaskId : null) ?? RuleTask(suggestion.Parts.FirstOrDefault(p => p.Target.ProjectId == id));
        if (take.Stretches is { Length: 2 } move && entry.IsRunning())
        {
            var (to, at) = move[1];
            var next = Guid.NewGuid();
            await owner.MutateAsync(async () =>
            {
                await owner.UpdateEntryAsync(entry, entry.Request() with { Stop = at });
                await owner.CreateEntryAsync(new CreateTimeEntryRequest { Id = next, Description = entry.Description, ProjectId = to, TaskId = TaskOn(to), TagIds = entry.TagIds, IsBillable = Billable(to), Start = at });
            });
            return new($"Switched to {catalog.Project(to)?.Name ?? "the other project"}", new SavedChange(entry, [next]));
        }
        if (take.Stretches is { Length: > 1 } stretches && entry.Stop is { } stop)
        {
            var added = stretches[..^1].Select(_ => Guid.NewGuid()).ToArray();
            await owner.MutateAsync(async () =>
            {
                for (var index = 0; index < stretches.Length - 1; index++)
                {
                    var (project, from) = stretches[index];
                    await owner.CreateEntryAsync(new CreateTimeEntryRequest
                    {
                        Id = added[index], Description = entry.Description, ProjectId = project, TaskId = TaskOn(project), TagIds = entry.TagIds,
                        IsBillable = Billable(project), Start = index == 0 ? entry.Start : from, Stop = stretches[index + 1].Start,
                    });
                }
                var (lastProject, lastStart) = stretches[^1];
                await owner.UpdateEntryAsync(entry, entry.Request() with { ProjectId = lastProject, TaskId = TaskOn(lastProject), IsBillable = Billable(lastProject), Start = lastStart, Stop = stop, Shares = [] });
            });
            return new($"Split into {stretches.Length} entries", new SavedChange(entry, added));
        }
        var parts = take.Parts!;
        var main = parts.First(p => p.Percent == parts.Max(q => q.Percent));
        await owner.MutateAsync(() => owner.UpdateEntryAsync(entry, entry.Request() with
        {
            ProjectId = main.ProjectId, TaskId = main.TaskId, IsBillable = main.IsBillable, Shares = parts.Select(p => p.Request()).ToArray(),
        }));
        return new("Split saved", new SavedChange(entry, []));
    }

    /// <summary>Takes back a one-click change: deletes the entries it added, then saves the entry as it was.</summary>
    public async Task UndoAsync(SavedChange change)
    {
        await owner.MutateAsync(async () =>
        {
            var restoring = change.Restoring(owner.Entries);
            foreach (var added in owner.Entries.Where(e => change.Added.Contains(e.Id)).ToList())
                await owner.DeleteEntryAsync(added);
            if (change.Before is { } before && restoring is not null)
                await owner.UpdateEntryAsync(owner.Entries.FirstOrDefault(e => e.Id == before.Id) ?? before, restoring);
        });
    }

    /// <summary>Links a repository to the project its work goes to, from now on, on every device. Its branch rules stay.</summary>
    public Task LinkAsync(AgentRepository repository, Guid projectId) => LinkAsync(repository, projectId, null);

    /// <summary>Stops suggesting a split for this entry, on every device.</summary>
    public async Task DismissAsync(TimeEntryResponse entry)
    {
        dismissed.Add(entry.Id);
        Revision++;
        Changed?.Invoke();
        if (owner.Api is { } api)
        {
            try
            {
                await api.DismissSplitSuggestionAsync(entry.WorkspaceId, entry.Id);
            }
            catch (Exception)
            {
                // Kept on this PC; the next dismissal or start syncs again.
            }
        }
    }

    /// <summary>
    /// "Atlas 1:20 · Beacon 0:40" for an entry's time ("Atlas 60% · Beacon 40%" without one), or "Atlas until 10:15,
    /// then Beacon" when the work moved.
    /// </summary>
    public string Summary(SplitSuggestion suggestion, TimeEntryResponse? entry = null)
    {
        string Name(SplitTarget target) => target.ProjectId is { } id ? owner.Catalog.Project(id)?.Name ?? "a project" : target.Repository?.Name ?? "a repository";
        if (suggestion.Stretches is { Count: > 1 } stretches)
        {
            var moves = stretches.Skip(1).Select((s, i) => $"{Name(stretches[i].Target)} until {owner.Time.Clock(DateTimeOffset.FromUnixTimeMilliseconds(s.Start))}");
            return string.Join(", ", moves) + ", then " + Name(stretches[^1].Target);
        }
        if (entry is null) return string.Join(" · ", suggestion.Parts.Select(p => $"{Name(p.Target)} {p.Percent}%"));
        var hours = Hours(suggestion, entry);
        return string.Join(" · ", suggestion.Parts.Select((p, i) => $"{Name(p.Target)} {Durations.ShortClock(hours[i])}"));
    }

    /// <summary>How much of an entry's time each part would get, in whole minutes that add up to the whole.</summary>
    public static long[] Hours(SplitSuggestion suggestion, TimeEntryResponse entry, DateTimeOffset? now = null)
    {
        var seconds = Math.Max(0, ((entry.Stop ?? now ?? DateTimeOffset.UtcNow) - entry.Start).TotalSeconds);
        return SplitMath.Allocate((long)Math.Round(seconds / 60), suggestion.Parts.Select(p => p.Percent).ToArray()).Select(m => m * 60).ToArray();
    }

    /// <summary>"Split at 10:42", "Split into 3 entries", or "Split".</summary>
    public string Title(SplitSuggestion suggestion)
    {
        if (suggestion.Stretches is { } stretches)
            return stretches.Count == 2 ? $"Split at {owner.Time.Clock(DateTimeOffset.FromUnixTimeMilliseconds(stretches[1].Start))}" : $"Split into {stretches.Count} entries";
        return "Split";
    }
}
