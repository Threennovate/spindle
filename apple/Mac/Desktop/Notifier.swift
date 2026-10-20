import Foundation
import UserNotifications

/// Notifications, e.g. a reminder to track or a Pomodoro break, with buttons that act in the app.
final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    enum Kind: String {
        case reminder, focusDone, breakOver
        /// The timer stopped on time that went to several projects.
        case splitOffer
        /// The timer runs on one project while the work moved to another.
        case switchOffer
        /// A split or switch was just taken with one click, to undo.
        case splitSaved
        /// What the quick entry panel just did.
        case quickEntry
        /// An agent proposed entries or changes to review.
        case proposals
        /// Working hours are over and the day has suggestions to review.
        case dayReview
    }

    enum Action: String {
        case startTimer, stopTimer, continueEntry
        /// Starts the timer where untracked agent work began: "Start from 9:12".
        case startFrom
        case split, switchProject, keep
        case undo
        case review
    }

    /// A notification's button was clicked, or the notification itself (with no action).
    var onAction: ((Action?, Kind) -> Void)?

    private let center = UNUserNotificationCenter.current()
    /// Each kind's buttons; a notification may name its own, e.g. "Switch to Beacon".
    private var categories: [String: UNNotificationCategory] = [:]

    override init() {
        super.init()
        center.delegate = self
        let categories: [UNNotificationCategory] = [
            UNNotificationCategory(identifier: Kind.reminder.rawValue, actions: [
                UNNotificationAction(identifier: Action.startTimer.rawValue, title: "Start Timer"),
            ], intentIdentifiers: []),
            UNNotificationCategory(identifier: Kind.focusDone.rawValue, actions: [
                UNNotificationAction(identifier: Action.stopTimer.rawValue, title: "Stop Timer"),
            ], intentIdentifiers: []),
            UNNotificationCategory(identifier: Kind.breakOver.rawValue, actions: [
                UNNotificationAction(identifier: Action.continueEntry.rawValue, title: "Continue"),
            ], intentIdentifiers: []),
            UNNotificationCategory(identifier: Kind.splitOffer.rawValue, actions: [
                UNNotificationAction(identifier: Action.split.rawValue, title: "Split"),
                UNNotificationAction(identifier: Action.keep.rawValue, title: "Keep as Is"),
            ], intentIdentifiers: []),
            UNNotificationCategory(identifier: Kind.quickEntry.rawValue, actions: [
                UNNotificationAction(identifier: Action.undo.rawValue, title: "Undo"),
            ], intentIdentifiers: []),
            UNNotificationCategory(identifier: Kind.proposals.rawValue, actions: [
                UNNotificationAction(identifier: Action.review.rawValue, title: "Review", options: [.foreground]),
            ], intentIdentifiers: []),
            UNNotificationCategory(identifier: Kind.switchOffer.rawValue, actions: [
                UNNotificationAction(identifier: Action.switchProject.rawValue, title: "Switch"),
                UNNotificationAction(identifier: Action.keep.rawValue, title: "Not Now"),
            ], intentIdentifiers: []),
            UNNotificationCategory(identifier: Kind.dayReview.rawValue, actions: [
                UNNotificationAction(identifier: Action.review.rawValue, title: "Review", options: [.foreground]),
            ], intentIdentifiers: []),
            UNNotificationCategory(identifier: Kind.splitSaved.rawValue, actions: [
                UNNotificationAction(identifier: Action.undo.rawValue, title: "Undo"),
            ], intentIdentifiers: []),
        ]
        self.categories = Dictionary(uniqueKeysWithValues: categories.map { ($0.identifier, $0) })
        center.setNotificationCategories(Set(categories))
    }

    /// Asks once whether the app may notify; true if it may.
    func requestPermission() async -> Bool {
        (try? await center.requestAuthorization(options: [.alert, .sound])) ?? false
    }

    /// Whether the person turned notifications off for the app in System Settings.
    func isDenied() async -> Bool {
        await center.notificationSettings().authorizationStatus == .denied
    }

    /// Posts a notification; `actions` names its buttons this time, e.g. "Switch to Beacon" and "Still on Atlas".
    func post(_ kind: Kind, title: String, body: String, sound: Bool = true, actions: [(Action, String)]? = nil) {
        if let actions {
            categories[kind.rawValue] = UNNotificationCategory(
                identifier: kind.rawValue, actions: actions.map { UNNotificationAction(identifier: $0.0.rawValue, title: $0.1) },
                intentIdentifiers: [])
            center.setNotificationCategories(Set(categories.values))
        }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.sound = sound ? .default : nil
        content.categoryIdentifier = kind.rawValue
        // One of a kind at a time: a new reminder replaces the one before.
        center.add(UNNotificationRequest(identifier: kind.rawValue, content: content, trigger: nil))
    }

    func withdraw(_ kind: Kind) {
        center.removeDeliveredNotifications(withIdentifiers: [kind.rawValue])
    }

    // MARK: - UNUserNotificationCenterDelegate

    /// Shown also while the app is in front.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .sound]
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        let action = Action(rawValue: response.actionIdentifier)
        let kind = Kind(rawValue: response.notification.request.content.categoryIdentifier) ?? .reminder
        await MainActor.run {
            onAction?(action, kind)
        }
    }
}
