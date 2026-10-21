using Avalonia;
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
/// Branch rules, "Agents now" and the review's holds, from the Mac's synthetic agent logs: on their day agents work
/// 9:00–11:35 in atlas, beacon and copper, and beacon's Claude Code session is on feature/search.
/// </summary>
public sealed class BranchRulesTests
{
    private static DateTimeOffset At(double hour)
    {
        var local = AgentLogs.Day.ToDateTime(TimeOnly.MinValue).AddMinutes(Math.Round(hour * 60));
        return new DateTimeOffset(local, ApiFixture.Zagreb.GetUtcOffset(local));
    }

    /// <summary>Atlas, Beacon, Copper, and Delta with a task, Support.</summary>
    private static ProjectResponse[] Projects()
    {
        var delta = Guid.NewGuid();
        return
        [
            new() { Id = Guid.NewGuid(), Name = "Atlas", Color = "#0B83D9", IsBillable = true },
            new() { Id = Guid.NewGuid(), Name = "Beacon", Color = "#2DA608" },
            new() { Id = Guid.NewGuid(), Name = "Copper", Color = "#D94182" },
            new() { Id = delta, Name = "Delta", Color = "#E36A00", Tasks = [new() { Id = Guid.NewGuid(), Name = "Support", ProjectId = delta }] },
        ];
    }

    private static string Beacon => RepositoryResolver.PathIdentity(AgentLogs.Repository("beacon"));

    /// <summary>beacon linked to Beacon, with feature/* going to Delta · Support.</summary>
    private static RepositoryLinkResponse[] Links(ApiFixture fixture, bool rules = true) =>
    [
        new()
        {
            Identity = Beacon, Name = "beacon", ProjectId = fixture.Projects[1].Id, WorkspaceId = fixture.Workspace,
            BranchRules = rules ? [new() { Pattern = "feature/*", ProjectId = fixture.Projects[3].Id, TaskId = fixture.Projects[3].Tasks[0].Id }] : [],
        },
    ];

    private static async Task<MainWindow> Window(ApiFixture fixture)
    {
        new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")).Save(new DesktopPreferences { AgentLogsEnabled = true, AgentLogsDecided = true });
        var window = await SignedIn(fixture);
        await window.Splits.LoadAsync([AgentLogs.Day]);
        return window;
    }

    [Fact]
    public async Task UntrackedTimeAndAgentsNowFollowBranchRules()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture { Projects = Projects() };
            var window = await Window(fixture);
            var (beacon, delta) = (fixture.Projects[1], fixture.Projects[3]);
            var withoutRules = window.Desktop.Untracked([AgentLogs.Day]);
            Assert.Contains(withoutRules, b => b.Stretch.Projects.Contains(beacon.Id));
            window.Quit();

            fixture.RepositoryLinks = Links(fixture);
            window = await Window(fixture);
            // beacon's work on feature/search goes to Delta now, untracked time too.
            var withRules = window.Desktop.Untracked([AgentLogs.Day]);
            Assert.Contains(withRules, b => b.Stretch.Projects.Contains(delta.Id));

            // "Agents now": the projects of the last 15 minutes, with the rule's task.
            window.Splits.Now = () => At(10.5);
            var now = window.Splits.AgentsNow();
            Assert.NotEmpty(now);
            Assert.All(now, p => Assert.NotEqual(beacon.Id, p.ProjectId));
            var picker = ProjectPicker.Create(window, null, null, (_, _) => Task.CompletedTask);
            var flyout = PickerList.Show(Controls<TextBox>(window).First(t => t.Name is "TimerDescription" or "RunningDescription"), picker);
            await Task.Delay(50);
            var texts = Controls<TextBlock>(picker).Select(t => t.Text).ToList();
            Assert.Equal("Agents now", texts[0]);
            Assert.Equal(now.Count, ProjectPicker.AgentsNow(window, (_, _) => false).Count);
            if (now.Any(p => p.ProjectId == delta.Id)) Assert.Contains("· Support", texts);
            await Preview(window, "picker-agents-now");
            flyout.Hide();
            // Nothing from agents while searching, nor too long after.
            picker.Search.Text = "Atl";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("Agents now", Controls<TextBlock>(picker).Select(t => t.Text));
            window.Splits.Now = () => At(13);
            Assert.Empty(window.Splits.AgentsNow());
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheBranchRulesEditorShowsWhereRecentBranchesGoAndSavesTheRulesWithTheLink()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture { Projects = Projects() };
            fixture.RepositoryLinks = Links(fixture, rules: false);
            var window = await Window(fixture);
            var (atlas, beacon, delta) = (fixture.Projects[0], fixture.Projects[1], fixture.Projects[3]);

