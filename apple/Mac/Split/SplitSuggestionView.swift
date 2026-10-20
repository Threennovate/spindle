import SwiftUI
import SpindleKit

/// "Agents were in spindle and acme-site": the split agent activity suggests, to apply with one click. When
/// the work moved from one project to the next, it offers separate entries cut where it moved. A repository
/// without a project is linked to one right here, and stays linked.
struct SplitSuggestionBanner: View {
    @Environment(Desktop.self) private var desktop
    let tracker: Tracker
    let suggestion: SplitSuggestion
    let onApply: ([SplitPart]) -> Void
    /// Takes the suggestion as separate entries; nil where only a split by percent can be taken.
    var onSeparate: (([TimeStretch]) -> Void)?
    var onDismiss: (() -> Void)?

    @State private var linking: ActivityRepository?
    @State private var branchRules: BranchRulesTarget?

    private var catalog: Catalog { tracker.catalog }
    private var parts: [SplitPart]? {
        SplitApplier.parts(suggestion, catalog: catalog)
    }
    private var stretches: [TimeStretch]? {
        guard onSeparate != nil else { return nil }
        return SplitApplier.stretches(suggestion, catalog: catalog)
    }
    private var separable: Bool { onSeparate != nil && suggestion.stretches != nil }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Image(systemName: ActivityStyle.symbol)
                    .foregroundStyle(Color.brandText)
                Text(title)
                    .font(.callout.weight(.semibold))
                Spacer()
                if let onDismiss {
                    Button("Keep as Is", action: onDismiss)
                        .buttonStyle(.borderless)
                        .foregroundStyle(.secondary)
                        .font(.callout)
                        .help("Leave the entry as it is and stop suggesting a split for it")
                }
                if separable {
                    Button("Split \(suggestion.parts.map { "\($0.percent)" }.joined(separator: " · "))") {
                        if let parts { onApply(parts) }
                    }
                    .controlSize(.small)
                    .disabled(parts == nil)
                    .help(parts == nil ? "Choose a project for each repository first." : "Keep one entry and share its time by percent; nothing is saved until you save.")
                    Button(SplitSuggestionChip.separateTitle(suggestion, time: tracker.time)) {
                        if let stretches { onSeparate?(stretches) }
                    }
                    .buttonStyle(.brand)
                    .controlSize(.small)
                    .disabled(stretches == nil)
                    .help(stretches == nil ? "Choose a project for each repository first." : "Make separate entries, cut where the work moved; nothing is saved until you save.")
                } else {
                    Button("Split") {
                        if let parts { onApply(parts) }
                    }
                    .buttonStyle(.brand)
                    .controlSize(.small)
                    .disabled(parts == nil)
                    .help(parts == nil ? "Choose a project for each repository first." : "Split the time as suggested; nothing is saved until you save.")
                }
            }

            EvidenceStrip(suggestion: suggestion, catalog: catalog, time: tracker.time)
            FlowRow {
                ForEach(suggestion.parts.indices, id: \.self) { index in
                    partLabel(suggestion.parts[index])
                }
            }
            if let note {
                Text(note)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(10)
        .cardSurface(cornerRadius: 8)
        .sheet(item: $branchRules) { target in
            BranchRulesEditor(repository: target.repository, tracker: tracker, splits: desktop.splits)
        }
    }

    /// Where the work moved; the strip shows how much of the time the logs account for.
    private var note: String? {
        guard separable, let stretches = suggestion.stretches else { return nil }
        let moves = stretches.dropFirst().map { tracker.time.clock($0.range.start) }
        return moves.count == 1
            ? "The work moved to \(EvidenceStrip.name(stretches[1].target, catalog: catalog)) at \(moves[0])."
            : "The work moved at \(moves.dropLast().joined(separator: ", ")) and \(moves.last!)."
    }

    private var title: String {
        suggestion.unlinked.isEmpty ? "Agents worked on \(suggestion.parts.count) projects in this time" : "Agents worked in \(suggestion.parts.count) places in this time"
    }

    private func help(_ part: SplitSuggestion.Part) -> String {
        let prompts = suggestion.touches[part.target, default: 0]
        let sent = "\(prompts) \(prompts == 1 ? "prompt" : "prompts")"
        return part.unattributed > 0
            ? "\(Durations.hoursMinutes(part.attention)) in agent sessions there (\(sent)), and \(Durations.hoursMinutes(part.unattributed)) the logs don't account for (hatched)"
            : "Time in agent sessions there, \(sent)"
    }

    private func color(_ part: SplitSuggestion.Part) -> Color {
        SplitSuggestionChip.color(part, catalog: catalog)
    }

    @ViewBuilder private func partLabel(_ part: SplitSuggestion.Part) -> some View {
        HStack(spacing: 5) {
            Circle().fill(color(part)).frame(width: 8, height: 8)
            switch part.target {
            case .project(let id): Text(catalog.project(id)?.name ?? "Project").fontWeight(.medium)
            case .repository(let repository): Text(repository.name).fontWeight(.medium)
            }
            Text("\(part.percent)%")
                .foregroundStyle(.secondary)
                .monospacedDigit()
            Text("· \(Durations.hoursMinutes(part.duration))")
                .foregroundStyle(.tertiary)
                .help(help(part))
            if case .repository(let repository) = part.target {
                Button {
                    linking = repository
                } label: {
                    Label("Choose Project", systemImage: "link")
                        .font(.caption)
                }
                .buttonStyle(.brandLink)
                .help("Which project is \(repository.name) for? It is remembered for your account, on the web too.")
                .popover(isPresented: Binding(get: { linking == repository }, set: { if !$0 { linking = nil } }), arrowEdge: .bottom) {
                    ProjectPicker(
                        tracker: tracker, projectId: nil, taskId: nil,
                        onChange: { projectId, _ in
                            if let projectId { desktop.splits.link(repository, to: projectId) } else { desktop.unlink(repository) }
                        },
                        choosingAnew: true,
                        placeholder: "Which project is \(repository.name) for?")
                }
            } else if let repository = part.repositories.first(where: { $0.identity != nil }) {
                // A repository whose branches belong to different projects, e.g. a monorepo.
                Button {
                    branchRules = BranchRulesTarget(repository: repository)
                } label: {
                    Image(systemName: "arrow.triangle.branch")
                        .font(.caption)
                }
                .buttonStyle(.borderless)
                .foregroundStyle(.secondary)
                .help("Different projects by branch in \(repository.name)…")
                .accessibilityLabel("Different Projects by Branch in \(repository.name)")
            }
        }
        .font(.callout)
    }
}

