using Spindle.Core;

namespace Spindle.Windows.Model;

/// <summary>A client's projects, or the projects without a client.</summary>
public sealed record ProjectGroup(ClientResponse? Client, ProjectResponse[] Projects);

/// <summary>
/// One part of an entry's time: the whole entry, or a share of a split. <see cref="Description"/> is what was done on
/// it when each part of a split is described on its own, so its project's reports and timesheet links show only that;
/// null when the parts share the entry's description.
/// </summary>
public sealed record SplitPart(Guid? ProjectId, Guid? TaskId, bool IsBillable, int Percent, string? Description = null)
{
    public TimeShareRequest Request() => new() { ProjectId = ProjectId, TaskId = TaskId, IsBillable = IsBillable, Percent = Percent, Description = Description };
    public static SplitPart From(TimeShareResponse share) => new(share.ProjectId, share.TaskId, share.IsBillable, share.Percent, share.Description);
}

/// <summary>A workspace's projects, tasks, clients and tags, with lookups by ID, as the Mac's catalog.</summary>
public sealed class Catalog
{
    public static readonly string[] Colors =
    [
        "#0B83D9", "#9E5BD9", "#D94182", "#E36A00", "#BF7000", "#2DA608", "#06A893", "#C9806B",
        "#465BB3", "#990099", "#C7AF14", "#566614", "#D92B2B", "#525266", "#991102",
    ];

    private readonly Dictionary<Guid, ProjectResponse> projectsById;
    private readonly Dictionary<Guid, TaskResponse> tasksById;
    private readonly Dictionary<Guid, ClientResponse> clientsById;
    private readonly Dictionary<Guid, TagResponse> tagsById;

    public Catalog(ProjectResponse[] projects, ClientResponse[] clients, TagResponse[] tags)
    {
        Projects = projects;
        Clients = clients;
        Tags = tags;
        projectsById = projects.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        tasksById = projects.SelectMany(p => p.Tasks).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        clientsById = clients.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        tagsById = tags.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
    }

    public static Catalog Empty { get; } = new([], [], []);

    public ProjectResponse[] Projects { get; }
    public ClientResponse[] Clients { get; }
    public TagResponse[] Tags { get; }

    public ProjectResponse? Project(Guid? id) => id is { } value && projectsById.TryGetValue(value, out var project) ? project : null;
    public TaskResponse? Task(Guid? id) => id is { } value && tasksById.TryGetValue(value, out var task) ? task : null;
    public ClientResponse? Client(Guid? id) => id is { } value && clientsById.TryGetValue(value, out var client) ? client : null;
    public TagResponse? Tag(Guid id) => tagsById.GetValueOrDefault(id);

    public TagResponse[] SortedTags => Tags.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    /// <summary>Projects to choose from, by client: those without a client first, then clients A to Z; archived ones left out but the chosen one.</summary>
    public ProjectGroup[] ProjectGroups(Guid? keeping = null) => Projects
        .Where(p => p.ArchivedAt is null || p.Id == keeping)
        .GroupBy(p => p.ClientId)
        .Select(g => new ProjectGroup(Client(g.Key), g.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()))
        .OrderBy(g => g.Client is null ? 0 : 1)
        .ThenBy(g => g.Client?.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    /// <summary>"Website · Design", for a part's name.</summary>
    public string Name(SplitPart part)
    {
        if (Project(part.ProjectId) is not { } project) return "No project";
        return Task(part.TaskId) is { } task ? $"{project.Name} · {task.Name}" : project.Name;
    }

    /// <summary>"Website 60%, Research 40%".</summary>
    public string Describe(IEnumerable<SplitPart> parts) => string.Join(", ", parts.Select(p => $"{Name(p)} {p.Percent}%"));

    public string? ColorOf(SplitPart part) => Project(part.ProjectId)?.Color;

    /// <summary>A colour for a new project, varied so consecutive projects are told apart.</summary>
    public string NextColor => Colors[Projects.Length % Colors.Length];
}

/// <summary>An entry's details as the parts of its time: one part, or two or more for a split.</summary>
public static class EntryParts
{
    public static bool IsSplit(this TimeEntryResponse entry) => entry.Shares.Length > 1;
    public static bool IsRunning(this TimeEntryResponse entry) => entry.Stop is null;

    public static SplitPart[] Parts(this TimeEntryResponse entry) => entry.IsSplit()
        ? entry.Shares.Select(SplitPart.From).ToArray()
        : [new SplitPart(entry.ProjectId, entry.TaskId, entry.IsBillable, 100)];

    public static bool AnyBillable(this TimeEntryResponse entry) => entry.Parts().Any(p => p.IsBillable);

    public static double Seconds(this TimeEntryResponse entry, DateTimeOffset now) =>
        Math.Max(0, ((entry.Stop ?? now) - entry.Start).TotalSeconds);

    public static TimeEntryRequest Request(this TimeEntryResponse entry) => new()
    {
        Description = entry.Description,
        ProjectId = entry.ProjectId,
        TaskId = entry.TaskId,
        TagIds = entry.TagIds,
        IsBillable = entry.IsBillable,
        Start = entry.Start,
        Stop = entry.Stop,
        // Without describeParts the server keeps the parts' own descriptions while the entry's description is unchanged,
        // and makes a changed one the description of all of them, as for apps that predate them.
        Shares = entry.Shares.Select(s => new TimeShareRequest { ProjectId = s.ProjectId, TaskId = s.TaskId, IsBillable = s.IsBillable, Percent = s.Percent, Description = s.Description }).ToArray(),
    };

