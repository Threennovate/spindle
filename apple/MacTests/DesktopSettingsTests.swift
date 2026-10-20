import Foundation
import Testing
@testable import Spindle
import SpindleKit

@MainActor
struct DesktopSettingsTests {
    @Test func turningAgentLogsOffForgetsThemButHidingTheCalendarLanesDoesNot() {
        let suite = UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        var clears = 0
        let settings = DesktopSettings(defaults: defaults, clearActivityCache: { clears += 1 })
        #expect(!settings.agentLogsDecided)
        settings.useAgentLogs = true
        #expect(settings.agentLogsDecided)
        settings.betaFeatures = true
        settings.showAgentActivity = true
        clears = 0
        settings.showAgentActivity = false
        settings.betaFeatures = false
        #expect(clears == 0)
        settings.useAgentLogs = false
        #expect(clears == 1)
        settings.useAgentLogs = true
        settings.setReads(.claude, false)
        #expect(clears == 1)
        settings.setReads(.codex, false)
        #expect(clears == 1)
        settings.setReads(.copilot, false)
        #expect(clears == 2)
    }

    @Test func aSourceAddedLaterIsOffOnlyForWhoWasAlreadyReadingAgentLogs() {
        for reading in [true, false, nil] as [Bool?] {
            let suite = UUID().uuidString
            let defaults = UserDefaults(suiteName: suite)!
            defer { defaults.removePersistentDomain(forName: suite) }
            // Settings from before there was a key per source.
            if let reading { defaults.set(reading, forKey: "useAgentLogs") }
            defaults.set(false, forKey: "activityCodex")
            let settings = DesktopSettings(defaults: defaults, clearActivityCache: {})
            #expect(settings.activitySources == (reading == true ? [.claude] : [.claude, .copilot]))
            // Turning agent logs on later reads what was chosen, as a later launch does.
            settings.useAgentLogs = true
            #expect(DesktopSettings(defaults: defaults, clearActivityCache: {}).activitySources == settings.activitySources)
        }
    }

    @Test func whoReadActivityInTheCalendarBeforeKeepsSuggestions() {
        let suite = UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(true, forKey: "betaFeatures")
        defaults.set(true, forKey: "showAgentActivity")

        let settings = DesktopSettings(defaults: defaults, clearActivityCache: {})

        #expect(settings.useAgentLogs)
        #expect(settings.agentLogsDecided)
        #expect(settings.notifySwitches)
    }

    @Test func shortcutsKeptFromBeforeGetQuickEntryOnceAndKeepItRemoved() throws {
        let suite = UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let before: [ShortcutAction: KeyCombo] = [.toggleTimer: ShortcutAction.defaults[.toggleTimer]!]
        defaults.set(try JSONEncoder().encode(before), forKey: "shortcuts")

        let settings = DesktopSettings(defaults: defaults, clearActivityCache: {})
        #expect(settings.shortcuts[.quickEntry] == ShortcutAction.defaults[.quickEntry])
        #expect(settings.shortcuts[.quickEntry]?.display.hasPrefix("⌃⌥⌘") == true)
        #expect(DesktopSettings(defaults: defaults, clearActivityCache: {}).shortcuts[.quickEntry] != nil)

        settings.shortcuts[.quickEntry] = nil
        #expect(DesktopSettings(defaults: defaults, clearActivityCache: {}).shortcuts[.quickEntry] == nil)
    }
}