/// "✦ 1:20 · 0:40", each with its project's colour, or "✦ Split at 10:42" when the work moved from one project to the
/// next: offers the split agent activity suggests, beside an entry. Hours here; percentages are for the editor.
struct SplitSuggestionChip: View {
    let catalog: Catalog
    let time: UserTime
    let suggestion: SplitSuggestion
    /// The time the suggestion divides; a running entry's grows.
    let start: Date
    let stop: Date?
    let action: () -> Void

    @State private var hovering = false
    @State private var showsEvidence = false

    /// "Split at 10:42", or "Split into 3 Entries" when the work moved more than once.
    static func separateTitle(_ suggestion: SplitSuggestion, time: UserTime) -> String {
        guard let stretches = suggestion.stretches else { return "Split" }
        return stretches.count == 2 ? "Split at \(time.clock(stretches[1].range.start))" : "Split into \(stretches.count) Entries"
    }

    /// A part's colour: its project's, or its repository's while it has no project.
    static func color(_ part: SplitSuggestion.Part, catalog: Catalog) -> Color {
        EvidenceStrip.color(part.target, catalog: catalog)
    }

    var body: some View {
        TimelineView(.periodic(from: .now, by: stop == nil ? 60 : 3600)) { context in
            let durations = SplitApplier.durations(suggestion, seconds: (stop ?? context.date).timeIntervalSince(start))
            Button(action: action) {
                HStack(spacing: 5) {
                    Image(systemName: ActivityStyle.symbol)
                        .foregroundStyle(Color.brandText)
                    if suggestion.stretches == nil {
                        ForEach(suggestion.parts.indices, id: \.self) { index in
                            if index > 0 {
                                Text("·").foregroundStyle(.secondary)
                            }
                            Circle()
                                .fill(Self.color(suggestion.parts[index], catalog: catalog))
                                .frame(width: 7, height: 7)
                            Text(Durations.format(durations[index], as: .hm))
                                .monospacedDigit()
                        }
                    } else {
                        Text(Self.separateTitle(suggestion, time: time))
                            .monospacedDigit()
                    }
                }
                .font(.caption.weight(.medium))
                .padding(.horizontal, 8)
                .padding(.vertical, 3)
                .background(Color.primary.opacity(0.07), in: Capsule())
                .foregroundStyle(.primary)
            }
            .buttonStyle(.plain)
            // Resting on the chip shows what the suggestion is built from, in place of a tooltip.
            .onHover { hovering = $0 }
            .task(id: hovering) {
                guard hovering else { showsEvidence = false; return }
                try? await Task.sleep(for: .milliseconds(600))
                if !Task.isCancelled { showsEvidence = true }
            }
            .popover(isPresented: $showsEvidence, arrowEdge: .bottom) {
                EvidencePopover(suggestion: suggestion, catalog: catalog, time: time)
            }
            .accessibilityHint(EvidenceStrip.spoken(suggestion, catalog: catalog, time: time))
            .accessibilityLabel(suggestion.stretches == nil
                ? "Split as agents worked: \(names(durations))"
                : "\(Self.separateTitle(suggestion, time: time)), as agents worked: \(names(durations))")
        }
    }

