import SwiftUI
import SpindleKit

/// Where the person goes through what their agents proposed: by day, each proposal beside the entries already
/// there, with what stands in its way, to add, edit or discard one at a time or all at once.
struct ProposalReview: View {
    @Environment(AppModel.self) private var model
    @Environment(Desktop.self) private var desktop
    let tracker: Tracker

    @State private var editing: EntryEditing?
    @State private var busy: Set<String> = []
    @State private var addingAll = false

    private var inbox: ProposalInbox { desktop.proposals }
    private var time: UserTime { tracker.time }

    var body: some View {
        let pending = inbox.pending(in: tracker)
        let issues = Dictionary(uniqueKeysWithValues: pending.map { ($0.id, inbox.issues($0, tracker: tracker)) })
        let ready = pending.filter { issues[$0.id]?.contains(where: \.blocking) != true }
        VStack(spacing: 0) {
            header(pending)
                .padding(20)
            Divider()
            if pending.isEmpty && inbox.done.isEmpty {
                ContentUnavailableView("Nothing waiting", systemImage: ActivityStyle.symbol,
                                       description: Text("When you ask Claude Code or Codex to add or change time, what they propose waits here."))
                    .frame(maxHeight: .infinity)
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 22) {
                        ForEach(days(pending), id: \.self) { day in
                            daySection(day, proposals: pending.filter { self.day(of: $0) == day }, issues: issues)
                        }
                        if !inbox.done.isEmpty {
                            doneSection
                        }
                    }
                    .padding(20)
                }
            }
            Divider()
            footer(pending: pending, ready: ready)
                .padding(.horizontal, 20)
                .padding(.vertical, 14)
        }
        .frame(width: 760, height: 600)
        // The entries of the proposals' days, as the server has them, to show beside them and find overlaps.
        .task(id: inbox.days(of: pending, tracker: tracker)) {
            let days = inbox.days(of: pending, tracker: tracker)
            guard let first = days.first, let last = days.last else { return }
            // A day before too, for an entry that runs over midnight into the first.
            _ = await tracker.entries(from: tracker.time.start(of: tracker.time.adding(-1, to: first)),
                                      to: tracker.time.start(of: tracker.time.adding(1, to: last)))
        }
        .sheet(item: $editing) { editing in
            EntryEditor(tracker: tracker, editing: editing)
                .environment(model.notices)
        }
    }

    // MARK: - Header and footer

    private func header(_ pending: [AgentProposal]) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: ActivityStyle.symbol)
                .font(.title2)
                .foregroundStyle(Color.brandText)
                .frame(width: 36, height: 36)
                .background(Color.brand.opacity(0.18), in: RoundedRectangle(cornerRadius: 9))
            VStack(alignment: .leading, spacing: 3) {
                Text(title(pending))
                    .font(.title2.weight(.semibold))
                Text(pending.isEmpty ? "All dealt with. Undo takes back anything saved here."
                     : Self.count(pending) + ". Nothing is saved until you add it.")
                    .foregroundStyle(.secondary)
            }
            Spacer()
        }
    }

    private func title(_ pending: [AgentProposal]) -> String {
        let clients = Set((pending + inbox.done.map(\.proposal)).map { $0.client ?? "" })
        if clients.count == 1, let client = clients.first, !client.isEmpty { return "Proposed by \(client)" }
        return "Proposed by your agents"
    }

    /// "3 new entries and 1 change".
    static func count(_ proposals: [AgentProposal]) -> String {
        let new = proposals.filter(\.isNew).count
        let changes = proposals.count - new
        let parts = [
            new == 0 ? nil : new == 1 ? "1 new entry" : "\(new) new entries",
            changes == 0 ? nil : changes == 1 ? "1 change" : "\(changes) changes",
        ].compactMap { $0 }
        return parts.isEmpty ? "Nothing waiting" : parts.joined(separator: " and ")
    }

    private func footer(pending: [AgentProposal], ready: [AgentProposal]) -> some View {
        HStack(spacing: 10) {
            if !pending.isEmpty {
                Button("Discard All", role: .destructive) {
                    inbox.discardAll(in: tracker)
                }
                .help("Throw away every proposal waiting here")
            }
            Spacer()
            if pending.count > ready.count && !ready.isEmpty {
                Text("\(pending.count - ready.count) need\(pending.count - ready.count == 1 ? "s" : "") a look first")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            Button(pending.isEmpty ? "Done" : "Close") { inbox.endReview() }
                .keyboardShortcut(.cancelAction)
            if !pending.isEmpty {
                Button(addingAll ? "Adding…" : ready.count == pending.count ? "Add All" : "Add \(ready.count)") {
                    addAll()
                }
                .buttonStyle(.brand)
                .keyboardShortcut(.defaultAction)
                .disabled(ready.isEmpty || addingAll)
                .help("Save every proposal nothing stands in the way of")
            }
        }
        .controlSize(.large)
    }

    // MARK: - Days

    private func day(of proposal: AgentProposal) -> DayKey {
        let start = proposal.fields.start ?? proposal.entryId.flatMap(tracker.entry(id:))?.start ?? proposal.original?.start ?? Date()
        return time.day(of: start)
    }

    private func days(_ pending: [AgentProposal]) -> [DayKey] {
        Array(Set(pending.map(day(of:)))).sorted()
    }

    private func daySection(_ day: DayKey, proposals: [AgentProposal], issues: [String: [AgentProposals.Issue]]) -> some View {
        let sorted = proposals.sorted { range($0).start < range($1).start }
        let added = sorted.filter(\.isNew).reduce(0.0) { $0 + range($1).duration }
        return VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                Text(time.dayHeading(day, now: Date()))
                    .font(.headline)
                Spacer()
                if added > 0 {
                    Text("+\(time.duration(added)) proposed")
                        .font(.callout.monospacedDigit())
                        .foregroundStyle(.secondary)
                }
            }
            DayStrip(tracker: tracker, day: day, proposals: sorted, range: range)
            VStack(spacing: 8) {
                ForEach(sorted) { proposal in
                    ProposalRow(
                        tracker: tracker, proposal: proposal, issues: issues[proposal.id] ?? [], busy: busy.contains(proposal.id),
                        onAccept: { accept(proposal) },
                        onEdit: { edit(proposal) },
                        onDiscard: { inbox.discard(proposal) })
                }
            }
        }
    }

    /// When the proposal's time would be, on the entry as it is now.
    private func range(_ proposal: AgentProposal) -> DateInterval {
        let request = proposal.request(current: proposal.entryId.flatMap(tracker.entry(id:)))
        return DateInterval(start: request.start, end: max(request.start, request.stop ?? Date()))
    }

    private var doneSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Saved in this review")
                .font(.headline)
            ForEach(inbox.done) { item in
                let request = item.proposal.request(current: tracker.entry(id: item.entryId))
                HStack(spacing: 10) {
                    Image(systemName: "checkmark.circle.fill")
                        .foregroundStyle(.green)
                    Text(request.details.description.isEmpty ? "No description" : request.details.description)
                        .lineLimit(1)
                    Text(item.before == nil ? "added" : "changed")
                        .foregroundStyle(.secondary)
                    Spacer()
                    Text("\(time.clock(request.start)) – \(request.stop.map(time.clock) ?? "now")")
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                    Button("Undo") { undo(item) }
                        .disabled(busy.contains(item.id))
                }
                .font(.callout)
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 8))
            }
        }
    }

    // MARK: - Actions

    private func accept(_ proposal: AgentProposal) {
        busy.insert(proposal.id)
        Task {
            defer { busy.remove(proposal.id) }
            do {
                try await inbox.accept(proposal, tracker: tracker)
            } catch {
                model.notices.show(error, fallback: proposal.isNew ? "Couldn't add the entry." : "Couldn't change the entry.")
            }
        }
    }

    private func addAll() {
        addingAll = true
        Task {
            defer { addingAll = false }
            let result = await inbox.acceptAll(in: tracker)
            if result.failed > 0 {
                model.notices.show("\(result.failed) couldn't be saved. They are still here.", kind: .error)
            } else if inbox.pending(in: tracker).isEmpty {
                model.notices.show(result.added == 1 ? "Saved 1 proposal" : "Saved \(result.added) proposals")
                inbox.endReview()
            }
        }
    }

    private func undo(_ item: ProposalInbox.Done) {
        busy.insert(item.id)
        Task {
            defer { busy.remove(item.id) }
            do {
                try await inbox.undo(item, tracker: tracker)
            } catch {
                model.notices.show(error, fallback: "Couldn't take it back.")
            }
        }
    }

    /// Opens the proposal in the entry editor, to change it before saving.
    private func edit(_ proposal: AgentProposal) {
        let current = proposal.entryId.flatMap(tracker.entry(id:))
        let request = proposal.request(current: current)
        var editing = current.map { EntryEditing(entry: $0) }
            ?? EntryEditing(newRange: DateInterval(start: request.start, end: max(request.start, request.stop ?? request.start)))
        editing.proposed = request
        editing.onSavedEntry = { [inbox] id in inbox.saved(proposal, as: id) }
        self.editing = editing
    }
}

