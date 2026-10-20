using Avalonia.Headless;
using Spindle.Core;
using Spindle.Windows.Desktop;
using static Spindle.Windows.Tests.TestUi;

namespace Spindle.Windows.Tests;

/// <summary>The proposals agents made, as the app reads them from the server and answers them there.</summary>
public sealed class ProposalInboxTests
{
    private static AgentProposal Proposal(Guid workspace, Guid project, string description, string client = "Codex") => new()
    {
        WorkspaceId = workspace,
        Fields = new(TestClock.Now.AddHours(-3), TestClock.Now.AddHours(-2), description, ProposalWork.One(project, null, false)),
        Reason = "Commits on main",
        Client = client,
        ProposedAt = TestClock.Now,
    };

    [Fact]
    public async Task Proposals_made_on_the_server_show_on_the_next_refresh_and_follow_the_workspace()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            fixture.Propose([Proposal(fixture.Workspace, fixture.Projects[0].Id, "Waiting before sign-in")]);
            var window = await SignedIn(fixture);
            await WaitFor(() => window.Proposals.Pending.Length == 1);
            // Those already waiting aren't news.
            Assert.DoesNotContain("proposed", ErrorText(window));

            // A sync message (or polling) refreshes the window, which reads them again.
            fixture.Propose([Proposal(fixture.Workspace, fixture.Projects[1].Id, "Review the API")]);
            await window.RefreshSafelyAsync();
            Assert.Equal(["Waiting before sign-in", "Review the API"], window.Proposals.Pending.Select(p => p.Fields.Description));
            Assert.Contains("Codex proposed a change to your time.", ErrorText(window));

            // Another workspace has its own.
            var other = Guid.NewGuid();
            fixture.Propose([Proposal(other, fixture.Projects[0].Id, "Elsewhere")]);
            await window.SwitchWorkspaceAsync(new WorkspaceSummary { Id = other, OrganizationId = fixture.Organization, Name = "Other", Role = WorkspaceRole.Admin });
            Assert.Equal(["Elsewhere"], window.Proposals.Pending.Select(p => p.Fields.Description));
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Answers_go_to_the_server_and_a_refused_one_comes_back()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var first = Proposal(fixture.Workspace, fixture.Projects[0].Id, "Fix the login redirect");
            var second = Proposal(fixture.Workspace, fixture.Projects[1].Id, "Review the API");
            fixture.Propose([first, second]);
            var window = await SignedIn(fixture);
            await WaitFor(() => window.Proposals.Pending.Length == 2);

            await window.Proposals.AcceptAsync(window.Proposals.Pending[0]);
            Assert.Contains(fixture.Entries, e => e.Description == "Fix the login redirect");
            Assert.Equal(("added", fixture.Entries.Single(e => e.Description == "Fix the login redirect").Id), (fixture.ProposalAnswers[0].Outcome, fixture.ProposalAnswers[0].EntryId!.Value));
            Assert.Single(window.Proposals.Saved);

            await window.Proposals.DiscardAsync(window.Proposals.Pending.Single());
            Assert.Equal("discarded", fixture.ProposalAnswers[1].Outcome);
            Assert.Empty(window.Proposals.Pending);

            // Answered elsewhere in the meantime: the server refuses, and the list shows what it has.
            fixture.Propose([Proposal(fixture.Workspace, fixture.Projects[0].Id, "Gone")]);
            await window.Proposals.RefreshAsync();
            var gone = window.Proposals.Pending.Single();
            fixture.ServerProposals.Clear();
            await Assert.ThrowsAsync<ApiException>(() => window.Proposals.DiscardAsync(gone));
            Assert.Empty(window.Proposals.Pending);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public void Server_proposals_become_the_reviews_with_their_split()
    {
        var project = Guid.NewGuid();
        var response = new AgentProposalResponse
        {
            Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), EntryId = Guid.NewGuid(), Reason = "Ran late", Client = "Claude Code", ProposedAt = TestClock.Now,
            Original = new() { Start = TestClock.Now.AddHours(-2), Stop = TestClock.Now.AddHours(-1), Description = "Standup", ProjectId = project, IsBillable = true },
            Fields = new()
            {
                Work = new() { ProjectId = project, IsBillable = true, Shares = [
                    new() { ProjectId = project, Percent = 70, IsBillable = true, Description = "Fix" },
                    new() { ProjectId = Guid.NewGuid(), Percent = 30, Description = "Review" }] },
            },
        };
        var proposal = ProposalInbox.FromServer(response);
        Assert.Equal(response.Id.ToString("D"), proposal.Id);
        Assert.False(proposal.IsNew);
        Assert.Equal("Standup", proposal.Original!.Description);
        Assert.Equal([70, 30], proposal.Fields.Work!.Shares.Select(s => s.Percent));
        Assert.Equal(["Fix", "Review"], proposal.Fields.Work.Shares.Select(s => s.Description));
        Assert.Null(proposal.Fields.Start);
    }
}
