using Spindle.Core;

namespace Spindle.Windows.Tests;

/// <summary>What saving a proposal sends, as the Mac's AgentProposalTests do; the review does it for the server's proposals.</summary>
public sealed class AgentProposalTests
{
    private static readonly Guid P1 = Guid.NewGuid(), P2 = Guid.NewGuid(), T1 = Guid.NewGuid(), Tag = Guid.NewGuid();
    private static readonly DateTimeOffset Nine = new(2026, 10, 12, 9, 0, 0, TimeSpan.FromHours(2));
    private static DateTimeOffset At(int hours) => Nine.AddHours(hours - 9);

    private static TimeEntryResponse Entry(string description = "", bool split = false) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), Description = description, ProjectId = P1, TagIds = [Tag], IsBillable = true, Start = At(9), Stop = At(10),
        Shares = split ? [new() { ProjectId = P1, IsBillable = true, Percent = 60 }, new() { ProjectId = P2, IsBillable = false, Percent = 40 }] : [],
    };

    private static AgentProposal Proposal(Guid? entryId, ProposalFields fields) => new()
    {
        WorkspaceId = Guid.NewGuid(), EntryId = entryId, Fields = fields, Client = "Claude Code", ProposedAt = At(12),
    };

    [Fact]
    public void A_change_applies_to_the_entry_as_it_is_now()
    {
        var before = Entry("Old words");
        var proposal = Proposal(before.Id, new(Work: ProposalWork.One(P2, null, false)));
        var request = proposal.Request(before with { Description = "Written since" });
        Assert.Equal("Written since", request.Description);
        Assert.Equal(P2, request.ProjectId);
        Assert.False(request.IsBillable);
        Assert.Equal([Tag], request.TagIds!);
        Assert.Equal(At(9), request.Start);
        Assert.Equal(At(10), request.Stop);
    }

    [Fact]
    public void A_new_entry_is_what_was_proposed()
    {
        var proposal = Proposal(null, new(At(9), At(10), "Fix login", ProposalWork.One(P1, T1, true)));
        Assert.True(proposal.IsNew);
        var request = proposal.Request(null);
        Assert.Equal(At(9), request.Start);
        Assert.Equal(At(10), request.Stop);
        Assert.Equal("Fix login", request.Description);
        Assert.Equal(P1, request.ProjectId);
        Assert.Equal(T1, request.TaskId);
    }

    [Fact]
    public void A_proposed_split_can_describe_each_project()
    {
        var work = new ProposalWork(P1, null, true, [new(P1, null, true, 60, "Fix login"), new(P2, null, false, 40)]);
        var request = Proposal(null, new(At(9), At(10), null, work)).Request(null);
        Assert.True(request.DescribeParts);
        Assert.Equal(["Fix login", ""], request.Shares!.Select(s => s.Description));
        Assert.Equal("Fix login", request.Description);
    }

    [Fact]
    public void A_changed_split_keeps_its_parts_descriptions_unless_one_description_replaces_them()
    {
        var described = Entry("Fix login; Docs", split: true);
        described = described with { Shares = [described.Shares[0] with { Description = "Fix login" }, described.Shares[1] with { Description = "Docs" }] };

        var reshared = Proposal(described.Id, new(Work: new ProposalWork(P1, null, true, [new(P1, null, true, 70, "Fix login"), new(P2, null, false, 30, "Docs")])));
        var request = reshared.Request(described);
        Assert.Equal([70, 30], request.Shares!.Select(s => s.Percent));
        Assert.Equal(["Fix login", "Docs"], request.Shares!.Select(s => s.Description));
        Assert.True(request.DescribeParts);

        var shared = Proposal(described.Id, new(Description: "Fix login; Docs")).Request(described);
        Assert.False(shared.DescribeParts);
        Assert.Equal("Fix login; Docs", shared.Description);
        Assert.All(shared.Shares!, s => Assert.Null(s.Description));

        var newWords = Proposal(described.Id, new(Work: new ProposalWork(P1, null, true, [new(P1, null, true, 60, "Fix the login"), new(P2, null, false, 40, "Docs")])));
        Assert.Equal("Fix the login; Docs", newWords.Request(described).Description);
    }
}
