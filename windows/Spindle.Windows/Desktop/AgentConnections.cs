using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Spindle.Windows.Desktop;

/// <summary>
/// Whether an agent has the server's MCP, read from the agent's own settings so nothing has to run: Claude Code keeps
/// servers for the user in <c>~/.claude.json</c>, Codex in <c>~/.codex/config.toml</c> (or <c>%CODEX_HOME%</c>). Only a
/// server called <c>spindle</c> at this app's <c>&lt;server&gt;/mcp</c> counts; anything else by that name is replaced
/// when the person connects. Whether the agent finished signing in isn't in those files: the server lists signed-in
/// agents under Account. The Mac's <c>AgentConnections</c>, in C#.
/// </summary>
public static class AgentConnections
{
    /// <summary>The name agents know the server by.</summary>
    public const string Name = "spindle";

    /// <summary>The MCP server on <paramref name="server"/>, the address the app signs in to: <c>/mcp</c> at its root.</summary>
    public static Uri McpUrl(Uri server) => new(server.AbsoluteUri.TrimEnd('/') + "/mcp");

    /// <summary>Whether Claude Code has <c>spindle</c> for the user as an HTTP server at <paramref name="url"/>.</summary>
    public static bool Claude(string home, Uri url)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, ".claude.json")));
            if (!json.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object
                || !servers.TryGetProperty(Name, out var server) || server.ValueKind != JsonValueKind.Object) return false;
            var type = server.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "http";
            return type == "http" && server.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String && Same(u.GetString(), url);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether Codex has <c>spindle</c> at <paramref name="url"/>, from <c>config.toml</c> in its home.</summary>
    public static bool Codex(string codexHome, Uri url)
    {
        try
        {
            return Same(CodexUrl(File.ReadAllText(Path.Combine(codexHome, "config.toml"))), url);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Where Codex keeps its settings: <c>%CODEX_HOME%</c>, or <c>.codex</c> in the home folder.</summary>
    public static string CodexHome(string home, string? codexHome) => string.IsNullOrEmpty(codexHome) ? Path.Combine(home, ".codex") : codexHome;

    /// <summary>The same address, give or take a trailing slash and the case of the scheme and host.</summary>
    internal static bool Same(string? text, Uri url)
    {
        static string? Normalized(string text) =>
            Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + uri.PathAndQuery.TrimEnd('/') : null;
        return text is not null && Normalized(text) is { } left && left == Normalized(url.AbsoluteUri);
    }

    /// <summary>
    /// The <c>url</c> of the <c>[mcp_servers.spindle]</c> table, as <c>codex mcp add --url</c> writes it, or null when
    /// there is no such table or it has no readable <c>url</c>. Only what that needs of TOML.
    /// </summary>
    internal static string? CodexUrl(string toml)
    {
        string[] headers = [$"[mcp_servers.{Name}]", $"[mcp_servers.\"{Name}\"]", $"[mcp_servers.'{Name}']"];
        var inTable = false;
        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inTable = headers.Contains(line.Split('#', 2)[0].Trim());
                continue;
            }
            var parts = line.Split('=', 2);
            if (inTable && parts.Length == 2 && parts[0].Trim() == "url") return TomlString(parts[1].Trim());
        }
        return null;
    }

    /// <summary>A TOML basic ("…", with escapes) or literal ('…') string at the start of <paramref name="value"/>.</summary>
    private static string? TomlString(string value)
    {
        if (value.Length == 0 || value[0] is not ('"' or '\'')) return null;
        var quote = value[0];
        var result = new StringBuilder();
        var escaped = false;
        foreach (var character in value[1..])
        {
            if (escaped)
            {
                result.Append(character switch { 'n' => '\n', 't' => '\t', _ => character });
                escaped = false;
            }
            else if (character == '\\' && quote == '"') escaped = true;
            else if (character == quote) return result.ToString();
            else result.Append(character);
        }
        return null;
    }

    /// <summary>What a command printed to a terminal, as plain lines: without colours, links and carriage returns.</summary>
    public static string PlainText(string output)
    {
        var text = Regex.Replace(output, "\u001B\\][^\u0007\u001B]*(\u0007|\u001B\\\\)", "");
        text = Regex.Replace(text, "\u001B\\[[0-9;?]*[ -/]*[@-~]", "");
        text = new string(text.Replace("\r\n", "\n").Replace('\r', '\n').Where(c => c is '\n' or '\t' || c >= ' ').ToArray());
        return string.Join('\n', text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }
}

