using Spindle.Domain.Common;

namespace Spindle.Domain.TimeEntries;

/// <summary>
/// A span of time a user spent on something. While the timer runs, <see cref="Stop"/> is null;
/// a user has at most one running entry, which the database enforces.
/// </summary>
public sealed class TimeEntry : Entity
{
    public const int DescriptionMaxLength = 3000;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(999);

    private TimeEntry()
    {
        Description = string.Empty;
        TagIds = [];
        Shares = [];
    }

    public Guid WorkspaceId { get; private set; }

    public Guid UserId { get; private set; }

    public string Description { get; private set; }

    public Guid? ProjectId { get; private set; }

    public Guid? TaskId { get; private set; }

    /// <summary>Stored in the entry's own row, so changing tags changes the entry's sync version.</summary>
    public Guid[] TagIds { get; private set; }

    public bool IsBillable { get; private set; }

    /// <summary>
    /// How the time is split between projects, when it went to several at once; empty otherwise. The project, task and
    /// billable flag above are then those of the largest part, so apps that know nothing of splits still show the
    /// entry where most of it went.
    /// </summary>
    public TimeShare[] Shares { get; private set; }

    /// <summary>Kept beside <see cref="Shares"/> so queries can tell split entries apart without reading the parts.</summary>
    public bool IsSplit { get; private set; }

    /// <summary>Where the time went: the parts of a split entry, or the whole of it on its one project.</summary>
    public IReadOnlyList<TimeShare> Parts => TimeShare.PartsOf(Shares, ProjectId, TaskId, IsBillable);

    public DateTimeOffset Start { get; private set; }

    public DateTimeOffset? Stop { get; private set; }

    public bool IsRunning => Stop is null;

    /// <summary>Starts a timer, or with <paramref name="stop"/> records a finished entry.</summary>
    public static TimeEntry Create(
        Guid? id,
        Guid workspaceId,
        Guid userId,
        TimeEntryDetails details,
        DateTimeOffset start,
        DateTimeOffset? stop)
    {
        var entry = new TimeEntry
        {
            WorkspaceId = Guard.Id(workspaceId),
            UserId = Guard.Id(userId),
        };

        if (id is { } clientId)
        {
            entry.Id = Guard.Id(clientId);
        }

        entry.SetDetails(details);
        entry.SetTimes(start, stop);
        return entry;
    }

    /// <summary>Changes a finished entry, or the running one. A finished entry cannot be made to run again.</summary>
    public void Update(TimeEntryDetails details, DateTimeOffset start, DateTimeOffset? stop)
    {
        if (!IsRunning && stop is null)
        {
            throw new DomainValidationException("stop", "A stopped time entry cannot be restarted. Continue it instead.");
        }

        SetDetails(details);
        SetTimes(start, stop);
    }

    public void StopAt(DateTimeOffset stop)
    {
        if (!IsRunning)
        {
            throw new DomainValidationException("stop", "The time entry is not running.");
        }

        SetTimes(Start, stop);
    }

    /// <summary>The project was deleted; the time stays, without a project.</summary>
    public void RemoveProject(Guid projectId) =>
        SetParts(TimeShare.Without(Parts, s => s.ProjectId == projectId ? s with { ProjectId = null, TaskId = null } : s));

    /// <summary>The task was deleted; the time stays on its project.</summary>
    public void RemoveTask(Guid taskId) =>
        SetParts(TimeShare.Without(Parts, s => s.TaskId == taskId ? s with { TaskId = null } : s));

    public void RemoveTag(Guid tagId) => TagIds = TagIds.Where(id => id != tagId).ToArray();

    private void SetDetails(TimeEntryDetails details)
    {
        Description = CheckDescription(details.Description?.Trim() ?? string.Empty);
        TagIds = details.TagIds.Distinct().ToArray();
        SetParts(details.Shares is { Count: > 1 } shares
            ? TimeShare.Check(shares)
            : [details.Shares is [var only] ? only with { Description = null } : new TimeShare(details.ProjectId, details.TaskId, details.IsBillable, 100)]);
    }

    private static string CheckDescription(string description) =>
        description.Length <= DescriptionMaxLength
            ? description
            : throw new DomainValidationException("description", $"Description must be at most {DescriptionMaxLength} characters.");

    /// <summary>
    /// Splits the time between two or more parts, or puts it all on the one part given. When the parts of a split have
    /// descriptions of their own, the entry's description is made of them, for wherever an entry shows just one.
    /// </summary>
    private void SetParts(IReadOnlyList<TimeShare> parts)
    {
        if (parts.Any(s => s.TaskId is not null && s.ProjectId is null))
        {
            throw new DomainValidationException(parts.Count > 1 ? "shares" : "taskId", "A task needs its project.");
        }

        // The first of the largest parts, so equal parts keep the order they were given in.
        var largest = parts.MaxBy(s => s.Percent)!;
        IsSplit = parts.Count > 1;
        Shares = IsSplit ? [.. parts] : [];
        if (IsSplit && TimeShare.AreDescribed(Shares))
        {
            Shares = [.. Shares.Select(s => s with { Description = s.Description?.Trim() ?? string.Empty })];
            Description = CheckDescription(TimeShare.JoinDescriptions(Shares));
        }

        ProjectId = largest.ProjectId;
        TaskId = largest.TaskId;
        IsBillable = largest.IsBillable;
    }

    private void SetTimes(DateTimeOffset start, DateTimeOffset? stop)
    {
        if (stop is { } end)
        {
            if (end < start)
            {
                throw new DomainValidationException("stop", "The end must not be before the start.");
            }

            if (end - start > MaxDuration)
            {
                throw new DomainValidationException("stop", $"A time entry can be at most {MaxDuration.TotalHours:0} hours long.");
            }
        }

        // Stored in UTC, so every client and report sees the same instant regardless of offsets.
        Start = start.ToUniversalTime();
        Stop = stop?.ToUniversalTime();
    }
}

/// <summary>
/// What an entry is about. With two or more <paramref name="Shares"/> its time is split between them and the project,
/// task and billable flag given beside them are not used; when the shares have descriptions of their own, neither is
/// <paramref name="Description"/>.
/// </summary>
public sealed record TimeEntryDetails(
    string? Description,
    Guid? ProjectId,
    Guid? TaskId,
    IReadOnlyCollection<Guid> TagIds,
    bool IsBillable,
    IReadOnlyList<TimeShare>? Shares = null);