    private func names(_ durations: [TimeInterval]) -> String {
        zip(suggestion.parts, durations).map { part, seconds in
            let name = switch part.target {
            case .project(let id): catalog.project(id)?.name ?? "a project"
            case .repository(let repository): repository.name
            }
            return "\(name) \(Durations.format(seconds, as: .hm))"
        }.joined(separator: ", ")
    }
}

/// "Agents worked on Atlas and Beacon": offers a split for the entry the timer just stopped, to take with one click,
/// look at first, or leave. It goes away on its own after a while; the entry keeps its chip, and the day's review
/// has it.
struct SplitOfferCard: View {
    @Environment(Desktop.self) private var desktop
    let offer: SplitOffer
    let tracker: Tracker

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: ActivityStyle.symbol)
                .foregroundStyle(Color.brandText)
                .font(.title3)
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.callout.weight(.semibold))
                    .lineLimit(1)
                Text(SplitApplier.summary(offer.suggestion, entry: offer.entry, catalog: tracker.catalog, time: tracker.time))
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
                    .lineLimit(1)
                EvidenceStrip(suggestion: offer.suggestion, catalog: tracker.catalog, time: tracker.time, showsTimes: false)
                    .frame(maxWidth: 260)
                    .padding(.top, 3)
                LaterInReview()
            }
            Spacer(minLength: 12)
            Button("Keep as Is") { desktop.keep(offer) }
                .help("Leave the entry on one project and stop suggesting a split for it")
            Button("Review…") { desktop.review(offer) }
                .help("Open the entry with the split, to change it first")
            Button(offer.suggestion.stretches == nil ? "Split" : SplitSuggestionChip.separateTitle(offer.suggestion, time: tracker.time)) {
                desktop.take(offer)
            }
            .buttonStyle(.brand)
            .keyboardShortcut(.defaultAction)
        }
        .controlSize(.small)
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .frame(maxWidth: 640)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.primary.opacity(0.12)))
        .shadow(color: .black.opacity(0.12), radius: 10, y: 4)
        .task(id: offer.id) {
            try? await Task.sleep(for: .seconds(45))
            if !Task.isCancelled { desktop.splits.withdrawOffer(offer.id) }
        }
    }

    private var title: String {
        let name = offer.entry.description.isEmpty ? "This time" : "“\(offer.entry.description)”"
        return "\(name) went to more than one project"
    }
}

/// "✦ End Split at 11:05": the running split's work changed after it was set. A menu takes it, opens the entry, or
/// puts it off until the split changes.
struct DriftChip: View {
    @Environment(Desktop.self) private var desktop
    let offer: DriftOffer
    let tracker: Tracker

