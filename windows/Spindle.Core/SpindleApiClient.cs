using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spindle.Core;

public sealed record ExportFile(byte[] Content, string FileName, string ContentType);

/// <summary>Native API authentication. Refresh rotation is serialized; writes are never retried after transport errors.</summary>
public sealed partial class SpindleApiClient
{
    private readonly HttpClient _http;
    private readonly ISessionStore _store;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private AuthTokensResponse? _tokens;
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();
    public string? SyncConnectionId { get; internal set; }
    public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => AccessTokenAsync(null, ct);
    public Uri ServerUri => _http.BaseAddress!;
    public bool IsAuthenticated => _tokens is not null;
    public event EventHandler? SessionChanged;

    public SpindleApiClient(HttpClient http, ISessionStore store)
    {
        if (http.BaseAddress is null || !http.BaseAddress.IsAbsoluteUri)
            throw new ArgumentException("HttpClient.BaseAddress must be an absolute API server URL.", nameof(http));
        _http = http;
        _store = store;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public Task LoginAsync(LoginRequest request, CancellationToken ct = default) => SignInAsync("/api/auth/login", request, ct);
    public Task RegisterAsync(RegisterRequest request, CancellationToken ct = default) => SignInAsync("/api/auth/register", request, ct);

    private async Task SignInAsync(string path, object body, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            using var response = await SendRawAsync(HttpMethod.Post, path, body, null, ct);
            await EnsureSuccessAsync(response, ct);
            await AcceptTokensAsync(await ReadAsync<AuthTokensResponse>(response, ct), ct);
        }
        finally { _sessionGate.Release(); }
    }

    public async Task<bool> RestoreSessionAsync(CancellationToken ct = default)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            var session = await _store.LoadAsync(ct);
            if (session is null) return false;
            try { await RefreshCoreAsync(session.RefreshToken, ct); return true; }
            catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            { await ClearSessionAsync(ct); return false; }
        }
        finally { _sessionGate.Release(); }
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            var token = _tokens?.RefreshToken;
            // Remove local credentials even when the server cannot be reached.
            await ClearSessionAsync(ct);
            if (token is not null)
            {
                using var response = await SendRawAsync(HttpMethod.Post, "/api/auth/logout", new RefreshRequest { RefreshToken = token }, null, ct);
                await EnsureSuccessAsync(response, ct);
            }
        }
        finally { _sessionGate.Release(); }
    }

    private async Task ClearSessionAsync(CancellationToken ct)
    {
        _tokens = null;
        await _store.ClearAsync(ct);
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task AcceptTokensAsync(AuthTokensResponse tokens, CancellationToken ct)
    {
        _tokens = tokens;
        await _store.SaveAsync(new(tokens.RefreshToken), ct);
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshCoreAsync(string refreshToken, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Post, "/api/auth/refresh", new RefreshRequest { RefreshToken = refreshToken }, null, ct);
        await EnsureSuccessAsync(response, ct);
        await AcceptTokensAsync(await ReadAsync<AuthTokensResponse>(response, ct), ct);
    }

    private async Task<string> AccessTokenAsync(string? rejectedToken, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            var tokens = _tokens ?? throw new ApiException(HttpStatusCode.Unauthorized, "Please sign in again.");
            if (tokens.AccessTokenExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30) || rejectedToken == tokens.AccessToken)
            {
                try { await RefreshCoreAsync(tokens.RefreshToken, ct); }
                catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                { await ClearSessionAsync(ct); throw; }
            }
            return _tokens!.AccessToken;
        }
        finally { _sessionGate.Release(); }
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, string? token, CancellationToken ct, string? version = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        // Only if the record still has the version this app saw; the server answers 412 otherwise.
        if (!string.IsNullOrEmpty(version)) request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (SyncConnectionId is { } connection) request.Headers.TryAddWithoutValidation("X-Sync-Connection", connection);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string? version = null)
    {
        var token = await AccessTokenAsync(null, ct);
        var response = await SendRawAsync(method, path, body, token, ct, version);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            token = await AccessTokenAsync(token, ct);
            response = await SendRawAsync(method, path, body, token, ct, version);
        }
        if (!response.IsSuccessStatusCode)
        {
            var error = await ApiException.FromResponseAsync(response, ct);
            response.Dispose();
            throw error;
        }
        return response;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw await ApiException.FromResponseAsync(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct) ?? throw new JsonException("The server returned an empty response.");

    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    { using var response = await SendAsync(HttpMethod.Get, path, null, ct); return await ReadAsync<T>(response, ct); }
    public async Task<T> PostAsync<T>(string path, object body, CancellationToken ct = default, string? version = null)
    { using var response = await SendAsync(HttpMethod.Post, path, body, ct, version); return await ReadAsync<T>(response, ct); }
    public async Task<T> PutAsync<T>(string path, object body, CancellationToken ct = default, string? version = null)
    { using var response = await SendAsync(HttpMethod.Put, path, body, ct, version); return await ReadAsync<T>(response, ct); }
    public async Task DeleteAsync(string path, CancellationToken ct = default, string? version = null)
    { using var response = await SendAsync(HttpMethod.Delete, path, null, ct, version); }
    public async Task PostAsync(string path, object? body = null, CancellationToken ct = default)
    { using var response = await SendAsync(HttpMethod.Post, path, body, ct); }

    private static string Workspace(Guid id) => $"/api/workspaces/{id:D}";
    private static string Instant(DateTimeOffset value) => Uri.EscapeDataString(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}
