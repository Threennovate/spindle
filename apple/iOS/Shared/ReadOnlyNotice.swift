import SwiftUI
import SpindleKit

/// Says why nothing can be changed, for as long as the current workspace's organization is read-only. Unlike the Mac
/// and the web, it doesn't point to choosing a plan: App Store apps may not lead to buying outside the App Store.
struct ReadOnlyNotice: View {
    let organization: OrganizationSummary

    var body: some View {
        let reason = organization.role == .member
            ? "Its trial or plan has ended, so time can't be added or changed. Everything is still here. Its owner can bring it back."
            : "Its trial or plan has ended. Everything is still here, and you can read, report and export it, but not add or change time."
        Label {
            Text("\(Text("\(organization.name) is read-only.").fontWeight(.semibold)) \(reason)")
        } icon: {
            Image(systemName: "lock.fill")
                .foregroundStyle(.red)
        }
        .font(.subheadline)
        .accessibilityElement(children: .combine)
    }
}
