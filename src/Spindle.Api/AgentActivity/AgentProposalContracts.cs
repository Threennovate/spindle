using System.ComponentModel.DataAnnotations;
using Spindle.Api.TimeEntries;
using Spindle.Domain.AgentActivity;

namespace Spindle.Api.AgentActivity;

// What an agent sends is read as text, as the agent wrote it, so that a mistake is refused with a reason it can act on
// rather than failing the whole call: times in the person's time zone or with an offset, IDs not yet checked.

/// <summary>A finished entry an agent proposes.</summary>
public sealed record ProposedEntryRequest
{
    /// <summary>ISO 8601 with an offset, or a wall-clock time in the person's time zone, e.g. <c>2026-10-12T14:30</c>.</summary>
    [Required]
    [MaxLength(64)]
    public string Start { get; init; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string Stop { get; init; } = string.Empty;

    [MaxLength(4000)]
    public string? Description { get; init; }

    [MaxLength(64)]
    public string? ProjectId { get; init; }

    [MaxLength(64)]
    public string? TaskId { get; init; }

    /// <summary>The parts of a split, instead of one project.</summary>
    [MaxLength(10)]
    public ProposedPartRequest[]? Split { get; init; }

    /// <summary>Why, in a few words, e.g. "Commits on fix/apns between 14:02 and 15:40".</summary>
    [MaxLength(1000)]
    public string? Reason { get; init; }
}

/// <summary>A change an agent proposes to one of the person's entries; what is left out stays as it is.</summary>
public sealed record ProposedChangeRequest
{
    [Required]
    [MaxLength(64)]
    public string Id { get; init; } = string.Empty;

    [MaxLength(64)]
    public string? Start { get; init; }

    [MaxLength(64)]
    public string? Stop { get; init; }

    [MaxLength(4000)]
    public string? Description { get; init; }

    [MaxLength(64)]
    public string? ProjectId { get; init; }

    [MaxLength(64)]
    public string? TaskId { get; init; }

    [MaxLength(10)]
    public ProposedPartRequest[]? Split { get; init; }

    [MaxLength(1000)]
    public string? Reason { get; init; }
}

/// <summary>One part of a proposed split, in whole percent, with what was done on it when the parts are described one by one.</summary>
public sealed record ProposedPartRequest
{
    [MaxLength(64)]
    public string? ProjectId { get; init; }

    public int Percent { get; init; }

    [MaxLength(64)]
    public string? TaskId { get; init; }

    [MaxLength(4000)]
    public string? Description { get; init; }
}

public sealed record ProposeEntriesRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(AgentProposalRules.Limit)]
    public ProposedEntryRequest[] Entries { get; init; } = [];

    /// <summary>Who proposes them, as the agent names itself, e.g. "claude-code".</summary>
    [MaxLength(AgentProposal.ClientMaxLength)]
    public string? Client { get; init; }
}

public sealed record ProposeChangesRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(AgentProposalRules.Limit)]
    public ProposedChangeRequest[] Changes { get; init; } = [];

    [MaxLength(AgentProposal.ClientMaxLength)]
    public string? Client { get; init; }
}

/// <summary>Something asked for that wasn't proposed: its place in the list, the entry it named, and why, for the agent.</summary>
public sealed record ProposalRefusalResponse(int Index, string? Id, string Reason);

/// <summary>What a call to propose kept and what it refused.</summary>
public sealed record ProposalPlanResponse(IReadOnlyList<AgentProposalResponse> Proposals, IReadOnlyList<ProposalRefusalResponse> Refused);

/// <summary>Where proposed time goes: one project and task, or the parts of a split.</summary>
public sealed record ProposedWorkResponse(Guid? ProjectId, Guid? TaskId, bool IsBillable, IReadOnlyList<TimeShareResponse> Shares)
{
    public static ProposedWorkResponse From(ProposedWork work) => new(work.ProjectId, work.TaskId, work.IsBillable, TimeShareResponse.From(work.Shares));
}

