namespace Spindle.Core;

/// <summary>One part of a proposed split, with what was done on it when the parts are described one by one.</summary>
public sealed record ProposalPart(Guid? ProjectId, Guid? TaskId, bool IsBillable, int Percent, string? Description = null)
{
    /// <summary>The same project and task: the same part of a split, whatever its share.</summary>
    public bool IsSameWork(ProposalPart other) => ProjectId == other.ProjectId && TaskId == other.TaskId;
    public static ProposalPart From(TimeShareResponse share) => new(share.ProjectId, share.TaskId, share.IsBillable, share.Percent, share.Description);
    public TimeShareRequest Request() => new() { ProjectId = ProjectId, TaskId = TaskId, IsBillable = IsBillable, Percent = Percent, Description = Description };
}

/// <summary>Where the time goes: one project (and task), or parts of several.</summary>
public sealed record ProposalWork(Guid? ProjectId, Guid? TaskId, bool IsBillable, ProposalPart[] Shares)
{
    /// <summary>The time on one project, not split.</summary>
    public static ProposalWork One(Guid? projectId, Guid? taskId, bool isBillable) => new(projectId, taskId, isBillable, []);

    /// <summary>Two or more parts when split, otherwise the one part that is the whole entry.</summary>
    public ProposalPart[] Parts => Shares.Length > 1 ? Shares : [new(ProjectId, TaskId, IsBillable, 100)];

    public bool SameAs(ProposalWork? other) => other is not null && ProjectId == other.ProjectId && TaskId == other.TaskId
        && IsBillable == other.IsBillable && Shares.SequenceEqual(other.Shares);
}

/// <summary>What a proposal sets; null leaves that part of the entry as it is. A new entry has all of them.</summary>
public sealed record ProposalFields(DateTimeOffset? Start = null, DateTimeOffset? Stop = null, string? Description = null, ProposalWork? Work = null)
{
    public bool IsEmpty => Start is null && Stop is null && Description is null && Work is null;

    public bool SameAs(ProposalFields other) => Start == other.Start && Stop == other.Stop && Description == other.Description
        && (Work is null ? other.Work is null : Work.SameAs(other.Work));
}

/// <summary>
/// Entry or change an agent proposes through the server's MCP, waiting there until the person adds it or throws it away.
/// Agents may write descriptions directly, but time, projects and splits are the person's to confirm, so a new entry
/// or a change to those is only ever a proposal. As the Mac's AgentProposal.
/// </summary>
public sealed record AgentProposal
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public Guid WorkspaceId { get; init; }
    /// <summary>The entry to change, or null for a new entry.</summary>
    public Guid? EntryId { get; init; }
    /// <summary>The entry as it was when the change was proposed, to tell whether it changed since.</summary>
    public TimeEntryResponse? Original { get; init; }
    public ProposalFields Fields { get; init; } = new();
    /// <summary>Why the agent proposes it, in its words, e.g. "Commits on fix/apns between 14:02 and 15:40".</summary>
    public string Reason { get; init; } = "";
    /// <summary>Who proposed it, as the agent named itself.</summary>
    public string? Client { get; init; }
    public DateTimeOffset ProposedAt { get; init; }

    public bool IsNew => EntryId is null;

    /// <summary>
    /// What saving the proposal sends: the entry as it is now with the proposed parts, or the new entry. A split whose
    /// parts have their own descriptions is saved with them, the entry's description made of them; one description
    /// proposed for a split is one for all of its parts again.
    /// </summary>
    public TimeEntryRequest Request(TimeEntryResponse? current)
    {
        var request = current is null
            ? new TimeEntryRequest { Description = "", TagIds = [], IsBillable = false, Start = Fields.Start ?? DateTimeOffset.UtcNow, Stop = Fields.Stop, Shares = [] }
            : new TimeEntryRequest
            {
                Description = current.Description, ProjectId = current.ProjectId, TaskId = current.TaskId, TagIds = current.TagIds,
                IsBillable = current.IsBillable, Start = current.Start, Stop = current.Stop,
                Shares = current.Shares.Length > 1 ? current.Shares.Select(s => ProposalPart.From(s).Request()).ToArray() : [],
            };
        if (Fields.Start is { } start) request = request with { Start = start };
        if (Fields.Stop is { } stop) request = request with { Stop = stop };
        if (Fields.Description is { } description)
            request = request with { Description = description, Shares = request.Shares!.Select(s => s with { Description = null }).ToArray() };
        if (Fields.Work is { } work)
        {
            request = work.Shares.Length > 1
                ? Splitting(request with { Shares = work.Shares.Select(s => s.Request()).ToArray() }, work.Shares)
                : request with { ProjectId = work.ProjectId, TaskId = work.TaskId, IsBillable = work.IsBillable, Shares = [] };
        }
        if (request.Shares!.Length > 1)
        {
            var described = request.Shares.Any(s => s.Description is not null);
            request = request with { DescribeParts = described };
            if (described)
            {
                var shares = request.Shares.Select(s => s with { Description = s.Description ?? "" }).ToArray();
                request = request with { Shares = shares, Description = SplitMath.JoinDescriptions(shares.Select(s => s.Description)) };
            }
        }
        return request;
    }

    /// <summary>The request with these parts, the entry's own project, task and billable those of the largest part: the
    /// largest, the first of equal ones, as the server keeps it.</summary>
    private static TimeEntryRequest Splitting(TimeEntryRequest request, IReadOnlyList<ProposalPart> parts)
    {
        var largest = parts.First(p => p.Percent == parts.Max(q => q.Percent));
        return request with { ProjectId = largest.ProjectId, TaskId = largest.TaskId, IsBillable = largest.IsBillable };
    }
}
