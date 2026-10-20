using Microsoft.AspNetCore.SignalR.Client;
namespace Spindle.Core;

public sealed record SyncChange(string Topic, Guid? WorkspaceId, Guid? UserId);

/// <summary>Server change notifications. Consumers reload protected resources and reload all after reconnecting.</summary>
public sealed class LiveSync : IAsyncDisposable
{
    private readonly SpindleApiClient _client;
    private readonly HubConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Guid? _workspace;
    public bool IsConnected => _connection.State == HubConnectionState.Connected;
    public event Action<SyncChange>? Changed;
    public event Action? Reconnected;

    public LiveSync(SpindleApiClient client)
    {
        _client = client;
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(client.ServerUri, "/api/hubs/sync"), options => options.AccessTokenProvider = async () => await client.GetAccessTokenAsync())
            .WithAutomaticReconnect(new ReconnectPolicy())
            .Build();
        _connection.On<SyncChange>("Changed", change => Changed?.Invoke(change));
        _connection.Reconnecting += _ => { _client.SyncConnectionId = null; return Task.CompletedTask; };
        _connection.Closed += _ => { _client.SyncConnectionId = null; return Task.CompletedTask; };
        _connection.Reconnected += async _ =>
        {
            _client.SyncConnectionId = _connection.ConnectionId;
            if (_workspace is { } workspace) await FollowAsync(workspace, CancellationToken.None);
            Reconnected?.Invoke();
        };
    }

    public async Task StartAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _workspace = workspaceId;
            if (_connection.State == HubConnectionState.Disconnected) await _connection.StartAsync(ct);
            _client.SyncConnectionId = _connection.ConnectionId;
            if (IsConnected) await FollowAsync(workspaceId, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task WatchAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var previous = _workspace;
            _workspace = workspaceId;
            if (!IsConnected) return;
            if (previous is { } old && old != workspaceId) await _connection.InvokeAsync("Unwatch", old, ct);
            await FollowAsync(workspaceId, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task FollowAsync(Guid workspaceId, CancellationToken ct)
    {
        if (!await _connection.InvokeAsync<bool>("Watch", workspaceId, ct))
            throw new ApiException(System.Net.HttpStatusCode.NotFound, "This workspace is no longer available.");
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { _client.SyncConnectionId = null; await _connection.StopAsync(ct); }
        finally { _gate.Release(); }
    }

    private sealed class ReconnectPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => TimeSpan.FromSeconds(
            retryContext.PreviousRetryCount switch { 0 => 0, 1 => 2, 2 => 5, 3 => 15, _ => 30 });
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
