using System.Net;
namespace Spindle.Core;

public sealed partial class SpindleApiClient
{
    public Task<MeResponse> GetMeAsync(CancellationToken ct = default) => GetAsync<MeResponse>("/api/me", ct);
    public async Task<WorkspaceSummary[]> GetWorkspacesAsync(CancellationToken ct = default) => (await GetMeAsync(ct)).Workspaces;
    public Task<WorkspaceSettingsResponse> GetWorkspaceSettingsAsync(Guid workspaceId, CancellationToken ct = default) => GetAsync<WorkspaceSettingsResponse>($"{Workspace(workspaceId)}/settings", ct);
    public Task<MeResponse> UpdateProfileAsync(ProfileRequest request, CancellationToken ct = default) => PutAsync<MeResponse>("/api/me/profile", request, ct);
    public Task<MeResponse> UpdatePreferencesAsync(PreferencesRequest request, CancellationToken ct = default) => PutAsync<MeResponse>("/api/me/preferences", request, ct);
    public Task<TimeEntryResponse[]> GetTimeEntriesAsync(Guid workspaceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        GetAsync<TimeEntryResponse[]>($"{Workspace(workspaceId)}/time-entries?from={Instant(from)}&to={Instant(to)}", ct);
    public async Task<TimeEntryResponse?> GetTimeEntryAsync(Guid workspaceId, Guid entryId, CancellationToken ct = default)
    {
        try { return await GetAsync<TimeEntryResponse>($"{Workspace(workspaceId)}/time-entries/{entryId:D}", ct); }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
    public async Task<TimeEntryResponse?> GetCurrentTimeEntryAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/me/time-entries/current", null, ct);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await ReadAsync<TimeEntryResponse>(response, ct);
    }
    public Task<TimeEntryResponse> CreateTimeEntryAsync(Guid workspaceId, CreateTimeEntryRequest request, CancellationToken ct = default) =>
        PostAsync<TimeEntryResponse>($"{Workspace(workspaceId)}/time-entries", request, ct);
    /// <summary>Saves the entry only if it is still as <paramref name="current"/> was (its version); otherwise 412 with the entry as it is.</summary>
    public Task<TimeEntryResponse> UpdateTimeEntryAsync(Guid workspaceId, TimeEntryResponse current, TimeEntryRequest request, CancellationToken ct = default) =>
        PutAsync<TimeEntryResponse>($"{Workspace(workspaceId)}/time-entries/{current.Id:D}", request, ct, current.Version);
    public Task<TimeEntryResponse> StopTimeEntryAsync(Guid workspaceId, Guid entryId, DateTimeOffset? stop = null, CancellationToken ct = default, string? version = null) =>
        PostAsync<TimeEntryResponse>($"{Workspace(workspaceId)}/time-entries/{entryId:D}/stop", new StopTimeEntryRequest { Stop = stop }, ct, version);
    public Task DeleteTimeEntryAsync(Guid workspaceId, TimeEntryResponse entry, CancellationToken ct = default) =>
        DeleteAsync($"{Workspace(workspaceId)}/time-entries/{entry.Id:D}", ct, entry.Version);
    public Task<TimeEntryResponse[]> BulkUpdateTimeEntriesAsync(Guid workspaceId, BulkUpdateTimeEntriesRequest request, CancellationToken ct = default) =>
        PostAsync<TimeEntryResponse[]>($"{Workspace(workspaceId)}/time-entries/bulk-update", request, ct);
    public Task BulkDeleteTimeEntriesAsync(Guid workspaceId, Guid[] ids, CancellationToken ct = default) =>
        PostAsync($"{Workspace(workspaceId)}/time-entries/bulk-delete", new BulkDeleteTimeEntriesRequest { Ids = ids }, ct);
    public Task<TimeEntrySuggestion[]> GetTimeEntrySuggestionsAsync(Guid workspaceId, string search, int limit = 10, CancellationToken ct = default) =>
        GetAsync<TimeEntrySuggestion[]>($"{Workspace(workspaceId)}/time-entries/suggestions?search={Uri.EscapeDataString(search)}&limit={limit}", ct);
    /// <summary>Descriptions written before that contain what is typed, those on the project first, to complete it.</summary>
    public Task<string[]> GetDescriptionSuggestionsAsync(Guid workspaceId, string search, Guid? projectId, int limit = 8, CancellationToken ct = default) =>
        GetAsync<string[]>($"{Workspace(workspaceId)}/time-entries/descriptions?search={Uri.EscapeDataString(search)}{(projectId is { } id ? $"&projectId={id}" : "")}&limit={limit}", ct);
    public Task<SplitSuggestionResponse[]> GetSplitSuggestionsAsync(Guid workspaceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        GetAsync<SplitSuggestionResponse[]>($"{Workspace(workspaceId)}/time-entries/split-suggestions?from={Instant(from)}&to={Instant(to)}", ct);
    public Task DismissSplitSuggestionAsync(Guid workspaceId, Guid entryId, CancellationToken ct = default) =>
        PostAsync($"{Workspace(workspaceId)}/time-entries/{entryId:D}/split-suggestion/dismiss", ct: ct);
}
