using System.ComponentModel.DataAnnotations;
using Spindle.Domain.AgentActivity;
using Spindle.Infrastructure.Identity;

namespace Spindle.Api.AgentActivity;

/// <summary>Whether the Mac app uploads agent activity for this person, so the web can suggest splits too.</summary>
/// <param name="Sync">Uploading is on, on all of the person's Macs. Off by default.</param>
/// <param name="Titles">Uploads include session titles.</param>
/// <param name="SyncedSince">When sync was last turned on. A Mac that uploaded before then uploads everything again.</param>
public sealed record AgentActivitySettingsResponse(bool Sync, bool Titles, DateTimeOffset? SyncedSince)
{
    public static AgentActivitySettingsResponse From(AppUser user) =>
        new(user.AgentActivitySync, user.AgentActivitySync && user.AgentActivityTitles, user.AgentActivitySync ? user.AgentActivitySyncedSince : null);
}

public sealed record AgentActivitySettingsRequest
{
    /// <summary>Turning it off deletes every session uploaded so far.</summary>
    [Required]
    public bool Sync { get; init; }

    /// <summary>Turning it off removes the titles uploaded so far.</summary>
    [Required]
    public bool Titles { get; init; }
}

/// <summary>
/// A repository without its local path: <c>origin:</c> and the normalised git origin without credentials
/// (<c>origin:github.com/owner/repo</c>), or <c>path:</c> and a hash of its path when it has no origin.
/// </summary>
public sealed record AgentRepositoryDto
{
    [Required]
    [StringLength(AgentRepository.IdentityMaxLength, MinimumLength = 6)]
    [RegularExpression(@"^(origin|path):\S+$", ErrorMessage = "Name a repository by origin: or path:.")]
    public string Identity { get; init; } = string.Empty;

