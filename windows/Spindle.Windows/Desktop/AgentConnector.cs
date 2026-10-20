namespace Spindle.Windows.Desktop;

/// <summary>
/// Connects the person's agents, Claude Code and Codex, to the MCP server on the Spindle server the app is signed in
/// to (<c>&lt;server&gt;/mcp</c>), with the agents' own commands: it adds the server and starts signing in, which the
/// person finishes in their browser. The same commands are shown for copying. The Mac's <c>AgentConnector</c>.
/// </summary>
public sealed class AgentConnector(Func<Uri?> server)
{
    public enum Agent { Claude, Codex }

    public enum Step { Adding, SigningIn }

    public sealed record Outcome(Agent Agent, bool Ok, string Message);

    private CancellationTokenSource? cancel;

    /// <summary>Something shown changed: what's connected, the step, or how it went.</summary>
    public event Action? Changed;

    /// <summary>Which agents have the server at this app's address, as last read from their settings.</summary>
    public IReadOnlySet<Agent> Connected { get; private set; } = new HashSet<Agent>();
    /// <summary>The agent being connected, and how far it got.</summary>
    public (Agent Agent, Step Step)? Working { get; private set; }
    public Outcome? Last { get; private set; }

    /// <summary>The MCP server on the server the app is signed in to.</summary>
    public Uri? Url => server() is { } address ? AgentConnections.McpUrl(address) : null;

    public static string Label(Agent agent) => agent == Agent.Claude ? "Claude Code" : "Codex";

    private static string Program(Agent agent) => agent == Agent.Claude ? "claude" : "codex";

    /// <summary>Adds the server for the user. Codex starts signing in on its own once it has added it.</summary>
    public static AgentCommand Add(Agent agent, Uri url) => agent == Agent.Claude
        ? new("claude", ["mcp", "add", "--transport", "http", "--scope", "user", AgentConnections.Name, url.AbsoluteUri])
        : new("codex", ["mcp", "add", AgentConnections.Name, "--url", url.AbsoluteUri]);

    /// <summary>Takes out whatever Claude Code has by that name, so it can be added again. Codex replaces it itself.</summary>
    public static AgentCommand Remove(Agent agent) => agent == Agent.Claude
        ? new("claude", ["mcp", "remove", "--scope", "user", AgentConnections.Name])
        : new("codex", ["mcp", "remove", AgentConnections.Name]);

    /// <summary>Signs in again: opens the browser and waits for it.</summary>
    public static AgentCommand Login(Agent agent) => new(Program(agent), ["mcp", "login", AgentConnections.Name]);

    /// <summary>What the person would type in a terminal to connect it.</summary>
    public static string Commands(Agent agent, Uri url) => agent == Agent.Claude ? $"{Add(agent, url)}\n{Login(agent)}" : Add(agent, url).ToString();

    /// <summary>Reads again which agents have the server, e.g. when Settings shows.</summary>
    public async Task RefreshAsync()
    {
        if (Url is not { } url) return;
        Connected = await Task.Run(() =>
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var agents = new HashSet<Agent>();
            if (AgentConnections.Claude(home, url)) agents.Add(Agent.Claude);
            if (AgentConnections.Codex(AgentConnections.CodexHome(home, Environment.GetEnvironmentVariable("CODEX_HOME")), url)) agents.Add(Agent.Codex);
            return agents;
        });
        Changed?.Invoke();
    }

    /// <summary>Adds the server to the agent, replacing anything it had by that name, and starts signing in.</summary>
    public Task ConnectAsync(Agent agent) => RunAsync(agent, async token =>
    {
        var url = Url!;
        if (agent == Agent.Codex)
        {
            // Codex replaces a server of the same name, and signs in straight after adding it.
            Show(agent, Step.SigningIn);
            return SignedIn(agent, await AgentCommandRunner.RunAsync(Add(agent, url), TimeSpan.FromMinutes(10), cancellation: token));
        }
        Show(agent, Step.Adding);
        var added = await AgentCommandRunner.RunAsync(Add(agent, url), TimeSpan.FromSeconds(30), cancellation: token);
        if (added.Output.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            await AgentCommandRunner.RunAsync(Remove(agent), TimeSpan.FromSeconds(30), cancellation: token);
            added = await AgentCommandRunner.RunAsync(Add(agent, url), TimeSpan.FromSeconds(30), cancellation: token);
        }
        if (Failure(agent, added) is { } failure) return failure;
        await RefreshAsync();
        return await SignInAsync(agent, token);
    });

    /// <summary>Signs in to an agent that has the server already.</summary>
    public Task SignInAgainAsync(Agent agent) => RunAsync(agent, token => SignInAsync(agent, token));

    /// <summary>Stops waiting for the browser.</summary>
    public void Cancel() => cancel?.Cancel();

    private async Task RunAsync(Agent agent, Func<CancellationToken, Task<Outcome>> work)
    {
        if (Working is not null || Url is null) return;
        Last = null;
        using var source = cancel = new CancellationTokenSource();
        Outcome outcome;
        try { outcome = await work(source.Token); }
        catch (Exception error) { outcome = new(agent, false, error.Message); }
        cancel = null;
        Working = null;
        Last = outcome;
        await RefreshAsync();
    }

    private void Show(Agent agent, Step step)
    {
        Working = (agent, step);
        Changed?.Invoke();
    }

    /// <summary>
    /// Claude Code signs in only in a terminal, so it gets a console window of its own, which stays open when it fails;
    /// Codex signs in without one.
    /// </summary>
    private async Task<Outcome> SignInAsync(Agent agent, CancellationToken token)
    {
        Show(agent, Step.SigningIn);
        var result = await AgentCommandRunner.RunAsync(Login(agent), TimeSpan.FromMinutes(10), console: agent == Agent.Claude, cancellation: token);
        if (agent == Agent.Claude && result is { ExitCode: not 0, NotFound: false, TimedOut: false, Cancelled: false })
            return new(agent, false, $"Claude Code didn't finish signing in. You can also run /mcp in Claude Code, choose {AgentConnections.Name}, then Authenticate.");
        return SignedIn(agent, result);
    }

    private static Outcome SignedIn(Agent agent, AgentCommandRunner.Result result)
    {
        if (result.Cancelled || result.TimedOut) return new(agent, false, $"Not signed in. Connect again when you're ready, or sign in from {Label(agent)}.");
        return Failure(agent, result) ?? new(agent, true, $"Connected. Start a new session in {Label(agent)} to use Spindle.");
    }

    private static Outcome? Failure(Agent agent, AgentCommandRunner.Result result)
    {
        if (result.NotFound) return new(agent, false, $"{Label(agent)} isn't installed, or its command isn't on the PATH. Copy the commands instead.");
        if (result.Cancelled || result.TimedOut) return new(agent, false, $"{Label(agent)} didn't answer. Copy the commands instead.");
        if (result.ExitCode == 0) return null;
        var text = AgentConnections.PlainText(result.Output);
        return new(agent, false, text.Length == 0 ? $"{Label(agent)} didn't connect." : text[Math.Max(0, text.Length - 300)..]);
    }
}
