using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Mcp;

/// <summary>An MCP client as the authorization server knows it, however it was registered.</summary>
/// <param name="FromMetadataDocument">Whether it named itself by a metadata document URL (its ID) rather than registering.</param>
internal sealed record OAuthClientInfo(string Id, string Name, IReadOnlyList<string> RedirectUris, byte[]? SecretHash, bool FromMetadataDocument)
{
    public bool IsPublic => SecretHash is null;
}

/// <summary>
/// Finds MCP clients: those that registered themselves (RFC 7591), and those whose ID is the HTTPS URL of a client
/// metadata document (draft-ietf-oauth-client-id-metadata-document), which is read and kept for a while.
/// </summary>
internal sealed class OAuthClients(AppDbContext db, ClientMetadataDocuments documents)
{
    public async Task<OAuthClientInfo?> FindAsync(string? clientId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(clientId) || clientId.Length > OAuthClient.RedirectUriMaxLength)
        {
            return null;
        }

        if (ClientMetadataDocuments.IsDocumentUrl(clientId))
        {
            return await documents.GetAsync(clientId, cancellationToken);
        }

        var client = await db.OAuthClients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken);
        return client is null ? null : new OAuthClientInfo(client.Id, client.Name, client.RedirectUris, client.SecretHash, FromMetadataDocument: false);
    }

    /// <summary>
    /// Whether a redirect URI may receive codes: HTTPS, or HTTP to this computer for command-line agents, without a
    /// fragment, as the MCP authorization spec requires.
    /// </summary>
    public static bool IsAllowedRedirect(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri.Host)));

    public static bool IsLoopback(string host) =>
        host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    /// <summary>
    /// Whether the redirect URI is one the client named. Loopback addresses may differ in port only, since command-line
    /// agents listen on whichever port is free (RFC 8252, section 7.3).
    /// </summary>
    public static bool Matches(OAuthClientInfo client, string redirectUri) =>
        client.RedirectUris.Any(registered => registered == redirectUri || SameLoopbackApartFromPort(registered, redirectUri));

    private static bool SameLoopbackApartFromPort(string registered, string presented) =>
        Uri.TryCreate(registered, UriKind.Absolute, out var a)
        && Uri.TryCreate(presented, UriKind.Absolute, out var b)
        && a.Scheme == Uri.UriSchemeHttp && b.Scheme == Uri.UriSchemeHttp
        && IsLoopback(a.Host) && a.Host == b.Host
        && a.AbsolutePath == b.AbsolutePath && a.Query == b.Query;
}

/// <summary>
/// Reads client metadata documents from the internet, carefully: HTTPS only, public addresses only (no requests into
/// the server's own network), no redirects, a small size and a short timeout. A document is kept for as long as its
/// cache headers allow, between five minutes and a day.
/// </summary>
internal sealed class ClientMetadataDocuments(HttpClient http, IMemoryCache cache, ILogger<ClientMetadataDocuments> logger)
{
    private const int MaxBytes = 64 * 1024;
    private static readonly TimeSpan ShortestKept = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongestKept = TimeSpan.FromDays(1);

    public static bool IsDocumentUrl(string clientId) =>
        Uri.TryCreate(clientId, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.AbsolutePath.Length > 1
        && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.UserInfo)
        && !uri.AbsolutePath.Split('/').Any(segment => segment is "." or "..");

    public static void Configure(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(5);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Spindle", "1.0"));
    }

    /// <summary>Connects only to public addresses, whatever the name resolves to, and follows no redirects.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            var address = addresses.FirstOrDefault(IsPublic)
                ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} is not a public address.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || b[0] == 0
                || b[0] >= 224);
        }

        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal);
    }

    public async Task<OAuthClientInfo?> GetAsync(string url, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue<OAuthClientInfo?>(CacheKey(url), out var known))
        {
            return known;
        }

        var (client, keep) = await FetchAsync(url, cancellationToken);
        cache.Set(CacheKey(url), client, keep);
        return client;
    }

    private async Task<(OAuthClientInfo? Client, TimeSpan Keep)> FetchAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
            {
                logger.LogInformation("The client metadata document {Url} answered {Status}", url, (int)response.StatusCode);
                return (null, ShortestKept);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[MaxBytes + 1];
            var read = 0;
            int count;
            while (read < buffer.Length && (count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
            {
                read += count;
            }

            if (read > MaxBytes)
            {
                return (null, ShortestKept);
            }

            var keep = response.Headers.CacheControl?.MaxAge is { } maxAge ? Math.Clamp(maxAge.Ticks, ShortestKept.Ticks, LongestKept.Ticks) : ShortestKept.Ticks;
            return (Parse(url, buffer.AsSpan(0, read)), TimeSpan.FromTicks(keep));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogInformation(error, "Couldn't read the client metadata document {Url}", url);
            return (null, ShortestKept);
        }
    }

    /// <summary>The client a document describes, if it describes the URL it was read from and can be used here.</summary>
    internal static OAuthClientInfo? Parse(string url, ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || Text(root, "client_id") != url
            || Text(root, "client_name") is not { Length: > 0 } name
            || !root.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // Spindle can't check private_key_jwt or secrets for a client it never registered: PKCE only.
        if (Text(root, "token_endpoint_auth_method") is { } method && method != "none")
        {
            return null;
        }

        var redirects = uris.EnumerateArray()
            .Select(u => u.ValueKind == JsonValueKind.String ? u.GetString()! : "")
            .Where(OAuthClients.IsAllowedRedirect)
            .Take(OAuthClient.MaxRedirectUris)
            .ToList();
        return redirects.Count == 0 ? null : new OAuthClientInfo(url, McpOAuth.DisplayName(name), redirects, null, FromMetadataDocument: true);
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string CacheKey(string url) => "oauth-client-metadata:" + url;
}
