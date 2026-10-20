using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Spindle.Activity;

/// <summary>Streaming metadata accumulator. JSON documents are disposed after each line.</summary>
public sealed partial class AgentLogParser(AgentSource source, long from = long.MinValue, long to = long.MaxValue)
{
    /// <summary>
    /// Raise it when the parser reads logs differently, so uploaded sessions are sent again. A new source needs no
    /// bump: the uploader reads the history again when the sources it reads grow.
    /// </summary>
    public const int Version = 1;

    private sealed class Session(string id, string cwd, string? parent)
    {
        public string Id = id, Cwd = cwd, Title = "";
        public string? Parent = parent;
        public bool TitleFinished;
        public HashSet<string> Branches = [];
        public List<AgentEvent> Events = [];
        public int? Turn;
        public string? TurnTitle;
    }
    private readonly Dictionary<string, Session> sessions = [];
    private readonly Dictionary<string, string> customTitles = [];
    private string? current;

    public int EventCount => sessions.Values.Sum(s => s.Events.Count);

    public IReadOnlyList<LocalAgentSession> Sessions => sessions.Values.Select(s => new LocalAgentSession(
        s.Id, source, s.Parent, s.Cwd, customTitles.GetValueOrDefault(s.Id, s.Title), s.Branches.ToArray(), s.Events.ToArray())).ToArray();

