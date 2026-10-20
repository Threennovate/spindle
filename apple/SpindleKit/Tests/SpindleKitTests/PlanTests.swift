import Foundation
import Testing
@testable import SpindleKit

/// The same cases as the web app's `describePlan` tests, without the buttons for paying.
@Suite struct PlanTests {
    let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
    let now = ISO8601DateFormatter().date(from: "2026-10-11T10:00:00Z")!
    /// Midnight in Zagreb at the start of 21 October, so the trial's last day is 20 October.
    let trialEnd = ISO8601DateFormatter().date(from: "2026-10-20T22:00:00Z")!

    func subscription(_ status: SubscriptionStatus, access: OrganizationAccess = .full, trialEndsAt: Date? = nil, paidUntil: Date? = nil,
                      hasPlan: Bool = false, cancelsAtPeriodEnd: Bool = false) -> Subscription {
        Subscription(status: status, access: access, trialEndsAt: trialEndsAt, paidUntil: paidUntil, seats: 3, checkoutAvailable: true,
                     hasPlan: hasPlan, interval: .month, cancelsAtPeriodEnd: cancelsAtPeriodEnd, prices: [])
    }

    func describe(_ subscription: Subscription) -> PlanDescription {
        PlanDescription(subscription, organizationName: "Acme", time: time, now: now)
    }

    @Test func countsTheDaysLeftOfATrial() {
        let plan = describe(subscription(.trialing, trialEndsAt: trialEnd))
        #expect(plan.badge == "Free trial")
        #expect(plan.title == "10 days left")
        #expect(plan.text.contains("20 October 2026"))
    }

    @Test func saysWhenTheFirstChargeComesForAPlanChosenDuringTheTrial() {
        let plan = describe(subscription(.trialing, trialEndsAt: trialEnd, hasPlan: true))
        #expect(plan.badge == "Plan chosen")
        #expect(plan.title == "Starts after 20 October 2026")
    }

    @Test func saysAnEndedTrialIsReadOnly() {
        let plan = describe(subscription(.trialing, access: .readOnly, trialEndsAt: trialEnd))
        #expect(plan.tone == .bad)
        #expect(plan.title == "The trial has ended")
        #expect(plan.text.contains("Acme is read-only"))
    }

    @Test func describesPaidCanceledAndComped() {
        #expect(describe(subscription(.active, paidUntil: trialEnd)).title == "Paid until 20 October 2026")
        #expect(describe(subscription(.active, paidUntil: trialEnd, cancelsAtPeriodEnd: true)).title == "Works until 20 October 2026")
        #expect(describe(subscription(.canceled, access: .readOnly, paidUntil: trialEnd)).title == "The plan has ended")
        #expect(describe(subscription(.comped)).badge == "Free")
    }

    @Test func countsAPartOfADayAsOne() {
        #expect(PlanDescription.daysLeft(until: now.addingTimeInterval(3600), now: now) == 1)
        #expect(PlanDescription.daysLeft(until: now.addingTimeInterval(-3600), now: now) == 0)
        #expect(PlanDescription.describeDaysLeft(1) == "Last day")
    }
}
