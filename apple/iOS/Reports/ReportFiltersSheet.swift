import SwiftUI
import SpindleKit

/// What a report is narrowed to, and how its durations are rounded. Changes apply as they are made.
struct ReportFiltersSheet: View {
    @Environment(\.dismiss) private var dismiss
    let tracker: Tracker
    let choices: ReportFilterChoices
    @Binding var settings: ReportSettings
    @State private var description = ""

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    if tracker.isAdmin {
                        link("Members", sections: choices.memberSections, selected: (settings.groups ?? []) + (settings.users ?? [])) { ids in
                            let groupIds = Set(choices.groups.map(\.id))
                            change {
                                $0.groups = nonEmpty(ids.filter { groupIds.contains($0) })
                                $0.users = nonEmpty(ids.filter { !groupIds.contains($0) })
                            }
                        }
                    }
                    link("Projects", sections: choices.projectSections, selected: settings.projects ?? []) { ids in
                        change { $0.projects = nonEmpty(ids) }
                    }
                    link("Clients", sections: choices.clientSections, selected: settings.clients ?? []) { ids in
                        change { $0.clients = nonEmpty(ids) }
                    }
                    link("Tags", sections: choices.tagSections, selected: settings.tags ?? []) { ids in
                        change { $0.tags = nonEmpty(ids) }
                    }
                    Picker("Billable", selection: Binding(get: { settings.billable ?? "" }, set: { value in change { $0.billable = value.isEmpty ? nil : value } })) {
                        Text("All time").tag("")
                        Text("Billable").tag("yes")
                        Text("Non-billable").tag("no")
                    }
                }
                Section("Description contains") {
                    TextField("Any description", text: $description)
                        .autocorrectionDisabled()
                        .submitLabel(.done)
                        .onChange(of: description) { applyDescription() }
                }
                Section {
                    Picker("Rounding", selection: Binding(get: { settings.rounding ?? .none }, set: { value in change { $0.rounding = value == RoundingMode.none ? nil : value } })) {
                        ForEach([RoundingMode.none, .up, .nearest, .down], id: \.self) { Text($0.label).tag($0) }
                    }
                    Picker("To", selection: Binding(get: { settings.roundTo ?? 15 }, set: { value in change { $0.roundTo = value == 15 ? nil : value } })) {
                        ForEach(ReportSettings.roundingMinutes, id: \.self) { minutes in
                            Text(minutes == 60 ? "1 hour" : "\(minutes) minutes").tag(minutes)
                        }
                    }
                    .disabled((settings.rounding ?? .none) == RoundingMode.none)
                } footer: {
                    Text("Rounds each entry's duration in the report; the entries themselves stay as they are.")
                }
                if settings.hasFilters {
                    Section {
                        Button("Clear Filters", role: .destructive) {
                            description = ""
                            settings = settings.withoutFilters
                        }
                    }
                }
            }
            .navigationTitle("Filters")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
            .onAppear { description = settings.q ?? "" }
        }
    }

    private func link(_ label: String, sections: [FilterSection], selected: [String], onChange: @escaping ([String]) -> Void) -> some View {
        NavigationLink {
            FilterChoiceList(title: label, sections: sections, selected: selected, onChange: onChange)
        } label: {
            LabeledContent(label, value: selected.isEmpty ? "All" : "\(selected.count) chosen")
        }
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
}

/// Ticks any number of a filter's options, e.g. projects; searching narrows the list.
private struct FilterChoiceList: View {
    let title: String
    let sections: [FilterSection]
    let onChange: ([String]) -> Void
    @State private var selected: [String]
    @State private var search = ""

    init(title: String, sections: [FilterSection], selected: [String], onChange: @escaping ([String]) -> Void) {
        self.title = title
        self.sections = sections
        self.onChange = onChange
        _selected = State(initialValue: selected)
    }

    var body: some View {
        let term = search.trimmingCharacters(in: .whitespaces)
        List {
            ForEach(sections) { section in
                let options = section.options.filter { option in
                    term.isEmpty || ([option.label] + option.keywords).contains { $0.localizedCaseInsensitiveContains(term) }
                }
                if !options.isEmpty {
                    Section {
                        ForEach(options) { option in
                            Button {
                                toggle(option.id)
                            } label: {
                                HStack(spacing: 10) {
                                    if let color = option.color {
                                        ProjectDot(color: color, size: 10)
                                    }
                                    Text(option.label).lineLimit(1)
                                    if let detail = option.detail {
                                        Text(detail).foregroundStyle(.secondary).lineLimit(1)
                                    }
                                    Spacer(minLength: 8)
                                    if selected.contains(option.id) {
                                        Image(systemName: "checkmark")
                                            .fontWeight(.semibold)
                                            .foregroundStyle(Color.brandText)
                                    }
                                }
                                .contentShape(Rectangle())
                            }
                            .tint(.primary)
                        }
                    } header: {
                        if let heading = section.heading { Text(heading) }
                    }
                }
            }
        }
        .searchable(text: $search, placement: .navigationBarDrawer(displayMode: .always))
        .autocorrectionDisabled()
        .navigationTitle(title)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            if !selected.isEmpty {
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Clear") {
                        selected = []
                        onChange([])
                    }
                }
            }
        }
    }

    private func toggle(_ id: String) {
        if let index = selected.firstIndex(of: id) {
            selected.remove(at: index)
        } else {
            selected.append(id)
        }
        onChange(selected)
    }
}
