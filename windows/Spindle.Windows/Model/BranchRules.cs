using Spindle.Activity;
using Spindle.Core;

namespace Spindle.Windows.Model;

/// <summary>A repository the person linked to a project, or said goes to no project (null), with its branch rules, as Settings lists them.</summary>
public sealed record LinkedRepository(AgentRepository Repository, Guid? ProjectId, IReadOnlyList<BranchRule> Rules);

/// <summary>What the person chose for a repository: a project (with branch rules, null keeping them), no project, or neither, leaving it to a learned link or its name.</summary>
public readonly record struct RepositoryChoice(Guid? ProjectId, bool NoProject, IReadOnlyList<BranchRule>? Rules)
{
    public static RepositoryChoice None => new(null, true, null);
    public static RepositoryChoice Forgotten => new(null, false, null);
}

// Branch rules and "Agents now", as the Mac's SplitAdvisor: rules go with the repository's link to the account, so
// every device places the work alike.
public sealed partial class SplitAdvisor
{
    /// <summary>Links chosen on this PC that the account doesn't have yet, with their rules when those changed (null keeps the account's).</summary>
    private readonly Dictionary<string, (AgentRepository Repository, RepositoryChoice Choice)> unsent = [];

    /// <summary>Linked repositories' names, from the account, for those the logs read so far don't name.</summary>
    private readonly Dictionary<string, string> names = [];

    /// <summary>How far back "Agents now" looks.</summary>
    public static readonly TimeSpan AgentsNowWindow = TimeSpan.FromMinutes(15);

    /// <summary>The branch rules on a repository's link, in their order.</summary>
    public IReadOnlyList<BranchRule> Rules(AgentRepository repository) => rules.GetValueOrDefault(repository.Identity) ?? [];

    /// <summary>The project a repository's other branches go to: the person's link, else the one learned or named like it.</summary>
    public Guid? ProjectOf(AgentRepository repository) => IsNoProject(repository) ? null : Chosen(repository) ?? Matcher()(repository);

    /// <summary>Whether the person said the repository's work goes to no project.</summary>
    public bool IsNoProject(AgentRepository repository) => noProject.Contains(repository.Identity);

    /// <summary>What the person chose for a repository now, to put back with <see cref="ChooseAsync"/>.</summary>
    public RepositoryChoice ChoiceOf(AgentRepository repository) =>
        IsNoProject(repository) ? RepositoryChoice.None
        : Chosen(repository) is { } projectId ? new RepositoryChoice(projectId, false, Rules(repository))
        : RepositoryChoice.Forgotten;

    /// <summary>The repositories the person linked to a project in this workspace, and those they said go to no project, by name.</summary>
    public List<LinkedRepository> Linked() =>
        chosen.Where(c => owner.Catalog.Project(c.Value) is not null)
            .Select(c => new LinkedRepository(Repository(c.Key), c.Value, rules.GetValueOrDefault(c.Key) ?? []))
            .Concat(noProject.Select(identity => new LinkedRepository(Repository(identity), null, [])))
            .OrderBy(l => l.Repository.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>A linked repository as the activity names it, or as its link was named.</summary>
    public AgentRepository Repository(string identity) =>
        days.Values.SelectMany(d => d.Snapshot.Attention).Select(a => a.Repository).FirstOrDefault(r => r?.Identity == identity)
        ?? new AgentRepository(identity, names.GetValueOrDefault(identity) ?? identity.Split('/').Last());

    /// <summary>The branches agents worked on in a repository, in the activity read so far, the most attention first.</summary>
    public List<string> Branches(AgentRepository repository)
    {
        var seconds = new Dictionary<string, long>();
        foreach (var segment in days.Values.SelectMany(d => d.Snapshot.Attention).Distinct().Where(a => a.Repository?.Identity == repository.Identity))
            foreach (var branch in segment.Branches) seconds[branch] = seconds.GetValueOrDefault(branch) + segment.Duration;
        return seconds.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).Select(s => s.Key).ToList();
    }

    /// <summary>
    /// Links a repository to <paramref name="projectId"/> with these branch rules, which replace those it had (null
    /// keeps them), on every device. Kept on this PC until the account has it.
    /// </summary>
    public Task LinkAsync(AgentRepository repository, Guid projectId, IReadOnlyList<BranchRule>? branchRules) =>
        ChooseAsync(repository, new RepositoryChoice(projectId, false, branchRules));

    /// <summary>Says a repository's work goes to no project, on every device: its link and branch rules go, and neither a learned link nor its name takes its work.</summary>
    public Task UnlinkAsync(AgentRepository repository) => ChooseAsync(repository, RepositoryChoice.None);

