using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Spindle.Core;
using Spindle.Windows.Desktop;
using Spindle.Windows.Views;
using static Spindle.Windows.Tests.TestUi;

namespace Spindle.Windows.Tests;

/// <summary>Time away, the Pomodoro and reminders, as the Mac's Desktop does them.</summary>
public sealed class DesktopTests
{
    [Fact]
    public async Task ComingBackAfterTimeAwayAsksAndDiscardingStopsWhenTheAwayTimeBegan()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var start = DateTimeOffset.UtcNow.AddMinutes(-60);
            fixture.Entries.Add(new TimeEntryResponse { Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = "Code review", ProjectId = fixture.Projects[1].Id, Start = start });
            var window = await SignedIn(fixture);
            window.Desktop.Stop();
            var away = TimeSpan.FromMinutes(12);
            var desktop = new DesktopService(window, () => away);
            var now = DateTimeOffset.UtcNow;
            await desktop.TickAsync(now);
            Assert.False(window.Dialogs.IsVisible);
            away = TimeSpan.FromSeconds(1);
            var asking = desktop.TickAsync(now);
            await WaitFor(() => window.Dialogs.IsVisible);
            Assert.Contains("You were away for 12m", ErrorText(window.Dialogs));
            await Preview(window, "time-away");
            await ClickNamed(window.Dialogs, "DiscardAway");
            await asking;
            var write = Assert.Single(fixture.Writes);
            Assert.Equal(HttpMethod.Put, write.Method);
            var request = JsonSerializer.Deserialize<TimeEntryRequest>(write.Body, SpindleApiClient.JsonOptions)!;
            Assert.Equal(now.AddMinutes(-12).ToUnixTimeSeconds(), request.Stop!.Value.ToUnixTimeSeconds());
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FocusIntervalEndingStopsTheTimerAndStartsTheBreak()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            fixture.Preferences = fixture.Preferences with { PomodoroEnabled = true, PomodoroMinutes = 25, PomodoroBreakMinutes = 5, PomodoroAutoStop = true };
            var start = DateTimeOffset.UtcNow.AddMinutes(-26);
            fixture.Entries.Add(new TimeEntryResponse { Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = "Write the release notes", ProjectId = fixture.Projects[1].Id, Start = start });
            var window = await SignedIn(fixture);
            window.Desktop.Stop();
            await Preview(window, "pomodoro-running", dark: false);
            var desktop = new DesktopService(window, () => TimeSpan.Zero);
            await desktop.TickAsync();
            var stop = Assert.Single(fixture.Writes, w => w.Path.EndsWith("/stop"));
            Assert.Equal(start.AddMinutes(25).ToUnixTimeSeconds(), JsonSerializer.Deserialize<StopTimeEntryRequest>(stop.Body, SpindleApiClient.JsonOptions)!.Stop!.Value.ToUnixTimeSeconds());
            Assert.NotNull(window.BreakEnds);
            Assert.True(Toasts.Shown.ContainsKey(ToastKind.FocusDone));
            await Preview(Toasts.Shown[ToastKind.FocusDone], "toast-focus-done");
            await Preview(window, "pomodoro-break", dark: false);
            Toasts.Close(ToastKind.FocusDone);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }
}
