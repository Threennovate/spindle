import ActivityKit
import AppIntents
import Foundation

/// The running timer as a Live Activity, on the Lock Screen and in the Dynamic Island. The app starts, updates and
/// ends it; the widget extension draws it. The time counts on its own from `start`, without updates.
nonisolated struct TimerActivityAttributes: ActivityAttributes {
    struct ContentState: Codable, Hashable {
        var entryId: String
        var description: String
        var projectName: String?
        var projectColor: String?
        var start: Date
    }
}

/// Stops the running timer, e.g. with the Live Activity's button, Siri or Shortcuts. The system runs it in the app,
/// which has the account; the widget extension only shows the button, so its `TimerIntentHandler` does nothing.
nonisolated struct StopTimerIntent: LiveActivityIntent {
    static let title: LocalizedStringResource = "Stop Timer"
    static let description = IntentDescription("Stops the running Spindle timer.")

    init() {}

    func perform() async throws -> some IntentResult {
        try await TimerIntentHandler.stop()
        return .result()
    }
}
