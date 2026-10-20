namespace Spindle.Core;

public sealed partial class SpindleApiClient
{
    public Task<ProjectResponse[]> GetProjectsAsync(Guid workspaceId, bool includeArchived = false, CancellationToken ct = default) => GetAsync<ProjectResponse[]>($"{Workspace(workspaceId)}/projects?includeArchived={includeArchived.ToString().ToLowerInvariant()}", ct);
    public Task<ProjectResponse> CreateProjectAsync(Guid workspaceId, ProjectRequest request, CancellationToken ct = default) => PostAsync<ProjectResponse>($"{Workspace(workspaceId)}/projects", request, ct);
    public Task<ProjectResponse> UpdateProjectAsync(Guid workspaceId, Guid id, ProjectRequest request, CancellationToken ct = default) => PutAsync<ProjectResponse>($"{Workspace(workspaceId)}/projects/{id:D}", request, ct);
    public Task DeleteProjectAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => DeleteAsync($"{Workspace(workspaceId)}/projects/{id:D}", ct);
    public Task<ProjectResponse> ArchiveProjectAsync(Guid workspaceId, Guid id, bool restore = false, CancellationToken ct = default) => PostAsync<ProjectResponse>($"{Workspace(workspaceId)}/projects/{id:D}/{(restore ? "restore" : "archive")}", new { }, ct);
    public Task<ClientResponse[]> GetClientsAsync(Guid workspaceId, bool includeArchived = false, CancellationToken ct = default) => GetAsync<ClientResponse[]>($"{Workspace(workspaceId)}/clients?includeArchived={includeArchived.ToString().ToLowerInvariant()}", ct);
    public Task<ClientResponse> CreateClientAsync(Guid workspaceId, ClientRequest request, CancellationToken ct = default) => PostAsync<ClientResponse>($"{Workspace(workspaceId)}/clients", request, ct);
    public Task<ClientResponse> UpdateClientAsync(Guid workspaceId, Guid id, ClientRequest request, CancellationToken ct = default) => PutAsync<ClientResponse>($"{Workspace(workspaceId)}/clients/{id:D}", request, ct);
    public Task DeleteClientAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => DeleteAsync($"{Workspace(workspaceId)}/clients/{id:D}", ct);
    public Task<ClientResponse> ArchiveClientAsync(Guid workspaceId, Guid id, bool restore = false, CancellationToken ct = default) => PostAsync<ClientResponse>($"{Workspace(workspaceId)}/clients/{id:D}/{(restore ? "restore" : "archive")}", new { }, ct);
    public Task<TagResponse[]> GetTagsAsync(Guid workspaceId, CancellationToken ct = default) => GetAsync<TagResponse[]>($"{Workspace(workspaceId)}/tags", ct);
    public Task<TagResponse> CreateTagAsync(Guid workspaceId, TagRequest request, CancellationToken ct = default) => PostAsync<TagResponse>($"{Workspace(workspaceId)}/tags", request, ct);
    public Task<TagResponse> UpdateTagAsync(Guid workspaceId, Guid id, TagRequest request, CancellationToken ct = default) => PutAsync<TagResponse>($"{Workspace(workspaceId)}/tags/{id:D}", request, ct);
    public Task DeleteTagAsync(Guid workspaceId, Guid id, CancellationToken ct = default) => DeleteAsync($"{Workspace(workspaceId)}/tags/{id:D}", ct);
    public Task<TaskResponse> CreateTaskAsync(Guid workspaceId, Guid projectId, TaskRequest request, CancellationToken ct = default) => PostAsync<TaskResponse>($"{Workspace(workspaceId)}/projects/{projectId:D}/tasks", request, ct);
    public Task<TaskResponse> UpdateTaskAsync(Guid workspaceId, Guid projectId, Guid taskId, TaskRequest request, CancellationToken ct = default) => PutAsync<TaskResponse>($"{Workspace(workspaceId)}/projects/{projectId:D}/tasks/{taskId:D}", request, ct);
    public Task DeleteTaskAsync(Guid workspaceId, Guid projectId, Guid taskId, CancellationToken ct = default) => DeleteAsync($"{Workspace(workspaceId)}/projects/{projectId:D}/tasks/{taskId:D}", ct);
}
