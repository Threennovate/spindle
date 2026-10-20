using Spindle.Core;
using Spindle.Windows.Model;

namespace Spindle.Windows.Desktop;

/// <summary>
/// Entries and changes the person's agents proposed, waiting on the server in the workspace shown, as the app answers
/// them: added, edited or discarded, one at a time or all at once, and taken back while the review is open. The server
/// says what stands in each one's way, saves them through the same checks as an entry, and lets them go after a week.
/// As the Mac's ProposalInbox.
/// </summary>
public sealed class ProposalInbox(MainWindow owner)
{
    /// <summary>Proposals added or applied since the review opened, which it can still take back.</summary>
    public sealed record Done(AgentProposal Proposal, Guid EntryId, TimeEntryResponse? Before);

    private readonly List<Done> done = [];
    private readonly HashSet<string> seen = [];
    private List<AgentProposal> pending = [];
    private Dictionary<string, List<ProposalIssue>> issues = [];
    private Guid? loadedFor;
    private bool hasLoaded;

    /// <summary>The proposals or what stands in their way changed.</summary>
    public event Action? Changed;
    /// <summary>Proposals arrived from an agent: how many, and who sent them.</summary>
    public event Action<int, string?>? Arrived;

    public IReadOnlyList<Done> Saved => done;

    /// <summary>The proposals waiting in the workspace shown, oldest first.</summary>
    public AgentProposal[] Pending => owner.Workspace is { } workspace && loadedFor == workspace.Id ? [.. pending] : [];

    /// <summary>The change waiting for an entry, if any.</summary>
    public AgentProposal? ChangeFor(Guid entryId) => Pending.FirstOrDefault(p => p.EntryId == entryId);

    public TimeEntryResponse? Entry(Guid? id) => id is null ? null : owner.Entries.FirstOrDefault(e => e.Id == id);

    /// <summary>The proposal as saving it would send it, on the entry as it is now.</summary>
    public TimeEntryRequest Request(AgentProposal proposal) => proposal.Request(Entry(proposal.EntryId));

    /// <summary>What stands in the way of a proposal now, as the server said when it was last read.</summary>
    public List<ProposalIssue> Issues(AgentProposal proposal, IReadOnlyList<AgentProposal>? pending = null) =>
        issues.TryGetValue(proposal.Id, out var found) ? found : [];

