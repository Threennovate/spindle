using Avalonia.Headless;
using Spindle.Windows.Views;
using static Spindle.Windows.Tests.TestUi;

namespace Spindle.Windows.Tests;

/// <summary>The quick entry panel and the notification-area panel.</summary>
public sealed class QuickEntryTests
{
    [Fact]
    public async Task TypedLineStartsTimerAtTheTimeReadAndMentionsBecomeChips()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            TestData.SeedWeek(fixture, running: false);
            var window = await SignedIn(fixture);
            var quick = new QuickEntryWindow(window);
            quick.Open();
            Assert.Equal("Start timer", quick.Rows[0].Title);
            Assert.Contains(quick.Rows, r => r.Section == "Continue");
            await Preview(quick, "quick-entry");
            quick.Type("Review the booking flow @Anc");
            Assert.StartsWith("mention:project:", quick.Rows[0].Id);
            await Preview(quick, "quick-entry-mention");
            await quick.RunRowAsync(0);
            quick.Type("Review the booking flow 25m ago");
            var preview = quick.Rows.Single(r => r.Id == "sentence.entry");
            Assert.Equal("Start “Review the booking flow”", preview.Title);
            Assert.Contains("AnchorNet", preview.Detail);
            await Preview(quick, "quick-entry-typed");
            var before = DateTimeOffset.UtcNow;
            await quick.RunRowAsync(quick.Rows.ToList().FindIndex(r => r.Id == "sentence.entry"));
            Assert.NotNull(window.Running);
            Assert.Equal("Review the booking flow", window.Running.Description);
            Assert.Equal(fixture.Projects[0].Id, window.Running.ProjectId);
            Assert.InRange((before - window.Running.Start).TotalMinutes, 24, 26.1);
            quick.Close();
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TrayPanelShowsTheRunningTimerAndRecentEntries()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            TestData.SeedWeek(fixture, running: true);
            var window = await SignedIn(fixture);
            var flyout = new TrayFlyout(window, () => { }, () => { });
            flyout.Toggle();
            Assert.Contains("Review PR for the booking flow", ErrorText(flyout));
            await Preview(flyout, "tray-running");
            await ClickNamed(flyout, "TrayStop");
            Assert.Null(window.Running);
            flyout.Render();
            await Preview(flyout, "tray-idle");
            flyout.Close();
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }
}
