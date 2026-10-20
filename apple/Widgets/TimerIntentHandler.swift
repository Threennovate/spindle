import Foundation

/// The widget extension's half of the timer intents: never called, because the system runs a Live Activity's
/// intent in the app (see iOS/Intents/TimerIntents.swift).
enum TimerIntentHandler {
    static func stop() async throws {}
}
