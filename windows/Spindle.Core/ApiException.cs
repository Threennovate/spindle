using System.Net;
using System.Text.Json;
namespace Spindle.Core;

public sealed class ApiException(HttpStatusCode statusCode, string message, string? problemType = null,
    IReadOnlyDictionary<string, string[]>? errors = null, TimeEntryResponse? current = null) : Exception(message)
{
    public const string EntryChangedType = "urn:spindle:problem:entry-changed";
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? ProblemType { get; } = problemType;
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
    public bool IsConflict => StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed;
    public bool IsReadOnly => ProblemType == "urn:spindle:problem:read-only";
    /// <summary>The entry as it is now, when a write was refused because it changed since this app saw it.</summary>
    public TimeEntryResponse? Current { get; } = current;

    internal static async Task<ApiException> FromResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var fallback = response.ReasonPhrase ?? "The server could not complete the request.";
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var type = root.TryGetProperty("type", out var p) ? p.GetString() : null;
            var errors = root.TryGetProperty("errors", out var e)
                ? e.Deserialize<Dictionary<string, string[]>>() : null;
            var current = type == EntryChangedType && root.TryGetProperty("current", out var c)
                ? c.Deserialize<TimeEntryResponse>(SpindleApiClient.JsonOptions) : null;
            var message = detail ?? (errors?.Count > 0 ? string.Join("\n", errors.Values.SelectMany(v => v)) : title) ?? fallback;
            return new(response.StatusCode, message, type, errors, current);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return new(response.StatusCode, fallback); }
    }
}
