import SwiftUI
import SpindleKit

/// What a report can be narrowed to: members, projects, clients, tags, billable time and descriptions, and how
/// its durations are rounded.
struct ReportFilters: View {
    let tracker: Tracker
    @Binding var settings: ReportSettings
    let members: [Member]
    let groups: [WorkspaceGroup]
    @State private var description = ""

    private var catalog: Catalog { tracker.catalog }

    var body: some View {
        HStack(alignment: .firstLineCenter, spacing: 8) {
            // Wraps onto a second line in a narrow window.
            FlowLayout(spacing: 2, lineSpacing: 4) {
                if tracker.isAdmin {
                    FilterMenu(label: "Member", sections: choices.memberSections, selected: (settings.groups ?? []) + (settings.users ?? [])) { ids in
                        let groupIds = Set(groups.map(\.id))
                        change {
                            $0.groups = nonEmpty(ids.filter { groupIds.contains($0) })
                            $0.users = nonEmpty(ids.filter { !groupIds.contains($0) })
                        }
                    }
                }
                FilterMenu(label: "Project", sections: choices.projectSections, selected: settings.projects ?? []) { ids in
                    change { $0.projects = nonEmpty(ids) }
                }
                FilterMenu(label: "Client", sections: choices.clientSections, selected: settings.clients ?? []) { ids in
                    change { $0.clients = nonEmpty(ids) }
                }
                FilterMenu(label: "Tag", sections: choices.tagSections, selected: settings.tags ?? []) { ids in
                    change { $0.tags = nonEmpty(ids) }
                }
                Menu {
                    Picker("Billable", selection: Binding(get: { settings.billable ?? "" }, set: { value in change { $0.billable = value.isEmpty ? nil : value } })) {
                        Text("Billable and non-billable").tag("")
                        Text("Billable").tag("yes")
                        Text("Non-billable").tag("no")
                    }
                    .pickerStyle(.inline)
                } label: {
                    FilterLabel(text: settings.billable == "no" ? "Non-billable" : "Billable", active: settings.billable != nil)
                }
                .menuStyle(.button)
                .buttonStyle(FieldButtonStyle())
                .menuIndicator(.hidden)
                .fixedSize()

                TextField("Description", text: $description)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 170)
                    .onSubmit { applyDescription() }
                    .onChange(of: description) { applyDescription() }
                    .accessibilityLabel("Description contains")

                if settings.hasFilters {
                    Button("Clear Filters") {
                        description = ""
                        settings = settings.withoutFilters
                    }
                    .buttonStyle(.brandLink)
                    .padding(.leading, 6)
                }
            }

            Spacer(minLength: 0)

            Menu {
                Picker("Rounding", selection: Binding(get: { settings.rounding ?? .none }, set: { value in change { $0.rounding = value == RoundingMode.none ? nil : value } })) {
                    ForEach([RoundingMode.none, .up, .nearest, .down], id: \.self) { mode in
                        Text(mode.label).tag(mode)
                    }
                }
                .pickerStyle(.inline)
                Picker("To", selection: Binding(get: { settings.roundTo ?? 15 }, set: { value in change { $0.roundTo = value == 15 ? nil : value } })) {
                    ForEach(ReportSettings.roundingMinutes, id: \.self) { minutes in
                        Text(minutes == 60 ? "1 hour" : "\(minutes) minutes").tag(minutes)
                    }
                }
                .pickerStyle(.inline)
                .disabled((settings.rounding ?? .none) == RoundingMode.none)
            } label: {
                FilterLabel(text: roundingLabel, systemImage: "arrow.up.and.down.text.horizontal", active: (settings.rounding ?? .none) != RoundingMode.none)
            }
            .menuStyle(.button)
            .buttonStyle(FieldButtonStyle())
            .menuIndicator(.hidden)
            .fixedSize()
            .help("Rounds each entry's duration in the report; the entries themselves stay as they are.")
        }
        .onAppear { description = settings.q ?? "" }
        .onChange(of: settings.q) {
            if (settings.q ?? "") != description.trimmingCharacters(in: .whitespaces) {
                description = settings.q ?? ""
            }
        }
    }

    private var roundingLabel: String {
        guard let rounding = settings.rounding, rounding != .none else { return "No rounding" }
        let minutes = settings.roundTo ?? 15
        return "\(rounding.label) \(minutes == 60 ? "hour" : "\(minutes) min")"
    }

    private func applyDescription() {
        let text = description.trimmingCharacters(in: .whitespaces)
        if text != (settings.q ?? "") {
            change { $0.q = text.isEmpty ? nil : String(text.prefix(200)) }
        }
    }

    private func change(_ edit: (inout ReportSettings) -> Void) {
        var next = settings
        edit(&next)
        next.page = nil
        settings = next
    }

    private func nonEmpty(_ ids: [String]) -> [String]? {
        ids.isEmpty ? nil : ids
    }

    private var choices: ReportFilterChoices {
        ReportFilterChoices(catalog: catalog, members: members, groups: groups)
    }
}
