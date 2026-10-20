import SwiftUI
import SpindleKit

/// Shows sign-in, the app, or what stands between them.
struct RootView: View {
    @Environment(AppModel.self) private var model
    @Environment(PomodoroNotifier.self) private var pomodoro
    @Environment(\.scenePhase) private var scenePhase
    @Environment(LiveActivityController.self) private var liveActivities

    var body: some View {
        Group {
            switch model.phase {
            case .starting:
                LaunchView()
            case .signedOut:
                AuthView()
            case .unavailable(let message):
                UnavailableView(message: message)
            case .signedIn:
                if model.workspace != nil {
                    MainTabView()
                } else {
                    NoWorkspaceView()
                }
            }
        }
        .overlay(alignment: .top) {
            if let notice = model.notices.current {
                NoticeView(notice: notice) { model.notices.dismiss() }
                    .padding(.horizontal, 16)
                    .padding(.top, 8)
                    .transition(.move(edge: .top).combined(with: .opacity))
            }
        }
        .animation(.snappy, value: model.notices.current)
        .confirmationDialog("Sign out with changes still waiting?", isPresented: Bindable(model).confirmingSignOut, titleVisibility: .visible) {
            Button("Sign Out Anyway", role: .destructive) {
                Task { await model.signOut() }
            }
        } message: {
            Text(model.waitingChanges == 1
                ? "1 change hasn't reached the server yet. If you sign out now, it is lost."
                : "\(model.waitingChanges) changes haven't reached the server yet. If you sign out now, they are lost.")
        }
        .task {
            // Tests run inside the app; they make their own models.
            guard !ProcessInfo.processInfo.isRunningTests else { return }
            await model.start()
            #if DEBUG
            await LaunchOptions.current.run(model)
            #endif
        }
        // The running timer on the Lock Screen and in the Dynamic Island, and the Pomodoro's notifications.
        .task(id: LiveActivityController.state(for: model.tracker?.running, catalog: model.tracker?.catalog)) {
            guard !ProcessInfo.processInfo.isRunningTests, model.phase != .starting else { return }
            await liveActivities.show(LiveActivityController.state(for: model.tracker?.running, catalog: model.tracker?.catalog))
        }
        // Each sign-in is a session of its own, which the server reaches with this device's tokens.
        .task(id: model.phase == .signedIn) {
            guard !ProcessInfo.processInfo.isRunningTests else { return }
            await liveActivities.register()
        }
        .task(id: PomodoroNotifier.plan(running: model.tracker?.running, me: model.me)) {
            guard !ProcessInfo.processInfo.isRunningTests, model.phase != .starting else { return }
            await pomodoro.schedule(PomodoroNotifier.plan(running: model.tracker?.running, me: model.me))
        }
        // While the app is in front, the Pomodoro's auto-stop keeps an eye on the running timer.
        .task(id: scenePhase == .active) {
            while scenePhase == .active && !Task.isCancelled {
                await pomodoro.autoStopIfDue()
                try? await Task.sleep(for: .seconds(5))
            }
        }
    }
}

/// While the app picks up the session from the last launch.
private struct LaunchView: View {
    var body: some View {
        VStack(spacing: 24) {
            Logo(height: 34)
            ProgressView()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
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
            .buttonStyle(.prominent)
            .disabled(retrying)
            Button("Sign Out") {
                model.requestSignOut()
            }
        }
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
    }
}