/// One proposal: what it adds or changes, why, what stands in its way, and the buttons to answer it.
private struct ProposalRow: View {
    let tracker: Tracker
    let proposal: AgentProposal
    let issues: [AgentProposals.Issue]
    let busy: Bool
    let onAccept: () -> Void
    let onEdit: () -> Void
    let onDiscard: () -> Void

    private var time: UserTime { tracker.time }
    private var catalog: Catalog { tracker.catalog }

    var body: some View {
        let current = proposal.entryId.flatMap(tracker.entry(id:))
        let request = proposal.request(current: current)
        let blocked = issues.contains(where: \.blocking)
        let color = catalog.project(request.details.projectId).map { Color(hex: $0.color) } ?? Color.secondary
        HStack(alignment: .top, spacing: 12) {
            RoundedRectangle(cornerRadius: 2)
                .fill(color)
                .frame(width: 4)
                .frame(maxHeight: .infinity)
            VStack(alignment: .leading, spacing: 5) {
                HStack(spacing: 6) {
                    if !proposal.isNew {
                        Text("Change")
                            .font(.caption.weight(.semibold))
                            .padding(.horizontal, 6)
                            .padding(.vertical, 1)
                            .background(Color.brand.opacity(0.22), in: Capsule())
                    }
                    // Parts described one by one show their own lines below instead.
                    if !describedPerProject(request.details) {
                        Text(request.details.description.isEmpty ? "No description" : request.details.description)
                            .fontWeight(.medium)
                            .foregroundStyle(request.details.description.isEmpty ? .secondary : .primary)
                            .lineLimit(2)
                    }
                }
                if describedPerProject(request.details) {
                    partLines(request.details.shares)
                } else {
                    work(request.details)
                        .font(.callout)
                }
                if let current, !proposal.isNew {
                    changes(from: current, to: request)
                }
                if !proposal.reason.isEmpty {
                    Label(proposal.reason, systemImage: "text.quote")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                }
                ForEach(issues, id: \.self) { issue in
                    Label(issue.message, systemImage: issue.blocking ? "exclamationmark.octagon.fill" : "exclamationmark.triangle.fill")
                        .font(.caption)
                        .foregroundStyle(issue.blocking ? Color.red : Color.orange)
                }
            }
            Spacer(minLength: 8)
            VStack(alignment: .trailing, spacing: 3) {
                Text("\(time.clock(request.start)) – \(request.stop.map(time.clock) ?? "now")")
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
                Text(time.duration(max(0, (request.stop ?? Date()).timeIntervalSince(request.start))))
                    .fontWeight(.semibold)
                    .monospacedDigit()
            }
            VStack(alignment: .trailing, spacing: 6) {
                Button(proposal.isNew ? "Add" : "Apply", action: onAccept)
                    .buttonStyle(.brand)
                    .disabled(blocked || busy)
                    .help(blocked ? "Edit it first: \(issues.first(where: \.blocking)?.message ?? "")" : "Save it as proposed")
                HStack(spacing: 4) {
                    Button("Edit…", action: onEdit)
                        .buttonStyle(.borderless)
                        .disabled(busy || (current == nil && !proposal.isNew))
                    Button(action: onDiscard) {
                        Image(systemName: "xmark")
                    }
                    .buttonStyle(.borderless)
                    .help("Discard")
                    .accessibilityLabel("Discard")
                    .disabled(busy)
                }
                .font(.callout)
            }
            .controlSize(.small)
        }
        .padding(12)
        .background(Color.primary.opacity(0.03), in: RoundedRectangle(cornerRadius: 10))
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(blocked ? Color.red.opacity(0.4) : Color.primary.opacity(0.14), style: StrokeStyle(lineWidth: 1, dash: [5, 3])))
        .accessibilityElement(children: .contain)
    }

    private func describedPerProject(_ details: EntryDetails) -> Bool {
        details.isSplit && details.shares.contains { $0.description != nil }
    }

    /// "● Atlas 60%  Fix the login": each part of a split described one by one, as its project's timesheet will show it.
    private func partLines(_ parts: [SplitPart]) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            ForEach(parts.indices, id: \.self) { index in
                let part = parts[index]
                let text = part.description ?? ""
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Circle()
                        .fill(catalog.color(of: part))
                        .frame(width: 7, height: 7)
                    Text(catalog.project(part.projectId)?.name ?? "No project")
                        .fontWeight(.medium)
                    Text("\(part.percent)%")
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                    Text(text.isEmpty ? "No description" : text)
                        .foregroundStyle(text.isEmpty ? .secondary : .primary)
                        .lineLimit(1)
                }
            }
        }
        .font(.callout)
    }

    @ViewBuilder private func work(_ details: EntryDetails) -> some View {
        if details.isSplit {
            SplitLabel(catalog: catalog, parts: details.shares)
        } else if let project = catalog.project(details.projectId) {
            ProjectLabel(project: project, task: catalog.task(details.taskId), client: catalog.client(project.clientId))
        } else {
            Text("No project").foregroundStyle(.secondary)
        }
    }

    /// What the change does, line by line: "Time 14:00–16:00 → 14:00–15:30".
    @ViewBuilder private func changes(from entry: TimeEntry, to request: EntryRequest) -> some View {
        let fields = proposal.fields
        VStack(alignment: .leading, spacing: 2) {
            if fields.start != nil || fields.stop != nil {
                line("Time", "\(span(entry.start, entry.stop)) → \(span(request.start, request.stop))")
            }
            // A split whose parts only got descriptions changes no project.
            if fields.work != nil, work(of: entry.details) != work(of: request.details) {
                line("Project", "\(work(of: entry.details)) → \(work(of: request.details))")
            }
            if fields.description != nil || request.details.description != entry.description {
                line("Description", "was “\(entry.description.isEmpty ? "No description" : entry.description)”")
            }
        }
        .font(.caption)
    }

    /// "Atlas · Login", or the parts of a split: "Atlas 60% · Beacon 40%".
    private func work(of details: EntryDetails) -> String {
        if details.isSplit { return catalog.describe(details.shares) }
        guard let project = catalog.project(details.projectId) else { return "No project" }
        return [project.name, catalog.task(details.taskId)?.name].compactMap { $0 }.joined(separator: " · ")
    }

    private func line(_ label: String, _ value: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Text(label)
                .foregroundStyle(.secondary)
                .frame(width: 72, alignment: .leading)
            Text(value)
                .monospacedDigit()
                .lineLimit(2)
        }
    }

    private func span(_ start: Date, _ stop: Date?) -> String {
        "\(time.clock(start))–\(stop.map(time.clock) ?? "now")"
    }
}

