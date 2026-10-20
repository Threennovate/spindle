namespace Spindle.Domain.AgentActivity;

/// <summary>
/// A repository as the Mac app names it without its local path: by its normalised git <c>origin</c>
/// (<c>origin:github.com/owner/repo</c>), or by a hash of its path when it has none (<c>path:…</c>).
/// </summary>
public sealed record AgentRepository(string Identity, string Name)
{
    public const int IdentityMaxLength = 300;
    public const int NameMaxLength = 200;
}

/// <summary>
/// One agent session as a Mac uploaded it: when the person sent prompts (touches), when the agent was busy (runs),
/// and where. Only the person who uploaded it can read it. Prompt text never leaves the Mac, nor does the local path.
/// A session is replaced whole each time it is uploaded again.
/// </summary>
public sealed class AgentSession
{
    public const int SessionIdMaxLength = 128;
    public const int TitleMaxLength = 200;
    public const int BranchMaxLength = 255;
    public const int SourceMaxLength = 16;
    /// <summary>
    /// What an agent is called: <c>claude</c>, <c>codex</c>, <c>copilot</c>. The apps name the agents they read; the
    /// server only keeps them apart, so a new agent needs no new server.
    /// </summary>
    public const string SourcePattern = "^[a-z][a-z0-9]{0,15}$";
    public const int MaxBranches = 50;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>The Mac it came from, so two computers' sessions merge and one's can be removed alone.</summary>
    public Guid DeviceId { get; set; }

    /// <summary>The agent whose logs it came from (<see cref="SourcePattern"/>).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The session's ID in the agent's own logs.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>The session that started this one, for a subagent. Subagents make no touches.</summary>
    public string? ParentSessionId { get; set; }

    /// <summary>Null for a folder outside git, whose prompts still end the attention before them.</summary>
    public string? RepositoryIdentity { get; set; }

    public string? RepositoryName { get; set; }

    /// <summary>Only when the person lets titles be synced.</summary>
    public string? Title { get; set; }

    public string[] Branches { get; set; } = [];

    /// <summary>When the person sent each prompt, in Unix milliseconds, in order.</summary>
    public long[] Touches { get; set; } = [];

    /// <summary>The starts of the agent's runs (events less than three minutes apart), in Unix milliseconds.</summary>
    public long[] RunStarts { get; set; } = [];

    /// <summary>The ends of the runs, paired with <see cref="RunStarts"/>.</summary>
    public long[] RunEnds { get; set; } = [];

    public DateTimeOffset FirstAt { get; set; }

    public DateTimeOffset LastAt { get; set; }

    /// <summary>The version of the uploading app's log parser (each platform counts its own; not comparable across devices); a newer one sends the session again.</summary>
    public int ParserVersion { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    public AgentRepository? Repository =>
        RepositoryIdentity is { } identity ? new AgentRepository(identity, RepositoryName ?? identity) : null;
}

/// <summary>
/// The project a person chose for a repository's work, or none: a repository they said goes to no project, so neither
/// a learned link nor a project named like it takes its work. A repository belongs to no workspace; the project does.
/// </summary>
public sealed class AgentRepositoryLink
{
    public Guid UserId { get; set; }

    public string Identity { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>None when the person said the repository's work goes to no project; such a link has no branch rules.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// Branches whose work goes to another project of the same workspace, in the person's order: the first rule that
    /// covers a session's branches beats <see cref="ProjectId"/>.
    /// </summary>
    public List<BranchRule> BranchRules { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An entry the person said not to suggest a split for ("Keep"), on any of their devices.</summary>
public sealed class SplitSuggestionDismissal
{
    public Guid UserId { get; set; }

    public Guid EntryId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
