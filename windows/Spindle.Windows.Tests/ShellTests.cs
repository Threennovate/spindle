using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Spindle.Core;
using Spindle.Platform;
using Spindle.Windows.Views;
using static Spindle.Windows.Tests.TestUi;

namespace Spindle.Windows.Tests;

public sealed class ShellTests
{
    [Fact]
    public async Task SignInLoadsWorkspaceAndReportsUseBackendTotalsAndSelectedFilters()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var window = new MainWindow(fixture.Api, new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")), fixture.DataDirectory);
            window.Show();
            await Preview(window, "sign-in");
            Controls<TextBox>(window).Single(x => x.Name == "Email").Text = "test@example.com";
            Controls<TextBox>(window).Single(x => x.Name == "Password").Text = "password";
            await Click(window, "Sign in");
            Assert.True(fixture.Api.IsAuthenticated);
            Assert.Contains(Controls<Button>(window), x => x.Name == "NavTimer");
            Assert.Equal(ApiFixture.Zagreb, window.Zone);
            fixture.Summary = TestData.Summary(fixture);
            await Click(window, "Reports");
            var reports = Controls<ReportsView>(window).Single();
            await WaitFor(() => ErrorText(window).Contains("41:30:00"));
            Assert.Contains("61% of the total", ErrorText(window));
            await Preview(window, "reports");
            reports.Projects.Add(fixture.Projects[0].Id);
            reports.Billable = true;
            reports.Changed();
            await WaitFor(() => fixture.Writes.Count(x => x.Path.EndsWith("/reports/summary")) == 2);
            var query = JsonSerializer.Deserialize<SummaryReportRequest>(fixture.Writes.Last().Body, SpindleApiClient.JsonOptions)!;
            Assert.Equal(new[] { fixture.Projects[0].Id }, query.ProjectIds);
            Assert.True(query.Billable);
            Assert.Equal(reports.Range.From, query.From);
            await Click(window, "Detailed");
            await WaitFor(() => fixture.Writes.Any(x => x.Path.EndsWith("/reports/detailed")));
            await Task.Delay(50);
            await Preview(window, "reports-detailed");
            await Click(window, "Weekly");
            await WaitFor(() => fixture.Writes.Any(x => x.Path.EndsWith("/reports/weekly")));
            await Task.Delay(50);
            await Preview(window, "reports-weekly");
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPINDLE_UI_PREVIEW")))
                foreach (var catalog in new[] { "Projects", "Clients", "Tags", "Settings" })
                {
                    await Click(window, catalog);
                    await Task.Delay(50);
                    await Preview(window, catalog.ToLowerInvariant());
                }
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TimerStartStopAndContinuePreserveSelectedProjectAndTask()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var window = await SignedIn(fixture);
            await Preview(window, "timer-empty");
            Controls<TextBox>(window).Single(x => x.Name == "TimerDescription").Text = "Windows implementation";
            var timer = Controls<TimerView>(window).Single();
            await timer.Bar.ChooseProjectAsync(fixture.Projects[0].Id, fixture.Projects[0].Tasks[0].Id);
            await Click(window, "Start timer");
            Assert.NotNull(window.Running);
            Assert.Equal("Windows implementation", window.Running.Description);
            Assert.Equal(fixture.Projects[0].Id, window.Running.ProjectId);
            Assert.Equal(fixture.Projects[0].Tasks[0].Id, window.Running.TaskId);
            Assert.True(window.Running.IsBillable);
            Assert.Contains(Controls<TextBox>(window), x => x.Name == "RunningDescription" && x.Text == "Windows implementation");
            await Click(window, "Stop timer");
            Assert.Null(window.Running);
            Assert.NotNull(Assert.Single(fixture.Entries).Stop);
            await Click(window, "Continue");
            Assert.NotNull(window.Running);
            Assert.Equal("Windows implementation", window.Running.Description);
            Assert.Equal(fixture.Projects[0].Tasks[0].Id, window.Running.TaskId);
            Assert.Equal(2, fixture.Entries.Count);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SnapshotsOfAWeekOfWork()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPINDLE_UI_PREVIEW"))) return;
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            TestData.SeedWeek(fixture, running: true);
            var window = await SignedIn(fixture);
            await Preview(window, "timer-running");
            await Click(window, "Calendar");
            await Task.Delay(100);
            await Preview(window, "calendar");
            var editing = window.EditAsync(window.Running);
            await Dialog<EntryEditor>(window);
            await Preview(window, "editor-running");
            window.Dialogs.Close(null);
            await editing;
            await window.StopAsync();
            await Click(window, "Timer");
            await Preview(window, "timer");
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReadOnlyWorkspaceShowsBannerAndCannotStart()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture { Access = OrganizationAccess.ReadOnly };
            var window = await SignedIn(fixture);
            Assert.Contains(Controls<Border>(window), x => x.Name == "ReadOnlyBanner");
            Assert.Contains("Kovač Studio is read-only.", string.Join(" ", Controls<TextBlock>(window).SelectMany(t => t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? [])));
            Assert.False(Controls<Button>(window).Single(x => x.Name == "StartTimer").IsEnabled);
            await Preview(window, "read-only", dark: false);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SigningOutAndIntoAnotherAccountBuildsTimerForNewWorkspace()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var window = new MainWindow(fixture.Api, new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")), fixture.DataDirectory);
            window.Show();
            async Task SignIn()
            {
                Controls<TextBox>(window).Single(x => x.Name == "Email").Text = "test@example.com";
                Controls<TextBox>(window).Single(x => x.Name == "Password").Text = "password";
                await Click(window, "Sign in");
            }
            await SignIn();
            var oldWorkspace = fixture.Workspace;
            await window.SignOutAsync();
            fixture.Workspace = Guid.NewGuid();
            fixture.UserId = Guid.NewGuid();
            await SignIn();
            await WaitFor(() => Controls<Button>(window).Any(b => b.Name == "StartTimer"));
            await Click(window, "Start timer");
            Assert.NotNull(window.Running);
            Assert.Equal(fixture.Workspace, window.Running.WorkspaceId);
            var write = Assert.Single(fixture.Writes);
            Assert.Contains(fixture.Workspace.ToString(), write.Path);
            Assert.DoesNotContain(oldWorkspace.ToString(), write.Path);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DeletingEntryRequiresConfirmationAndCancelKeepsIt()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            fixture.Entries.Add(new() { Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = "Completed work",
                Start = TestClock.Now.AddHours(-1), Stop = TestClock.Now.AddMinutes(-30) });
            var window = await SignedIn(fixture);
            var timer = Controls<TimerView>(window).Single();
            var deleting = timer.DeleteAsync(window.Entries.Single());
            await WaitFor(() => window.Dialogs.IsVisible);
            Assert.Contains("“Completed work” and its 0:30:00 will be removed.", ErrorText(window.Dialogs));
            await Preview(window, "delete-confirmation");
            await Click(window.Dialogs, "Cancel");
            await deleting;
            Assert.Single(fixture.Entries);
            Assert.Empty(fixture.Writes);
            deleting = timer.DeleteAsync(window.Entries.Single());
            await WaitFor(() => window.Dialogs.IsVisible);
            await Click(window.Dialogs, "Delete");
            await deleting;
            await WaitFor(() => fixture.Entries.Count == 0);
            Assert.Equal(HttpMethod.Delete, Assert.Single(fixture.Writes).Method);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MissingCredentialsShowsErrorWithoutAuthentication()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var window = new MainWindow(fixture.Api, new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")), fixture.DataDirectory);
            window.Show();
            await Click(window, "Sign in");
            Assert.False(fixture.Api.IsAuthenticated);
            Assert.Contains("Enter your email address.", ErrorText(window));
            await Preview(window, "sign-in-error", dark: false);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    private static ComboBox FieldSelector(Control view, string label) => Controls<StackPanel>(view)
        .Single(panel => panel.Children.Count == 2 && panel.Children[0] is TextBlock text && text.Text == label && panel.Children[1] is ComboBox)
        .Children.OfType<ComboBox>().Single();
}