            // Settings lists the linked repositories, each with its rules.
            await Click(window, "Settings");
            var settings = Controls<SettingsView>(window).Single();
            await WaitFor(() => Controls<Grid>(settings).Any(g => g.Name == "LinkedRepository"));
            var row = Controls<Grid>(settings).Single(g => g.Name == "LinkedRepository");
            Assert.Contains("beacon", Controls<TextBlock>(row).Select(t => t.Text));
            (window.Width, window.Height) = (1360, 860);
            await Task.Delay(50);
            var scroller = Controls<ScrollViewer>(settings).First();
            scroller.Offset = new Avalonia.Vector(0, row.TranslatePoint(new Point(), (Control)scroller.Content!)!.Value.Y - 360);
            await Task.Delay(50);
            await Preview(window, "settings-linked-repositories");
            var opening = Controls<Button>(row).Single(b => b.Name == "BranchRules");
            opening.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var editor = await Dialog<BranchRulesEditor>(window);
            // The recent branches, and where each goes with no rule yet: the repository's project.
            Assert.Equal("feature/search → Beacon", BranchLine(editor));
            Assert.Null(editor.Problem());
            window.Dialogs.Close(null);
            await WaitFor(() => !window.Dialogs.IsVisible);

            // With a rule written, the branch follows it as it is typed.
            editor = new BranchRulesEditor(window, window.Splits.Repository(Beacon), rules: [new BranchRule("release/*", delta.Id, delta.Tasks[0].Id)]);
            var shown = window.Dialogs.ShowAsync(editor, 580);
            await Dialog<BranchRulesEditor>(window);
            Assert.Equal("feature/search → Beacon", BranchLine(editor));
            Controls<TextBox>(editor).Single(t => t.Name == "BranchRulePattern").Text = "feature/*";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("feature/search → Delta · Support rule", BranchLine(editor));
            await Preview(window, "branch-rules");
            // A rule needs a project before it can be saved.
            await ClickNamed(editor, "BranchRuleAdd");
            Controls<TextBox>(editor).Where(t => t.Name == "BranchRulePattern").Last().Text = "FEATURE/*";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("Choose a project for each rule.", editor.Problem());
            Assert.False(Controls<Button>(editor).Single(b => b.Name == "BranchRulesSave").IsEnabled);
            Controls<TextBox>(editor).Where(t => t.Name == "BranchRulePattern").Last().Text = "";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Null(editor.Problem());

            await ClickNamed(editor, "BranchRulesSave");
            Assert.Equal(true, await shown);
            var sent = fixture.Writes.Last(w => w.Path.EndsWith("/agent-activity/links"));
            Assert.Contains("\"pattern\":\"feature/*\"", sent.Body);
            Assert.Contains(delta.Tasks[0].Id.ToString(), sent.Body);
            Assert.Contains(beacon.Id.ToString(), sent.Body);
            Assert.Equal([new BranchRule("feature/*", delta.Id, delta.Tasks[0].Id)], window.Splits.Rules(window.Splits.Repository(Beacon)));
            // The Search entry's split now sends beacon's work to Delta.
            var search = new TimeEntryResponse { Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = "Search", ProjectId = atlas.Id, Start = At(9), Stop = At(11.5) };
            Assert.Contains(window.Splits.Suggestion(search.Start, search.Stop!.Value, atlas.Id)!.Parts, p => p.Target.ProjectId == delta.Id);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task UnlinkingARepositoryKeepsItAtNoProjectWithUndo()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture { Projects = Projects() };
            fixture.RepositoryLinks = Links(fixture);
            var window = await Window(fixture);
            var (beacon, delta) = (fixture.Projects[1], fixture.Projects[3]);
            var repository = window.Splits.Repository(Beacon);

