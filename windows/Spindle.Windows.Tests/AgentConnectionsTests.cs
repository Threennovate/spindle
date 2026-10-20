using Spindle.Windows.Desktop;

namespace Spindle.Windows.Tests;

public sealed class AgentConnectionsTests : IDisposable
{
    private static readonly Uri Url = new("https://app.spindletrack.com/mcp");
    private readonly string root = Path.Combine(Path.GetTempPath(), "agent-connections-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private string Folder(params (string Path, string Text)[] files)
    {
        var folder = Path.Combine(root, Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder);
        foreach (var (path, text) in files)
        {
            var file = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        return folder;
    }

    [Theory]
    [InlineData("https://app.spindletrack.com", "https://app.spindletrack.com/mcp")]
    [InlineData("https://app.spindletrack.com/", "https://app.spindletrack.com/mcp")]
    [InlineData("http://localhost:5258/", "http://localhost:5258/mcp")]
    public void TheMcpServerIsAtTheServersRoot(string server, string mcp) => Assert.Equal(mcp, AgentConnections.McpUrl(new Uri(server)).AbsoluteUri);

    [Fact]
    public void ClaudeCodeIsConnectedWithAnHttpServerAtThisUrlForTheUser()
    {
        var home = Folder((".claude.json", """
            {"projects":{"C:/x":{"mcpServers":{"spindle":{"type":"http","url":"https://elsewhere.example/mcp"}}}},
             "mcpServers":{"spindle":{"type":"http","url":"https://app.spindletrack.com/mcp/"}}}
            """));
        Assert.True(AgentConnections.Claude(home, Url));
        Assert.True(AgentConnections.Claude(home, new Uri("HTTPS://App.Spindletrack.com/mcp")));
        Assert.False(AgentConnections.Claude(home, new Uri("https://spindle.example.com/mcp")));
    }

    [Theory]
    [InlineData("""{"mcpServers":{"spindle":{"type":"stdio","command":"C:\\Apps\\Spindle\\spindle-mcp.exe","args":[]}}}""")]
    [InlineData("""{"mcpServers":{"spindle":{"type":"sse","url":"https://app.spindletrack.com/mcp"}}}""")]
    [InlineData("""{"mcpServers":{"other":{"type":"http","url":"https://app.spindletrack.com/mcp"}}}""")]
    [InlineData("""{"mcpServers":{"spindle":"https://app.spindletrack.com/mcp"}}""")]
    [InlineData("{not json")]
    public void AnythingElseIsNotConnected(string json) => Assert.False(AgentConnections.Claude(Folder((".claude.json", json)), Url));

    [Fact]
    public void ClaudeCodeWithoutSettingsIsNotConnected() => Assert.False(AgentConnections.Claude(Folder(), Url));

    [Fact]
    public void CodexIsConnectedWithItsUrl()
    {
        var home = Folder(("config.toml", """
            model = "gpt-5"

            [mcp_servers.other]
            url = "https://elsewhere.example/mcp"

            [mcp_servers.spindle]
            url = "https://app.spindletrack.com/mcp"

            [features]
            url = "https://not.a.server"
            """.Replace("\n", "\r\n")));
        Assert.True(AgentConnections.Codex(home, Url));
        Assert.False(AgentConnections.Codex(home, new Uri("https://elsewhere.example/mcp")));
        Assert.False(AgentConnections.Codex(Folder(), Url));
        Assert.False(AgentConnections.Codex(Folder(("config.toml", "[mcp_servers.spindle]\ncommand = 'C:\\Apps\\Spindle\\spindle-mcp.exe'\n")), Url));
    }

    [Theory]
    [InlineData("[mcp_servers.\"spindle\"] # ours\nurl='https://a/mcp'", "https://a/mcp")]
    [InlineData("[mcp_servers.'spindle']\nurl = \"https://a/\\\"b\" # x", "https://a/\"b")]
    [InlineData("[mcp_servers.spindle]\nurls = \"https://a\"", null)]
    [InlineData("[mcp_servers.spindle.env]\nurl = \"https://a\"", null)]
    [InlineData("[mcp_servers.spindle]\nurl = https", null)]
    public void CodexStrings(string toml, string? url) => Assert.Equal(url, AgentConnections.CodexUrl(toml));

    [Fact]
    public void CodexHome()
    {
        Assert.Equal(Path.Combine("home", ".codex"), AgentConnections.CodexHome("home", null));
        Assert.Equal(Path.Combine("home", ".codex"), AgentConnections.CodexHome("home", ""));
        Assert.Equal("D:\\codex", AgentConnections.CodexHome("home", "D:\\codex"));
    }

    [Fact]
    public void TerminalOutputBecomesPlainLines()
    {
        var output = "\u001B[1mAdded\u001B[0m global MCP server 'spindle'.\r\nAuthorize: \u001B]8;;https://a/b\u0007https://a/b\u001B]8;;\u0007\r\n\r\n";
        Assert.Equal("Added global MCP server 'spindle'.\nAuthorize: https://a/b", AgentConnections.PlainText(output));
    }

    [Fact]
    public void TheCommandsAreTheDocumentedOnes()
    {
        Assert.Equal("claude mcp add --transport http --scope user spindle https://app.spindletrack.com/mcp\nclaude mcp login spindle",
            AgentConnector.Commands(AgentConnector.Agent.Claude, Url));
        Assert.Equal("codex mcp add spindle --url https://app.spindletrack.com/mcp", AgentConnector.Commands(AgentConnector.Agent.Codex, Url));
    }

    [Theory]
    [InlineData("spindle", "spindle")]
    [InlineData("https://spindle.example.com:8443/mcp", "https://spindle.example.com:8443/mcp")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\Program Files\\", "\"C:\\Program Files\\\\\"")]
    [InlineData("a\\\\b c", "\"a\\\\b c\"")]
    public void ArgumentsAreQuotedAsWindowsProgramsReadThem(string argument, string quoted) => Assert.Equal(quoted, AgentCommand.Quote(argument));

    [Theory]
    [InlineData("https://a/mcp?x=1&y=2", "\"https://a/mcp?x=1&y=2\"")]
    [InlineData("a|b", "\"a|b\"")]
    [InlineData("plain", "plain")]
    public void BatchFilesGetCmdsCharactersQuoted(string argument, string quoted) => Assert.Equal(quoted, AgentCommand.Quote(argument, batch: true));

    [Theory]
    [InlineData("100%")]
    [InlineData("say \"hi\"")]
    public void BatchFilesRefuseWhatCmdWouldExpand(string argument) => Assert.Throws<ArgumentException>(() => AgentCommand.Quote(argument, batch: true));

    [Fact]
    public void AnExeStartsDirectlyAndABatchFileThroughCmd()
    {
        var command = new AgentCommand("codex", ["mcp", "add", "spindle", "--url", "https://a/mcp"]);
        Assert.Equal(("C:\\bin\\codex.exe", "mcp add spindle --url https://a/mcp"), command.StartWith("C:\\bin\\codex.exe"));
        var (file, arguments) = command.StartWith("C:\\Users\\Ana Kovač\\AppData\\Roaming\\npm\\codex.cmd");
        Assert.EndsWith("cmd.exe", file, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/d /s /c \"\"C:\\Users\\Ana Kovač\\AppData\\Roaming\\npm\\codex.cmd\" mcp add spindle --url https://a/mcp\"", arguments);
        var (_, login) = new AgentCommand("claude", ["mcp", "login", "spindle"]).StartWith("C:\\bin\\claude.exe", " || (pause & exit /b 1)");
        Assert.Equal("/d /s /c \"C:\\bin\\claude.exe mcp login spindle || (pause & exit /b 1)\"", login);
    }

    [Fact]
    public void CommandsAreFoundAsExeOrBatchFiles()
    {
        var first = Folder(("codex.cmd", ""));
        var second = Folder(("claude.exe", ""), ("codex.exe", ""));
        Assert.Equal(Path.Combine(first, "codex.cmd"), AgentCommand.Find("codex", ["", first, second], windows: true));
        Assert.Equal(Path.Combine(second, "claude.exe"), AgentCommand.Find("claude", ["\"" + first + "\"", second], windows: true));
        Assert.Null(AgentCommand.Find("gemini", [first, second], windows: true));
        var unix = Folder(("claude", ""));
        Assert.Equal(Path.Combine(unix, "claude"), AgentCommand.Find("claude", [first, second, unix], windows: false));
    }
}
