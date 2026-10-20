using Avalonia.Controls;
using Avalonia.Headless;
using Spindle.Activity;
using Spindle.Core;
using Spindle.Platform;
using Spindle.Windows.Model;
using Spindle.Windows.Views;
using static Spindle.Windows.Tests.TestUi;

namespace Spindle.Windows.Tests;

/// <summary>Split suggestions and the calendar's activity lane, from the Mac's synthetic agent logs.</summary>
public sealed class ActivityTests
{
    [Fact]
    public async Task AgentActivitySuggestsSplitAndShowsBesideTheCalendar()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture();
            fixture.Projects =
            [
                new() { Id = Guid.NewGuid(), Name = "Atlas", Color = "#0B83D9", IsBillable = true },
                new() { Id = Guid.NewGuid(), Name = "Beacon", Color = "#2DA608" },
                new() { Id = Guid.NewGuid(), Name = "Copper", Color = "#D94182" },
            ];
            var zone = ApiFixture.Zagreb;
            DateTimeOffset At(double hour)
            {
                var local = AgentLogs.Day.ToDateTime(TimeOnly.MinValue).AddHours(hour);
                return new DateTimeOffset(local, zone.GetUtcOffset(local));
            }
            var entry = new TimeEntryResponse
            {
                Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = "Search and onboarding", ProjectId = fixture.Projects[0].Id,
                IsBillable = true, Start = At(9), Stop = At(11.5),
            };
            fixture.Entries.Add(entry);
            fixture.Entries.Add(new TimeEntryResponse { Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = "Lunch walk", Start = At(12), Stop = At(12.75) });
            new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")).Save(new DesktopPreferences { AgentLogsEnabled = true, AgentLogsDecided = true, ShowAgentActivity = true });
            var window = await SignedIn(fixture);
            await window.Splits.LoadAsync([AgentLogs.Day]);
            var suggestion = window.Splits.Suggestion(window.Entries.Single(e => e.Id == entry.Id));
            Assert.NotNull(suggestion);
            // Strong enough for the card after a stop, which only strong suggestions get.
            Assert.Equal(SplitStrength.Strong, suggestion.Strength);
            Assert.True(suggestion.Parts.Count >= 2);
            Assert.Contains(suggestion.Parts, p => p.Target.ProjectId == fixture.Projects[0].Id);
            Assert.Equal(100, suggestion.Parts.Sum(p => p.Percent));
            await window.NavigateAsync(Section.Timer);
            await WaitFor(() => Controls<Button>(window).Any(b => b.Name == "SplitSuggestion"));
            await Preview(window, "timer-suggestion");
            // The editor offers it with the evidence it rests on.
            var banner = window.EditEntryAsync(window.Entries.Single(e => e.Id == entry.Id));
            await Dialog<EntryEditor>(window);
            await WaitFor(() => Controls<SuggestionBanner>(window).Any() && Controls<EvidenceStrip>(window).Any());
            await Preview(window, "editor-evidence");
            window.Dialogs.Close(null);
            await banner;
            await window.NavigateAsync(Section.Calendar);
            var calendar = Controls<CalendarView>(window).Single();
            await calendar.ShowAsync(AgentLogs.Day, dayMode: true);
            await Preview(window, "calendar-activity-day");
            await calendar.ShowAsync(AgentLogs.Day, dayMode: false);
            await Preview(window, "calendar-activity-week");
            await window.Desktop.OfferAfterStopAsync(window.Entries.Single(e => e.Id == entry.Id));
            Assert.NotNull(window.Desktop.Offer);
            await Preview(window, "split-offer");
            await ClickNamed(window, "TakeOffer");
            await WaitFor(() => fixture.Writes.Any(w => w.Method == HttpMethod.Put || w.Method == HttpMethod.Post));
            // One click took it; Undo puts the entry back as it was and removes any entries it added.
            await WaitFor(() => window.Notices.Action == "Undo");
            await ClickNamed(window, "NoticeAction");
            await WaitFor(() => window.Notices.Current == "Undone");
            Assert.Equal(2, fixture.Entries.Count);
            var restored = fixture.Entries.Single(e => e.Id == entry.Id);
            Assert.Equal(fixture.Projects[0].Id, restored.ProjectId);
            Assert.Empty(restored.Shares);
            Assert.Equal((entry.Start, entry.Stop), (restored.Start, restored.Stop));
            var reviewing = window.Splits.ReviewAsync(window.Entries.Single(e => e.Id == entry.Id));
            var editor = await Dialog<EntryEditor>(window);
            await Preview(window, "editor-suggestion");
            window.Dialogs.Close(null);
            await reviewing;
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ABranchRuleSendsARepositorysWorkOnItsBranchesToAnotherProjectAndTask()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture();
            var support = new TaskResponse { Id = Guid.NewGuid(), Name = "Support" };
            fixture.Projects =
            [
                new() { Id = Guid.NewGuid(), Name = "Atlas", Color = "#0B83D9", IsBillable = true },
                new() { Id = Guid.NewGuid(), Name = "Beacon", Color = "#2DA608" },
                new() { Id = Guid.NewGuid(), Name = "Copper", Color = "#D94182" },
                new() { Id = Guid.NewGuid(), Name = "Delta", Color = "#E36A00" },
            ];
            fixture.Projects[3] = fixture.Projects[3] with { Tasks = [support with { ProjectId = fixture.Projects[3].Id }] };
            // The synthetic Claude Code session in beacon is on feature/search.
            fixture.RepositoryLinks =
            [
                new()
                {
                    Identity = RepositoryResolver.PathIdentity(AgentLogs.Repository("beacon")), Name = "beacon", ProjectId = fixture.Projects[1].Id,
                    WorkspaceId = fixture.Workspace, BranchRules = [new() { Pattern = "feature/*", ProjectId = fixture.Projects[3].Id, TaskId = support.Id }],
                },
            ];
            var local = AgentLogs.Day.ToDateTime(TimeOnly.MinValue);
            DateTimeOffset At(double hour) => new(local.AddHours(hour), ApiFixture.Zagreb.GetUtcOffset(local.AddHours(hour)));
            var entry = new TimeEntryResponse
            {
                Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = "Search and onboarding", ProjectId = fixture.Projects[0].Id,
                IsBillable = true, Start = At(9), Stop = At(11.5),
            };
            fixture.Entries.Add(entry);
            new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")).Save(new DesktopPreferences { AgentLogsEnabled = true, AgentLogsDecided = true });
            var window = await SignedIn(fixture);
            await window.Splits.LoadAsync([AgentLogs.Day]);

