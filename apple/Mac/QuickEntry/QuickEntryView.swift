import SwiftUI
import SpindleKit

/// The quick entry panel's content: one field, the running timer, rows for what can be done, and a footer that says
/// what Enter, ⌘↩ and Tab do for the selected row, so nothing happens by surprise.
struct QuickEntryView: View {
    @Bindable var quick: QuickEntry
    /// The content's height, for the panel to fit it while keeping its top in place.
    var onHeight: (CGFloat) -> Void = { _ in }
    @State private var focused = true
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let sections = quick.sections
        let selected = quick.selected
        VStack(spacing: 0) {
            header
            Divider()
            if let tracker = quick.tracker {
                if quick.page == nil, quick.query.isEmpty, quick.chips.isEmpty, let running = tracker.running {
                    RunningCard(entry: running, catalog: tracker.catalog)
                        .padding(.horizontal, 12)
                        .padding(.top, 10)
                }
                if case let .split(draft) = quick.page {
                    QuickSplitBar(draft: draft, quick: quick, catalog: tracker.catalog)
                        .padding(.horizontal, 18)
                        .padding(.top, 12)
                }
                list(sections, selected: selected)
            } else {
                Text(quick.model.phase == .signedIn ? "Loading…" : "Sign in to Spindle to track time from here.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, minHeight: 80)
            }
            Divider()
            footer(selected)
        }
        .frame(width: 640)
        .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { onHeight($0) }
        .animation(reduceMotion ? nil : .snappy(duration: 0.15), value: sections.map(\.id))
    }

    // MARK: - The field

    private var header: some View {
        HStack(spacing: 8) {
            if quick.page != nil {
                Button { quick.back() } label: {
                    Image(systemName: "chevron.left")
                        .frame(width: 22, height: 22)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .accessibilityLabel("Back")
                Text(quick.page!.title)
                    .font(.callout.weight(.medium))
                    .lineLimit(1)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(.quaternary, in: RoundedRectangle(cornerRadius: 6))
                    .frame(maxWidth: 260, alignment: .leading)
                    .fixedSize(horizontal: true, vertical: false)
            } else {
                Image(systemName: "magnifyingglass")
                    .font(.system(size: 15))
                    .foregroundStyle(.secondary)
                    .accessibilityHidden(true)
            }
            if quick.page == nil, !quick.chips.isEmpty, let tracker = quick.tracker {
                ChipStrip(chips: quick.chips, catalog: tracker.catalog) { quick.removeChip(at: $0) }
            }
            CommandTextField(
                text: $quick.query,
                placeholder: quick.placeholder,
                font: .systemFont(ofSize: 17),
                isFocused: $focused,
                onCommand: { command in
                    switch command {
                    case .up: quick.move(-1)
                    case .down: quick.move(1)
                    case .submit: quick.submit()
                    case .cancel: quick.cancel()
                    }
                    return true
                },
                onKey: { key in
                    switch key {
                    case .tab:
                        quick.tab()
                        return true
                    case .backtab:
                        return true
                    case let .left(shift), let .right(shift):
                        guard case let .split(draft) = quick.page else { return false }
                        let grows = if case .right = key { true } else { false }
                        draft.grow(quick.splitIndex, by: (grows ? 1 : -1) * (shift ? 1 : SplitDraft.step), typed: quick.query)
                        quick.query = ""
                        return true
                    case .deleteAtStart:
                        return quick.deleteAtStart()
                    case .alternateSubmit:
                        quick.submitAlternate()
                        return true
                    }
                },
                onCaret: { quick.caretMoved(to: $0) },
                caretRequest: $quick.caretRequest)
                .frame(height: 24)
                .accessibilityLabel("Quick entry")
                .accessibilityHint("Type @ for a project, # for a tag, $ for billable, and a time at the end, such as since 9:40 or 9:00-10:30.")
            Keycap("esc")
        }
        .padding(.horizontal, 16)
        .frame(height: 54)
    }

    // MARK: - Rows

    private func list(_ sections: [QuickSection], selected: QuickRow?) -> some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(sections) { section in
                        if let title = section.title {
                            Text(title.uppercased())
                                .font(.caption2.weight(.semibold))
                                .tracking(0.4)
                                .foregroundStyle(.secondary)
                                .padding(.horizontal, 10)
                                .padding(.top, 8)
                                .padding(.bottom, 2)
                        }
                        ForEach(section.rows) { row in
                            QuickRowView(row: row, selected: row.id == selected?.id, catalog: quick.tracker?.catalog)
                                .id(row.id)
                                .onTapGesture { quick.select(row.id); quick.run(row, row.primary) }
                                .onHover { if $0 { quick.select(row.id) } }
                        }
                    }
                    if sections.isEmpty {
                        Text(emptyText)
                            .font(.callout)
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 24)
                    }
                }
                .padding(8)
            }
            .scrollBounceBehavior(.basedOnSize)
            .frame(maxHeight: 420)
            .fixedSize(horizontal: false, vertical: true)
            .onChange(of: selected?.id) { _, id in
                if let id { proxy.scrollTo(id) }
            }
        }
    }

    private var emptyText: String {
        switch quick.page {
        case .startTime: "Type a time such as 9:40 or 940, or how long ago, such as -15 or -1:30."
        case .tags: "No tags yet. Type a name to create one."
        default: "Nothing fits."
        }
    }

    // MARK: - The footer

    private func footer(_ row: QuickRow?) -> some View {
        HStack(spacing: 14) {
            if let error = quick.error {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .lineLimit(1)
            } else if quick.page != nil {
                Hint("Back", keys: ["⌫"])
            } else {
                HStack(spacing: 6) {
                    LogoMark(size: 14)
                    Text(quick.model.workspace?.name ?? "Spindle")
                        .lineLimit(1)
                }
            }
            Spacer(minLength: 8)
            if let row {
                Hint(row.primary.label, keys: ["↩"], strong: true)
                if let secondary = row.secondary {
                    Hint(secondary.label, keys: ["⌘", "↩"])
                }
                if quick.mention != nil, row.id.hasPrefix("mention:") {
                    Hint("Pick", keys: ["⇥"])
                } else if row.secondary != nil || !row.more.isEmpty {
                    Hint("Actions", keys: ["⇥"])
                }
            }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .padding(.horizontal, 14)
        .frame(height: 34)
        .background(.quaternary.opacity(0.4))
    }
}

