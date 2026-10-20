import SwiftUI
import SpindleKit

@main
struct SpindleApp: App {
    @NSApplicationDelegateAdaptor private var delegate: AppDelegate
    @State private var model: AppModel
    @State private var desktop: Desktop
    @State private var updates: Updates

    init() {
        let model = AppModel.forLaunch()
        _model = State(initialValue: model)
        _desktop = State(initialValue: Desktop(model: model, active: Self.isInteractive))
        _updates = State(initialValue: Updates(active: Self.isInteractive))
    }

    /// Tests and snapshot runs have no one at the Mac: no shortcuts, reminders, questions about time away or updates.
    private static var isInteractive: Bool {
        #if DEBUG
        if SnapshotRun.current != nil { return false }
        #endif
        return !ProcessInfo.processInfo.isRunningTests
    }

    var body: some Scene {
        Window("Spindle", id: "main") {
            RootView()
                .environment(model)
                .environment(desktop)
        }
        .defaultSize(width: 1100, height: 720)
        .windowResizability(.contentMinSize)
        .commands {
            UpdateCommands(updates: updates)
            AccountCommands(model: model)
            TimerCommands(model: model)
            CalendarCommands(model: model, desktop: desktop)
        }

        MenuBarExtra(isInserted: Bindable(desktop.settings).showMenuBarTimer) {
            MenuBarPanel(model: model)
                .environment(model)
                .environment(desktop)
                .environment(model.notices)
        } label: {
            MenuBarLabel(model: model, desktop: desktop)
        }
        .menuBarExtraStyle(.window)

        Settings {
            SettingsView()
                .environment(model)
                .environment(desktop)
                .environment(updates)
                .environment(model.notices)
        }
    }
}

/// Closing the window leaves Spindle running, with the timer in the menu bar; only Quit ends it.
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
}

extension AppModel {
    /// The model for this launch; snapshot runs keep their sign-in in memory, away from the real one.
    static func forLaunch() -> AppModel {
        #if DEBUG
        if SnapshotRun.current != nil {
            return AppModel(tokens: MemoryTokenStore(), openStore: { _, _ in try LocalStore() })
        }
        #endif
        return AppModel()
    }
}

/// Shows sign-in, the app, or what stands between them.
struct RootView: View {
    @Environment(AppModel.self) private var model
    @Environment(Desktop.self) private var desktop
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Group {
            switch model.phase {
            case .starting:
                ProgressView()
                    .controlSize(.large)
                    .frame(minWidth: 480, maxWidth: .infinity, minHeight: 560, maxHeight: .infinity)
            case .signedOut:
                AuthView()
            case .unavailable(let message):
                UnavailableView(message: message)
            case .signedIn:
                if model.workspace != nil {
                    MainView()
                } else {
                    NoWorkspaceView()
                }
            }
        }
        .environment(model.notices)
        .confirmationDialog("Sign out with changes still waiting?", isPresented: Bindable(model).confirmingSignOut) {
            Button("Sign Out Anyway", role: .destructive) {
                Task { await model.signOut() }
            }
        } message: {
            Text(model.waitingChanges == 1
                ? "1 change hasn't reached the server yet. If you sign out now, it is lost."
                : "\(model.waitingChanges) changes haven't reached the server yet. If you sign out now, they are lost.")
        }
        .onAppear {
            desktop.openMainWindow = { [openWindow] in openWindow(id: "main") }
        }
        .task {
            // Tests run inside the app; they make their own models.
            guard !ProcessInfo.processInfo.isRunningTests else { return }
            await model.start()
            #if DEBUG
            await SnapshotRun.current?.run(model, desktop: desktop)
            #endif
        }
    }
}

/// Signed in, but the account did not load.
private struct UnavailableView: View {
    @Environment(AppModel.self) private var model
    let message: String
    @State private var retrying = false

    var body: some View {
        ContentUnavailableView {
            Label("Spindle isn't available", systemImage: "wifi.exclamationmark")
        } description: {
            Text(message)
        } actions: {
            Button(retrying ? "Trying again…" : "Try Again") {
                retrying = true
                Task {
                    await model.loadAccount()
                    retrying = false
                }
            }
            .buttonStyle(.brand)
            .keyboardShortcut(.defaultAction)
            .disabled(retrying)
            Button("Sign Out") {
                model.requestSignOut()
            }
        }
        .frame(minWidth: 480, maxWidth: .infinity, minHeight: 560, maxHeight: .infinity)
    }
}

/// The person was removed from their last workspace.
private struct NoWorkspaceView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        ContentUnavailableView {
            Label("You are not in a workspace", systemImage: "person.crop.circle.badge.questionmark")
        } description: {
            Text("You were removed from your last workspace. Ask an admin to invite you again.")
        } actions: {
            Button("Sign Out") {
                model.requestSignOut()
            }
        }
        .frame(minWidth: 480, maxWidth: .infinity, minHeight: 560, maxHeight: .infinity)
    }
}

/// "Check for Updates…" in the app menu, below "About Spindle".
struct UpdateCommands: Commands {
    let updates: Updates

    var body: some Commands {
        CommandGroup(after: .appInfo) {
            if updates.isAvailable {
                Button("Check for Updates…") { updates.checkForUpdates() }
                    .disabled(!updates.canCheck)
            }
        }
    }
}

/// Signing out from the app menu.
struct AccountCommands: Commands {
    let model: AppModel

    var body: some Commands {
        CommandGroup(before: .appTermination) {
            if model.phase != .signedOut && model.phase != .starting {
                Button("Sign Out") {
                    model.requestSignOut()
                }
                Divider()
            }
        }
    }
}

/// "Show Agent Activity" in the View menu, for the calendar's lanes, while agent activity is a beta feature.
struct CalendarCommands: Commands {
    let model: AppModel
    let desktop: Desktop

    var body: some Commands {
        CommandGroup(before: .toolbar) {
            if desktop.canShowActivity {
                Button(desktop.showsActivity ? "Hide Agent Activity" : "Show Agent Activity") { desktop.toggleActivity() }
                    .keyboardShortcut("a", modifiers: [.command, .option])
                    .disabled(model.tracker == nil)
                Divider()
            }
        }
    }
}

/// Tracking time from the menu bar, with the keyboard.
struct TimerCommands: Commands {
    let model: AppModel

    var body: some Commands {
        CommandMenu("Timer") {
            let signedIn = model.tracker != nil
            Button("New Entry") { model.focusDescription() }
                .keyboardShortcut("n")
                .disabled(!signedIn)
            if model.tracker?.running != nil {
                Button("Stop Timer") { Task { await model.stop() } }
                    .keyboardShortcut("s")
            } else {
                Button("Start Timer") { Task { await model.start() } }
                    .keyboardShortcut("s")
                    .disabled(!signedIn)
            }
            Button("Continue Last Entry") { Task { await model.continueLastEntry() } }
                .keyboardShortcut("o", modifiers: [.command, .shift])
                .disabled(!signedIn)
            Divider()
            Button("Refresh") { Task { await model.refresh() } }
                .keyboardShortcut("r")
                .disabled(!signedIn)
        }
        CommandGroup(after: .sidebar) {
            ForEach(Array(AppSection.allCases.enumerated()), id: \.element) { index, section in
                Button(section.title) { model.section = section }
                    .keyboardShortcut(KeyEquivalent(Character("\(index + 1)")))
                    .disabled(model.tracker == nil)
            }
            Divider()
        }
    }
}