            await Click(window, "Settings");
            var settings = Controls<SettingsView>(window).Single();
            await WaitFor(() => Controls<Grid>(settings).Any(g => g.Name == "LinkedRepository"));
            Controls<Button>(Controls<Grid>(settings).Single(g => g.Name == "LinkedRepository")).Single(b => b.Name == "BranchRules")
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var editor = await Dialog<BranchRulesEditor>(window);
            await ClickNamed(editor, "BranchRulesUnlink");

            // The repository goes to no project, even though a project is named like it, and its rules go.
            await WaitFor(() => window.Notices.Current == "Unlinked beacon");
            Assert.Contains("\"noProject\":true", fixture.Writes.Last(w => w.Path.EndsWith("/agent-activity/links")).Body);
            Assert.True(window.Splits.IsNoProject(repository));
            Assert.Null(window.Splits.ProjectOf(repository));
            Assert.Empty(window.Splits.Rules(repository));
            await WaitFor(() => Controls<Grid>(settings).Any(g => g.Name == "LinkedRepository" && Controls<Button>(g).All(b => b.Name != "BranchRules")));
            Assert.Contains("No project", Controls<TextBlock>(Controls<Grid>(settings).Single(g => g.Name == "LinkedRepository")).Select(t => t.Text));

            // Undo links it to its project again, with its rules.
            await ClickNamed(window, "NoticeAction");
            await WaitFor(() => window.Splits.ProjectOf(repository) == beacon.Id);
            Assert.Equal([new BranchRule("feature/*", delta.Id, delta.Tasks[0].Id)], window.Splits.Rules(repository));
            Assert.Contains(beacon.Id.ToString(), fixture.Writes.Last(w => w.Path.EndsWith("/agent-activity/links")).Body);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task IgnoringARepositoryLeavesItOutGoesAsAHashAndUndoBringsItsLinkBack()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture { Projects = Projects() };
            fixture.RepositoryLinks = Links(fixture, rules: false);
            var elsewhere = new string('0', 64);
            fixture.Ignored.Add(elsewhere);
            var window = await Window(fixture);
            var (atlas, beacon) = (fixture.Projects[0], fixture.Projects[1]);
            var repository = window.Splits.Repository(Beacon);
            var hash = IgnoredRepositories.Hash(Beacon, fixture.UserId.ToString("D"));
            Assert.Contains(window.Splits.Suggestion(At(9), At(11.5), atlas.Id)!.Parts, p => p.Target.ProjectId == beacon.Id);

            await RepositoryLinkButton.IgnoreAsync(window, repository);

            // Only its hash goes; nothing of it shows, and its link is gone here as on the server.
            await WaitFor(() => window.Notices.Current == "Ignored beacon");
            Assert.Contains(fixture.Writes, w => w.Method == HttpMethod.Put && w.Path == $"/api/me/agent-activity/ignored/{hash}");
            Assert.True(window.Splits.IsIgnored(repository));
            Assert.Null(window.Splits.Chosen(repository));
            await window.Splits.LoadAsync([AgentLogs.Day]);
            Assert.DoesNotContain(window.Splits.Day(AgentLogs.Day)!.Attention, a => a.Repository?.Identity == Beacon);
            Assert.DoesNotContain(window.Splits.Day(AgentLogs.Day)!.Runs, r => r.Repository?.Identity == Beacon);
            Assert.NotEmpty(window.Splits.Day(AgentLogs.Day)!.Ignored);
            Assert.DoesNotContain(window.Splits.Suggestion(At(9), At(11.5), atlas.Id)?.Parts ?? [], p => p.Target.ProjectId == beacon.Id);

            await Click(window, "Settings");
            var settings = Controls<SettingsView>(window).Single();
            await WaitFor(() => Controls<Grid>(settings).Count(g => g.Name == "IgnoredRepository") == 2);
            Assert.DoesNotContain(Controls<Grid>(settings), g => g.Name == "LinkedRepository" && Controls<TextBlock>(g).Any(t => t.Text == "beacon"));
            Assert.Equal(["beacon", "A repository ignored on another device"],
                Controls<Grid>(settings).Where(g => g.Name == "IgnoredRepository").Select(g => Controls<TextBlock>(g).First().Text));

