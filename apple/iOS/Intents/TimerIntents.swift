import AppIntents
import Foundation
import SpindleKit

/// The app's half of the timer intents: they act on the same model as the app, also when the system runs them
/// with the app in the background.
enum TimerIntentHandler {
    @MainActor
    static func stop() async throws {
        let model = try await signedInModel()
        // What's running may have changed elsewhere since the app last looked.
        await model.tracker?.refreshRunning()
        guard model.tracker?.running != nil else { return }
        guard await model.stop() else {
            throw TimerIntentError(model.editing != nil
                ? "Open Spindle to add the details this workspace requires, then stop the timer."
                : "Spindle couldn't stop the timer.")
        }
    }

    @MainActor
    static func start(description: String?) async throws {
        let model = try await signedInModel()
        var details = EntryDetails()
        details.description = description?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard await model.start(details) else {
            throw TimerIntentError("Spindle couldn't start the timer.")
        }
    }

    /// The app's model, picked up from the last launch if the system started the app just for this.
    @MainActor
    private static func signedInModel() async throws -> AppModel {
        guard let model = AppModelHolder.current else { throw TimerIntentError("Spindle isn't ready yet.") }
        if model.phase == .starting {
            await model.start()
        }
        guard model.phase == .signedIn, model.tracker != nil else {
            throw TimerIntentError("Sign in to Spindle first.")
        }
        return model
    }
}

/// What Siri says, or Shortcuts shows, when a timer intent can't do its job.
struct TimerIntentError: Error, CustomLocalizedStringResourceConvertible {
    let message: String

    init(_ message: String) {
        self.message = message
    }

    var localizedStringResource: LocalizedStringResource { "\(message)" }
}

/// Starts a timer, with what you're working on if you say it.
struct StartTimerIntent: AppIntent {
    static let title: LocalizedStringResource = "Start Timer"
    static let description = IntentDescription("Starts a Spindle timer, with a description if you give one.")

    @Parameter(title: "Description")
    var text: String?

    static var parameterSummary: some ParameterSummary {
        Summary("Start a timer for \(\.$text)")
    }

    func perform() async throws -> some IntentResult & ProvidesDialog {
        try await TimerIntentHandler.start(description: text)
        return .result(dialog: "Timer started.")
    }
}

/// The timer in Siri, Spotlight and the Shortcuts app, without any setup.
struct SpindleShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(
            intent: StartTimerIntent(),
            phrases: ["Start a timer in \(.applicationName)", "Start tracking in \(.applicationName)"],
            shortTitle: "Start Timer",
            systemImageName: "play.fill")
        AppShortcut(
            intent: StopTimerIntent(),
            phrases: ["Stop the timer in \(.applicationName)", "Stop tracking in \(.applicationName)"],
            shortTitle: "Stop Timer",
            systemImageName: "stop.fill")
    }
}

/// The app's model, for intents the system runs outside any view.
@MainActor
enum AppModelHolder {
    static var current: AppModel?
}
