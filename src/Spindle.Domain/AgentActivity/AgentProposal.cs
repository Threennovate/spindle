using Spindle.Domain.TimeEntries;

namespace Spindle.Domain.AgentActivity;

/// <summary>Where proposed time goes: one project (and task), or the parts of a split.</summary>
/// <param name="Shares">Two or more parts when split, with descriptions when each part has its own; otherwise empty.</param>
public sealed record ProposedWork(Guid? ProjectId, Guid? TaskId, bool IsBillable, TimeShare[] Shares)
{
    /// <summary>The parts of a split, or the one part that is the whole entry.</summary>
    public IReadOnlyList<TimeShare> Parts => TimeShare.PartsOf(Shares, ProjectId, TaskId, IsBillable);

    public bool SameAs(ProposedWork? other) => other is not null && ProjectId == other.ProjectId && TaskId == other.TaskId
        && IsBillable == other.IsBillable && Shares.SequenceEqual(other.Shares);
}

/// <summary>What a proposal sets; null leaves that part of the entry as it is. A new entry has all of them.</summary>
public sealed record ProposedFields(DateTimeOffset? Start = null, DateTimeOffset? Stop = null, string? Description = null, ProposedWork? Work = null)
{
    public bool IsEmpty => Start is null && Stop is null && Description is null && Work is null;
}

/// <summary>An entry as it was when a change to it was proposed, to tell whether it changed since.</summary>
public sealed record ProposedEntrySnapshot(
    DateTimeOffset Start,
    DateTimeOffset? Stop,
    string Description,
    Guid? ProjectId,
    Guid? TaskId,
    bool IsBillable,
    TimeShare[] Shares)
{
    public static ProposedEntrySnapshot Of(TimeEntry entry) =>
        new(entry.Start, entry.Stop, entry.Description, entry.ProjectId, entry.TaskId, entry.IsBillable, entry.Shares);

    public bool SameAs(TimeEntry entry) => Start == entry.Start && Stop == entry.Stop && Description == entry.Description
        && ProjectId == entry.ProjectId && TaskId == entry.TaskId && IsBillable == entry.IsBillable && Shares.SequenceEqual(entry.Shares);
}

public enum AgentProposalStatus
{
    Pending,
    Added,
    Changed,
    Discarded,
    Withdrawn,
}

/// <summary>
/// A new entry, or a change to one, that the person's agent proposed, waiting for the person to add it, edit it or
/// throw it away. Agents may write descriptions directly, but time, projects and splits are the person's to confirm,
/// so those only ever arrive as proposals. A proposal is the person's own, like their agent activity: nobody else sees
/// it, and rows are deleted for good once they are old.
/// </summary>
public sealed class AgentProposal
{
    public const int ReasonMaxLength = 300;
    public const int ClientMaxLength = 100;

    /// <summary>How long a proposal waits before it expires, and how long what became of it is remembered.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkspaceId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The entry to change, or null for a new entry.</summary>
    public Guid? EntryId { get; set; }

    /// <summary>The entry as it was when the change was proposed; null for a new entry.</summary>
    public ProposedEntrySnapshot? Original { get; set; }

    public ProposedFields Fields { get; set; } = new();

    /// <summary>Why the agent proposes it, in its words, e.g. "Commits on fix/apns between 14:02 and 15:40".</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Who proposed it, as the agent named itself.</summary>
    public string? Client { get; set; }

    public DateTimeOffset ProposedAt { get; set; }

    public AgentProposalStatus Status { get; set; }

    /// <summary>When the person (or the agent, withdrawing it) dealt with it.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>The entry the person saved from it.</summary>
    public Guid? ResultEntryId { get; set; }

    public bool IsNew => EntryId is null;

    public bool IsExpired(DateTimeOffset now) => Status == AgentProposalStatus.Pending && now - ProposedAt > Lifetime;

    public bool IsWaiting(DateTimeOffset now) => Status == AgentProposalStatus.Pending && !IsExpired(now);

    public void Resolve(AgentProposalStatus status, Guid? entryId, DateTimeOffset now)
    {
        Status = status;
        ResultEntryId = entryId;
        ResolvedAt = now;
    }

    public void Reopen()
    {
        Status = AgentProposalStatus.Pending;
        ResultEntryId = null;
        ResolvedAt = null;
    }
}
