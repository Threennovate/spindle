import SwiftUI
import SpindleKit

/// Where the organization's trial or plan stands, for its owners and admins on the hosted service. It only says so:
/// App Store apps may not sell outside the App Store or point to where that happens, so choosing or managing a plan
/// is left to the web, as on the Mac, without a link from here.
struct PlanView: View {
    @Environment(AppModel.self) private var model
    let organization: OrganizationSummary
    @State private var subscription: Subscription?
    @State private var error: String?

    var body: some View {
        List {
            if let subscription, let me = model.me {
                let plan = PlanDescription(subscription, organizationName: organization.name, time: UserTime(me))
                Section {
                    VStack(alignment: .leading, spacing: 8) {
                        Text(plan.badge)
                            .font(.caption.weight(.semibold))
                            .padding(.horizontal, 8)
                            .padding(.vertical, 3)
                            .foregroundStyle(color(plan.tone))
                            .background(color(plan.tone).opacity(0.12), in: Capsule())
                        Text(plan.title)
                            .font(.title3.weight(.semibold))
                        Text(plan.text)
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                    }
                    .padding(.vertical, 4)
                    .accessibilityElement(children: .combine)
                }
                Section {
                    LabeledContent("Seats", value: subscription.seats == 1 ? "1 person" : "\(subscription.seats) people")
                } footer: {
                    Text("A seat is a person in at least one of \(organization.name)'s workspaces.")
                }
            } else if let error {
                Section {
                    Text(error).foregroundStyle(.red)
                }
            } else {
                ProgressView()
                    .frame(maxWidth: .infinity)
                    .listRowBackground(Color.clear)
            }
        }
        .navigationTitle("Plan")
        .navigationBarTitleDisplayMode(.inline)
        .task { await load() }
        .refreshable { await load() }
    }

    private func color(_ tone: PlanDescription.Tone) -> Color {
        switch tone {
        case .good: .green
        case .neutral: .secondary
        case .bad: .red
        }
    }

    private func load() async {
        do {
            subscription = try await model.client.subscription(of: organization.id)
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
    }
}

/// The plan's state in a word or two, for its row in Settings, from what `/me` already says.
extension OrganizationSummary {
    var planSummary: String? {
        if access == .readOnly {
            return "Ended"
        }
        return trialEndsAt.map { PlanDescription.describeDaysLeft(PlanDescription.daysLeft(until: $0)) }
    }
}
