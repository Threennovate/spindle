import Foundation
import SpindleAPI

public typealias Subscription = Components.Schemas.SubscriptionResponse
public typealias SubscriptionStatus = Components.Schemas.SubscriptionStatus
public typealias OrganizationAccess = Components.Schemas.OrganizationAccess

extension SpindleClient {
    /// Where the organization's trial or plan stands. Only its owners and admins may ask, and only on the hosted
    /// service: anyone else gets a 403 or 404.
    public func subscription(of organizationId: String) async throws -> Subscription {
        try await call { try await api.getSubscription(path: .init(organizationId: organizationId)).ok.body.json }
    }
}

/// What the apps say about an organization's trial or plan, in the same words as the web's plan page. It names no
/// way to pay: the iPhone app doesn't point to buying outside the App Store, and the Mac opens the web for that.
public struct PlanDescription: Equatable, Sendable {
    public enum Tone: Sendable { case good, neutral, bad }

    public var badge: String
    public var tone: Tone
    public var title: String
    public var text: String

    /// Describes a subscription. The end of a trial or paid period is shown as its last day, in the person's time.
    public init(_ subscription: Subscription, organizationName: String, time: UserTime, now: Date = Date()) {
        let lastDay = { (end: Date) in time.longDay(time.day(of: end.addingTimeInterval(-1))) }
        let readOnly = "\(organizationName) is read-only: everything is still here and can be read, reported and exported, but no time can be added or changed."

        switch subscription.status {
        case .trialing:
            if subscription.hasPlan {
                let firstCharge = subscription.nextChargeAt ?? subscription.trialEndsAt
                self.init(
                    badge: "Plan chosen", tone: .good,
                    title: firstCharge.map { "Starts after \(lastDay($0))" } ?? "Plan chosen",
                    text: "The trial goes on until then, with nothing charged. The first charge comes when it ends, for the seats there are then.")
            } else if let trialEndsAt = subscription.trialEndsAt, subscription.access != .readOnly {
                self.init(
                    badge: "Free trial", tone: .neutral,
                    title: Self.describeDaysLeft(Self.daysLeft(until: trialEndsAt, now: now)),
                    text: "The trial lasts until the end of \(lastDay(trialEndsAt)), with everything working.")
            } else {
                self.init(
                    badge: "Trial ended", tone: .bad, title: "The trial has ended",
                    text: subscription.trialEndsAt.map { "Its last day was \(lastDay($0)). \(readOnly)" } ?? readOnly)
            }
        case .active:
            if subscription.cancelsAtPeriodEnd, let paidUntil = subscription.paidUntil {
                self.init(badge: "Canceled", tone: .neutral, title: "Works until \(lastDay(paidUntil))", text: "The plan won’t renew.")
            } else {
                self.init(
                    badge: "Paid", tone: .good,
                    title: subscription.paidUntil.map { "Paid until \(lastDay($0))" } ?? "Paid",
                    text: "The plan renews by itself, and its seats follow the people in the organization.")
            }
        case .pastDue:
            self.init(
                badge: "Payment failed", tone: .bad, title: "A payment didn’t go through",
                text: "It is being tried again, and everything keeps working meanwhile.")
        case .canceled:
            if let paidUntil = subscription.paidUntil, subscription.access != .readOnly {
                self.init(badge: "Canceled", tone: .neutral, title: "Works until \(lastDay(paidUntil))", text: "The plan won’t renew.")
            } else {
                self.init(badge: "Ended", tone: .bad, title: "The plan has ended", text: readOnly)
            }
        case .comped:
            self.init(badge: "Free", tone: .good, title: "Free, on us", text: "\(organizationName) gets everything in Spindle at no charge.")
        }
    }

    public init(badge: String, tone: Tone, title: String, text: String) {
        self.badge = badge
        self.tone = tone
        self.title = title
        self.text = text
    }

    /// Whole days until an end, counting a part of a day as one; 0 once it has passed.
    public static func daysLeft(until end: Date, now: Date = Date()) -> Int {
        max(0, Int((end.timeIntervalSince(now) / 86_400).rounded(.up)))
    }

    /// "12 days left", or "Last day" when less than a day is left.
    public static func describeDaysLeft(_ days: Int) -> String {
        days <= 1 ? "Last day" : "\(days) days left"
    }
}