            // Undo stops ignoring it and links it to its project again.
            await ClickNamed(window, "NoticeAction");
            await WaitFor(() => window.Splits.ProjectOf(repository) == beacon.Id);
            Assert.False(window.Splits.IsIgnored(repository));
            Assert.Contains(fixture.Writes, w => w.Method == HttpMethod.Delete && w.Path == $"/api/me/agent-activity/ignored/{hash}");
            Assert.Equal([elsewhere], fixture.Ignored);
            await window.Splits.LoadAsync([AgentLogs.Day]);
            Assert.Contains(window.Splits.Suggestion(At(9), At(11.5), atlas.Id)!.Parts, p => p.Target.ProjectId == beacon.Id);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheSuggestionBannerOpensBranchRulesOverTheEditorAndBringsItBack()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture { Projects = Projects() };
            fixture.RepositoryLinks = Links(fixture, rules: false);
            var atlas = fixture.Projects[0];
            var entry = new TimeEntryResponse { Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = "Search", ProjectId = atlas.Id, IsBillable = true, Start = At(9), Stop = At(11.5) };
            fixture.Entries.Add(entry);
            var window = await Window(fixture);
            var editing = window.EditAsync(window.Entries.Single(e => e.Id == entry.Id));
            var editor = await Dialog<EntryEditor>(window);
            await WaitFor(() => Controls<Button>(editor).Any(b => b.Name == "BannerBranchRules"));
            Controls<Button>(editor).First(b => b.Name == "BannerBranchRules").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Dialog<BranchRulesEditor>(window);
            await Click(window, "Cancel");
            Assert.Same(editor, await Dialog<EntryEditor>(window));
            window.Dialogs.Close(false);
            await editing;
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WhatOneClickCantTakeHasChangeAsItsMainButtonWithTheReason()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture { Projects = Projects() };
            // Beacon has a task now, and the workspace requires one.
            var beacon = fixture.Projects[1] = fixture.Projects[1] with { Tasks = [new() { Id = Guid.NewGuid(), Name = "Search", ProjectId = fixture.Projects[1].Id }] };
            fixture.Settings = fixture.Settings with { RequireTask = true };
            fixture.Entries.Add(new() { Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = "Copper import", ProjectId = fixture.Projects[2].Id, Start = At(10), Stop = At(10 + 25 / 60.0) });
            var window = await Window(fixture);
            var reviewing = window.OpenReviewAsync(ReviewScope.Day(AgentLogs.Day));
            var review = await Dialog<DayReviewView>(window);
            await WaitFor(() => review.Items.Count >= 2);
            var held = review.Items.Where(i => window.Desktop.Hold(i) is not null).ToList();
            var midnight = review.Items.First();
            Assert.Equal("Needs a task on Beacon. Change… to add it.", window.Desktop.Hold(midnight)?.Reason);
            // Its row has Change… as the main button, with the reason, and no take.
            var row = Controls<Border>(review).First(b => b.Name == "ReviewItem" && (string?)b.Tag == midnight.Id);
            Assert.DoesNotContain(Controls<Button>(row), b => b.Name == "ReviewTake");
            Assert.Contains("primary", Controls<Button>(row).Single(b => b.Name == "ReviewChange").Classes);
            Assert.Equal("Needs a task on Beacon. Change… to add it.", Controls<TextBlock>(row).Single(t => t.Name == "ReviewReason").Text);
            await Preview(window, "review-needs-details");

            // Take all says what it left.
            var takeable = review.Items.Count - held.Count;
            await ClickNamed(window, "ReviewTakeAll");
            await WaitFor(() => review.Handled.Count == takeable);
            var saved = takeable == 1 ? "Saved 1 suggestion" : $"Saved {takeable} suggestions";
            Assert.Equal($"{saved} · {held.Count} {(held.Count == 1 ? "needs" : "need")} a project or details", review.Notice?.Text);
            Assert.Equal(held.Select(i => i.Id), review.Items.Select(i => i.Id));

            // Enter on it opens the editor rather than failing.
            KeyDown(review, Key.Enter);
            await Dialog<EntryEditor>(window);
            window.Dialogs.Close(null);
            review = await Dialog<DayReviewView>(window);
            window.Dialogs.Close(null);
            await reviewing;
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    private static void KeyDown(Control control, Key key) =>
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });

    /// <summary>The first recent branch and where it goes, as the editor says it.</summary>
    private static string BranchLine(BranchRulesEditor editor) =>
        string.Join(" ", Controls<TextBlock>(Controls<StackPanel>(editor).First(p => p.Name == "BranchRulesBranch")).Select(t => t.Text));
}