    /// <summary>A new entry with the same details, as Continue and Duplicate start it.</summary>
    public static CreateTimeEntryRequest Continuing(this TimeEntryResponse entry, DateTimeOffset start) =>
        Copying(entry.Description, entry.ProjectId, entry.TaskId, entry.TagIds, entry.IsBillable, entry.Shares) with { Id = Guid.NewGuid(), Start = start };

    /// <summary>A new entry with a suggestion's details, as picking it in the timer starts it.</summary>
    public static CreateTimeEntryRequest Starting(this TimeEntrySuggestion suggestion) =>
        Copying(suggestion.Description, suggestion.ProjectId, suggestion.TaskId, suggestion.TagIds, suggestion.IsBillable, suggestion.Shares);

    /// <summary>
    /// A new entry with an earlier one's details. A split keeps each part's own description and says whether they are
    /// described one by one, as the server's MCP continue_entry does: otherwise the server gives every part the joined
    /// description, and one project's reports and timesheet links would show what was done on the others.
    /// </summary>
    private static CreateTimeEntryRequest Copying(string description, Guid? projectId, Guid? taskId, Guid[] tagIds, bool isBillable, TimeShareResponse[] shares)
    {
        var split = shares.Length > 1;
        return new()
        {
            Description = description,
            ProjectId = projectId,
            TaskId = taskId,
            TagIds = tagIds,
            IsBillable = isBillable,
            Shares = split ? shares.Select(SplitPart.From).Select(p => p.Request()).ToArray() : null,
            DescribeParts = split ? shares.Any(s => s.Description is not null) : null,
        };
    }

    /// <summary>The seconds each part of an entry counts for, by the same rule as the server's <c>TimeShare.Divide</c>.</summary>
    public static long[] PartSeconds(this TimeEntryResponse entry, DateTimeOffset now) =>
        SplitMath.Divide(entry, (long)entry.Seconds(now)).Select(a => a.Seconds).ToArray();
}

/// <summary>The workspace's rules for entries, as the server applies them, so the app only offers what will work.</summary>
public sealed class EntryRules(WorkspaceSettingsResponse settings, bool isAdmin)
{
    public WorkspaceSettingsResponse Settings { get; } = settings;
    public bool IsAdmin { get; } = isAdmin;

    public bool CanCreateProjects => IsAdmin || !Settings.OnlyAdminsCreateProjects;
    public bool CanCreateTags => IsAdmin || !Settings.OnlyAdminsCreateTags;

    public bool RequiresTask(ProjectResponse? project) => Settings.RequireTask && project?.Tasks.Any(t => !t.IsDone) == true;

    /// <summary>
    /// What a finished entry is missing that the workspace requires, with the server's messages, in field order. Parts
    /// described one by one (<paramref name="describesParts"/>) are each checked for theirs by <see cref="MissingInParts"/>.
    /// </summary>
    public IReadOnlyList<(string Field, string Message)> Missing(string description, Guid? projectId, Guid? taskId, Guid[] tagIds, bool isSplit, ProjectResponse? project, bool describesParts = false)
    {
        var missing = new List<(string, string)>();
        if (Settings.RequireDescription && !describesParts && string.IsNullOrWhiteSpace(description)) missing.Add(("description", DescriptionRequired));
        if (!isSplit && Settings.RequireProject && projectId is null) missing.Add(("projectId", "A project is required in this workspace."));
        if (!isSplit && taskId is null && projectId is not null && RequiresTask(project)) missing.Add(("taskId", "A task is required on this project."));
        if (Settings.RequireTag && tagIds.Length == 0) missing.Add(("tagIds", "At least one tag is required in this workspace."));
        return missing;
    }

    public const string DescriptionRequired = "A description is required in this workspace.";

    /// <summary>What each part of a split is missing, by position. A part's own description is required when the parts are described one by one.</summary>
    public Dictionary<int, string> MissingInParts(IReadOnlyList<SplitPart> parts, Catalog catalog)
    {
        var missing = new Dictionary<int, string>();
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            if (Settings.RequireProject && part.ProjectId is null) missing[index] = "A project is required in this workspace.";
            else if (part.TaskId is null && part.ProjectId is not null && RequiresTask(catalog.Project(part.ProjectId))) missing[index] = "A task is required on this project.";
            else if (Settings.RequireDescription && part.Description is { } own && string.IsNullOrWhiteSpace(own)) missing[index] = DescriptionRequired;
        }
        return missing;
    }

    /// <summary>The first day this person can still change; admins can change any day.</summary>
    public DateOnly? FirstOpenDay => IsAdmin ? null : Settings.LockedBefore;
    public bool IsLocked(DateOnly day) => FirstOpenDay is { } first && day < first;
    public string LockMessage(UserTime time) => Settings.LockedBefore is { } first ? $"Time before {time.LongDay(first)} is locked. Ask an admin to change it." : "";
}
