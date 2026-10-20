using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Spindle.Core;
using Spindle.Windows.Model;
using Spindle.Windows.Views;
using static Spindle.Windows.Tests.TestUi;

namespace Spindle.Windows.Tests;

/// <summary>What agents propose, as the person reviews it, with what the server says stands in the way of each.</summary>
public sealed class ProposalReviewTests
{
    private static readonly TimeZoneInfo Zagreb = ApiFixture.Zagreb;
    private static readonly DateOnly Yesterday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(TestClock.Now, Zagreb).DateTime).AddDays(-1);
    private static DateTimeOffset At(double hours)
    {
        var time = Yesterday.ToDateTime(new TimeOnly((int)hours, (int)Math.Round(hours % 1 * 60)));
        return new(time, Zagreb.GetUtcOffset(time));
    }

    private static AgentProposal Proposal(ApiFixture fixture, Guid? entryId, TimeEntryResponse? original, ProposalFields fields, string reason) => new()
    {
        WorkspaceId = fixture.Workspace, EntryId = entryId, Original = original, Fields = fields, Reason = reason, Client = "Claude Code", ProposedAt = TestClock.Now,
    };

    /// <summary>Two new entries (one split described per project) and a change, as Claude Code would propose them filling in yesterday.
    /// The first overlaps a tracked entry, which the server says in a warning.</summary>
    private static (AgentProposal Added, AgentProposal Split, AgentProposal Change) Propose(MainWindow window, ApiFixture fixture)
    {
        var release = window.Entries.First(e => e.Description == "Design review with Acme");
        var (atlas, other) = (fixture.Projects[0], fixture.Projects[1]);
        var added = Proposal(fixture, null, null,
            new(At(11.75), At(12.75), "Fix the booking confirmation email", ProposalWork.One(atlas.Id, atlas.Tasks[0].Id, atlas.IsBillable)),
            "Commits on fix/booking-email 11:46–12:41");
        var split = Proposal(fixture, null, null,
            new(At(17.75), At(19.25), null, new ProposalWork(atlas.Id, null, atlas.IsBillable, [
                new(atlas.Id, null, atlas.IsBillable, 60, "Review the booking API"),
                new(other.Id, null, other.IsBillable, 40, "Release notes for 2.3")])),
            "atlas and spindle at the same time");
        var change = Proposal(fixture, release.Id, release, new(Stop: At(14.25)), "Call ended 14:12");
        fixture.Propose(added, warnings: ["Overlaps “Booking flow with agents”, 09:30–12:15."]);
        fixture.Propose(split);
        fixture.Propose(change);
        return (added, split, change);
    }

    [Fact]
    public async Task Proposals_wait_beside_the_entries_until_the_person_adds_edits_or_discards_them()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            TestData.SeedWeek(fixture, running: false);
            var window = await SignedIn(fixture);
            var (added, split, change) = Propose(window, fixture);
            await window.Proposals.RefreshAsync();
            await WaitFor(() => Controls<Border>(window).Any(b => b.Name == "ProposalBanner"));
            var banner = Controls<Border>(window).Single(b => b.Name == "ProposalBanner");
            var said = string.Concat(Controls<TextBlock>(banner).SelectMany(t => t.Inlines ?? []).OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
            Assert.Equal("Claude Code proposed 2 new entries and 1 change. Nothing is saved until you add it.", said);
            Assert.Contains(Controls<Button>(window), b => b.Name == "ProposalChip");
            await Preview(window, "proposals-banner");

            // On the calendar they wait beside the entries, dashed and not saved.
            await window.NavigateAsync(Section.Calendar);
            var calendar = Controls<CalendarView>(window).Single();
            await calendar.ShowAsync(Yesterday, dayMode: false);
            await WaitFor(() => Controls<Panel>(calendar).Count(p => p.Name == "ProposalGhost") == 3);
            await Preview(window, "proposals-calendar");
            await window.NavigateAsync(Section.Timer);

            var reviewing = window.ReviewProposalsAsync();
            var review = await Dialog<ProposalReview>(window);
            Assert.Equal("Proposed by Claude Code", Controls<TextBlock>(review).Single(t => t.Name == "ProposalsTitle").Text);
            Assert.Equal(3, Controls<Panel>(review).Count(p => p.Name == "Proposal"));
            // The split described per project shows a line per project.
            var parts = Controls<Grid>(review).Where(g => g.Name == "ProposalPart").ToArray();
            Assert.Equal(2, parts.Length);
            Assert.Contains("Review the booking API", ErrorText(parts[0]));
            Assert.Contains("Release notes for 2.3", ErrorText(parts[1]));
            Assert.Contains("Overlaps “Booking flow with agents”", ErrorText(review));
            Assert.Contains("“Call ended 14:12”", ErrorText(review));
            await Preview(window, "proposals-review");

            // Adding one saves it as proposed; it can be taken back while the review is open.
            var writes = fixture.Writes.Count;
            var first = Controls<Panel>(review).First(p => p.Name == "Proposal" && ErrorText(p).Contains("booking confirmation"));
            await Press(Controls<Button>(first).Single(b => b.Name == "ProposalAccept"));
            await WaitFor(() => fixture.Entries.Any(e => e.Description == "Fix the booking confirmation email"));
            var created = fixture.Entries.Single(e => e.Description == "Fix the booking confirmation email");
            Assert.Equal(At(11.75), created.Start);
            Assert.Equal(fixture.Projects[0].Tasks[0].Id, created.TaskId);
            await WaitFor(() => Controls<Border>(review).Any(b => b.Name == "ProposalSaved"));
            Assert.Equal("added", fixture.ProposalAnswers.Single().Outcome);
            await Preview(window, "proposals-review-saved");
            await Press(Controls<Button>(review).Single(b => b.Name == "ProposalUndo"));
            await WaitFor(() => fixture.Entries.All(e => e.Description != "Fix the booking confirmation email"));
            Assert.Equal(3, window.Proposals.Pending.Length);
            Assert.True(fixture.Writes.Count > writes);

            // Applying the change ends the design review at 14:15.
            var changeRow = Controls<Panel>(review).First(p => p.Name == "Proposal" && ErrorText(p).Contains("Change"));
            await Press(Controls<Button>(changeRow).Single(b => b.Name == "ProposalAccept"));
            await WaitFor(() => fixture.Entries.Single(e => e.Id == change.EntryId).Stop == At(14.25));

            // Discarding throws it away.
            var addedRow = Controls<Panel>(review).First(p => p.Name == "Proposal" && ErrorText(p).Contains("booking confirmation"));
            await Press(Controls<Button>(addedRow).Single(b => b.Name == "ProposalDiscard"));
            await WaitFor(() => window.Proposals.Pending.Length == 1);
            Assert.Contains(fixture.ProposalAnswers, o => o.Id == Guid.Parse(added.Id) && o.Outcome == "discarded");

            // Editing opens the editor with what was proposed; saving it there is the person's answer.
            await WaitFor(() => Controls<Panel>(review).Count(p => p.Name == "Proposal") == 1);
            var splitRow = Controls<Panel>(review).Single(p => p.Name == "Proposal");
            Assert.Contains("Review the booking API", ErrorText(splitRow));
            Tap(splitRow, "Edit…");
            var editor = await Dialog<EntryEditor>(window);
            Assert.Equal("Proposed entry", Controls<TextBlock>(editor).Single(t => t.Name == "EditorTitle").Text);
            Assert.Contains("Split and described per project", ErrorText(editor));
            await Preview(window, "proposals-edit");
            await ClickNamed(editor, "EditorSave");
            await WaitFor(() => fixture.Entries.Any(e => e.Shares.Length == 2 && e.Start == At(17.75)));
            var saved = fixture.Entries.Single(e => e.Start == At(17.75));
            Assert.Equal(["Review the booking API", "Release notes for 2.3"], saved.Shares.Select(s => s.Description));
            await WaitFor(() => fixture.ProposalAnswers.Any(o => o.Id == Guid.Parse(split.Id) && o.Outcome == "saved" && o.EntryId == saved.Id));

            // The review opens again, with everything dealt with.
            review = await Dialog<ProposalReview>(window);
            Assert.Empty(window.Proposals.Pending);
            Assert.Contains("All dealt with", ErrorText(review));
            await Click(review, "Done");
            await reviewing;
            Assert.DoesNotContain(Controls<Border>(window), b => b.Name == "ProposalBanner");
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Add_all_saves_what_nothing_stands_in_the_way_of()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            TestData.SeedWeek(fixture, running: false);
            fixture.Settings = fixture.Settings with { RequireDescription = true };
            var window = await SignedIn(fixture);
            var project = fixture.Projects[1];
            // The server says the one without a description can't be saved as it is, in a workspace that requires one.
            fixture.Propose(Proposal(fixture, null, null, new(At(6), At(7), "Plan the sprint", ProposalWork.One(project.Id, null, project.IsBillable)), "Commits on main"));
            fixture.Propose(Proposal(fixture, null, null, new(At(7), At(7.5), null, ProposalWork.One(project.Id, null, project.IsBillable)), "Commits on main"),
                blocking: [EntryRules.DescriptionRequired]);
            var reviewing = window.ReviewProposalsAsync();
            var review = await Dialog<ProposalReview>(window);
            Assert.Contains("1 needs a look first", ErrorText(review));
            Assert.Contains(EntryRules.DescriptionRequired, ErrorText(review));
            var blocked = Controls<Panel>(review).Single(p => p.Name == "Proposal" && ErrorText(p).Contains("No description"));
            Assert.False(Controls<Button>(blocked).Single(b => b.Name == "ProposalAccept").IsEnabled);
            await Preview(window, "proposals-review-blocked");
            await Press(Controls<Button>(review).Single(b => b.Name == "ProposalsAddAll"));
            await WaitFor(() => fixture.Entries.Any(e => e.Description == "Plan the sprint"));
            Assert.Single(window.Proposals.Pending);
            await Click(review, "Discard all");
            await WaitFor(() => window.Proposals.Pending.Length == 0);
            await WaitFor(() => Controls<Button>(review).Any(b => Label(b) == "Done"));
            await Click(review, "Done");
            await reviewing;
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Nothing_waiting_says_where_proposals_come_from()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var window = await SignedIn(fixture);
            var reviewing = window.ReviewProposalsAsync();
            var review = await Dialog<ProposalReview>(window);
            Assert.Contains(Controls<StackPanel>(review), p => p.Name == "ProposalsEmpty");
            await Preview(window, "proposals-empty", dark: false);
            await Click(review, "Done");
            await reviewing;
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }
}