/// <summary>One of an agent's commands: the program, as found on the PATH, and its arguments.</summary>
public sealed record AgentCommand(string Program, IReadOnlyList<string> Arguments)
{
    /// <summary>As the person would type it in a terminal.</summary>
    public override string ToString() => string.Join(' ', [Program, .. Arguments.Select(a => Quote(a))]);

    /// <summary>
    /// An argument as Windows programs read their command line (the Microsoft C runtime's rules): in double quotes when
    /// it has spaces, quotes or, for a batch file, which cmd.exe runs, characters cmd.exe would act on.
    /// </summary>
    public static string Quote(string argument, bool batch = false)
    {
        if (batch && argument.IndexOfAny(['%', '"', '\n', '\r']) >= 0)
            throw new ArgumentException("cmd.exe can't be given this argument safely.", nameof(argument));
        var special = batch ? " \t\"&|<>^(),;=" : " \t\"";
        if (argument.Length > 0 && argument.IndexOfAny(special.ToCharArray()) < 0) return argument;
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { backslashes++; continue; }
            // Backslashes count only before a quote: double them there, and escape the quote.
            quoted.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes).Append(character);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>
    /// The program and command line to start it with: an .exe directly, a batch file (npm's <c>codex.cmd</c>) through
    /// cmd.exe, with <c>/s</c> so it takes everything between the outer quotes as it is.
    /// </summary>
    public (string FileName, string Arguments) StartWith(string executable, string? then = null)
    {
        var batch = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        if (!batch && then is null) return (executable, string.Join(' ', Arguments.Select(a => Quote(a))));
        var line = string.Join(' ', [Quote(executable, true), .. Arguments.Select(a => Quote(a, true))]);
        var shell = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe";
        return (shell, $"/d /s /c \"{line}{then}\"");
    }

    /// <summary>
    /// Where <paramref name="program"/> is: the first folder of <paramref name="folders"/> with it as an .exe or a
    /// batch file (or, off Windows, as it is), or null when it isn't installed.
    /// </summary>
    public static string? Find(string program, IEnumerable<string> folders, bool windows)
    {
        string[] names = windows ? [program + ".exe", program + ".cmd", program + ".bat"] : [program];
        foreach (var folder in folders.Where(f => f.Length > 0))
        {
            foreach (var name in names)
            {
                var path = Path.Combine(folder.Trim('"'), name);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    /// <summary>
    /// The folders to look in: this process's PATH, then the user's and the machine's as they are now, in case an agent
    /// was installed after Spindle started, then where agents' installers put their commands.
    /// </summary>
    public static IEnumerable<string> SearchPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var paths = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) : null,
            OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) : null,
        };
        string[] usual = [Path.Combine(home, ".local", "bin"), Path.Combine(appData, "npm"), Path.Combine(home, ".bun", "bin"), Path.Combine(home, ".volta", "bin")];
        return paths.SelectMany(p => (p ?? "").Split(Path.PathSeparator)).Concat(usual).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Runs an agent's command without a console window, or in one when it needs a terminal, and can stop it.</summary>
public static class AgentCommandRunner
{
    public sealed record Result(int ExitCode, string Output, bool NotFound = false, bool TimedOut = false, bool Cancelled = false);

    /// <summary>
    /// Runs the command and waits for it. Hidden, with what it prints; or, with <paramref name="console"/>, in a console
    /// window of its own that stays open when the command fails, for a command that needs a terminal to ask in.
    /// </summary>
    public static async Task<Result> RunAsync(AgentCommand command, TimeSpan timeout, bool console = false, CancellationToken cancellation = default)
    {
        if (AgentCommand.Find(command.Program, AgentCommand.SearchPath(), OperatingSystem.IsWindows()) is not { } executable) return new(-1, "", NotFound: true);
        string fileName, arguments;
        try { (fileName, arguments) = command.StartWith(executable, console && OperatingSystem.IsWindows() ? " || (pause & exit /b 1)" : null); }
        catch (ArgumentException error) { return new(-1, error.Message); }
        var start = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = !console,
            RedirectStandardInput = !console,
            RedirectStandardOutput = !console,
            RedirectStandardError = !console,
        };
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception error) { return new(-1, error.Message); }
        Task<string> output = Task.FromResult(""), errors = Task.FromResult("");
        if (!console)
        {
            process.StandardInput.Close();
            output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new(-1, "", TimedOut: !cancellation.IsCancellationRequested, Cancelled: cancellation.IsCancellationRequested);
        }
        return new(process.ExitCode, (await output + "\n" + await errors).Trim());
    }
}