            var suggestion = window.Splits.Suggestion(window.Entries.Single(e => e.Id == entry.Id));
            Assert.NotNull(suggestion);
            Assert.DoesNotContain(suggestion.Parts, p => p.Target.ProjectId == fixture.Projects[1].Id);
            var delta = Assert.Single(suggestion.Parts, p => p.Target.ProjectId == fixture.Projects[3].Id);
            Assert.Equal(support.Id, delta.TaskId);
            var take = window.Splits.Take(suggestion, entry.TaskId, entry.IsBillable, entry.ProjectId);
            Assert.Equal(support.Id, Assert.Single(take!.Parts!, p => p.ProjectId == fixture.Projects[3].Id).TaskId);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ASplitWhoseWorkChangedEndsWhereItChangedAndUndoPutsItBack()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture();
            fixture.Projects =
            [
                new() { Id = Guid.NewGuid(), Name = "Atlas", Color = "#0B83D9", IsBillable = true },
                new() { Id = Guid.NewGuid(), Name = "Beacon", Color = "#2DA608" },
            ];
            var (atlas, beacon) = (fixture.Projects[0].Id, fixture.Projects[1].Id);
            var zone = ApiFixture.Zagreb;
            DateTimeOffset At(double hour)
            {
                var local = AgentLogs.Day.ToDateTime(TimeOnly.MinValue).AddHours(hour);
                return new DateTimeOffset(local, zone.GetUtcOffset(local));
            }
            var entry = new TimeEntryResponse
            {
                Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = "Search and docs", ProjectId = atlas, IsBillable = true,
                Start = At(9), Stop = At(10 + 20 / 60.0), TagIds = [],
                Shares = [new() { ProjectId = atlas, IsBillable = true, Percent = 50 }, new() { ProjectId = beacon, Percent = 50 }],
            };
            fixture.Entries.Add(entry);
            // Split when the timer started, and seen running on this PC.
            new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")).Save(new DesktopPreferences
            {
                AgentLogsEnabled = true, AgentLogsDecided = true,
                SplitStatements = new() { [entry.Id] = new(SplitAdvisor.PartsKey(entry.Parts()), At(9)) },
            });
            var window = await SignedIn(fixture);

            await window.Desktop.OfferAfterStopAsync(window.Entries.Single(e => e.Id == entry.Id));
            var offer = Assert.IsType<DriftOffer>(window.Desktop.DriftOffer);
            Assert.Equal([beacon], offer.Drift.Quiet);
            var cut = DateTimeOffset.FromUnixTimeMilliseconds(offer.Drift.At);
            Assert.InRange(cut, At(9), At(10));
            Assert.StartsWith("End split at ", window.Splits.Title(offer.Drift));
            await Preview(window, "drift-offer");

            await ClickNamed(window, "TakeDrift");
            await WaitFor(() => fixture.Entries.Count == 2);
            var ended = fixture.Entries.Single(e => e.Id == entry.Id);
            Assert.Equal(cut, ended.Stop);
            Assert.Equal(2, ended.Shares.Length);
            var next = fixture.Entries.Single(e => e.Id != entry.Id);
            Assert.Equal((cut, entry.Stop, atlas, "Search and docs"), (next.Start, next.Stop, next.ProjectId, next.Description));
            Assert.Empty(next.Shares);

            await WaitFor(() => window.Notices.Action == "Undo");
            await ClickNamed(window, "NoticeAction");
            await WaitFor(() => window.Notices.Current == "Undone");
            var restored = Assert.Single(fixture.Entries);
            Assert.Equal((entry.Start, entry.Stop, 2), (restored.Start, restored.Stop, restored.Shares.Length));
            // Answered once: nothing more is offered for this split.
            await window.Desktop.OfferAfterStopAsync(restored);
            Assert.Null(window.Desktop.DriftOffer);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }
}
