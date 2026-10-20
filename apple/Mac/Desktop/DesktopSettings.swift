import Foundation
import Observation
import ServiceManagement
import SpindleKit

/// How the app behaves on this Mac, kept on it: the menu bar timer, shortcuts, reminders and idle time. The
/// Pomodoro and how times are shown belong to the account, the same on every device.
@Observable
final class DesktopSettings {
    private let defaults: UserDefaults
    @ObservationIgnored private let clearActivityCache: () -> Void

    /// Changes even when logs are turned off and back on between two reads.
    private(set) var activityRevision = 0

    /// Shows features that are still being built, such as agent activity in the calendar.
    var betaFeatures: Bool {
        didSet { defaults.set(betaFeatures, forKey: "betaFeatures") }
    }
    /// Reads Claude Code and Codex logs on this Mac, to suggest splitting time between projects. Turning it off
    /// forgets everything read from them.
    var useAgentLogs: Bool {
        didSet {
            if useAgentLogs != oldValue { activityRevision += 1 }
            defaults.set(useAgentLogs, forKey: "useAgentLogs")
            agentLogsDecided = true
            releaseInactiveActivity()
        }
    }
    /// Whether the person answered the offer to use agent logs, so it is made once.
    private(set) var agentLogsDecided: Bool
    /// Notifies when the timer seems to be on one project while the work moved to another.
    var notifySwitches: Bool {
        didSet { defaults.set(notifySwitches, forKey: "notifySwitches") }
    }
    /// Notifies once at the end of working hours when the day has suggestions waiting in its review.
    var notifyDayReview: Bool {
        didSet { defaults.set(notifyDayReview, forKey: "notifyDayReview") }
    }
    /// Draws agent activity beside the entries in the calendar. Only a view: suggestions don't depend on it.
    var showAgentActivity: Bool {
        didSet { defaults.set(showAgentActivity, forKey: "showAgentActivity") }
    }
    /// The sources whose logs are read, when agent logs are; each is kept as `activity.<source>`.
    private(set) var activitySources: Set<AgentSource> {
        didSet {
            guard activitySources != oldValue else { return }
            activityRevision += 1
            for source in AgentSource.allCases { defaults.set(activitySources.contains(source), forKey: Self.key(source)) }
            releaseInactiveActivity()
        }
    }
    func reads(_ source: AgentSource) -> Bool { activitySources.contains(source) }
    func setReads(_ source: AgentSource, _ on: Bool) {
        if on { activitySources.insert(source) } else { activitySources.remove(source) }
    }
    private static func key(_ source: AgentSource) -> String { "activity." + source.rawValue }

    var showMenuBarTimer: Bool {
        didSet { defaults.set(showMenuBarTimer, forKey: "showMenuBarTimer") }
    }

    /// Asks what to do with the time away when someone comes back to a running timer.
    var detectIdle: Bool {
        didSet { defaults.set(detectIdle, forKey: "detectIdle") }
    }

    var idleMinutes: Int {
        didSet { defaults.set(idleMinutes, forKey: "idleMinutes") }
    }

    /// Reminds to track time on working days when nothing runs.
    var remindToTrack: Bool {
        didSet { defaults.set(remindToTrack, forKey: "remindToTrack") }
    }

    var reminders: ReminderSchedule {
        didSet { defaults.set(try? JSONEncoder().encode(reminders), forKey: "reminders") }
    }

    var shortcuts: [ShortcutAction: KeyCombo] {
        didSet { defaults.set(try? JSONEncoder().encode(shortcuts), forKey: "shortcuts") }
    }

    private func releaseInactiveActivity() {
        if !useAgentLogs || activitySources.isEmpty { clearActivityCache() }
    }

