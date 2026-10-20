using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Spindle.Core;

/// <summary>A way to sign in besides the password that the server offers, e.g. "google" shown as "Google".</summary>
public sealed record SignInProvider(string Name, string DisplayName);

/// <summary>The verifier and challenge of RFC 7636 (S256): a code caught on the way back is useless without the verifier this app keeps.</summary>
public static class Pkce
{
    public static string NewVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}

/// <summary>Where the browser comes back to: a code to exchange, or an error ("cancelled" when the person turned back).</summary>
public sealed record ExternalCallback(string? Code, string? Error)
{
    public const string Path = "/auth/callback";

    /// <summary>Reads a request target such as <c>/auth/callback?code=x</c>; null for anything else the browser asks for, e.g. a favicon.</summary>
    public static ExternalCallback? Parse(string target)
    {
        var mark = target.IndexOf('?');
        if ((mark < 0 ? target : target[..mark]) != Path) return null;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in (mark < 0 ? "" : target[(mark + 1)..]).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            query.TryAdd(key, equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' ')));
        }
        if (query.TryGetValue("code", out var code) && code.Length > 0) return new(code, null);
        // Without a code the sign-in can't go on, so anything else is an error too.
        return new(null, query.TryGetValue("error", out var error) && error.Length > 0 ? error : "missing");
    }
}

/// <summary>
/// Signing in with Google or Apple as RFC 8252 asks of a desktop app: in the default browser, which the person trusts
/// and is often already signed in to, back to a listener on this PC's loopback, with PKCE.
/// </summary>
public static class ExternalSignIn
{
    /// <summary>How long the browser has to come back; after that the person is told to try again.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public static Uri StartUri(Uri server, string provider, string redirectUri, string codeChallenge, string timeZone, string? invitation = null)
    {
        // Continuing is agreeing to the terms, as the sign-in page says next to the buttons.
        var query = $"redirectUri={Uri.EscapeDataString(redirectUri)}&codeChallenge={Uri.EscapeDataString(codeChallenge)}&timeZone={Uri.EscapeDataString(timeZone)}&acceptTerms=true";
        if (!string.IsNullOrEmpty(invitation)) query += "&invitation=" + Uri.EscapeDataString(invitation);
        return new Uri(server, $"/api/auth/external/{Uri.EscapeDataString(provider)}/start?{query}");
    }

    /// <summary>
    /// Opens the provider's sign-in in the browser, waits for it to come back and signs <paramref name="api"/> in.
    /// Returns whether that made a new account, or null when the person turned back in the browser.
    /// </summary>
    public static async Task<bool?> RunAsync(SpindleApiClient api, string provider, string timeZone, string deviceName, string? invitation,
        Action<Uri> openBrowser, CancellationToken ct = default)
    {
        var verifier = Pkce.NewVerifier();
        using var loopback = new LoopbackCallback();
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waiting.CancelAfter(Timeout);
        openBrowser(StartUri(api.ServerUri, provider, loopback.RedirectUri, Pkce.Challenge(verifier), timeZone, invitation));
        ExternalCallback callback;
        try
        {
            callback = await loopback.WaitAsync(waiting.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Signing in took too long. Try again.");
        }
        if (callback.Error == "cancelled") return null;
        if (callback.Code is null) throw new InvalidOperationException("Signing in didn't finish. Try again.");
        return await api.ExchangeExternalSignInAsync(new ExternalExchangeRequest { Code = callback.Code, CodeVerifier = verifier, DeviceName = deviceName }, ct);
    }
}

/// <summary>
/// Listens on 127.0.0.1 for the browser coming back, answers it with a page that says to go back to Spindle, and stops.
/// A plain socket rather than HttpListener, which on Windows needs a URL reservation for 127.0.0.1.
/// </summary>
public sealed class LoopbackCallback : IDisposable
{
    private static readonly byte[] Done = Page("200 OK", "You can close this tab and go back to Spindle.");
    private static readonly byte[] NotFound = Page("404 Not Found", "Not found.");
    private readonly TcpListener listener;

    /// <summary>Exactly what the server accepts: no query and no trailing slash.</summary>
    public string RedirectUri { get; }

    public LoopbackCallback()
    {
        // Port 0 lets the system pick a free port and hold it, so nothing can take it between choosing and listening.
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        RedirectUri = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{ExternalCallback.Path}";
    }

    /// <summary>Waits for the browser's request to the callback path, answers it, and stops listening.</summary>
    public async Task<ExternalCallback> WaitAsync(CancellationToken ct)
    {
        var result = new TaskCompletionSource<ExternalCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var cancelled = ct.Register(() => result.TrySetCanceled(ct));
        var accepting = AcceptAsync(result, stop.Token);
        try
        {
            return await result.Task;
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            await accepting;
        }
    }

    private async Task AcceptAsync(TaskCompletionSource<ExternalCallback> result, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Each connection on its own: browsers open spare ones that may never send a request.
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = AnswerAsync(client, result, ct);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopped: the browser came back, the person cancelled, or the time ran out.
        }
    }

    private static async Task AnswerAsync(TcpClient client, TaskCompletionSource<ExternalCallback> result, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var reading = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reading.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();
                var callback = await ReadTargetAsync(stream, reading.Token) is { } target ? ExternalCallback.Parse(target) : null;
                await stream.WriteAsync(callback is null ? NotFound : Done, reading.Token);
                await stream.FlushAsync(reading.Token);
                if (callback is not null) result.TrySetResult(callback);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // A connection that went away or never asked for anything.
            }
        }
    }

    /// <summary>The target of a GET, e.g. <c>/auth/callback?code=x</c>, from the request line; the headers are read and ignored.</summary>
    private static async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
            if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        var request = Encoding.ASCII.GetString(buffer, 0, length);
        var line = request.Split("\r\n", 2)[0].Split(' ');
        return line is ["GET", var target, ..] ? target : null;
    }

    private static byte[] Page(string status, string message)
    {
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Spindle</title></head><body style=\"font-family:system-ui,sans-serif;text-align:center;padding-top:20vh\"><p>{message}</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        return [.. head, .. body];
    }

    public void Dispose() => listener.Stop();
}
