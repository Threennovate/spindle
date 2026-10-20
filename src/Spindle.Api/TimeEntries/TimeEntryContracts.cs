using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Spindle.Domain.TimeEntries;

namespace Spindle.Api.TimeEntries;

/// <param name="Version">
/// Changes whenever the entry does. Send it back as <c>If-Match: "version"</c> when changing, stopping or deleting the
/// entry, so the write fails with 412 instead of overwriting a change made elsewhere since. Opaque: compare it, don't
/// order it.
/// </param>
public sealed record TimeEntryResponse(
    Guid Id,
    Guid WorkspaceId,
    string Description,
    Guid? ProjectId,
    Guid? TaskId,
    IReadOnlyList<Guid> TagIds,
    bool IsBillable,
    DateTimeOffset Start,
    DateTimeOffset? Stop,
    IReadOnlyList<TimeShareResponse> Shares,
    string Version)
{
    public static TimeEntryResponse From(TimeEntry entry) => new(
        entry.Id,
        entry.WorkspaceId,
        entry.Description,
        entry.ProjectId,
        entry.TaskId,
        entry.TagIds,
        entry.IsBillable,
        entry.Start,
        entry.Stop,
        TimeShareResponse.From(entry.Shares),
        entry.Version.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// One part of a split entry's time. A split entry's own project, task and billable flag are those of its largest
/// part, for apps that show a single project. <see cref="Description"/> is the part's own, when each part has one; the
/// entry's description is then made of them.
/// </summary>
public sealed record TimeShareResponse(Guid? ProjectId, Guid? TaskId, bool IsBillable, int Percent, string? Description)
{
    public static IReadOnlyList<TimeShareResponse> From(IEnumerable<TimeShare> shares) =>
        shares.Select(s => new TimeShareResponse(s.ProjectId, s.TaskId, s.IsBillable, s.Percent, s.Description)).ToList();
}

/// <summary>One part of a split entry's time, in whole percent.</summary>
public sealed record TimeShareRequest
{
    public Guid? ProjectId { get; init; }

    public Guid? TaskId { get; init; }

    /// <summary>Defaults to the project's billable setting.</summary>
    public bool? IsBillable { get; init; }

    [Range(1, 99)]
    public int Percent { get; init; }

    /// <summary>What was done on this part, when <see cref="TimeEntryRequest.DescribeParts"/> is true.</summary>
    [StringLength(TimeEntry.DescriptionMaxLength)]
    public string? Description { get; init; }
}

/// <summary>A time entry's full state. Without <see cref="Stop"/> the timer runs.</summary>
public record TimeEntryRequest
{
    [StringLength(TimeEntry.DescriptionMaxLength)]
    public string? Description { get; init; }

    public Guid? ProjectId { get; init; }

    public Guid? TaskId { get; init; }

    [MaxLength(100)]
    public Guid[]? TagIds { get; init; }

    /// <summary>Defaults to the project's billable setting.</summary>
    public bool? IsBillable { get; init; }

    /// <summary>
    /// Splits the time between two or more projects, in parts that add up to 100 percent; the project, task and
    /// billable flag are then left out. Send an empty list for one project. Left out entirely, by apps that predate
    /// splits, an entry's split is kept as long as its project and task stay as they were.
    /// </summary>
    [MaxLength(TimeShare.MaxShares)]
    public TimeShareRequest[]? Shares { get; init; }

    /// <summary>
    /// True when each part of a split has its own description, which its project's reports and timesheets show instead
    /// of the entry's; <see cref="Description"/> is then made of them. False for one description for all parts. Left
    /// out, by apps that predate it, the parts keep their own descriptions as long as the entry's description is
    /// sent as it was.
    /// </summary>
    public bool? DescribeParts { get; init; }

    [Required]
    public DateTimeOffset? Start { get; init; }

    public DateTimeOffset? Stop { get; init; }
}

public sealed record CreateTimeEntryRequest : TimeEntryRequest
{
    /// <summary>
    /// Optional ID chosen by the client, so an entry created offline keeps its ID. Sending the same create again
    /// returns the entry that was stored the first time.
    /// </summary>
    public Guid? Id { get; init; }
}

public sealed record StopTimeEntryRequest
{
    /// <summary>Defaults to now.</summary>
    public DateTimeOffset? Stop { get; init; }
}

/// <summary>A previous entry offered while typing a description, to start it again in one step.</summary>
public sealed record TimeEntrySuggestion(
    string Description,
    Guid? ProjectId,
    Guid? TaskId,
    IReadOnlyList<Guid> TagIds,
    bool IsBillable,
    IReadOnlyList<TimeShareResponse> Shares);

/// <summary>
/// Changes several entries at once. Fields left out stay as they are on each entry; tags are added and removed
/// rather than replaced, so entries keep their other tags.
/// </summary>
public sealed record BulkUpdateTimeEntriesRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(500)]
    public Guid[] Ids { get; init; } = [];

    [StringLength(TimeEntry.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>The project and task to set, which ends any split; leave out to keep each entry's projects.</summary>
    public ProjectChoice? Project { get; init; }

    /// <summary>Applies to every part of a split entry.</summary>
    public bool? IsBillable { get; init; }

    [MaxLength(100)]
    public Guid[]? AddTagIds { get; init; }

    [MaxLength(100)]
    public Guid[]? RemoveTagIds { get; init; }
}

/// <summary>A project and optionally one of its tasks. A null project means "no project".</summary>
public sealed record ProjectChoice(Guid? ProjectId, Guid? TaskId);

public sealed record BulkDeleteTimeEntriesRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(500)]
    public Guid[] Ids { get; init; } = [];
}

/// <summary>
/// The answer (412, type <c>urn:spindle:problem:entry-changed</c>) when a write's <c>If-Match</c> names a version the
/// entry no longer has: it changed elsewhere since the client saw it. <see cref="Current"/> is the entry as it is now,
/// so the client can show both and let the person choose.
/// </summary>
public sealed record TimeEntryChangedProblem(string Type, string Title, int Status, string? Detail, TimeEntryResponse Current);
