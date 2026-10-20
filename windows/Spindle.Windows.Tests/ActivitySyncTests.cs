using System.Text.Json;
using Avalonia.Headless;
using Spindle.Core;
using Spindle.Platform;
using static Spindle.Windows.Tests.TestUi;

namespace Spindle.Windows.Tests;

/// <summary>Uploading agent activity to the account when the person turned that on, from the Mac's synthetic agent logs.</summary>
public sealed class ActivitySyncTests
{
    private static readonly DateTimeOffset AfterTheLogs = new(AgentLogs.Day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static async Task<MainWindow> Window(ApiFixture fixture, bool logs = true)
    {
        new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")).Save(new DesktopPreferences { AgentLogsEnabled = logs, AgentLogsDecided = true });
        var window = await SignedIn(fixture);
        window.Desktop.Stop();
        return window;
    }

    private static Write[] Uploads(ApiFixture fixture) => fixture.Writes.Where(w => w.Path == "/api/me/agent-activity/sessions").ToArray();

    [Fact]
    public async Task SyncSendsSessionsWithoutPathsOnlyOnceAndAgainWhenTheAccountSettingChanges()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture();
            fixture.AgentActivity = new() { Sync = true, Titles = true, SyncedSince = AfterTheLogs.AddDays(-60) };
            var window = await Window(fixture);
            var sync = window.ActivitySync;
            Assert.True(sync.IsSyncing);

            await sync.TickAsync(AfterTheLogs);
            var first = Assert.Single(Uploads(fixture));
            using var body = JsonDocument.Parse(first.Body);
            Assert.Equal(sync.Device(), body.RootElement.GetProperty("deviceId").GetGuid());
            var sessions = body.RootElement.GetProperty("sessions").EnumerateArray().ToArray();
            Assert.NotEmpty(sessions);
            Assert.All(sessions, s =>
            {
                Assert.Matches("^(claude|codex):", s.GetProperty("sessionId").GetString());
                Assert.NotEmpty(s.GetProperty("runs").EnumerateArray());
                if (s.GetProperty("repository").ValueKind == JsonValueKind.Object)
                    Assert.Matches("^(origin|path):", s.GetProperty("repository").GetProperty("identity").GetString());
            });
            Assert.Contains(sessions, s => s.GetProperty("title").ValueKind == JsonValueKind.String);
            Assert.DoesNotContain("/tmp", first.Body);
            Assert.DoesNotContain(logs.Home, first.Body);

            // Nothing changed: nothing goes, and not before the interval is over.
            await sync.TickAsync(AfterTheLogs.AddMinutes(1));
            await sync.TickAsync(AfterTheLogs.AddMinutes(6));
            Assert.Single(Uploads(fixture));

            // Titles allowed again after being off: everything goes again, without titles while they're off.
            fixture.AgentActivity = new() { Sync = true, Titles = false, SyncedSince = AfterTheLogs.AddMinutes(7) };
            await sync.TickAsync(AfterTheLogs.AddMinutes(12));
            Assert.Equal(2, Uploads(fixture).Length);
            using var again = JsonDocument.Parse(Uploads(fixture)[1].Body);
            Assert.All(again.RootElement.GetProperty("sessions").EnumerateArray(), s => Assert.Equal(JsonValueKind.Null, s.GetProperty("title").ValueKind));
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NothingIsReadOrSentWithoutSyncOrWithoutConsentToReadTheLogs()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var off = new ApiFixture();
            var window = await Window(off);
            await window.ActivitySync.TickAsync(AfterTheLogs);
            Assert.Empty(Uploads(off));

            using var noConsent = new ApiFixture { AgentActivity = new() { Sync = true, SyncedSince = AfterTheLogs.AddDays(-60) } };
            var other = await Window(noConsent, logs: false);
            await other.ActivitySync.TickAsync(AfterTheLogs);
            Assert.Empty(Uploads(noConsent));
            window.Quit();
            other.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TurningSyncOffElsewhereStopsUploadingAndTurningItOnAgainSendsEverythingAgain()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture { AgentActivity = new() { Sync = true, SyncedSince = AfterTheLogs.AddDays(-60) } };
            var window = await Window(fixture);
            var sync = window.ActivitySync;
            await sync.TickAsync(AfterTheLogs);
            Assert.Single(Uploads(fixture));

            fixture.AgentActivity = new();
            await sync.TickAsync(AfterTheLogs.AddMinutes(6));
            Assert.False(sync.IsSyncing);
            Assert.Single(Uploads(fixture));

            // Turned on in settings here: the server's new "since" makes everything go again.
            await sync.ChangeAsync(sync: true, titles: false);
            Assert.Contains(fixture.Writes, w => w.Path == "/api/me/agent-activity/settings");
            await sync.TickAsync(AfterTheLogs.AddMinutes(12));
            Assert.Equal(2, Uploads(fixture).Length);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChangingTheSettingIsMarkedWhileItIsUnderWayAndOnlyOnePutGoesOut()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var logs = new AgentLogs();
            using var fixture = new ApiFixture();
            var window = await Window(fixture);
            var sync = window.ActivitySync;
            Assert.False(sync.IsChanging);

            fixture.HoldSettingsChange = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = sync.ChangeAsync(sync: true, titles: false);
            Assert.True(sync.IsChanging);
            // A second toggle while the first is under way sends nothing.
            await sync.ChangeAsync(sync: false, titles: false);
            fixture.HoldSettingsChange.SetResult();
            await first;
            Assert.False(sync.IsChanging);
            Assert.True(sync.IsSyncing);
            Assert.Single(fixture.Writes, w => w.Path == "/api/me/agent-activity/settings");

            // After a failure too.
            fixture.HoldSettingsChange = null;
            fixture.FailSettingsChange = true;
            await Assert.ThrowsAsync<ApiException>(() => sync.ChangeAsync(sync: false, titles: false));
            Assert.False(sync.IsChanging);
            Assert.True(sync.IsSyncing);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }
}
