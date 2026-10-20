using Avalonia.Controls;
using Avalonia.Headless;
using Spindle.Platform;
using Spindle.Windows.Views;
using static Spindle.Windows.Tests.TestUi;

namespace Spindle.Windows.Tests;

public sealed class SettingsTests
{
    [Fact]
    public async Task SettingsAreSavedOnThisPcAndPomodoroOnTheAccount()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var window = await SignedIn(fixture);
            await Click(window, "Settings");
            var settings = Controls<SettingsView>(window).Single();
            await Preview(window, "settings");
            Controls<ToggleSwitch>(settings).Single(x => x.Name == "RemindToTrack").IsChecked = true;
            Assert.True(window.Preferences.RemindToTrack);
            Assert.True(new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")).Load().RemindToTrack);
            await Preview(window, "settings-reminders", dark: false);
            settings.Show(1);
            Controls<ToggleSwitch>(settings).Single(x => x.Name == "PomodoroEnabled").IsChecked = true;
            await Preview(window, "settings-pomodoro");
            await ClickNamed(settings, "SavePomodoro");
            await WaitFor(() => fixture.Writes.Any(w => w.Path == "/api/me/preferences"));
            Assert.Contains("\"pomodoroEnabled\":true", fixture.Writes.Single(w => w.Path == "/api/me/preferences").Body);
            settings.Show(2);
            await Preview(window, "settings-agents");
            settings.Show(3);
            await Task.Delay(50);
            await Preview(window, "settings-account");
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Space", 0x0003u, 0x20u)]
    [InlineData("Ctrl+Shift+K", 0x0006u, 0x4Bu)]
    [InlineData("Win+Alt+F9", 0x0009u, 0x78u)]
    public void ShortcutsBecomeWindowsHotKeys(string text, uint modifiers, uint key) => Assert.Equal((modifiers, key), HotKeyText.Parse(text));

    [Theory]
    [InlineData("Shift+K")]
    [InlineData("Ctrl+Alt")]
    [InlineData("")]
    public void ShortcutsNeedAModifierAndAKey(string text) => Assert.Null(HotKeyText.Parse(text));
}
