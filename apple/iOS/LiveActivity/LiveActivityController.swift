import ActivityKit
import Foundation
import Observation
import SpindleKit

/// Keeps one Live Activity for the running timer: starts it when a timer starts, changes it with the entry, and
/// ends it when the timer stops or the person signs out. A timer changed or stopped on another device reaches it
/// through APNs, from the server, with the token this sends it; one started elsewhere shows when the app is opened, as
/// starting an activity by push would come with an alert.
@MainActor
@Observable
final class LiveActivityController {
    @ObservationIgnored private let model: AppModel
    /// Each activity's push token, with the entry it shows, by activity.
    @ObservationIgnored private var activityTokens: [String: (entryId: String, token: String)] = [:]
    @ObservationIgnored private var observed: Set<String> = []
    /// What the server has, so an unchanged token isn't sent again.
    @ObservationIgnored private var registered: Registration?

    private struct Registration: Equatable {
        var activityToken: String?
        var entryId: String?
    }

    init(model: AppModel) {
        self.model = model
        guard !ProcessInfo.processInfo.isRunningTests else { return }
        Task { await observeActivities() }
    }

    /// What the activity shows, or nil when nothing runs.
    func show(_ state: TimerActivityAttributes.ContentState?) async {
        let activities = Activity<TimerActivityAttributes>.activities
        guard let state else {
            for activity in activities {
                await activity.end(nil, dismissalPolicy: .immediate)
            }
            return
        }
        let content = ActivityContent(state: state, staleDate: nil)
        // The one for this entry changes; any other, e.g. left from a timer stopped elsewhere, goes.
        var current: Activity<TimerActivityAttributes>?
        for activity in activities {
            if activity.content.state.entryId == state.entryId && current == nil {
                current = activity
            } else {
                await activity.end(nil, dismissalPolicy: .immediate)
            }
        }
        if let current {
            if current.content.state != state {
                await current.update(content)
            }
        } else if ActivityAuthorizationInfo().areActivitiesEnabled {
            _ = try? Activity.request(attributes: TimerActivityAttributes(), content: content, pushType: .token)
        }
    }

    /// The activity for a running entry, with its project from the catalog.
    static func state(for running: TimeEntry?, catalog: Catalog?) -> TimerActivityAttributes.ContentState? {
        guard let running else { return nil }
        let project = catalog?.project(running.projectId)
        // A split entry shows its projects together, in the color of the largest part.
        let name = running.isSplit
            ? running.parts.map { catalog?.project($0.projectId)?.name ?? "No project" }.joined(separator: " + ")
            : project?.name
        return TimerActivityAttributes.ContentState(
            entryId: running.id, description: running.description, projectName: name, projectColor: project?.color, start: running.start)
    }

    // MARK: - Push tokens

    /// Sends the server the activity's token, if it changed. Signed out, the server forgets them on its own.
    func register() async {
        guard model.client.session.hasStoredSession, model.phase != .signedOut else {
            registered = nil
            return
        }
        let running = model.tracker?.running?.id
        let activity = activityTokens.values.first { $0.entryId == running } ?? activityTokens.values.first
        let registration = Registration(activityToken: activity?.token, entryId: activity?.entryId)
        guard registration != registered else { return }
        #if DEBUG
        let sandbox = true
        #else
        let sandbox = false
        #endif
        do {
            _ = try await model.client.registerPushDevice(
                sandbox: sandbox, liveActivityToken: registration.activityToken, liveActivityEntryId: registration.entryId)
            registered = registration
        } catch {
            // Sent again with the next token, or the next launch.
        }
    }

    private func observeActivities() async {
        for activity in Activity<TimerActivityAttributes>.activities {
            observe(activity)
        }
        for await activity in Activity<TimerActivityAttributes>.activityUpdates {
            observe(activity)
        }
    }

    /// Follows an activity's push token until it ends.
    private func observe(_ activity: Activity<TimerActivityAttributes>) {
        guard observed.insert(activity.id).inserted else { return }
        let entryId = activity.content.state.entryId
        Task {
            for await token in activity.pushTokenUpdates {
                activityTokens[activity.id] = (entryId, token.hexString)
                await register()
            }
        }
        Task {
            for await state in activity.activityStateUpdates where state == .ended || state == .dismissed {
                activityTokens[activity.id] = nil
                observed.remove(activity.id)
                await register()
                break
            }
        }
    }
}

private extension Data {
    var hexString: String { map { String(format: "%02x", $0) }.joined() }
}
