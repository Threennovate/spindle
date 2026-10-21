namespace Spindle.Core;

public sealed partial class SpindleApiClient
{
    public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default) => PostAsync("/api/me/password", request, ct);
    public Task<RepositoryLinkResponse[]> GetRepositoryLinksAsync(CancellationToken ct = default) => GetAsync<RepositoryLinkResponse[]>("/api/me/agent-activity/links?includeNoProject=true", ct);
    public Task<Guid[]> GetSplitSuggestionDismissalsAsync(CancellationToken ct = default) => GetAsync<Guid[]>("/api/me/agent-activity/dismissals", ct);
    /// <summary>The repositories the person ignores, as the hashes their devices sent (<c>IgnoredRepositories.Hash</c>).</summary>
    public Task<string[]> GetIgnoredRepositoriesAsync(CancellationToken ct = default) => GetAsync<string[]>("/api/me/agent-activity/ignored", ct);
    /// <summary>Ignores a repository on all the person's devices, by its hash alone; the server deletes what it had of it.</summary>
    public async Task IgnoreRepositoryAsync(string hash, CancellationToken ct = default)
    { using var response = await SendAsync(HttpMethod.Put, $"/api/me/agent-activity/ignored/{Uri.EscapeDataString(hash)}", null, ct); }
    public async Task UnignoreRepositoryAsync(string hash, CancellationToken ct = default)
    { using var response = await SendAsync(HttpMethod.Delete, $"/api/me/agent-activity/ignored/{Uri.EscapeDataString(hash)}", null, ct); }
    public async Task LinkRepositoryAsync(Guid workspaceId, RepositoryLinkRequest request, CancellationToken ct = default)
    { using var response = await SendAsync(HttpMethod.Put, $"{Workspace(workspaceId)}/agent-activity/links", request, ct); }
    public Task<AgentActivitySettingsResponse> GetAgentActivitySettingsAsync(CancellationToken ct = default) =>
        GetAsync<AgentActivitySettingsResponse>("/api/me/agent-activity/settings", ct);
    /// <summary>Uploads sessions read from the agents' logs, each replacing what this device sent for it before. 409 when sync is off.</summary>
    public async Task UploadAgentSessionsAsync(Guid deviceId, IReadOnlyList<object> sessions, CancellationToken ct = default)
    { using var response = await SendAsync(HttpMethod.Put, "/api/me/agent-activity/sessions", new { deviceId, sessions }, ct); }
    public Task<AgentActivitySettingsResponse> UpdateAgentActivitySettingsAsync(AgentActivitySettingsRequest request, CancellationToken ct = default) =>
        PutAsync<AgentActivitySettingsResponse>("/api/me/agent-activity/settings", request, ct);
}