    init(defaults: UserDefaults = .standard, clearActivityCache: @escaping () -> Void = {
        Task { await AgentActivityLoader.shared.clearCache() }
    }) {
        self.defaults = defaults
        self.clearActivityCache = clearActivityCache
        betaFeatures = ActivitySnapshot.root != nil || defaults.bool(forKey: "betaFeatures")
        showAgentActivity = ActivitySnapshot.root != nil || defaults.bool(forKey: "showAgentActivity")
        // Before suggestions had a setting of their own, they came with the calendar's agent activity.
        let readBefore = defaults.bool(forKey: "betaFeatures") && defaults.bool(forKey: "showAgentActivity")
        let reading = ActivitySnapshot.root != nil || (defaults.object(forKey: "useAgentLogs") as? Bool ?? readBefore)
        useAgentLogs = reading
        agentLogsDecided = ActivitySnapshot.root != nil || defaults.object(forKey: "useAgentLogs") != nil || readBefore
        notifySwitches = defaults.object(forKey: "notifySwitches") as? Bool ?? true
        notifyDayReview = defaults.object(forKey: "notifyDayReview") as? Bool ?? true
        // Each source as it was chosen, or as the settings before there was a key per source had it. Someone who was
        // already reading agent logs agreed to Claude Code's and Codex's, so a source added since stays off for them
        // until they turn it on; for anyone else it's on, so turning agent logs on reads what the offer names. It's
        // kept at once, so that turning agent logs on later doesn't change it.
        func chosen(_ source: AgentSource) -> Bool {
            if let on = defaults.object(forKey: Self.key(source)) as? Bool { return on }
            switch source {
            case .claude: return defaults.object(forKey: "activityClaude") as? Bool ?? true
            case .codex: return defaults.object(forKey: "activityCodex") as? Bool ?? true
            case .copilot: return !reading
            }
        }
        let sources = Set(AgentSource.allCases.filter(chosen))
        for source in AgentSource.allCases { defaults.set(sources.contains(source), forKey: Self.key(source)) }
        activitySources = sources
        showMenuBarTimer = defaults.object(forKey: "showMenuBarTimer") as? Bool ?? true
        detectIdle = defaults.object(forKey: "detectIdle") as? Bool ?? true
        idleMinutes = defaults.object(forKey: "idleMinutes") as? Int ?? 5
        remindToTrack = defaults.bool(forKey: "remindToTrack")
        reminders = defaults.data(forKey: "reminders").flatMap { try? JSONDecoder().decode(ReminderSchedule.self, from: $0) } ?? ReminderSchedule()
        shortcuts = defaults.data(forKey: "shortcuts").flatMap { try? JSONDecoder().decode([ShortcutAction: KeyCombo].self, from: $0) }
            ?? ShortcutAction.defaults
        // Shortcuts kept from before quick entry came get its default once; a person who removes it keeps it removed.
        if !defaults.bool(forKey: "quickEntryShortcutOffered") {
            defaults.set(true, forKey: "quickEntryShortcutOffered")
            if shortcuts[.quickEntry] == nil, let combo = ShortcutAction.defaults[.quickEntry], !shortcuts.values.contains(combo) {
                shortcuts[.quickEntry] = combo
                // Observers don't run in an initializer.
                defaults.set(try? JSONEncoder().encode(shortcuts), forKey: "shortcuts")
            }
        }
    }

    // MARK: - Open at login

    /// Whether the app opens when the person logs in to the Mac, as macOS keeps it.
    var opensAtLogin: Bool {
        get { SMAppService.mainApp.status == .enabled }
        set {
            do {
                if newValue {
                    try SMAppService.mainApp.register()
                } else {
                    try SMAppService.mainApp.unregister()
                }
            } catch {
                loginItemError = error.localizedDescription
            }
            loginItemRevision += 1
        }
    }

    /// Counts up when the login item changed, so views reading `opensAtLogin` draw again.
    private(set) var loginItemRevision = 0
    var loginItemError: String?

    /// macOS wants the person to allow the login item in System Settings.
    var loginItemNeedsApproval: Bool {
        _ = loginItemRevision
        return SMAppService.mainApp.status == .requiresApproval
    }
}

/// What a global shortcut does.
enum ShortcutAction: String, Codable, CaseIterable, Identifiable {
    case quickEntry
    case toggleTimer
    case newEntry

    var id: Self { self }

    var label: String {
        switch self {
        case .quickEntry: "Quick entry"
        case .toggleTimer: "Start or stop the timer"
        case .newEntry: "New entry"
        }
    }

    var detail: String {
        switch self {
        case .quickEntry: "Opens a panel over any app: type what you work on, e.g. “Review PR @Acme since 9:40”, or split the timer."
        case .toggleTimer: "Starts a timer without a description, or stops the running one."
        case .newEntry: "Brings Spindle to the front, ready to type what you're working on."
        }
    }

    /// Control-Option-Command with a letter: rarely taken by other apps. Quick entry is K, as ⌘K is the web palette.
    static let defaults: [ShortcutAction: KeyCombo] = [
        .quickEntry: KeyCombo(keyCode: 40, modifiers: [.control, .option, .command]), // K
        .toggleTimer: KeyCombo(keyCode: 1, modifiers: [.control, .option, .command]), // S
        .newEntry: KeyCombo(keyCode: 45, modifiers: [.control, .option, .command]), // N
    ]
}
