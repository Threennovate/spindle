using System.Text.Json;

namespace Spindle.Platform;

public sealed record DesktopPreferences
{
    public string ServerUrl { get; init; } = "https://app.spindletrack.com";
    public Guid? WorkspaceId { get; init; }
    public bool LaunchAtLogin { get; init; }
    public bool MinimizeToTray { get; init; } = true;
    public bool GlobalShortcutEnabled { get; init; }
    public bool AgentLogsEnabled { get; init; }
    public int IdleReminderMinutes { get; init; } = 10;
    /// <summary>The timer bar adds time by start and end instead of starting a timer.</summary>
    public bool ManualEntry { get; init; }
    /// <summary>Ask about time away from a running timer, after this many idle minutes.</summary>
    public bool DetectIdle { get; init; } = true;
    public int IdleMinutes { get; init; } = 5;
    /// <summary>Remind to track on these days (0 = Sunday) between these minutes of the day, every so often.</summary>
    public bool RemindToTrack { get; init; }
    /// <summary>The days to remind on, a bit per weekday from Sunday (bit 0); Monday to Friday by default.</summary>
    public int ReminderDayMask { get; init; } = 0b0111110;
    public bool RemindsOn(int weekday) => (ReminderDayMask & (1 << weekday)) != 0;
    [System.Text.Json.Serialization.JsonIgnore]
    public int[] ReminderDays => Enumerable.Range(0, 7).Where(RemindsOn).ToArray();
    public int ReminderFrom { get; init; } = 9 * 60;
    public int ReminderTo { get; init; } = 17 * 60;
    public int ReminderEveryMinutes { get; init; } = 30;
    /// <summary>Notify when agents' work moves to another project while the timer runs.</summary>
    public bool NotifySwitches { get; init; } = true;
    /// <summary>Notify once after working hours when the day has suggestions waiting in its review.</summary>
    public bool NotifyDayReview { get; init; } = true;
    /// <summary>Which agents' logs are read, when agent logs are.</summary>
    public bool ReadClaudeLogs { get; init; } = true;
    public bool ReadCodexLogs { get; init; } = true;
    /// <summary>
    /// In preferences from before Copilot was read, someone already reading agent logs agreed to Claude Code's and
    /// Codex's, so it stays off for them until they turn it on; for anyone else it's on (<see cref="PreferencesStore.Load"/>).
    /// </summary>
    public bool ReadCopilotLogs { get; init; } = true;
    /// <summary>The person answered the offer to read agent logs.</summary>
    public bool AgentLogsDecided { get; init; }
    public bool BetaFeatures { get; init; }
    public bool ShowAgentActivity { get; init; }
    /// <summary>System-wide shortcuts as Windows writes them, e.g. Ctrl+Alt+Space; null when off.</summary>
    public string? QuickEntryShortcut { get; init; } = "Ctrl+Alt+Space";
    public string? ToggleTimerShortcut { get; init; }
    public string? NewEntryShortcut { get; init; }
    public bool CalendarDayMode { get; init; }
    /// <summary>When each recent entry's split was set, as this PC first saw it, by entry.</summary>
    public SplitStatements SplitStatements { get; init; } = [];
    /// <summary>Untracked agent time dismissed in the calendar, by day and start minute ("2026-10-15|552"), on this PC.</summary>
    public KeptList DismissedUntrackedTime { get; init; } = [];
    /// <summary>What the day's review was told "Not now" for, by item; chips and the calendar keep it. On this PC.</summary>
    public KeptList ReviewPutOff { get; init; } = [];
    /// <summary>The day ("2026-10-15") whose card above the timer was put off with "Not now".</summary>
    public string? ReviewCardDismissed { get; init; }
    /// <summary>The working day whose review the end-of-day notification offered already.</summary>
    public string? DayReviewOffered { get; init; }
}

/// <summary>Keys kept on this PC, the newest last, equal by content so preferences stay comparable as values.</summary>
public sealed class KeptList : List<string>
{
    /// <summary>The most keys kept; the oldest go first.</summary>
    public const int Limit = 500;

    public KeptList() { }

    public KeptList(IEnumerable<string> keys) : base(keys) { }

    /// <summary>This list with <paramref name="key"/> added, within <see cref="Limit"/>.</summary>
    public KeptList Adding(string key) => Contains(key) ? this : new KeptList(this.Append(key).TakeLast(Limit));

    /// <summary>This list without <paramref name="key"/>.</summary>
    public KeptList Removing(string key) => Contains(key) ? new KeptList(this.Where(k => k != key)) : this;

    public override bool Equals(object? obj) => obj is KeptList other && this.SequenceEqual(other);

    public override int GetHashCode() => Count;
}

/// <summary>Split statements by entry, equal by content so preferences stay comparable as values.</summary>
public sealed class SplitStatements : Dictionary<Guid, SplitStatement>
{
    public SplitStatements() { }

    public SplitStatements(IEnumerable<KeyValuePair<Guid, SplitStatement>> statements) : base(statements) { }

    public override bool Equals(object? obj) =>
        obj is SplitStatements other && Count == other.Count && this.All(s => other.TryGetValue(s.Key, out var value) && value == s.Value);

    public override int GetHashCode() => Count;
}

/// <summary>
/// When an entry's current split was set, as this PC saw it, and what became of the change offered for it:
/// <see cref="Notified"/> when a notification offered it (ignoring that does nothing), <see cref="Answered"/> when it
/// was taken, looked at, or put off with "Not now". Another split (<see cref="Parts"/>) starts over.
/// </summary>
public sealed record SplitStatement(string Parts, DateTimeOffset At, bool Notified = false, bool Answered = false);

/// <summary>Only non-secret preferences are persisted here.</summary>
public sealed class PreferencesStore
{
    private readonly string path;
    public PreferencesStore(string? path = null) => this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spindle", "preferences.json");
    public DesktopPreferences Load()
    {
        if (!File.Exists(path)) return new();
        DesktopPreferences loaded;
        bool fromBeforeCopilot;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            loaded = json.Deserialize<DesktopPreferences>() ?? new();
            fromBeforeCopilot = json.RootElement.ValueKind == JsonValueKind.Object && !json.RootElement.TryGetProperty(nameof(DesktopPreferences.ReadCopilotLogs), out _);
        }
        catch (JsonException) { return new(); }
        if (!fromBeforeCopilot) return loaded;
        // Kept at once, so that turning agent logs on later doesn't change it.
        loaded = loaded with { ReadCopilotLogs = !loaded.AgentLogsEnabled };
        try { Save(loaded); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return loaded;
    }
    public void Save(DesktopPreferences preferences)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
