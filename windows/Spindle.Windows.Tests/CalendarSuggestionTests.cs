using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Spindle.Activity;
using Spindle.Core;
using Spindle.Platform;
using Spindle.Windows.Model;
using Spindle.Windows.Views;
using static Spindle.Windows.Tests.TestUi;

namespace Spindle.Windows.Tests;

/// <summary>
/// A calendar block's suggestion drawn on the block from its ✦, and taken from there, from the Mac's synthetic agent
/// logs: on their day agents work 9:00–11:35 in atlas, beacon and copper.
/// It moves a real pointer, which only reaches the window in front, so it runs alone: other tests' windows would take
/// the front.
/// </summary>
[Collection(nameof(CalendarSuggestionTests))]
public sealed class CalendarSuggestionTests
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
        Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = description, ProjectId = project.Id, IsBillable = project.IsBillable, Start = At(from), Stop = At(to), TagIds = [],
    };

    private static async Task<(MainWindow Window, CalendarView Calendar)> Calendar(ApiFixture fixture)
    {
        new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")).Save(new DesktopPreferences { AgentLogsEnabled = true, AgentLogsDecided = true, CalendarDayMode = true });
        var window = await SignedIn(fixture);
        // The size snapshots are drawn at, so the pointer stays where it was put.
        (window.Width, window.Height) = (1360, 860);
        await window.NavigateAsync(Section.Calendar);
        var calendar = Controls<CalendarView>(window).Single();
        await calendar.ShowAsync(AgentLogs.Day, dayMode: true);
        await WaitFor(() => Sparks(window).Any());
        // The pointer only reaches the window in front.
        window.Activate();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (window, calendar);
    }

    private static List<Button> Sparks(Control root) => Controls<Button>(root).Where(b => b.Name == "CalendarSuggestion").ToList();

    /// <summary>The ✦, the preview and its words on the block for <paramref name="entry"/>.</summary>
    private static (Button Spark, Control Preview, string[] Labels) Block(MainWindow window, TimeEntryResponse entry)
    {
        var block = Controls<Border>(window).Single(b => b.Tag is DaySegment segment && segment.Entry.Id == entry.Id);
        var labels = Controls<Control>(block).Single(c => c.Name == "CalendarSuggestionLabels");
        Assert.Equal(Controls<Control>(block).Single(c => c.Name == "CalendarSuggestionPreview").IsVisible, labels.IsVisible);
        return (Sparks(block).Single(), Controls<Control>(block).Single(c => c.Name == "CalendarSuggestionPreview"), Controls<TextBlock>(labels).Select(t => t.Text ?? "").ToArray());
    }

    /// <summary>Moves the pointer onto the control, as a mouse does, and lets the window catch up.</summary>
    private static void Hover(MainWindow window, Control control) => Move(window, Middle(window, control));

    /// <summary>The pointer goes by what was last drawn, so the window draws first, and moves as a mouse does: in steps.</summary>
    private static void Move(MainWindow window, Point at)
    {
        foreach (var step in new[] { at + new Point(-1, 1), at })
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.MouseMove(step);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static bool NothingDrawn(MainWindow window) =>
        Controls<Control>(window).Where(c => c.Name is "CalendarSuggestionPreview" or "CalendarSuggestionLabels").All(c => !c.IsVisible);

    private static Point Middle(MainWindow window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    [Fact]
    public void RowsCutWhereTheWorkMovedWithinTheDayABlockShows()
    {
        var (atlas, beacon, copper) = (SplitTarget.Project(Guid.NewGuid()), SplitTarget.Project(Guid.NewGuid()), SplitTarget.Project(Guid.NewGuid()));
        var suggestion = new SplitSuggestion([], 1, [new(atlas, 0, 40), new(beacon, 40, 70), new(copper, 70, 100)]) { Start = 0, End = 100 };
        Assert.Equal([new(atlas, 0, 40, false), new(beacon, 40, 70, true), new(copper, 70, 100, true)], SuggestionPreview.Rows(suggestion, 0, 100));
        // The second day of an entry over midnight starts inside a stretch: no cut at its top.
        Assert.Equal([new(beacon, 50, 70, false), new(copper, 70, 100, true)], SuggestionPreview.Rows(suggestion, 50, 100));
        Assert.Equal([new(atlas, 0, 30, false)], SuggestionPreview.Rows(suggestion, 0, 30));
        // Shares by percent have no rows: they are columns.
        Assert.Empty(SuggestionPreview.Rows(suggestion with { Stretches = null }, 0, 100));
    }

    [Fact]
    public async Task HoveringOrFocusingTheSparkDrawsTheSuggestionAndAClickTakesItWithUndo()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture { Projects = Projects() };
            var (atlas, beacon) = (fixture.Projects[0], fixture.Projects[1]);
            // The work moved from atlas to beacon in this one; the other went back and forth between all three.
            var moved = Entry(fixture, "Search", atlas, 8.75, 9.75);
            var shared = Entry(fixture, "Docs and import", beacon, 10.5, 11.75);
            fixture.Entries.AddRange([moved, shared]);
            var (window, _) = await Calendar(fixture);
            Assert.Equal(2, Sparks(window).Count);
            Assert.NotNull(window.Splits.Suggestion(moved)?.Stretches);
            Assert.Null(window.Splits.Suggestion(shared)?.Stretches);

            // Nothing is drawn until the ✦ is hovered.
            var (spark, preview, _) = Block(window, moved);
            Assert.False(preview.IsVisible);
            Assert.StartsWith("Split at ", AutomationProperties.GetName(spark));
            Assert.EndsWith("Activate to take it; Undo puts it back.", AutomationProperties.GetName(spark));
            Assert.StartsWith("Agent activity from ", AutomationProperties.GetHelpText(spark));
            Assert.EndsWith(", split suggested", AutomationProperties.GetName(Controls<Border>(window).Single(b => b.Tag is DaySegment s && s.Entry.Id == moved.Id)));
            Hover(window, spark);
            // Blocks are drawn anew as the window catches up; the preview stays while the pointer does.
            (spark, preview, _) = Block(window, moved);
            Assert.True(preview.IsVisible);
            // A cut line where the work moved, with its time.
            var cut = window.Splits.Suggestion(moved)!.Stretches![1].Start;
            var clock = window.Time.Clock(DateTimeOffset.FromUnixTimeMilliseconds(cut));
            var words = Block(window, moved).Labels;
            Assert.Contains(clock, words);
            // What a click does, and each stretch with its project and time.
            Assert.Contains($"Split at {clock} · click ✦ to apply", words);
            Assert.Contains(words, w => w.StartsWith("Atlas "));
            // The block's own words stay, under the proposal.
            Assert.Contains(Controls<TextBlock>(Controls<Border>(window).Single(b => b.Tag is DaySegment s && s.Entry.Id == moved.Id)), t => t.Text == "Search" && t.IsEffectivelyVisible);
            await Preview(window, "calendar-suggestion-split-at");
            // The minute's redraw keeps it drawn while the pointer stays.
            window.Splits.Clear();
            await window.Splits.LoadAsync([AgentLogs.Day]);
            Controls<CalendarView>(window).Single().Refresh();
            (spark, preview, _) = Block(window, moved);
            Assert.True(preview.IsVisible);
            Move(window, new Point(4, 4));
            Assert.False(Block(window, moved).Preview.IsVisible);

            // Focusing it from the keyboard draws it too: one column a project, as wide as its share.
            var (sharedSpark, sharedPreview, _) = Block(window, shared);
            sharedSpark.Focus(NavigationMethod.Tab);
            Assert.True(sharedPreview.IsVisible);
            var parts = window.Splits.Suggestion(shared)!.Parts;
            var sharedWords = Block(window, shared).Labels;
            Assert.Contains(sharedWords, w => w.StartsWith("Split ") && w.EndsWith(" · click ✦ to apply"));
            Assert.All(parts, p => Assert.Contains(sharedWords, w => w.StartsWith(window.Catalog.Project(p.Target.ProjectId!.Value)!.Name + " ")));
            // The hint names every part, as narrow parts show their time alone.
            Assert.Contains(sharedWords, w => w.StartsWith("Split ") && w.EndsWith(" · click ✦ to apply")
                && parts.All(p => w.Contains(window.Catalog.Project(p.Target.ProjectId!.Value)!.Name + " ")));
            sharedSpark.BringIntoView();
            await Preview(window, "calendar-suggestion-shares");
            Assert.True(Block(window, shared).Preview.IsVisible);

            // A click on the ✦ takes it, without moving the entry, and Undo puts it back.
            // The snapshots' theme changes settle the scroll first. A redraw between press and release (the calendar's
            // minute) takes the ✦ away under the pointer, as it would from a mouse: then it is clicked again.
            for (var attempt = 0; attempt < 3 && window.Notices.Current != "Split saved"; attempt++)
            {
                Hover(window, Block(window, moved).Spark);
                var at = Middle(window, Block(window, moved).Spark);
                Move(window, at);
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                for (var i = 0; i < 100 && window.Notices.Current != "Split saved"; i++) await Task.Delay(10);
            }
            await WaitFor(() => window.Notices.Current == "Split saved" && window.Notices.Action == "Undo");
            Assert.Equal(3, fixture.Entries.Count);
            Assert.Equal(beacon.Id, fixture.Entries.Single(e => e.Id == moved.Id).ProjectId);
            Assert.False(window.Dialogs.IsVisible);
            // Taken, nothing stays drawn; nor when Undo brings the ✦ back under the resting pointer.
            Assert.True(NothingDrawn(window));
            await ClickNamed(window, "NoticeAction");
            await WaitFor(() => window.Notices.Current == "Undone");
            Assert.True(NothingDrawn(window));
            Assert.Equal(2, fixture.Entries.Count);
            var restored = fixture.Entries.Single(e => e.Id == moved.Id);
            Assert.Equal((atlas.Id, moved.Start, moved.Stop), (restored.ProjectId, restored.Start, restored.Stop));

            // Enter on the focused ✦ takes the shares.
            (sharedSpark, _, _) = Block(window, shared);
            sharedSpark.Focus(NavigationMethod.Tab);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await WaitFor(() => window.Notices.Current == "Split saved" && fixture.Entries.Single(e => e.Id == shared.Id).Shares.Length > 0);
            Assert.Equal(parts.Count, fixture.Entries.Single(e => e.Id == shared.Id).Shares.Length);
            Assert.True(NothingDrawn(window));
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AWeakSuggestionIsQuieterAndARepositoryWithoutAProjectOpensTheEditor()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            // No Copper project: its repository has none yet.
            using var fixture = new ApiFixture { Projects = Projects()[..2] };
            var atlas = fixture.Projects[0];
            var weak = Entry(fixture, "Search", atlas, 8.5, 9.25);
            var unlinked = Entry(fixture, "Import", atlas, 10.5, 11.5);
            fixture.Entries.AddRange([weak, unlinked]);
            var (window, _) = await Calendar(fixture);
            Assert.Equal(SplitStrength.Weak, window.Splits.Suggestion(weak)!.Strength);
            var (spark, preview, _) = Block(window, weak);
            Assert.Contains("The logs back this only lightly.", AutomationProperties.GetName(spark));
            Assert.Contains("secondary", Assert.IsType<PathIcon>(spark.Content).Classes);
            Hover(window, spark);
            Assert.True(Block(window, weak).Preview.IsVisible);
            Assert.Contains(Block(window, weak).Labels, w => w.EndsWith("click ✦ to apply"));
            await Preview(window, "calendar-suggestion-weak");
            Move(window, new Point(4, 4));

            var suggestion = window.Splits.Suggestion(unlinked)!;
            Assert.Contains(suggestion.Parts, p => p.Target.ProjectId is null);
            (spark, _, _) = Block(window, unlinked);
            Assert.EndsWith("Activate to choose a project for each repository.", AutomationProperties.GetName(spark));
            var opening = Press(spark);
            await Dialog<EntryEditor>(window);
            Assert.Equal(2, fixture.Entries.Count);
            window.Dialogs.Close(null);
            await opening;
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ClickingAnOutlineInTheReviewsStripSelectsItsItem()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture { Projects = Projects() };
            fixture.Entries.Add(Entry(fixture, "Search and onboarding", fixture.Projects[0], 9, 10));
            new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")).Save(new DesktopPreferences { AgentLogsEnabled = true, AgentLogsDecided = true });
            var window = await SignedIn(fixture);
            (window.Width, window.Height) = (1360, 860);
            var reviewing = window.OpenReviewAsync(ReviewScope.Day(AgentLogs.Day));
            var review = await Dialog<DayReviewView>(window);
            await WaitFor(() => review.Items.Count >= 2);
            window.Activate();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(review.Items[0].Id, review.Selected);
            var last = review.Items[^1];
            var outline = Controls<Avalonia.Controls.Shapes.Rectangle>(review).Single(r => r.Name == "ReviewOutline" && (string?)r.Tag == last.Id);
            var at = Middle(window, outline);
            Move(window, at);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            await WaitFor(() => review.Selected == last.Id);
            window.Dialogs.Close(null);
            await reviewing;
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }
}

[CollectionDefinition(nameof(CalendarSuggestionTests), DisableParallelization = true)]
public sealed class CalendarSuggestionCollection;