/// A day at a glance: the entries already there as bars, the proposals as dashed outlines over them, so overlaps
/// show before anything is added.
private struct DayStrip: View {
    let tracker: Tracker
    let day: DayKey
    let proposals: [AgentProposal]
    let range: (AgentProposal) -> DateInterval

    var body: some View {
        let time = tracker.time
        let dayStart = time.start(of: day)
        let entries = tracker.entries(overlapping: [day])
        let spans = entries.map { DateInterval(start: $0.start, end: max($0.start, $0.stop ?? Date())) } + proposals.map(range)
        let first = spans.map(\.start).min() ?? dayStart.addingTimeInterval(9 * 3600)
        let last = spans.map(\.end).max() ?? first.addingTimeInterval(8 * 3600)
        // Whole hours around what is there, at least four of them.
        let from = max(dayStart, Date(timeIntervalSinceReferenceDate: (first.timeIntervalSinceReferenceDate / 3600).rounded(.down) * 3600))
        let to = max(from.addingTimeInterval(4 * 3600),
                     Date(timeIntervalSinceReferenceDate: (last.timeIntervalSinceReferenceDate / 3600).rounded(.up) * 3600))
        let hours = Int(to.timeIntervalSince(from) / 3600)
        let step = hours > 12 ? 3 : hours > 6 ? 2 : 1
        GeometryReader { geometry in
            let width = geometry.size.width
            let x = { (date: Date) in CGFloat(date.timeIntervalSince(from) / to.timeIntervalSince(from)) * width }
            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 6)
                    .fill(Color.primary.opacity(0.04))
                    .frame(height: 30)
                ForEach(Array(stride(from: 0, through: hours, by: step)), id: \.self) { hour in
                    let at = from.addingTimeInterval(TimeInterval(hour) * 3600)
                    Rectangle()
                        .fill(Color.primary.opacity(0.08))
                        .frame(width: 1, height: 30)
                        .offset(x: x(at))
                    Text(time.clock(at))
                        .font(.system(size: 9).monospacedDigit())
                        .foregroundStyle(.secondary)
                        .fixedSize()
                        .offset(x: min(max(0, x(at) - 12), width - 26), y: 33)
                }
                ForEach(entries) { entry in
                    let color = tracker.catalog.project(entry.projectId).map { Color(hex: $0.color) } ?? .secondary
                    let span = DateInterval(start: max(from, entry.start), end: min(to, max(entry.start, entry.stop ?? Date())))
                    RoundedRectangle(cornerRadius: 4)
                        .fill(color.opacity(0.35))
                        .frame(width: max(2, x(span.end) - x(span.start)), height: 18)
                        .offset(x: x(span.start), y: 6)
                        .help(entry.description.isEmpty ? "No description" : entry.description)
                }
                ForEach(proposals) { proposal in
                    let span = range(proposal)
                    let projectId = proposal.request(current: proposal.entryId.flatMap(tracker.entry(id:))).details.projectId
                    let color = tracker.catalog.project(projectId).map { Color(hex: $0.color) } ?? Color.brand
                    RoundedRectangle(cornerRadius: 4)
                        .strokeBorder(color, style: StrokeStyle(lineWidth: 1.5, dash: [4, 2]))
                        .background(color.opacity(0.12), in: RoundedRectangle(cornerRadius: 4))
                        .frame(width: max(4, x(min(to, span.end)) - x(max(from, span.start))), height: 26)
                        .offset(x: x(max(from, span.start)), y: 2)
                }
            }
        }
        .frame(height: 46)
        .accessibilityHidden(true)
    }
}

