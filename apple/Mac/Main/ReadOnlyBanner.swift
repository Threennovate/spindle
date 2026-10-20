import SwiftUI
import SpindleKit

/// Says why nothing can be changed, above every section, for as long as the organization is read-only.
struct ReadOnlyBanner: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if let organization = model.readOnlyOrganization {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Image(systemName: "lock.fill")
                    .foregroundStyle(.red)
                let reason = organization.role == .member
                    ? "Its trial or plan has ended, so time can't be added or changed. Everything is still here. Its owner can bring it back."
                    : "Its trial or plan has ended. Everything is still here, and you can read, report and export it, but not add or change time."
                Text("\(Text("\(organization.name) is read-only.").fontWeight(.semibold)) \(reason)")
                    .frame(maxWidth: .infinity, alignment: .leading)
                if organization.role != .member {
                    // We don't sell inside the app: the plan is chosen on the web.
                    Link("Choose a plan", destination: model.webURL("/plan"))
                }
            }
            .font(.callout)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 20)
            .padding(.vertical, 10)
            .background(Color.red.opacity(0.08))
            .overlay(alignment: .bottom) { Divider() }
            .accessibilityElement(children: .combine)
        }
    }
}
