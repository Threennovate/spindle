namespace Spindle.Activity;

public enum AgentSource { Claude, Codex, Copilot }
public sealed record AgentRepository(string Identity, string Name);
public readonly record struct AgentEvent(long At, bool IsTouch);
/// <summary>Only local metadata; prompt text and answers are never retained.</summary>
public sealed record LocalAgentSession(string Id, AgentSource Source, string? ParentId, string Cwd,
    string Title, IReadOnlyList<string> Branches, IReadOnlyList<AgentEvent> Events);
public sealed record AgentRun(string SessionId, AgentSource Source, AgentRepository? Repository, long Start, long End);
public sealed record ActivitySnapshot(IReadOnlyList<LocalAgentSession> Sessions,
    IReadOnlyList<AttentionSegment> Attention, IReadOnlyList<AgentRun> Runs, IReadOnlyList<string> Errors)
{
    public static ActivitySnapshot Empty { get; } = new([], [], [], []);
}
