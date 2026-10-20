using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Spindle.Activity;
using Spindle.Core;
using Spindle.Platform;
using Spindle.Windows.Model;
using Spindle.Windows.Views;
using static Spindle.Windows.Tests.TestUi;

namespace Spindle.Windows.Tests;

/// <summary>
/// Time nobody tracked and the day's review, from the Mac's synthetic agent logs. On their day agents work 9:00–11:35
/// in atlas, beacon and copper, and a session carries beacon over midnight into 0:00–0:24.
/// </summary>
public sealed class ReviewTests
{
    private static DateTimeOffset At(double hour)
    {
        var local = AgentLogs.Day.ToDateTime(TimeOnly.MinValue).AddMinutes(Math.Round(hour * 60));
        return new DateTimeOffset(local, ApiFixture.Zagreb.GetUtcOffset(local));
    }

    private static ProjectResponse[] Projects() =>
    [
        new() { Id = Guid.NewGuid(), Name = "Atlas", Color = "#0B83D9", IsBillable = true },
        new() { Id = Guid.NewGuid(), Name = "Beacon", Color = "#2DA608" },
        new() { Id = Guid.NewGuid(), Name = "Copper", Color = "#D94182" },
    ];

    private static TimeEntryResponse Entry(ApiFixture fixture, string description, ProjectResponse project, double from, double to) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = description, ProjectId = project.Id, IsBillable = project.IsBillable, Start = At(from), Stop = At(to),
    };

    private static void KeyDown(Control control, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

    [Fact]
    public async Task TheDaysReviewHasEverySuggestionAndTakeAllHasOneUndo()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture { Projects = Projects() };
            var (atlas, copper) = (fixture.Projects[0], fixture.Projects[2]);
            var search = Entry(fixture, "Search and onboarding", atlas, 9, 10);
            fixture.Entries.Add(search);
            fixture.Entries.Add(Entry(fixture, "Copper import", copper, 10, 10 + 25 / 60.0));
            new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")).Save(new DesktopPreferences { AgentLogsEnabled = true, AgentLogsDecided = true, CalendarDayMode = true });
            var window = await SignedIn(fixture);
            await window.Splits.LoadAsync([AgentLogs.Day]);

            // Untracked time needs a prompt of its own; what the entries cover isn't untracked.
            var ghosts = window.Desktop.Untracked([AgentLogs.Day]);
            Assert.Equal([(At(0), At(0.4)), (At(10.5), At(11 + 35 / 60.0))], ghosts.Select(g => (g.Start, g.End)));
            Assert.Equal([fixture.Projects[1].Id], ghosts[0].Stretch.Projects);
            Assert.Equal(3, ghosts[1].Stretch.Projects.Count);

            await window.NavigateAsync(Section.Calendar);
            var calendar = Controls<CalendarView>(window).Single();
            await calendar.ShowAsync(AgentLogs.Day, dayMode: true);
            await WaitFor(() => Controls<Panel>(window).Count(p => p.Name == "UntrackedGhost") == 2);
            Assert.Contains(Controls<Button>(window), b => b.Name == "CalendarReview");
            await Preview(window, "calendar-untracked");

            // The review: the splits of both entries (the Copper one began in atlas) and both untracked stretches, in order.
            var reviewing = window.OpenReviewAsync(ReviewScope.Day(AgentLogs.Day));
            var review = await Dialog<DayReviewView>(window);
            await WaitFor(() => review.Items.Count == 4);
            Assert.Collection(review.Items,
                item => Assert.IsType<ReviewItem.Untracked>(item),
                item => Assert.Equal(search.Id, Assert.IsType<ReviewItem.Split>(item).Offer.Entry.Id),
                item => Assert.IsType<ReviewItem.Split>(item),
                item => Assert.IsType<ReviewItem.Untracked>(item));
            Assert.Equal("2 splits, 2 untracked stretches", ReviewItem.Breakdown(review.Items));
            await Preview(window, "review-day");

            // ↑↓ move the selection.
            Assert.Equal(review.Items[0].Id, review.Selected);
            KeyDown(review, Key.Down);
            Assert.Equal(review.Items[1].Id, review.Selected);
            KeyDown(review, Key.Up);
            Assert.Equal(review.Items[0].Id, review.Selected);

            // Each row says what the time is now and what it becomes.
            var lines = Controls<Border>(review).Where(b => b.Name == "ReviewChangeLine").Select(b => string.Join(" ", Controls<TextBlock>(b).Select(t => t.Text))).ToList();
            Assert.Equal(4, lines.Count);
            Assert.StartsWith("Nothing tracked → New entry Beacon", lines[0]);
            Assert.StartsWith("Atlas 1:00 →", lines[1]);
            Assert.Equal("0 of 4 done", Controls<TextBlock>(review).Single(t => t.Name == "ReviewProgressText").Text);

            // Take all saves all four, with one Undo for them; the rows stay in their place, saying what became of them.
            await ClickNamed(window, "ReviewTakeAll");
            await WaitFor(() => review.Items.Count == 0 && review.Handled.Count == 4);
            // Two new entries at least, for the untracked time, and the Atlas entry split or cut where the work moved.
            Assert.True(fixture.Entries.Count >= 4);
            Assert.True(fixture.Entries.Single(e => e.Id == search.Id) is var taken && (taken.Shares.Length > 1 || taken.Start != search.Start || taken.ProjectId != atlas.Id));
            Assert.Equal("Saved 4 suggestions", review.Notice?.Text);
            Assert.All(review.Handled, d => Assert.Equal(ReviewOutcome.Taken, d.Outcome));
            Assert.Contains(review.Handled, d => d.Title == "Added Beacon 00:00–00:24");
            Assert.Equal("4 of 4 done", Controls<TextBlock>(review).Single(t => t.Name == "ReviewProgressText").Text);
            Assert.Single(Controls<Border>(review), b => b.Name == "ReviewAllDone");
            Assert.Equal(4, Controls<Border>(review).Count(b => b.Name == "ReviewDone"));
            Assert.Null(window.Notices.Current);
            await Preview(window, "review-after-take-all");
            await ClickNamed(window, "ReviewUndo");
            await WaitFor(() => review.Items.Count == 4 && review.Handled.Count == 0);
            Assert.Equal(2, fixture.Entries.Count);
            var restored = fixture.Entries.Single(e => e.Id == search.Id);
            Assert.Equal((atlas.Id, search.Start, search.Stop), (restored.ProjectId, restored.Start, restored.Stop));
            Assert.Empty(restored.Shares);

            // Enter takes the selected item; the one after it is selected, and the taken one keeps its own Undo.
            Assert.Equal(review.Items[0].Id, review.Selected);
            var first = review.Items[0];
            KeyDown(review, Key.Enter);
            await WaitFor(() => review.Handled.Count == 1);
            Assert.Equal(review.Items[0].Id, review.Selected);
            Assert.Equal(3, fixture.Entries.Count);
            await ClickNamed(review, "ReviewDoneUndo");
            await WaitFor(() => review.Items.Count == 4 && review.Handled.Count == 0);
            Assert.Equal(first.Id, review.Selected);
            Assert.Equal(2, fixture.Entries.Count);

            // Backspace leaves an item out of the review on this PC; nothing else changes, and Undo brings it back.
            KeyDown(review, Key.Back);
            await WaitFor(() => review.Items.Count == 3);
            Assert.Equal(ReviewOutcome.NotNow, Assert.Single(review.Handled).Outcome);
            Assert.Single(window.Preferences.ReviewPutOff);
            Assert.Equal(2, fixture.Entries.Count);
            await Preview(window, "review-not-now");
            await ClickNamed(review, "ReviewDoneUndo");
            await WaitFor(() => review.Items.Count == 4);
            Assert.Empty(window.Preferences.ReviewPutOff);
            KeyDown(review, Key.Back);
            await WaitFor(() => review.Items.Count == 3);
            // The week shows the same day's items.
            await Click(review, "Week");
            await WaitFor(() => review.Scope.IsWeek && review.Items.Count == 3);
            await Preview(window, "review-week");

            // E opens the editor with the untracked time filled in; the review comes back after.
            KeyDown(review, Key.E);
            var editor = await Dialog<EntryEditor>(window);
            await Preview(window, "review-change");
            window.Dialogs.Close(null);
            review = await Dialog<DayReviewView>(window);
            Assert.True(review.Scope.IsWeek);
            // What was done stays while the review is open, across the editor.
            Assert.Single(review.Handled);
            await WaitFor(() => review.Items.Count == 3);

            // Saved in the editor, the item says so in its place.
            var changing = review.Items[0];
            KeyDown(review, Key.E);
            await Dialog<EntryEditor>(window);
            await ClickNamed(window, "EditorSave");
            review = await Dialog<DayReviewView>(window);
            await WaitFor(() => review.Handled.Count == 2);
            Assert.Equal((ReviewOutcome.Changed, "Saved in the editor"), (review.Handled.Single(d => d.Item.Id == changing.Id).Outcome, review.Handled.Single(d => d.Item.Id == changing.Id).Title));
            window.Dialogs.Close(null);
            await reviewing;
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task UntrackedWorkMovesTheStartBackAndNamesTheReminder()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture { Projects = Projects() };
            var (atlas, beacon, copper) = (fixture.Projects[0], fixture.Projects[1], fixture.Projects[2]);
            fixture.Entries.Add(Entry(fixture, "Copper import", copper, 10, 10 + 25 / 60.0));
            new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")).Save(new DesktopPreferences
            {
                AgentLogsEnabled = true, AgentLogsDecided = true, RemindToTrack = true, ReminderFrom = 9 * 60, ReminderTo = 17 * 60,
            });
            var window = await SignedIn(fixture);

            // B2: agents in a linked project for 15 minutes with no timer: the reminder names it.
            await window.Desktop.TickAsync(At(11 + 20 / 60.0));
            var reminder = Toasts.Shown[ToastKind.Reminder];
            Assert.Equal("Agents have been working in Atlas since 10:30", reminder.Title);
            Assert.Contains("Start from 10:30", Controls<Button>((Control)reminder.Content!).Select(Label));
            await Preview(reminder, "toast-reminder-agents");
            Toasts.Close(ToastKind.Reminder);
            await window.Desktop.StartRemindedWorkAsync();
            var started = Assert.Single(fixture.Entries, e => e.Stop is null);
            Assert.Equal((atlas.Id, At(10.5)), (started.ProjectId, started.Start));
            await WaitFor(() => window.Notices.Current == "Started Atlas from 10:30");
            await ClickNamed(window, "NoticeAction");
            await WaitFor(() => window.Notices.Current == "Undone");
            Assert.DoesNotContain(fixture.Entries, e => e.Stop is null);

            // B1: a timer started right after agents were at work offers to start where they did, never before the
            // last stop. The project stays the timer's.
            await window.StartAsync(new CreateTimeEntryRequest { Description = "Docs", ProjectId = beacon.Id, Start = At(11 + 40 / 60.0) });
            window.Splits.Now = () => At(11 + 42 / 60.0);
            var running = window.Running!;
            var offer = window.Splits.StartEarlier(running);
            Assert.NotNull(offer);
            Assert.Equal(At(10.5), offer.Start);
            Assert.Equal(copper.Id, offer.OtherProjectId);
            Assert.Null(window.Splits.StartEarlier(running, At(11 + 51 / 60.0)));
            await window.NavigateAsync(Section.Timer);
            window.RefreshViews();
            await WaitFor(() => Controls<Button>(window).Any(b => b.Name == "EarlierStart"));
            await Preview(window, "timer-earlier-start");
            await ClickNamed(window, "EarlierStart");
            await WaitFor(() => fixture.Entries.Single(e => e.Stop is null).Start == At(10.5));
            Assert.Equal(beacon.Id, fixture.Entries.Single(e => e.Stop is null).ProjectId);
            await WaitFor(() => window.Notices.Current == "Started at 10:30");
            await ClickNamed(window, "NoticeAction");
            await WaitFor(() => fixture.Entries.Single(e => e.Stop is null).Start == At(11 + 40 / 60.0));

            // A timer without a project gets the agents' project along with the start, and Undo takes both back.
            await window.UpdateRunningAsync(r => r with { ProjectId = null, TaskId = null, Shares = [] });
            offer = window.Splits.StartEarlier(window.Running!);
            Assert.NotNull(offer);
            Assert.Equal((copper.Id, (Guid?)null), (offer.FillsProjectId, offer.OtherProjectId));
            window.RefreshViews();
            await WaitFor(() => Controls<Button>(window).Any(b => b.Name == "EarlierStart"));
            await ClickNamed(window, "EarlierStart");
            await WaitFor(() => window.Notices.Current == "Started Copper at 10:30");
            Assert.Equal((copper.Id, At(10.5)), (fixture.Entries.Single(e => e.Stop is null).ProjectId, fixture.Entries.Single(e => e.Stop is null).Start));
            await ClickNamed(window, "NoticeAction");
            await WaitFor(() => fixture.Entries.Single(e => e.Stop is null).Start == At(11 + 40 / 60.0));
            Assert.Null(fixture.Entries.Single(e => e.Stop is null).ProjectId);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheEndOfTheWorkingDayOffersItsReviewOnceAndTheNextMorningHasACard()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture { Projects = Projects() };
            fixture.Entries.Add(Entry(fixture, "Search and onboarding", fixture.Projects[0], 9, 10));
            new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")).Save(new DesktopPreferences { AgentLogsEnabled = true, AgentLogsDecided = true });
            var window = await SignedIn(fixture);

            // During working hours, nothing.
            await window.Desktop.TickAsync(At(16));
            Assert.False(Toasts.Shown.ContainsKey(ToastKind.DayReview));
            await window.Desktop.TickAsync(At(17.5));
            var toast = Toasts.Shown[ToastKind.DayReview];
            Assert.Equal("Today: 3 to review", toast.Title);
            await Preview(toast, "toast-day-review");
            Toasts.Close(ToastKind.DayReview);
            Assert.Equal($"{AgentLogs.Day:yyyy-MM-dd}", window.Preferences.DayReviewOffered);
            await window.Desktop.TickAsync(At(18));
            Assert.False(Toasts.Shown.ContainsKey(ToastKind.DayReview));

            // The next morning, a card above the timer; Not now hides it for that day.
            var card = ReviewViews.Card(window, AgentLogs.Day.AddDays(1));
            Assert.NotNull(card);
            Assert.Contains(Controls<TextBlock>(card).Select(t => t.Text), t => t?.EndsWith(": 3 to review") == true);
            window.SavePreferences(window.Preferences with { ReviewCardDismissed = $"{AgentLogs.Day:yyyy-MM-dd}" });
            Assert.Null(ReviewViews.Card(window, AgentLogs.Day.AddDays(1)));
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TurningLogsOnShowsWhatTheLastWeekFoundAndLinksRepositories()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture { Projects = Projects() };
            fixture.Entries.Add(Entry(fixture, "Search and onboarding", fixture.Projects[0], 9, 10));
            new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")).Save(new DesktopPreferences { AgentLogsEnabled = true, AgentLogsDecided = true });
            var window = await SignedIn(fixture);

            var onboarding = new AgentLogsOnboarding(window, today: AgentLogs.Day);
            var shown = window.Dialogs.ShowAsync(onboarding, 620);
            await WaitFor(() => onboarding.Found is not null, 500);
            var found = onboarding.Found!;
            Assert.True(found.Elsewhere.OtherProjects > 0);
            Assert.Equal(1, found.Elsewhere.Entries);
            Assert.Equal(3600_000, found.Tracked);
            Assert.True(found.Untracked > 0);
            Assert.Equal(["atlas", "beacon", "copper"], found.Repositories.Select(r => r.Repository.Name).Order());
            await Preview(window, "onboarding-found");

            await ClickNamed(window, "OnboardingNext");
            Assert.Equal(AgentLogsOnboarding.Step.Link, onboarding.Current);
            await Preview(window, "onboarding-link");
            // Each repository can send some branches to another project.
            Assert.Equal(3, Controls<Button>(window).Count(b => b.Name == "BranchRules"));
            // Each repository's project is guessed from its name; linking saves them as the person's own links.
            Assert.Equal("Link 3 repositories", Label(Controls<Button>(window).Single(b => b.Name == "OnboardingLink")));
            await ClickNamed(window, "OnboardingLink");
            Assert.Equal(3, fixture.Writes.Count(w => w.Path.EndsWith("/agent-activity/links")));
            Assert.Equal(AgentLogsOnboarding.Step.Review, onboarding.Current);
            await Preview(window, "onboarding-review");

            var open = Controls<Button>(window).Single(b => b.Name == "OnboardingReview");
            Assert.Equal("Review last week (3)", Label(open));
            await Press(open);
            var scope = Assert.IsType<ReviewScope>(await shown);
            Assert.Equal(ReviewScope.LastSevenDays(AgentLogs.Day), scope);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }
}
