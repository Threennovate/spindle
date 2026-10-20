namespace Spindle.Activity;

/// <summary>Which logs make one session, which the server replaces whole when it is uploaded.</summary>
public enum AgentLogGrouping
{
    /// <summary>Each log is a session.</summary>
    Log,
    /// <summary><c>project/session.jsonl</c> and everything in <c>project/session/</c> (Claude Code's subagents) are one session.</summary>
    ProjectSession,
}

/// <summary>
/// Where a source keeps its session logs and which of its files they are. The service, the upload and the views read
/// sources only through this, so adding one is an entry here and its lines in <see cref="AgentLogParser"/>. The Kit's
/// <c>AgentLogFormat.swift</c> has the same fields, and both run the discovery cases in
/// <c>tests/vectors/agent-logs.json</c>.
/// </summary>
/// <param name="Id">How the source is named in uploads: <c>claude</c>.</param>
/// <param name="ShortLabel">Short enough for a calendar lane's heading.</param>
/// <param name="Folder">The tool's own folder in the home folder: <c>.claude</c>.</param>
/// <param name="FixtureFolder">Its folder in synthetic logs, kept without dots: <c>claude</c>.</param>
/// <param name="Logs">Where its session logs are, below <paramref name="Folder"/>: <c>projects</c>.</param>
/// <param name="Prefix">A log's name starts with it and ends in <c>.jsonl</c>, unless it is <paramref name="Name"/> exactly.</param>
/// <param name="MinLevel">How many folders below <paramref name="Logs"/> a log is, at least.</param>
/// <param name="MaxLevel">And at most.</param>
/// <param name="TitleIndex">A file in <paramref name="Folder"/> whose rows name sessions (Codex's <c>session_index.jsonl</c>), read by <see cref="AgentLogParser.IndexTitle"/>.</param>
public sealed record AgentLogFormat(AgentSource Source, string Id, string Label, string ShortLabel, string Folder, string FixtureFolder,
    string Logs, string Prefix, string? Name, int MinLevel, int MaxLevel, AgentLogGrouping Grouping, string? TitleIndex)
{
    /// <summary>Every source, in the order lanes and settings show them.</summary>
    public static IReadOnlyList<AgentLogFormat> All { get; } = [
        new(AgentSource.Claude, "claude", "Claude Code", "Claude", ".claude", "claude", "projects", "", null, 0, int.MaxValue,
            AgentLogGrouping.ProjectSession, null),
        // Date folders of rollout-<time>-<id>.jsonl.
        new(AgentSource.Codex, "codex", "Codex", "Codex", ".codex", "codex", "sessions", "rollout-", null, 0, int.MaxValue,
            AgentLogGrouping.Log, "session_index.jsonl"),
        // <session>/events.jsonl, beside the session's checkpoints and files, which aren't read.
        new(AgentSource.Copilot, "copilot", "GitHub Copilot CLI", "Copilot", ".copilot", "copilot", "session-state", "", "events.jsonl", 1, 1,
            AgentLogGrouping.Log, null),
    ];

    public static AgentLogFormat Of(AgentSource source) => All.First(f => f.Source == source);

    /// <summary>Every source by name, for copy: "Claude Code, Codex and GitHub Copilot CLI".</summary>
    public static string Names => string.Join(", ", All.SkipLast(1).Select(f => f.Label)) + " and " + All[^1].Label;

    public string Root(string home) => Path.Combine(home, Folder);

    public string LogsPath(string home) => Path.Combine(Root(home), Logs);

    /// <summary>Whether a file, by its path below <see cref="Logs"/> (<c>2026/09/28/rollout-x.jsonl</c>), is a session log.</summary>
    public bool IsLog(string relative)
    {
        relative = relative.Replace('\\', '/');
        var level = relative.Count(c => c == '/');
        if (level < MinLevel || level > MaxLevel) return false;
        var file = relative[(relative.LastIndexOf('/') + 1)..];
        return Name is not null ? file == Name : file.StartsWith(Prefix, StringComparison.Ordinal) && file.EndsWith(".jsonl", StringComparison.Ordinal);
    }

    /// <summary>The session a log belongs to, by its path below <see cref="Logs"/>; logs with the same one are uploaded together.</summary>
    public string Session(string relative)
    {
        relative = relative.Replace('\\', '/');
        if (Grouping != AgentLogGrouping.ProjectSession) return relative;
        var parts = relative.Split('/', 3);
        if (parts.Length < 2) return relative;
        return parts[0] + "/" + (parts[1].EndsWith(".jsonl", StringComparison.Ordinal) ? parts[1][..^".jsonl".Length] : parts[1]);
    }

    /// <summary>The session logs under <paramref name="home"/>, without listing folders deeper than they can be.</summary>
    public IEnumerable<FileInfo> Files(string home)
    {
        var folder = LogsPath(home);
        if (!Directory.Exists(folder)) return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = MaxLevel };
        return new DirectoryInfo(folder).EnumerateFiles("*.jsonl", options).Where(f => IsLog(Path.GetRelativePath(folder, f.FullName)));
    }
}