    var body: some View {
        Menu {
            Text(offer.headline(catalog: tracker.catalog, time: tracker.time))
            Button(offer.title(catalog: tracker.catalog, time: tracker.time)) { desktop.take(offer) }
            Button("Review…") { desktop.review(offer) }
            Divider()
            Button("Not Now") { desktop.splits.answer(offer) }
        } label: {
            HStack(spacing: 5) {
                Image(systemName: ActivityStyle.symbol)
                    .foregroundStyle(Color.brandText)
                Text(offer.title(catalog: tracker.catalog, time: tracker.time))
                    .monospacedDigit()
            }
            .font(.caption.weight(.medium))
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(Color.primary.opacity(0.07), in: Capsule())
            .foregroundStyle(.primary)
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
        .help(offer.headline(catalog: tracker.catalog, time: tracker.time) + ". " + offer.question(catalog: tracker.catalog, time: tracker.time))
    }
}

/// "✦ Start at 9:12?": for the first minutes of a running timer, when agents were at work right before it with
/// nothing tracked. One click moves the start back; the project stays as the person chose it, and a timer without one
/// gets the agents' ("✦ Start Atlas at 9:12?").
struct EarlierStartChip: View {
    @Environment(Desktop.self) private var desktop
    let offer: EarlierStart
    let tracker: Tracker

    var body: some View {
        let clock = tracker.time.clock(offer.start)
        let other = offer.otherProjectId.flatMap { tracker.catalog.project($0)?.name }
        let fills = offer.fillsProjectId.flatMap { tracker.catalog.project($0)?.name }
        let start = fills.map { "Start \($0) at \(clock)?" } ?? "Start at \(clock)?"
        Button {
            desktop.take(offer)
        } label: {
            HStack(spacing: 5) {
                Image(systemName: ActivityStyle.symbol)
                    .foregroundStyle(Color.brandText)
                if let other {
                    Text("Agents were in \(other) since \(clock)")
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .frame(maxWidth: 220)
                    Text("·").foregroundStyle(.secondary)
                }
                Text(start)
                    .monospacedDigit()
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .frame(maxWidth: 280)
            }
            .font(.caption.weight(.medium))
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(Color.primary.opacity(0.07), in: Capsule())
            .foregroundStyle(.primary)
        }
        .buttonStyle(.plain)
        .fixedSize()
        .help(fills.map { "Agents were working in \($0) from \(clock) with nothing tracked. Click to start the timer at \(clock) on \($0)." }
              ?? "Agents were working \(other.map { "in \($0) " } ?? "")from \(clock) with nothing tracked. Click to start the timer at \(clock); its project stays as it is.")
        .accessibilityLabel(String(start.dropLast()))
    }
}

/// "Beacon has been quiet since 11:05": for a split entry the timer just stopped, when its work changed while it ran
/// and nobody answered that, offers to end the split there. It goes away on its own after a while.
struct DriftOfferCard: View {
    @Environment(Desktop.self) private var desktop
    let offer: DriftOffer
    let tracker: Tracker

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: ActivityStyle.symbol)
                .foregroundStyle(Color.brandText)
                .font(.title3)
            VStack(alignment: .leading, spacing: 2) {
                Text(offer.headline(catalog: tracker.catalog, time: tracker.time))
                    .font(.callout.weight(.semibold))
                    .lineLimit(1)
                Text(offer.question(catalog: tracker.catalog, time: tracker.time))
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
                    .lineLimit(1)
                LaterInReview()
            }
            Spacer(minLength: 12)
            Button("Not Now") { desktop.splits.answer(offer) }
                .help("Leave the entry as it is; nothing more is offered until its split changes")
            Button("Review…") { desktop.review(offer) }
                .help("Open the entry to change its split or times first")
            Button(offer.title(catalog: tracker.catalog, time: tracker.time)) { desktop.take(offer) }
                .buttonStyle(.brand)
                .keyboardShortcut(.defaultAction)
        }
        .controlSize(.small)
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .frame(maxWidth: 640)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.primary.opacity(0.12)))
        .shadow(color: .black.opacity(0.12), radius: 10, y: 4)
        .task(id: offer.id) {
            try? await Task.sleep(for: .seconds(45))
            if !Task.isCancelled { desktop.splits.withdrawOffer(offer.id) }
        }
    }
}

/// Under a card after a stop: when it goes away unanswered, nothing is lost.
private struct LaterInReview: View {
    var body: some View {
        Text("Also in today's review")
            .font(.caption)
            .foregroundStyle(.tertiary)
            .padding(.top, 2)
    }
}

/// Asks once whether to suggest splits from agent logs, when this Mac has logs of an agent Spindle reads.
struct AgentLogsOffer: View {
    @Environment(Desktop.self) private var desktop

