import SwiftUI
import SpindleKit

@main
struct SpindleiOSApp: App {
    @State private var model: AppModel
    @State private var pomodoro: PomodoroNotifier
    @State private var liveActivities: LiveActivityController
    @Environment(\.scenePhase) private var scenePhase

    init() {
        let model = AppModel.forLaunch()
        _model = State(initialValue: model)
        _pomodoro = State(initialValue: PomodoroNotifier(model: model))
        _liveActivities = State(initialValue: LiveActivityController(model: model))
        // For intents the system runs, e.g. the Live Activity's stop button, also with the app in the background.
        AppModelHolder.current = model
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(model)
                .environment(model.notices)
                .environment(pomodoro)
                .environment(liveActivities)
                .tint(.control)
        }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active {
                Task {
                    await model.becameActive()
                    await pomodoro.autoStopIfDue()
                }
            }
        }
    }
}

extension AppModel {
    /// The model for this launch; automated runs keep their sign-in in memory, away from the real one.
    static func forLaunch() -> AppModel {
        #if DEBUG
        if LaunchOptions.current.isAutomated {
            return AppModel(tokens: MemoryTokenStore(), openStore: { _, _ in try LocalStore() })
        }
        #endif
        return AppModel()
    }
}