/// One row: an icon or the project's colour, the title with its detail, and a fact on the right.
private struct QuickRowView: View {
    let row: QuickRow
    let selected: Bool
    let catalog: Catalog?

    var body: some View {
        HStack(spacing: 10) {
            leading
                .frame(width: 18)
            if row.stacked {
                VStack(alignment: .leading, spacing: 2) {
                    Text(row.title).fontWeight(.medium).lineLimit(1)
                    if let detail = row.detail {
                        Text(detail)
                            .font(.callout)
                            .foregroundStyle(tint)
                            .lineLimit(2)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            } else {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(row.title).lineLimit(1)
                    if let detail = row.detail {
                        Text(detail)
                            .font(.callout)
                            .foregroundStyle(tint)
                            .lineLimit(1)
                    }
                }
            }
            Spacer(minLength: 8)
            if let meta = row.meta {
                Text(meta)
                    .font(.callout.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            if row.checked {
                Image(systemName: "checkmark")
                    .foregroundStyle(Color.brandText)
                    .accessibilityLabel("On")
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, row.stacked ? 8 : 0)
        .frame(minHeight: row.stacked ? 52 : 36)
        .background(selected ? Color.highlight : .clear, in: RoundedRectangle(cornerRadius: 8))
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(selected ? [.isSelected, .isButton] : .isButton)
        .accessibilityHint(row.primary.label)
    }

    private var tint: Color {
        switch row.tone {
        case .normal: .secondary
        case .warning: .orange
        case .blocked: .red
        }
    }

    @ViewBuilder private var leading: some View {
        if let parts = row.parts, let catalog {
            SplitDots(catalog: catalog, parts: parts)
        } else if let color = row.color {
            ProjectDot(color: color)
        } else if let symbol = row.symbol {
            Image(systemName: symbol)
                .font(.system(size: 13))
                .foregroundStyle(row.tone == .normal ? Color.secondary : row.tone == .warning ? .orange : .red)
        }
    }
}

/// The running timer at the top, ticking.
private struct RunningCard: View {
    let entry: TimeEntry
    let catalog: Catalog

    var body: some View {
        HStack(spacing: 12) {
            Circle()
                .fill(Color.brand)
                .frame(width: 9, height: 9)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(entry.description.isEmpty ? "No description" : entry.description)
                    .fontWeight(.medium)
                    .foregroundStyle(entry.description.isEmpty ? .secondary : .primary)
                    .lineLimit(1)
                if entry.isSplit {
                    SplitLabel(catalog: catalog, parts: entry.parts).font(.callout)
                } else if let project = catalog.project(entry.projectId) {
                    ProjectLabel(project: project, task: catalog.task(entry.taskId)).font(.callout)
                } else {
                    Text("No project").font(.callout).foregroundStyle(.secondary)
                }
            }
            Spacer()
            TimelineView(.periodic(from: .now, by: 1)) { context in
                Text(Durations.clock(entry.seconds(at: context.date)))
                    .font(.system(size: 20, weight: .semibold).monospacedDigit())
            }
            .accessibilityLabel("Running for")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .cardSurface(cornerRadius: 12)
    }
}

/// The chips picked for the line, in the field, before the text.
private struct ChipStrip: View {
    let chips: [EntrySentence.Chip]
    let catalog: Catalog
    let onRemove: (Int) -> Void

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 4) {
                ForEach(Array(chips.enumerated()), id: \.offset) { index, chip in
                    HStack(spacing: 5) {
                        label(chip)
                        Button { onRemove(index) } label: {
                            Image(systemName: "xmark").font(.system(size: 8, weight: .bold))
                        }
                        .buttonStyle(.plain)
                        .foregroundStyle(.secondary)
                        .accessibilityLabel("Remove \(name(chip))")
                    }
                    .font(.callout.weight(.medium))
                    .padding(.leading, 8)
                    .padding(.trailing, 6)
                    .frame(height: 26)
                    .background(.quaternary, in: RoundedRectangle(cornerRadius: 6))
                }
            }
        }
        .frame(maxWidth: 330)
        .fixedSize(horizontal: true, vertical: false)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Picked for the entry")
    }

    @ViewBuilder private func label(_ chip: EntrySentence.Chip) -> some View {
        switch chip {
        case let .project(projectID, taskID):
            ProjectDot(color: catalog.project(projectID)?.color ?? "#8E8E93", size: 8)
            Text(catalog.task(taskID)?.name ?? catalog.project(projectID)?.name ?? "").lineLimit(1)
        case let .client(id):
            Image(systemName: "building.2").foregroundStyle(.secondary)
            Text(catalog.client(id)?.name ?? "").lineLimit(1)
        case let .tag(id):
            Image(systemName: "number").foregroundStyle(.secondary)
            Text(catalog.tag(id)?.name ?? "").lineLimit(1)
        case .billable:
            Image(systemName: "dollarsign.circle").foregroundStyle(.secondary)
            Text("Billable")
        }
    }

    private func name(_ chip: EntrySentence.Chip) -> String {
        switch chip {
        case let .project(projectID, taskID): catalog.task(taskID)?.name ?? catalog.project(projectID)?.name ?? ""
        case let .client(id): catalog.client(id)?.name ?? ""
        case let .tag(id): "tag \(catalog.tag(id)?.name ?? "")"
        case .billable: "billable"
        }
    }
}

/// The split as a bar in the projects' colours, with the keys that change it.
private struct QuickSplitBar: View {
    let draft: SplitDraft
    let quick: QuickEntry
    let catalog: Catalog
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let index = quick.splitIndex
        let parts = draft.shown(typed: quick.query, at: index)
        VStack(alignment: .leading, spacing: 8) {
            GeometryReader { geometry in
                HStack(spacing: 2) {
                    ForEach(parts.indices, id: \.self) { i in
                        catalog.color(of: parts[i])
                            .opacity(i == index ? 1 : 0.55)
                            .frame(width: max(0, geometry.size.width * CGFloat(parts[i].percent) / 100 - 2))
                    }
                }
            }
            .frame(height: 12)
            .clipShape(Capsule())
            .animation(reduceMotion ? nil : .snappy(duration: 0.15), value: parts.map(\.percent))
            .accessibilityElement()
            .accessibilityLabel("Split: \(catalog.describe(parts))")
            HStack(spacing: 12) {
                HStack(spacing: 4) { Keycap("←"); Keycap("→"); Text("resize, with ⇧ by 1") }
                Text("Type a share, e.g. 60")
                if parts.count < SplitDraft.maxParts {
                    HStack(spacing: 4) { Keycap("+"); Text("another project") }
                }
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
    }
}

private struct Hint: View {
    let label: String
    let keys: [String]
    var strong = false

    init(_ label: String, keys: [String], strong: Bool = false) {
        self.label = label
        self.keys = keys
        self.strong = strong
    }

    var body: some View {
        HStack(spacing: 5) {
            Text(label)
                .fontWeight(strong ? .medium : .regular)
                .foregroundStyle(strong ? .primary : .secondary)
                .lineLimit(1)
            HStack(spacing: 2) { ForEach(keys, id: \.self) { Keycap($0) } }
        }
    }
}

/// A key as printed on the keyboard.
private struct Keycap: View {
    let key: String
    init(_ key: String) { self.key = key }

    var body: some View {
        Text(key)
            .font(.system(size: 10, weight: .medium))
            .foregroundStyle(.secondary)
            .frame(minWidth: 18, minHeight: 18)
            .padding(.horizontal, key.count > 1 ? 3 : 0)
            .background(RoundedRectangle(cornerRadius: 4).fill(.background.opacity(0.7)))
            .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(.separator))
            .accessibilityHidden(true)
    }
}
