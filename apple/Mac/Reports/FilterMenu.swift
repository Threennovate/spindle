import SwiftUI
import SpindleKit

/// A button for one filter of a report, e.g. "Project", that opens a searchable list to tick any number of.
struct FilterMenu: View {
    let label: String
    let sections: [FilterSection]
    let selected: [String]
    let onChange: ([String]) -> Void
    @State private var open = false

    var body: some View {
        Button {
            open = true
        } label: {
            FilterLabel(text: label, count: selected.count, active: !selected.isEmpty)
        }
        .buttonStyle(FieldButtonStyle())
        .accessibilityLabel(selected.isEmpty ? label : "\(label), \(selected.count) chosen")
        .popover(isPresented: $open, arrowEdge: .bottom) {
            FilterList(label: label, sections: sections, selected: selected, onChange: onChange)
        }
    }
}

/// How a report's filter reads: its name, how many are chosen, and a chevron, in the brand's colour while it filters.
/// Menus of one choice use it too, so the whole row looks alike.
struct FilterLabel: View {
    let text: String
    var systemImage: String?
    var count = 0
    var active = false

    var body: some View {
        HStack(spacing: 4) {
            if let systemImage {
                Image(systemName: systemImage)
            }
            Text(text)
            if count > 0 {
                Text("\(count)")
                    .font(.caption.weight(.semibold))
                    .padding(.horizontal, 5)
                    .foregroundStyle(Color.ink)
                    .background(Color.brand, in: Capsule())
            }
            Image(systemName: "chevron.down")
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
        .foregroundStyle(active ? Color.brandText : Color.primary)
    }
}

private struct FilterList: View {
    let label: String
    let sections: [FilterSection]
    let selected: [String]
    let onChange: ([String]) -> Void
    @State private var search = ""
    @State private var searching = true
    @State private var highlighted: String?

    private var shown: [FilterSection] {
        let term = search.trimmingCharacters(in: .whitespaces)
        return sections.compactMap { section in
            let options = section.options.filter { option in
                term.isEmpty || ([option.label, option.detail ?? ""] + option.keywords).contains { $0.localizedCaseInsensitiveContains(term) }
            }
            return options.isEmpty ? nil : FilterSection(heading: section.heading, options: options)
        }
    }

    var body: some View {
        let shown = shown
        let choices = shown.flatMap(\.options).map(\.id)
        VStack(spacing: 0) {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                CommandTextField(text: $search, placeholder: "Search", isFocused: $searching) { command in
                    let index = choices.firstIndex { $0 == highlighted }
                    switch command {
                    case .down where !choices.isEmpty: highlighted = choices[min((index ?? -1) + 1, choices.count - 1)]
                    case .up where !choices.isEmpty: highlighted = choices[max((index ?? choices.count) - 1, 0)]
                    case .submit: if let highlighted { toggle(highlighted) }
                    default: break
                    }
                    return true
                }
                if !selected.isEmpty {
                    Button("Clear") { onChange([]) }
                        .buttonStyle(.brandLink)
                        .font(.callout)
                }
            }
            .padding(10)
            Divider()
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 1) {
                    ForEach(shown) { section in
                        if let heading = section.heading {
                            Text(heading)
                                .font(.caption.weight(.semibold))
                                .foregroundStyle(.secondary)
                                .padding(.horizontal, 8)
                                .padding(.top, 8)
                        }
                        ForEach(section.options) { option in
                            row(option)
                        }
                    }
                    if shown.isEmpty {
                        Text("Nothing matches.").foregroundStyle(.secondary).padding(12)
                    }
                }
                .padding(5)
            }
        }
        .frame(width: 300, height: 340)
        .accessibilityLabel(label)
    }

    private func row(_ option: FilterOption) -> some View {
        Button {
            toggle(option.id)
        } label: {
            HStack(spacing: 8) {
                Image(systemName: selected.contains(option.id) ? "checkmark.square.fill" : "square")
                    .foregroundStyle(selected.contains(option.id) ? Color.brandText : .secondary)
                if let color = option.color {
                    ProjectDot(color: color)
                }
                Text(option.label).lineLimit(1)
                Spacer(minLength: 6)
                if let detail = option.detail {
                    Text(detail).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .contentShape(Rectangle())
            .background(highlighted == option.id ? Color.highlight : .clear, in: RoundedRectangle(cornerRadius: 5))
        }
        .buttonStyle(.plain)
        .onHover { if $0 { highlighted = option.id } }
        .accessibilityAddTraits(selected.contains(option.id) ? .isSelected : [])
    }

    private func toggle(_ id: String) {
        onChange(selected.contains(id) ? selected.filter { $0 != id } : selected + [id])
    }
}