    /// <summary>The repository's folder name, as the apps show it.</summary>
    [Required]
    [StringLength(AgentRepository.NameMaxLength, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    public static AgentRepositoryDto From(AgentRepository repository) => new() { Identity = repository.Identity, Name = repository.Name };
}

/// <summary>An interval in which the agent was busy, in Unix milliseconds.</summary>
public sealed record AgentRunDto
{
    [Required]
    public long Start { get; init; }

    [Required]
    public long End { get; init; }
}

/// <summary>One session as a Mac or Windows PC read it from the agent's logs. It replaces what was uploaded for it before.</summary>
public sealed record AgentSessionUpload
{
    /// <summary>The agent whose logs it came from, as the apps name it: <c>claude</c>, <c>codex</c>, <c>copilot</c>.</summary>
    [Required]
    [StringLength(AgentSession.SourceMaxLength, MinimumLength = 1)]
    [RegularExpression(AgentSession.SourcePattern, ErrorMessage = "Name the agent in lower-case letters and digits, starting with a letter.")]
    public string Source { get; init; } = string.Empty;

    /// <summary>The session's ID in the agent's logs.</summary>
    [Required]
    [StringLength(AgentSession.SessionIdMaxLength, MinimumLength = 1)]
    public string SessionId { get; init; } = string.Empty;

    /// <summary>The session that started this one, for a subagent.</summary>
    [StringLength(AgentSession.SessionIdMaxLength)]
    public string? ParentSessionId { get; init; }

    /// <summary>Where it ran; none for a folder outside git, whose prompts still end the attention before them.</summary>
    public AgentRepositoryDto? Repository { get; init; }

    /// <summary>Dropped unless the person lets titles be synced.</summary>
    [StringLength(AgentSession.TitleMaxLength)]
    public string? Title { get; init; }

    [Required]
    [MaxLength(AgentSession.MaxBranches)]
    public IReadOnlyList<string> Branches { get; init; } = [];

    /// <summary>When the person sent each prompt, in Unix milliseconds.</summary>
    [Required]
    [MaxLength(AgentActivityLimits.MaxTimes)]
    public IReadOnlyList<long> Touches { get; init; } = [];

    [Required]
    [MaxLength(AgentActivityLimits.MaxTimes)]
    public IReadOnlyList<AgentRunDto> Runs { get; init; } = [];

    /// <summary>The version of the uploading app's log parser (each platform counts its own; not comparable across devices).</summary>
    [Required]
    [Range(1, int.MaxValue)]
    public int ParserVersion { get; init; }
}

public sealed record AgentActivityUploadRequest
{
    /// <summary>A stable ID for the device, so sessions from two computers are told apart.</summary>
    [Required]
    public Guid DeviceId { get; init; }

    [Required]
    [MaxLength(AgentActivityLimits.MaxSessionsPerUpload)]
    public IReadOnlyList<AgentSessionUpload> Sessions { get; init; } = [];
}

/// <param name="Saved">How many sessions were kept; sessions without prompts or runs are not.</param>
public sealed record AgentActivityUploadResponse(int Saved);

/// <summary>
/// The project the person chose for a repository's work, and its workspace; both none for a repository they said goes
/// to no project, in any workspace.
/// </summary>
public sealed record RepositoryLinkResponse(string Identity, string Name, Guid? ProjectId, Guid? WorkspaceId, IReadOnlyList<BranchRuleDto> BranchRules);

public sealed record RepositoryLinkRequest
{
    [Required]
    public AgentRepositoryDto Repository { get; init; } = new();

    /// <summary>A project of this workspace, or none to forget the link and its branch rules.</summary>
    public Guid? ProjectId { get; init; }

    /// <summary>
    /// With no project: the repository goes to no project from now on, so neither a learned link nor a project named
    /// like it takes its work. Its branch rules go.
    /// </summary>
    public bool NoProject { get; init; }

    /// <summary>The repository's branch rules, in order, replacing those it had; none keeps them as they are.</summary>
    [MaxLength(BranchRule.MaxRules)]
    public IReadOnlyList<BranchRuleDto>? BranchRules { get; init; }
}

/// <summary>Work on branches matching the pattern goes to this project of the link's workspace, and task, if any.</summary>
public sealed record BranchRuleDto
{
    /// <summary>A branch name where <c>*</c> stands for any characters, <c>/</c> included: <c>acme/*</c>, <c>ACME-*</c>. Case doesn't matter.</summary>
    [Required]
    [StringLength(BranchRule.PatternMaxLength, MinimumLength = 1)]
    public string Pattern { get; init; } = string.Empty;

    [Required]
    public Guid ProjectId { get; init; }

    public Guid? TaskId { get; init; }

    public static BranchRuleDto From(BranchRule rule) => new() { Pattern = rule.Pattern, ProjectId = rule.ProjectId, TaskId = rule.TaskId };
}

/// <summary>Where part of the time went: a project, or a repository still to link to one.</summary>
public sealed record SplitTargetResponse
{
    public Guid? ProjectId { get; init; }

    public AgentRepositoryDto? Repository { get; init; }

    public static SplitTargetResponse From(SplitTarget target) => new()
    {
        ProjectId = target.ProjectId,
        Repository = target.Repository is { } repository ? AgentRepositoryDto.From(repository) : null,
    };
}

/// <param name="Repositories">The repositories whose attention makes up this part.</param>
/// <param name="AttentionSeconds">Time the agent logs place here.</param>
/// <param name="UnattributedSeconds">Time without agent activity, which stays on the entry's own project.</param>
/// <param name="Prompts">How many prompts the person sent here within the time.</param>
public sealed record SplitSuggestionPartResponse(
    SplitTargetResponse Target,
    IReadOnlyList<AgentRepositoryDto> Repositories,
    long AttentionSeconds,
    long UnattributedSeconds,
    int Percent,
    int Prompts);

public sealed record SplitStretchResponse(SplitTargetResponse Target, DateTimeOffset Start, DateTimeOffset End);

/// <summary>Time the agent logs place with a target: attention, or the time between two prompts for it.</summary>
public sealed record SplitPieceResponse(SplitTargetResponse Target, DateTimeOffset Start, DateTimeOffset End);

/// <summary>A span of time.</summary>
public sealed record TimeRangeResponse(DateTimeOffset Start, DateTimeOffset End);

/// <summary>A prompt the person sent, and where it went.</summary>
public sealed record SplitPromptResponse(SplitTargetResponse Target, DateTimeOffset At);

/// <summary>How an entry's time could be split, from where agent attention went while it ran.</summary>
/// <param name="Coverage">How much of the entry the agent logs account for, from 0 to 1.</param>
/// <param name="Stretches">
/// The entry as consecutive stretches, one per part, when the work moved from project to project; none when it went
/// back and forth.
/// </param>
/// <param name="Strength">
/// How firmly the logs back it: strong suggestions may interrupt (the card after a stop, a notification), weak ones
/// wait quietly beside the entry.
/// </param>
/// <param name="Start">The time the suggestion divides, which the evidence is drawn over; a running entry's ends now.</param>
/// <param name="Pieces">What the logs place within it, in order, including targets too small to be a part.</param>
/// <param name="Open">The time within it the logs don't account for, in order, which stays on <paramref name="Fallback"/>.</param>
/// <param name="Prompts">The person's prompts within it, in order.</param>
/// <param name="Fallback">The entry's own project, which keeps the time the logs leave open.</param>
public sealed record SplitSuggestionResponse(
    Guid EntryId,
    IReadOnlyList<SplitSuggestionPartResponse> Parts,
    double Coverage,
    IReadOnlyList<SplitStretchResponse>? Stretches,
    SplitStrength Strength,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<SplitPieceResponse> Pieces,
    IReadOnlyList<TimeRangeResponse> Open,
    IReadOnlyList<SplitPromptResponse> Prompts,
    SplitTargetResponse? Fallback)
{
    public static SplitSuggestionResponse From(Guid entryId, SplitSuggestion suggestion)
    {
        var touches = suggestion.Touches;
        return new(
            entryId,
            suggestion.Parts.Select(p => new SplitSuggestionPartResponse(
                SplitTargetResponse.From(p.Target),
                p.Repositories.Select(AgentRepositoryDto.From).ToList(),
                (long)Math.Round(p.Attention / 1000.0),
                (long)Math.Round(p.Unattributed / 1000.0),
                p.Percent,
                touches.GetValueOrDefault(p.Target))).ToList(),
            suggestion.Coverage,
            suggestion.Stretches?.Select(s => new SplitStretchResponse(SplitTargetResponse.From(s.Target), Time(s.Start), Time(s.End))).ToList(),
            suggestion.Strength,
            Time(suggestion.Start),
            Time(suggestion.End),
            suggestion.Pieces.Select(p => new SplitPieceResponse(SplitTargetResponse.From(p.Target), Time(p.Start), Time(p.End))).ToList(),
            suggestion.Open.Select(o => new TimeRangeResponse(Time(o.Start), Time(o.End))).ToList(),
            suggestion.Prompts.Select(p => new SplitPromptResponse(SplitTargetResponse.From(p.Target), Time(p.At))).ToList(),
            suggestion.Fallback is { } fallback ? SplitTargetResponse.From(fallback) : null);
    }

    internal static DateTimeOffset Time(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
}

/// <summary>
/// Agent attention no entry covers, for the calendar: the attention only (a prompt and up to 15 minutes after it),
/// never more than the logs show.
/// </summary>
/// <param name="ProjectIds">The projects its attention went to, most first; none when only repositories without a project.</param>
/// <param name="UnlinkedSeconds">Attention in repositories without a project.</param>
/// <param name="Parts">
/// The split its attention suggests when it went to more than one project or repository, as a new entry for it takes it,
/// with the first project keeping the time the logs leave open; none when it went to one.
/// </param>
/// <param name="Stretches">The same as consecutive stretches, when the work moved from project to project.</param>
public sealed record UntrackedTimeResponse(
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<Guid> ProjectIds,
    long UnlinkedSeconds,
    IReadOnlyList<SplitSuggestionPartResponse>? Parts,
    IReadOnlyList<SplitStretchResponse>? Stretches);

/// <summary>
/// A project agents worked in lately, for "Agents now" at the top of the project picker.
/// </summary>
/// <param name="LastAt">When its latest attention ended, up to now.</param>
/// <param name="Seconds">Its attention within the window.</param>
public sealed record AgentsNowResponse(Guid ProjectId, DateTimeOffset LastAt, long Seconds);

internal static class AgentActivityLimits
{
    public const int MaxSessionsPerUpload = 200;

    /// <summary>Prompts or runs in one session; a busy week-long Codex thread has a few thousand.</summary>
    public const int MaxTimes = 100_000;

    /// <summary>The most dismissed entries the apps are told about, newest first.</summary>
    public const int DismissalsListed = 500;

    /// <summary>Sessions that ended longer ago than this go, as on the Mac.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(400);
}