    public void Consume(ReadOnlyMemory<byte> line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var record = json.RootElement;
            if (record.ValueKind != JsonValueKind.Object) return;
            var type = Text(record, "type");
            var timestamp = Text(record, "timestamp");
            var validDate = DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date);
            var at = validDate ? date.ToUnixTimeMilliseconds() : 0;
            if (source == AgentSource.Copilot) ConsumeCopilot(record, type, validDate, at);
            else if (source == AgentSource.Claude)
            {
                if (Text(record, "sessionId") is not { } id) return;
                var key = "claude:" + id;
                if (type == "custom-title" && Text(record, "customTitle") is { } custom)
                { customTitles[key] = Title(custom); return; }
                if (type is not ("user" or "assistant") || !validDate || Text(record, "cwd") is not { } cwd) return;
                if (!sessions.TryGetValue(key, out var session)) sessions[key] = session = new(key, cwd, null);
                var sidechain = Flag(record, "isSidechain");
                if (!sidechain) session.Cwd = cwd;
                if (Text(record, "gitBranch") is { } branch) session.Branches.Add(branch);
                var content = Get(Get(record, "message"), "content");
                var touch = type == "user" && !sidechain && !Flag(record, "isMeta")
                    && Text(record, "promptSource") != "system" && IsPrompt(content);
                string? title = null;
                if (touch && session.Title.Length == 0) session.Title = title = ContentTitle(content);
                Add(session, at, touch, title);
                if (type == "assistant" && !sidechain && content.ValueKind == JsonValueKind.Array)
                    foreach (var block in content.EnumerateArray())
                        if (Text(block, "type") is "tool_use" or "server_tool_use")
                            Note(session, Text(block, "type") == "server_tool_use" ? Call.Other : Classify(Text(block, "name"), null, null, null));
            }
            else
            {
                var payload = Get(record, "payload");
                if (type == "session_meta" && Text(payload, "id") is { } id && Text(payload, "cwd") is { } cwd)
                {
                    current = "codex:" + id;
                    var parent = Text(payload, "parent_thread_id")
                        ?? Text(Get(Get(Get(payload, "source"), "subagent"), "thread_spawn"), "parent_thread_id");
                    if (!sessions.TryGetValue(current, out var created))
                        sessions[current] = created = new(current, cwd, parent is null ? null : "codex:" + parent);
                    if (Text(Get(payload, "git"), "branch") is { } branch) created.Branches.Add(branch);
                    return;
                }
                if (current is null || !sessions.TryGetValue(current, out var session)) return;
                if (type == "event_msg" && Text(payload, "type") == "user_message" && session.Title.Length == 0)
                    session.Title = Title(Text(payload, "message") ?? "");
                if (type == "response_item" && !session.TitleFinished)
                {
                    if (Text(payload, "role") == "user") session.Title = ContentTitle(Get(payload, "content"));
                    if (Text(payload, "role") == "assistant") session.TitleFinished = true;
                }
                var touch = type == "event_msg" && Text(payload, "type") == "task_started" && session.Parent is null;
                if (validDate && (touch || type == "response_item")) Add(session, at, touch, null);
                if (type == "response_item" && Text(payload, "type") is { } callType && callType.EndsWith("_call", StringComparison.Ordinal))
                    Note(session, Classify(Text(payload, "name"), Text(payload, "namespace"), callType, Text(payload, "input")));
            }
        }
        catch (JsonException) { } // Live logs may end in a partially written record.
    }

    /// <summary>
    /// One event of GitHub Copilot CLI's <c>session-state/&lt;session&gt;/events.jsonl</c> (github/copilot-sdk,
    /// <c>session-events.ts</c>). The session is named by its first event, <c>session.start</c>; later events carry no
    /// session ID.
    /// </summary>
    private void ConsumeCopilot(JsonElement record, string? type, bool validDate, long at)
    {
        var data = Get(record, "data");
        if (type == "session.start" && Text(data, "sessionId") is { } id)
        {
            current = "copilot:" + id;
            if (!sessions.ContainsKey(current)) sessions[current] = new(current, Text(Get(data, "context"), "cwd") ?? "", null);
        }
        if (type is null || current is null || !sessions.TryGetValue(current, out var session)) return;
        // Where it works: at the start, on resuming, and when the folder or branch changes.
        if (type is "session.start" or "session.resume" or "session.context_changed")
        {
            var context = type == "session.context_changed" ? data : Get(data, "context");
            if (Text(context, "cwd") is { } cwd) session.Cwd = cwd;
            if (Text(context, "branch") is { } branch) session.Branches.Add(branch);
        }
        if (!validDate || !(type == "user.message" || type.StartsWith("assistant.", StringComparison.Ordinal)
            || type.StartsWith("tool.", StringComparison.Ordinal) || type.StartsWith("subagent.", StringComparison.Ordinal))) return;
        // A subagent's events are the agent's work, never a prompt; autopilot's continuations, skills' hidden text and
        // other agents' messages (source) aren't the person's, and neither is a slash command.
        var subagent = Text(record, "agentId") is not null;
        var content = Text(data, "content") ?? "";
        var touch = type == "user.message" && !subagent && Text(data, "source") is null
            && !Flag(data, "isAutopilotContinuation") && content.Length > 0 && !IsCommand(content);
        string? title = null;
        if (touch && session.Title.Length == 0) session.Title = title = Title(content);
        Add(session, at, touch, title);
        if (type == "tool.execution_start" && !subagent)
            Note(session, Text(data, "mcpServerName") is { } server ? OwnServers.Contains(server) ? Call.Own : Call.Other
                : Text(data, "toolName") == "report_intent" ? Call.Neutral : Classify(Text(data, "toolName"), null, null, null));
    }

    /// <summary>A row of a source's title index (<see cref="AgentLogFormat.TitleIndex"/>): the session it names and its title.</summary>
    public static (string Id, string Title)? IndexTitle(ReadOnlyMemory<byte> line, AgentSource source)
    {
        if (source != AgentSource.Codex) return null;
        try
        {
            using var json = JsonDocument.Parse(line);
            return Text(json.RootElement, "id") is { } id && Text(json.RootElement, "thread_name") is { } title
                ? ("codex:" + id, string.Concat(title.EnumerateRunes().Take(80).Select(r => r.ToString()))) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// A slash command such as <c>/model</c> or <c>/mcp add</c>: a word after one slash. A prompt that starts with a
    /// path (<c>/Users/me/app fails</c>) is a prompt.
    /// </summary>
    public static bool IsCommand(string text)
    {
        if (!text.StartsWith('/')) return false;
        var word = text[1..].Split((char[]?)null, 2)[0];
        return word.Length > 0 && !word.Contains('/');
    }

    /// <summary>The names Spindle's MCP server is added under, as the Kit's <c>AgentLogParser.ownServers</c>.</summary>
    public static IReadOnlyList<string> OwnServers { get; } = ["spindle"];

    private void Add(Session session, long at, bool touch, string? title)
    {
        if (touch) { session.Turn = null; session.TurnTitle = title; }
        if (at < from || at >= to) return;
        if (touch) session.Turn = session.Events.Count;
        session.Events.Add(new(at, touch));
    }

    private static void Note(Session session, Call call)
    {
        if (call == Call.Neutral || session.Turn is not { } index) return;
        var isTouch = call == Call.Other;
        session.Events[index] = session.Events[index] with { IsTouch = isTouch };
        if (session.TurnTitle is { } title) session.Title = isTouch ? title : "";
        if (isTouch) session.Turn = null;
    }

    private enum Call { Own, Neutral, Other }
    private static Call Classify(string? name, string? ns, string? type, string? input)
    {
        if (type == "custom_tool_call" && name == "exec" && ns is null && input is not null)
        {
            var names = ScriptTools().Matches(input).Select(m => m.Groups[1].Value)
                .Concat(McpTools().Matches(input).Select(m => m.Value));
            var calls = names.Select(n => Classify(n, null, null, null)).ToArray();
            return calls.Contains(Call.Other) ? Call.Other : calls.Contains(Call.Own) ? Call.Own : Call.Neutral;
        }
        if (OwnServers.Any(s => ns == "mcp__" + s || name?.StartsWith("mcp__" + s + "__", StringComparison.Ordinal) == true)) return Call.Own;
        if (name == "ToolSearch" || type == "tool_search_call" || ns == "clock"
            || name?.StartsWith("clock__", StringComparison.Ordinal) == true) return Call.Neutral;
        return Call.Other;
    }

    private static bool IsPrompt(JsonElement content) => content.ValueKind == JsonValueKind.String
        || content.ValueKind == JsonValueKind.Array && !content.EnumerateArray().Any(b => Text(b, "type") == "tool_result");
    private static string ContentTitle(JsonElement content) => Title(content.ValueKind == JsonValueKind.String
        ? content.GetString() ?? "" : content.ValueKind == JsonValueKind.Array
        ? content.EnumerateArray().Select(b => Text(b, "text")).FirstOrDefault(t => t is not null) ?? "" : "");
    private static string Title(string text) => string.Concat(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault()?.EnumerateRunes().Take(80).Select(r => r.ToString()) ?? []);
    private static JsonElement Get(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) ? value : default;
    private static string? Text(JsonElement element, string name) => Get(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static bool Flag(JsonElement element, string name) => Get(element, name).ValueKind == JsonValueKind.True;
    [GeneratedRegex("tools\\s*(?:\\.\\s*|\\[\\s*[\"'`])([A-Za-z0-9_-]+)")]
    private static partial Regex ScriptTools();
    [GeneratedRegex("mcp__[A-Za-z0-9_-]+?__[A-Za-z0-9_-]+")]
    private static partial Regex McpTools();
}
