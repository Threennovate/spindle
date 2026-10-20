import Foundation
import Observation
import SpindleKit
import UserNotifications

/// The Pomodoro on the phone. A notification tells of each focus interval's end, with a button that stops the
/// timer then. With auto-stop, the timer also stops at the first interval's end as soon as the app is in front, as
/// it does on the Mac; a phone app can't act while it's in the background.
@MainActor
@Observable
final class PomodoroNotifier: NSObject, UNUserNotificationCenterDelegate {
    @ObservationIgnored private let model: AppModel
    @ObservationIgnored private let center = UNUserNotificationCenter.current()
    /// The entry auto-stop last tried, so a refusal (e.g. missing details) isn't tried again and again.
    @ObservationIgnored private var autoStopped: String?

    nonisolated private static let prefix = "pomodoro-"
    nonisolated private static let category = "pomodoro"
    nonisolated private static let stopAction = "stop"

    init(model: AppModel) {
        self.model = model
        super.init()
        // Set at launch, so the button works also when the system starts the app for it.
        center.delegate = self
        center.setNotificationCategories([
            UNNotificationCategory(
                identifier: Self.category,
                actions: [UNNotificationAction(identifier: Self.stopAction, title: "Stop Timer")],
                intentIdentifiers: []),
        ])
    }

    /// What the Pomodoro depends on; a change schedules the notifications again.
    struct Plan: Equatable {
        var entryId: String?
        var start: Date?
        var enabled = false
        var focusMinutes = 25
        var breakMinutes = 5
        var autoStop = false
    }

    static func plan(running: TimeEntry?, me: Me?) -> Plan {
        guard let running, let preferences = me?.preferences, preferences.pomodoroEnabled else { return Plan() }
        return Plan(
            entryId: running.id, start: running.start, enabled: true, focusMinutes: Int(preferences.pomodoroMinutes),
            breakMinutes: Int(preferences.pomodoroBreakMinutes), autoStop: preferences.pomodoroAutoStop)
    }

    /// Schedules the ends of the running timer's next focus intervals, or clears them.
    func schedule(_ plan: Plan) async {
        let pending = await center.pendingNotificationRequests().map(\.identifier).filter { $0.hasPrefix(Self.prefix) }
        center.removePendingNotificationRequests(withIdentifiers: pending)
        guard plan.enabled, let entryId = plan.entryId, let start = plan.start else { return }
        guard (try? await center.requestAuthorization(options: [.alert, .sound])) == true else { return }
        let now = Date()
        // The next few hours of intervals; with auto-stop, only the first matters.
        for interval in 1...(plan.autoStop ? 1 : 8) {
            let end = start.addingTimeInterval(Double(interval * plan.focusMinutes) * 60)
            guard end > now else { continue }
            let content = UNMutableNotificationContent()
            content.title = "Time for a break"
            content.body = plan.autoStop
                ? "\(plan.focusMinutes) minutes of focus done. Stop the timer and take \(plan.breakMinutes) minutes off."
                : "\(plan.focusMinutes) minutes of focus done. Take \(plan.breakMinutes) minutes off."
            content.sound = .default
            content.categoryIdentifier = Self.category
            content.userInfo = ["entryId": entryId, "end": end.timeIntervalSince1970]
            let trigger = UNTimeIntervalNotificationTrigger(timeInterval: end.timeIntervalSince(now), repeats: false)
            try? await center.add(UNNotificationRequest(identifier: "\(Self.prefix)\(entryId)-\(interval)", content: content, trigger: trigger))
        }
    }

    /// With auto-stop, stops a timer that ran past its first focus interval, at that interval's end.
    func autoStopIfDue() async {
        guard let running = model.tracker?.running, let preferences = model.me?.preferences,
              preferences.pomodoroEnabled, preferences.pomodoroAutoStop, autoStopped != running.id else { return }
        let focus = Int(preferences.pomodoroMinutes)
        guard Pomodoro.intervalsDone(elapsed: Date().timeIntervalSince(running.start), focusMinutes: focus) > 0 else { return }
        autoStopped = running.id
        if await model.stop(at: running.start.addingTimeInterval(Double(focus) * 60)) {
            model.notices.show("The Pomodoro stopped the timer. Take \(preferences.pomodoroBreakMinutes) minutes off.")
        }
    }

    private func stop(entryId: String, at end: Date) async {
        if model.phase == .starting {
            await model.start()
        }
        await model.tracker?.refreshRunning()
        guard model.tracker?.running?.id == entryId else { return }
        await model.stop(at: min(end, Date()))
    }

    // MARK: - UNUserNotificationCenterDelegate

    /// Shown also while the app is in front.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .sound]
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        let info = response.notification.request.content.userInfo
        guard response.actionIdentifier == Self.stopAction,
              let entryId = info["entryId"] as? String,
              let end = info["end"] as? Double else { return }
        await stop(entryId: entryId, at: Date(timeIntervalSince1970: end))
    }
}