/// Above every section while agents' proposals wait: how many, and the way to review them.
struct ProposalBanner: View {
    @Environment(AppModel.self) private var model
    @Environment(Desktop.self) private var desktop

    var body: some View {
        if let tracker = model.tracker {
            let pending = desktop.proposals.pending(in: tracker)
            if !pending.isEmpty {
                HStack(spacing: 10) {
                    Image(systemName: ActivityStyle.symbol)
                        .foregroundStyle(Color.brandText)
                    let who = Set(pending.map { $0.client ?? "" }).count == 1 ? pending[0].client ?? "An agent" : "Your agents"
                    Text("\(Text("\(who) proposed \(ProposalReview.count(pending)).").fontWeight(.semibold)) Nothing is saved until you add it.")
                        .frame(maxWidth: .infinity, alignment: .leading)
                    Button("Review…") { desktop.proposals.reviewing = true }
                        .buttonStyle(.brand)
                        .controlSize(.small)
                }
                .font(.callout)
                .padding(.horizontal, 20)
                .padding(.vertical, 9)
                .background(Color.brand.opacity(0.1))
                .overlay(alignment: .bottom) { Divider() }
                .accessibilityElement(children: .combine)
            }
        }
    }
}

/// Beside an entry an agent proposed a change to.
struct ProposalChip: View {
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Label("Change proposed", systemImage: ActivityStyle.symbol)
                .font(.caption.weight(.medium))
                .padding(.horizontal, 7)
                .padding(.vertical, 2)
                .foregroundStyle(Color.brandText)
                .background(Color.brand.opacity(0.18), in: Capsule())
                .overlay(Capsule().strokeBorder(Color.brand.opacity(0.5), style: StrokeStyle(lineWidth: 1, dash: [3, 2])))
        }
        .buttonStyle(.plain)
        .help("An agent proposed changing this entry. Review it.")
    }
}