/// <summary>What a proposal sets; what is null stays as the entry has it.</summary>
public sealed record ProposedFieldsResponse(DateTimeOffset? Start, DateTimeOffset? Stop, string? Description, ProposedWorkResponse? Work)
{
    public static ProposedFieldsResponse From(ProposedFields fields) =>
        new(fields.Start, fields.Stop, fields.Description, fields.Work is null ? null : ProposedWorkResponse.From(fields.Work));
}

/// <summary>An entry as it was when a change to it was proposed.</summary>
public sealed record ProposedEntrySnapshotResponse(
    DateTimeOffset Start,
    DateTimeOffset? Stop,
    string Description,
    Guid? ProjectId,
    Guid? TaskId,
    bool IsBillable,
    IReadOnlyList<TimeShareResponse> Shares)
{
    public static ProposedEntrySnapshotResponse From(ProposedEntrySnapshot entry) =>
        new(entry.Start, entry.Stop, entry.Description, entry.ProjectId, entry.TaskId, entry.IsBillable, TimeShareResponse.From(entry.Shares));
}

/// <summary>
/// A new entry or a change an agent proposed, waiting for the person. <see cref="Saves"/> is the entry saving it
/// would store now, for showing it and for filling the entry editor; <see cref="Blocking"/> says what keeps it from
/// being saved as it is, and <see cref="Warnings"/> what the person should know first.
/// </summary>
public sealed record AgentProposalResponse(
    Guid Id,
    Guid WorkspaceId,
    Guid? EntryId,
    ProposedEntrySnapshotResponse? Original,
    ProposedFieldsResponse Fields,
    string Reason,
    string? Client,
    DateTimeOffset ProposedAt,
    DateTimeOffset ExpiresAt,
    TimeEntryRequest? Saves,
    IReadOnlyList<string> Blocking,
    IReadOnlyList<string> Warnings)
{
    public static AgentProposalResponse From(AgentProposal proposal, TimeEntryRequest? saves = null, IReadOnlyList<string>? blocking = null, IReadOnlyList<string>? warnings = null) => new(
        proposal.Id,
        proposal.WorkspaceId,
        proposal.EntryId,
        proposal.Original is null ? null : ProposedEntrySnapshotResponse.From(proposal.Original),
        ProposedFieldsResponse.From(proposal.Fields),
        proposal.Reason,
        proposal.Client,
        proposal.ProposedAt,
        proposal.ProposedAt + AgentProposal.Lifetime,
        saves,
        blocking ?? [],
        warnings ?? []);
}

/// <summary>What became of a proposal: <c>added</c>, <c>changed</c>, <c>discarded</c> or <c>expired</c>.</summary>
public sealed record ProposalOutcomeResponse(Guid ProposalId, string Outcome, Guid? EntryId, DateTimeOffset At);

/// <summary>The proposals waiting in the workspace, oldest first, and what became of those dealt with in the last week, newest first.</summary>
public sealed record AgentProposalsResponse(IReadOnlyList<AgentProposalResponse> Pending, IReadOnlyList<ProposalOutcomeResponse> Outcomes);

/// <summary>
/// The person's answer to a proposal they dealt with in the entry editor: <c>saved</c> (after editing it, with the
/// entry saved) or <c>discarded</c>.
/// </summary>
public sealed record ResolveAgentProposalRequest
{
    [Required]
    [RegularExpression("^(saved|discarded)$")]
    public string Outcome { get; init; } = string.Empty;

    /// <summary>The entry saved, for <c>saved</c>.</summary>
    public Guid? EntryId { get; init; }
}

public sealed record WithdrawAgentProposalsRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(AgentProposalRules.Limit)]
    public Guid[] Ids { get; init; } = [];
}

/// <summary>The proposals taken back; those not listed were no longer waiting.</summary>
public sealed record WithdrawAgentProposalsResponse(IReadOnlyList<Guid> Withdrawn);

/// <summary>A proposal saved: the entry it became.</summary>
public sealed record AppliedAgentProposalResponse(Guid ProposalId, TimeEntryResponse Entry);