    /// <summary>The days the proposals fall on, for the entries already there to be loaded and compared with.</summary>
    public DateOnly[] Days(IReadOnlyList<AgentProposal> proposals)
    {
        var times = proposals.SelectMany(p => new[] { p.Fields.Start ?? p.Original?.Start, p.Fields.Stop ?? p.Original?.Stop }).OfType<DateTimeOffset>().ToArray();
        if (times.Length == 0) return [];
        var from = owner.Time.Day(times.Min());
        var to = owner.Time.Day(times.Max());
        return Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays).ToArray();
    }

    /// <summary>
    /// Reads the workspace's proposals from the server: on every refresh of the window (sign-in, a workspace chosen, a
    /// sync message, polling) and after each answer. Offline, what was read last stays.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (owner.Api is not { } api || owner.Workspace is not { } workspace) return;
        if (loadedFor != workspace.Id)
        {
            loadedFor = workspace.Id;
            hasLoaded = false;
            seen.Clear();
            pending = [];
            issues = [];
            done.Clear();
        }
        AgentProposalsResponse list;
        try
        {
            list = await api.GetAgentProposalsAsync(workspace.Id);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or ApiException)
        {
            return;
        }
        if (loadedFor != workspace.Id) return;
        var proposals = list.Pending.Select(FromServer).ToList();
        var arrived = proposals.Where(p => !seen.Contains(p.Id)).ToList();
        pending = proposals;
        issues = list.Pending.ToDictionary(p => p.Id.ToString("D"),
            p => p.Blocking.Select(m => new ProposalIssue(m, true)).Concat(p.Warnings.Select(m => new ProposalIssue(m, false))).ToList());
        seen.UnionWith(proposals.Select(p => p.Id));
        Changed?.Invoke();
        // Those waiting when the app first reads them aren't news.
        if (hasLoaded && arrived.Count > 0) Arrived?.Invoke(arrived.Count, arrived[^1].Client);
        hasLoaded = true;
    }

    /// <summary>A proposal as the server has it, as the review shows it.</summary>
    public static AgentProposal FromServer(AgentProposalResponse proposal)
    {
        static ProposalPart[] Parts(TimeShareResponse[] shares) => shares.Length > 1 ? shares.Select(ProposalPart.From).ToArray() : [];
        return new AgentProposal
        {
            Id = proposal.Id.ToString("D"),
            WorkspaceId = proposal.WorkspaceId,
            EntryId = proposal.EntryId,
            Original = proposal.Original is { } original ? new TimeEntryResponse
            {
                Id = proposal.EntryId ?? Guid.Empty, WorkspaceId = proposal.WorkspaceId, Description = original.Description, ProjectId = original.ProjectId,
                TaskId = original.TaskId, IsBillable = original.IsBillable, Start = original.Start, Stop = original.Stop, Shares = original.Shares.Length > 1 ? original.Shares : [],
            } : null,
            Fields = new ProposalFields(proposal.Fields.Start, proposal.Fields.Stop, proposal.Fields.Description,
                proposal.Fields.Work is { } work ? new ProposalWork(work.ProjectId, work.TaskId, work.IsBillable, Parts(work.Shares)) : null),
            Reason = proposal.Reason,
            Client = proposal.Client,
            ProposedAt = proposal.ProposedAt,
        };
    }

    /// <summary>Reads the proposals, then loads the entries of their days, and the day before for one running over midnight.</summary>
    public async Task LoadAsync()
    {
        await RefreshAsync();
        var days = Days(Pending);
        if (days.Length == 0) return;
        await owner.LoadEntriesAsync(owner.Time.StartOf(days[0].AddDays(-1)), owner.Time.StartOf(days[^1].AddDays(1)));
    }

    /// <summary>Saves the proposal as it is, on the server, which checks it as an entry.</summary>
    public async Task AcceptAsync(AgentProposal proposal)
    {
        await ApplyAsync(proposal);
        await owner.RefreshSafelyAsync();
    }

    private async Task ApplyAsync(AgentProposal proposal)
    {
        var current = Entry(proposal.EntryId);
        var applied = await owner.Api!.ApplyAgentProposalAsync(proposal.WorkspaceId, Guid.Parse(proposal.Id));
        pending.RemoveAll(p => p.Id == proposal.Id);
        done.Add(new(proposal, applied.Entry.Id, current));
    }

    /// <summary>Adds every proposal of the workspace that nothing stands in the way of; says how many it added and how many it couldn't.</summary>
    public async Task<(int Added, int Failed)> AcceptAllAsync()
    {
        var ready = Pending.Where(p => !Issues(p).Any(i => i.Blocking)).ToArray();
        int added = 0, failed = 0;
        foreach (var proposal in ready)
        {
            try
            {
                await ApplyAsync(proposal);
                added++;
            }
            catch (Exception error) when (error is ApiException or HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                failed++;
            }
        }
        await owner.RefreshSafelyAsync();
        return (added, failed);
    }

    /// <summary>The person saved the proposal in the editor, as they changed it, as the entry <paramref name="entryId"/>.</summary>
    public Task SavedInEditorAsync(AgentProposal proposal, Guid? entryId) =>
        AnswerAsync(proposal, api => api.SavedAgentProposalAsync(proposal.WorkspaceId, Guid.Parse(proposal.Id), entryId ?? proposal.EntryId));

    public Task DiscardAsync(AgentProposal proposal) =>
        AnswerAsync(proposal, api => api.DiscardAgentProposalAsync(proposal.WorkspaceId, Guid.Parse(proposal.Id)));

    public async Task DiscardAllAsync()
    {
        foreach (var proposal in Pending) await DiscardAsync(proposal);
    }

    /// <summary>Tells the server, then reads the proposals again, so one the server refused comes back.</summary>
    private async Task AnswerAsync(AgentProposal proposal, Func<SpindleApiClient, Task> answer)
    {
        pending.RemoveAll(p => p.Id == proposal.Id);
        Changed?.Invoke();
        try
        {
            await answer(owner.Api!);
        }
        finally
        {
            await RefreshAsync();
        }
    }

    /// <summary>Takes back an entry added or a change applied in this review, and puts the proposal back.</summary>
    public async Task UndoAsync(Done item)
    {
        var entry = Entry(item.EntryId) ?? throw new InvalidOperationException("The entry is gone.");
        if (item.Before is { } before)
            await owner.UpdateEntryAsync(entry, before.Request() with { DescribeParts = before.Shares.Length > 1 ? SplitMath.DescribesParts(before) : null });
        else
            await owner.DeleteEntryAsync(entry);
        done.Remove(item);
        await owner.Api!.ReopenAgentProposalAsync(item.Proposal.WorkspaceId, Guid.Parse(item.Proposal.Id));
        await RefreshAsync();
    }

    /// <summary>The review closed: what was done there stays done.</summary>
    public void EndReview() => done.Clear();

    /// <summary>Signed out: nothing of the account stays.</summary>
    public void Clear()
    {
        loadedFor = null;
        hasLoaded = false;
        seen.Clear();
        pending = [];
        issues = [];
        done.Clear();
    }
}