    /// Whether to ask: the person hasn't answered yet, and there are logs to read.
    static func applies(_ settings: DesktopSettings) -> Bool {
        guard !settings.agentLogsDecided, ActivitySnapshot.root == nil else { return false }
        return AgentSource.allCases.contains {
            FileManager.default.fileExists(atPath: ($0.format.displayPath as NSString).expandingTildeInPath)
        }
    }

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: ActivityStyle.symbol)
                .foregroundStyle(Color.brandText)
                .font(.title2)
            VStack(alignment: .leading, spacing: 4) {
                Text("Split parallel work between projects")
                    .font(.headline)
                // Fixed vertical sizing here inflates the navigation's minimum height when it measures a narrow proposal.
                Text("Spindle can read \(AgentSource.names) logs on this Mac to notice when an entry's time went to more than one project, and offer to split it. Only times, folders and session titles are kept, and the logs stay on this Mac.")
                    .foregroundStyle(.secondary)
                HStack {
                    Button("Turn On") {
                        desktop.settings.useAgentLogs = true
                        desktop.onboarding = true
                        Task { _ = await desktop.allowNotifications() }
                    }
                    .buttonStyle(.brand)
                    Button("Not Now") { desktop.settings.useAgentLogs = false }
                }
                .padding(.top, 4)
            }
            Spacer(minLength: 0)
        }
        .padding(14)
        .cardSurface()
    }
}

/// The entries a stretch of time will become, one after another, each on its project.
struct StretchList: View {
    let tracker: Tracker
    let stretches: [TimeStretch]
    /// The entry's start and end as edited; the first stretch starts and the last ends there.
    let start: Date
    let end: Date?
    let onKeepOne: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            ForEach(stretches.indices, id: \.self) { index in
                let from = index == 0 ? start : stretches[index].range.start
                let to = index == stretches.count - 1 ? end : stretches[index + 1].range.start
                let project = tracker.catalog.project(stretches[index].projectId)
                HStack(spacing: 8) {
                    Circle()
                        .fill(project.map { Color(hex: $0.color) } ?? .secondary)
                        .frame(width: 8, height: 8)
                    Text(project?.name ?? "Project")
                        .fontWeight(.medium)
                    Spacer(minLength: 8)
                    Text("\(tracker.time.clock(from)) – \(to.map(tracker.time.clock) ?? "now")")
                        .foregroundStyle(.secondary)
                    TimelineView(.periodic(from: .now, by: to == nil ? 1 : 3600)) { context in
                        Text(Durations.clock(max(0, (to ?? context.date).timeIntervalSince(from))))
                            .monospacedDigit()
                            .foregroundStyle(.secondary)
                            .frame(minWidth: 64, alignment: .trailing)
                    }
                }
                .accessibilityElement(children: .combine)
            }
            HStack {
                Text("Each entry keeps the description and tags.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                Button("Back to One Entry", action: onKeepOne)
                    .buttonStyle(.brandLink)
                    .font(.callout)
            }
        }
        .padding(10)
        .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(.separator))
    }
}

/// Lays its children out in rows, wrapping to the next row when one is full.
struct FlowRow: Layout {
    var spacing: CGFloat = 14
    var rowSpacing: CGFloat = 4

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let rows = arrange(subviews, width: proposal.width ?? .infinity)
        let width = rows.map { $0.width }.max() ?? 0
        let height = rows.reduce(0) { $0 + $1.height } + rowSpacing * CGFloat(max(0, rows.count - 1))
        return CGSize(width: width, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var y = bounds.minY
        for row in arrange(subviews, width: bounds.width) {
            var x = bounds.minX
            for index in row.indices {
                let size = subviews[index].sizeThatFits(.unspecified)
                subviews[index].place(at: CGPoint(x: x, y: y + (row.height - size.height) / 2), proposal: .unspecified)
                x += size.width + spacing
            }
            y += row.height + rowSpacing
        }
    }

    private func arrange(_ subviews: Subviews, width: CGFloat) -> [(indices: [Int], width: CGFloat, height: CGFloat)] {
        var rows: [(indices: [Int], width: CGFloat, height: CGFloat)] = []
        for index in subviews.indices {
            let size = subviews[index].sizeThatFits(.unspecified)
            if var last = rows.last, last.width + spacing + size.width <= width {
                last.indices.append(index)
                last.width += spacing + size.width
                last.height = max(last.height, size.height)
                rows[rows.count - 1] = last
            } else {
                rows.append(([index], size.width, size.height))
            }
        }
        return rows
    }
}
