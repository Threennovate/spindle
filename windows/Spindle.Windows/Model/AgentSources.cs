using Spindle.Activity;
using Spindle.Platform;

namespace Spindle.Windows.Model;

/// <summary>Which agents' logs the preferences let this PC read, as the Mac's <c>DesktopSettings.activitySources</c>.</summary>
public static class AgentSources
{
    public static bool Reads(this DesktopPreferences preferences, AgentSource source) => source switch
    {
        AgentSource.Claude => preferences.ReadClaudeLogs,
        AgentSource.Codex => preferences.ReadCodexLogs,
        AgentSource.Copilot => preferences.ReadCopilotLogs,
        _ => false,
    };

    public static DesktopPreferences Reading(this DesktopPreferences preferences, AgentSource source, bool on) => source switch
    {
        AgentSource.Claude => preferences with { ReadClaudeLogs = on },
        AgentSource.Codex => preferences with { ReadCodexLogs = on },
        AgentSource.Copilot => preferences with { ReadCopilotLogs = on },
        _ => preferences,
    };

    public static IReadOnlySet<AgentSource> Sources(this DesktopPreferences preferences) =>
        AgentLogFormat.All.Select(f => f.Source).Where(preferences.Reads).ToHashSet();
}