    /// <summary>Saves what the person chose for a repository, e.g. putting back what it was before an Unlink. Kept on this PC until the account has it.</summary>
    public async Task ChooseAsync(AgentRepository repository, RepositoryChoice choice)
    {
        var identity = repository.Identity;
        names[identity] = repository.Name;
        Apply(identity, choice);
        var rulesToSend = choice.ProjectId is null ? null
            : choice.Rules ?? (unsent.TryGetValue(identity, out var pending) && pending.Choice.ProjectId is not null ? pending.Choice.Rules : null);
        unsent[identity] = (repository, choice with { Rules = rulesToSend });
        Revision++;
        Changed?.Invoke();
        await SendAsync(identity);
    }

    private void Apply(string identity, RepositoryChoice choice)
    {
        if (choice.ProjectId is { } projectId)
        {
            chosen[identity] = projectId;
            noProject.Remove(identity);
            if (choice.Rules is { } own) rules[identity] = own;
            return;
        }
        chosen.Remove(identity);
        rules.Remove(identity);
        if (choice.NoProject) noProject.Add(identity);
        else noProject.Remove(identity);
    }

    /// <summary>Asks the account's links again, e.g. after another device changed them.</summary>
    public async Task ReloadLinksAsync()
    {
        if (accountFor is null) return;
        accountFor = null;
        await LoadAsync([]);
    }

    /// <summary>The account's links and rules; those chosen here and not yet sent still count.</summary>
    private void Absorb(IReadOnlyList<RepositoryLinkResponse> links)
    {
        chosen.Clear();
        rules.Clear();
        noProject.Clear();
        foreach (var link in links)
        {
            names[link.Identity] = link.Name;
            if (link.ProjectId is not { } projectId)
            {
                noProject.Add(link.Identity);
                continue;
            }
            chosen[link.Identity] = projectId;
            if (link.BranchRules.Length > 0) rules[link.Identity] = link.BranchRules.Select(r => new BranchRule(r.Pattern, r.ProjectId, r.TaskId)).ToList();
        }
        foreach (var (identity, link) in unsent) Apply(identity, link.Choice);
    }

    private async Task SendUnsentAsync()
    {
        foreach (var identity in unsent.Keys.ToList()) await SendAsync(identity);
    }

    /// <summary>
    /// Sends a link chosen here. Without a connection it waits for the next time the account's links are read; a link
    /// the server turns down is said to the caller.
    /// </summary>
    private async Task SendAsync(string identity)
    {
        if (owner.Api is not { } api || !unsent.TryGetValue(identity, out var link)) return;
        try
        {
            await api.LinkRepositoryAsync(owner.WorkspaceId, new RepositoryLinkRequest
            {
                Repository = new AgentRepositoryDto { Identity = identity, Name = link.Repository.Name },
                ProjectId = link.Choice.ProjectId,
                NoProject = link.Choice.NoProject,
                BranchRules = link.Choice.Rules?.Select(r => new BranchRuleDto { Pattern = r.Pattern, ProjectId = r.ProjectId, TaskId = r.TaskId }).ToArray(),
            });
            if (unsent.TryGetValue(identity, out var now) && now == link) unsent.Remove(identity);
        }
        catch (ApiException)
        {
            if (unsent.TryGetValue(identity, out var now) && now == link) unsent.Remove(identity);
            throw;
        }
        catch (Exception)
        {
            // Sent again when the account's links are read next.
        }
    }

    /// <summary>
    /// The projects agents were at work in during the last <see cref="AgentsNowWindow"/>, the most attention first,
    /// each with the task a branch rule names: what the project picker offers first. Repositories without a project,
    /// and folders outside git, don't count.
    /// </summary>
    public List<SplitPlacement> AgentsNow(DateTimeOffset? at = null)
    {
        if (!Enabled) return [];
        var now = at ?? Now();
        var from = now - AgentsNowWindow;
        var time = owner.Time;
        var segments = new[] { time.Day(from), time.Day(now) }.Distinct()
            .SelectMany(d => days.TryGetValue(d, out var read) ? read.Snapshot.Attention : []).Distinct().ToList();
        if (segments.Count == 0) return [];
        var place = Placing();
        var window = new TimeRange(from.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds());
        var seconds = new Dictionary<SplitPlacement, long>();
        foreach (var segment in segments.Where(s => s.Repository is not null))
        {
            if (new TimeRange(segment.Start, segment.End).Intersect(window) is not { Duration: > 0 } overlap || place(segment) is not { } placement) continue;
            seconds[placement] = seconds.GetValueOrDefault(placement) + overlap.Duration;
        }
        return seconds.OrderByDescending(s => s.Value).ThenBy(s => s.Key.ProjectId).Select(s => s.Key).ToList();
    }

    /// <summary>Reads today's activity (and yesterday's, just after midnight) for "Agents now", unless it was read lately.</summary>
    public Task LoadNowAsync()
    {
        var now = Now();
        return LoadAsync(new[] { owner.Time.Day(now - AgentsNowWindow), owner.Time.Day(now) }.Distinct());
    }
}
