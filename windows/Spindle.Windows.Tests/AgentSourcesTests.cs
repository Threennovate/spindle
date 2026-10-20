using Spindle.Activity;
using Spindle.Platform;
using Spindle.Windows.Model;

namespace Spindle.Windows.Tests;

public sealed class AgentSourcesTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_source_added_later_is_off_only_for_who_was_already_reading_agent_logs(bool reading, bool copilot)
    {
        var directory = Path.Combine(Path.GetTempPath(), "spindle-preferences-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "preferences.json");
            // Preferences from before Copilot was read.
            File.WriteAllText(path, $$"""{"AgentLogsEnabled":{{(reading ? "true" : "false")}},"AgentLogsDecided":true,"ReadCodexLogs":false}""");
            var store = new PreferencesStore(path);
            var loaded = store.Load();
            Assert.Equal(copilot, loaded.ReadCopilotLogs);
            Assert.Equal(copilot ? [AgentSource.Claude, AgentSource.Copilot] : [AgentSource.Claude], loaded.Sources().Order());
            // Turning agent logs on later reads what was chosen, as a later launch does.
            store.Save(store.Load() with { AgentLogsEnabled = true });
            Assert.Equal(copilot, store.Load().ReadCopilotLogs);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void A_new_install_reads_every_agent()
    {
        var path = Path.Combine(Path.GetTempPath(), "spindle-preferences-" + Guid.NewGuid(), "preferences.json");
        var preferences = new PreferencesStore(path).Load();
        Assert.Equal(AgentLogFormat.All.Select(f => f.Source), preferences.Sources().Order());
        Assert.False(File.Exists(path));
        Assert.False(preferences.Reading(AgentSource.Copilot, false).Reads(AgentSource.Copilot));
    }
}
