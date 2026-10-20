namespace Spindle.Core;

/// <summary>What agents proposed, waiting on the server for the person to add, edit or discard.</summary>
public sealed partial class SpindleApiClient
{
    public Task<AgentProposalsResponse> GetAgentProposalsAsync(Guid workspaceId, CancellationToken ct = default) =>
        GetAsync<AgentProposalsResponse>($"{Workspace(workspaceId)}/agent-proposals", ct);

    /// <summary>Saves a proposal as it is, through the same checks as saving an entry; returns the entry it became.</summary>
    public Task<AppliedAgentProposalResponse> ApplyAgentProposalAsync(Guid workspaceId, Guid proposalId, CancellationToken ct = default) =>
        PostAsync<AppliedAgentProposalResponse>($"{Workspace(workspaceId)}/agent-proposals/{proposalId:D}/apply", new { }, ct);

    /// <summary>The person saved the proposal after editing it, as the entry <paramref name="entryId"/>.</summary>
    public Task SavedAgentProposalAsync(Guid workspaceId, Guid proposalId, Guid? entryId, CancellationToken ct = default) =>
        PostAsync($"{Workspace(workspaceId)}/agent-proposals/{proposalId:D}/resolve", new { outcome = "saved", entryId }, ct);

    public Task DiscardAgentProposalAsync(Guid workspaceId, Guid proposalId, CancellationToken ct = default) =>
        PostAsync($"{Workspace(workspaceId)}/agent-proposals/{proposalId:D}/resolve", new { outcome = "discarded" }, ct);

    /// <summary>Puts a proposal back to wait, once the person took back what saving it did.</summary>
    public Task ReopenAgentProposalAsync(Guid workspaceId, Guid proposalId, CancellationToken ct = default) =>
        PostAsync($"{Workspace(workspaceId)}/agent-proposals/{proposalId:D}/reopen", null, ct);
}
