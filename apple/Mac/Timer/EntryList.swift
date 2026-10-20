import SwiftUI
import SpindleKit

/// The person's entries, a week at a time, grouped by day. Select several to change or delete them together.
struct EntryList: View {
    @Environment(AppModel.self) private var model
    @Environment(Desktop.self) private var desktop
    let tracker: Tracker

    @State private var selection: Set<String> = []
    @State private var deleting: [TimeEntry] = []
    @State private var loadingMore = false

    var body: some View {
        switch tracker.state {
        case .loading where tracker.weeksLoaded == 0:
            ProgressView()
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        case .failed(let message) where tracker.weeksLoaded == 0:
            ContentUnavailableView {
                Label("Couldn't load your time entries", systemImage: "exclamationmark.triangle")
            } description: {
                Text(message)
            } actions: {
                Button("Try Again") {
                    Task { await tracker.load() }
                }
            }
        default:
            list
        }
    }

    private var list: some View {
        let _ = Performance.event("Entry list drawn")
        let days = DayGroup.grouping(tracker.entries, in: tracker.time)
        // Worked out once for all the days shown, not once for each day's header.
        let toReview = Dictionary(grouping: desktop.review(on: days.map(\.day), tracker: tracker)) { tracker.time.day(of: $0.range.start) }
        return List(selection: $selection) {
            Section {
                if days.isEmpty {
                    VStack(spacing: 4) {
                        Text("Nothing tracked yet this week")
                            .font(.headline)
                        Text("Type what you are working on and press Return to start. To add time you forgot to track, switch to manual mode.")
                            .foregroundStyle(.secondary)
                            .multilineTextAlignment(.center)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 28)
                    .selectionDisabled()
                }
            } header: {
                WeekTotal(tracker: tracker)
            }

            ForEach(days) { day in
                Section {
                    ForEach(day.entries) { entry in
                        let locked = tracker.isLocked(entry.start)
                        EntryRow(
                            entry: entry, tracker: tracker, locked: locked,
                            suggestion: locked ? nil : desktop.splits.suggestion(for: entry, tracker: tracker),
                            onSuggestion: { suggest(entry) },
                            proposed: desktop.proposals.change(for: entry.id) != nil,
                            onProposal: { desktop.proposals.reviewing = true }
                        ) {
                            continueEntry(entry)
                        }
                        .tag(entry.id)
                        .selectionDisabled(tracker.isLocked(entry.start))
                    }
                } header: {
                    DayHeader(day: day, time: tracker.time, toReview: toReview[day.day]?.count ?? 0)
                }
            }

            Section {
                VStack(spacing: 6) {
                    Button(loadingMore ? "Loading…" : "Load the Week Before") {
                        loadWeekBefore()
                    }
                    .disabled(loadingMore)
                    if let since = tracker.loadedSince {
                        Text("Showing entries since \(tracker.time.longDay(tracker.time.day(of: since)))")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 8)
                .selectionDisabled()
            }
        }
        .listStyle(.inset)
        .scrollContentBackground(.hidden)
        .task(id: desktop.splits.request(SplitAdvisor.days(of: tracker.entries, time: tracker.time), time: tracker.time)) {
            // Agent activity of the days shown, to offer splitting entries whose time went to several projects;
            // today's again when agents wrote to their logs.
            await desktop.splits.load(SplitAdvisor.days(of: tracker.entries, time: tracker.time), tracker: tracker)
        }
        .contextMenu(forSelectionType: String.self) { ids in
            menu(for: ids)
        } primaryAction: { ids in
            if ids.count == 1, let entry = entry(ids.first) {
                model.editing = EntryEditing(entry: entry)
            }
        }
        .onDeleteCommand {
            deleting = selectedEntries(selection).filter { !tracker.isLocked($0.start) }
        }
        .confirmationDialog(deleteTitle, isPresented: Binding(get: { !deleting.isEmpty }, set: { if !$0 { deleting = [] } })) {
            Button("Delete", role: .destructive) {
                delete(deleting)
            }
        } message: {
            Text(deleteMessage)
        }
    }

    @ViewBuilder private func menu(for ids: Set<String>) -> some View {
        let entries = selectedEntries(ids)
        if entries.count == 1, let entry = entries.first {
            let locked = tracker.isLocked(entry.start)
            Button("Continue") { continueEntry(entry) }
                .disabled(entry.isRunning)
            Button(locked ? "View…" : "Edit…") { model.editing = EntryEditing(entry: entry) }
            if !locked {
                if desktop.splits.suggestion(for: entry, tracker: tracker) != nil {
                    Button("Split as Agents Worked…") { suggest(entry) }
                }
                Button(entry.isSplit ? "Change Split…" : "Split Time…") {
                    model.editing = EntryEditing(entry: entry, startSplit: !entry.isSplit)
                }
            }
            Button("Duplicate") { duplicate(entry) }
                .disabled(entry.isRunning || locked)
            Divider()
            Button("Delete…", role: .destructive) { deleting = [entry] }
                .disabled(locked)
        } else if entries.count > 1 {
            bulkMenu(entries)
        }
    }

    /// Opens the entry with the split agent activity suggests, ready to save.
    private func suggest(_ entry: TimeEntry) {
        model.editing = SplitAdvisor.editing(entry, as: desktop.splits.suggestion(for: entry, tracker: tracker), catalog: tracker.catalog)
    }

    /// Changes for several entries at once, as on the web app's selection bar.
    @ViewBuilder private func bulkMenu(_ entries: [TimeEntry]) -> some View {
        let ids = entries.map(\.id)
        let count = "\(ids.count) entries"
        let catalog = tracker.catalog
        Menu("Project") {
            Button("No Project") { apply(ids, "Moved \(count)") { try await $0.update(ids, project: (nil, nil)) } }
            ForEach(catalog.projectGroups()) { group in
                Section(group.client?.name ?? "No client") {
                    ForEach(group.projects) { project in
                        let tasks = project.tasks.filter { !$0.isDone }
                        if tasks.isEmpty {
                            Button(project.name) { apply(ids, "Moved \(count)") { try await $0.update(ids, project: (project.id, nil)) } }
                        } else {
                            Menu(project.name) {
                                Button("No Task") { apply(ids, "Moved \(count)") { try await $0.update(ids, project: (project.id, nil)) } }
                                Divider()
                                ForEach(tasks) { task in
                                    Button(task.name) { apply(ids, "Moved \(count)") { try await $0.update(ids, project: (project.id, task.id)) } }
                                }
                            }
                        }
                    }
                }
            }
        }
        Menu("Add Tag") {
            ForEach(catalog.sortedTags) { tag in
                Button(tag.name) { apply(ids, "Tagged \(count)") { try await $0.update(ids, addTagIds: [tag.id]) } }
            }
        }
        .disabled(catalog.tags.isEmpty)
        let tagsOnSelection = Set(entries.flatMap(\.tagIds))
        Menu("Remove Tag") {
            ForEach(catalog.sortedTags.filter { tagsOnSelection.contains($0.id) }) { tag in
                Button(tag.name) { apply(ids, "Untagged \(count)") { try await $0.update(ids, removeTagIds: [tag.id]) } }
            }
        }
        .disabled(tagsOnSelection.isEmpty)
        Button("Mark Billable") { apply(ids, "Marked billable: \(count)") { try await $0.update(ids, isBillable: true) } }
        Button("Mark Non-billable") { apply(ids, "Marked non-billable: \(count)") { try await $0.update(ids, isBillable: false) } }
        Divider()
        Button("Delete \(count)…", role: .destructive) { deleting = entries }
    }

    private func apply(_ ids: [String], _ done: String, _ change: @escaping (Tracker) async throws -> Void) {
        Task {
            do {
                try await change(tracker)
                model.notices.show(done)
            } catch {
                model.notices.show(error, fallback: "Couldn't change the entries.")
            }
        }
    }

    private func entry(_ id: String?) -> TimeEntry? {
        tracker.entries.first { $0.id == id }
    }

    private func selectedEntries(_ ids: Set<String>) -> [TimeEntry] {
        tracker.entries.filter { ids.contains($0.id) }
    }

    private var deleteTitle: String {
        deleting.count == 1 ? "Delete this time entry?" : "Delete \(deleting.count) time entries?"
    }

    private var deleteMessage: String {
        guard deleting.count == 1, let entry = deleting.first else {
            return "They will be removed, with all their time."
        }
        let duration = tracker.time.duration(entry.seconds(at: Date()))
        return entry.description.isEmpty ? "The entry and its \(duration) will be removed." : "“\(entry.description)” and its \(duration) will be removed."
    }

    private func continueEntry(_ entry: TimeEntry) {
        Task {
            do {
                try await tracker.continueEntry(entry)
            } catch {
                model.notices.show(error, fallback: "Couldn't start the timer.")
            }
        }
    }

    private func duplicate(_ entry: TimeEntry) {
        Task {
            do {
                try await tracker.duplicate(entry)
                model.notices.show("Time entry duplicated")
            } catch {
                model.notices.show(error, fallback: "Couldn't duplicate the time entry.")
            }
        }
    }

    private func delete(_ entries: [TimeEntry]) {
        let ids = entries.map(\.id)
        deleting = []
        selection.subtract(ids)
        Task {
            do {
                if let entry = entries.first, entries.count == 1 {
                    try await tracker.delete(entry)
                    model.notices.show("Time entry deleted")
                } else {
                    try await tracker.delete(ids)
                    model.notices.show("\(ids.count) time entries deleted")
                }
            } catch {
                model.notices.show(error, fallback: "Couldn't delete the time entries.")
            }
        }
    }

    private func loadWeekBefore() {
        loadingMore = true
        Task {
            defer { loadingMore = false }
            do {
                try await tracker.loadWeekBefore()
            } catch {
                model.notices.show(error, fallback: "Couldn't load the week before.")
            }
        }
    }
}

/// "This week" and the time tracked in it.
private struct WeekTotal: View {
    let tracker: Tracker

    var body: some View {
        let week = tracker.time.week(weeksBack: 0, from: Date())
        let entries = tracker.entries.filter { week.contains($0.start) }
        HStack {
            Text("This week")
                .font(.headline)
            Spacer()
            TimelineView(.periodic(from: .now, by: entries.contains(where: \.isRunning) ? 1 : 3600)) { context in
                Text("Total ").foregroundStyle(.secondary)
                    + Text(tracker.time.duration(entries.reduce(0) { $0 + $1.seconds(at: context.date) })).fontWeight(.semibold)
            }
            .font(.body.monospacedDigit())
        }
        .foregroundStyle(.primary)
        .padding(.vertical, 6)
    }
}

private struct DayHeader: View {
    let day: DayGroup
    let time: UserTime
    /// What the day's review offers; its button shows when there is something.
    var toReview = 0

    var body: some View {
        HStack {
            Text(time.dayHeading(day.day, now: Date()))
            if toReview > 0 {
                ReviewDayButton(count: toReview, scope: .day(day.day))
            }
            Spacer()
            TimelineView(.periodic(from: .now, by: day.entries.contains(where: \.isRunning) ? 1 : 3600)) { context in
                Text(time.duration(day.totalSeconds(at: context.date)))
                    .monospacedDigit()
            }
        }
        .font(.callout.weight(.semibold))
        .foregroundStyle(.primary)
    }
}

/// One entry: what it was, when, and for how long.
struct EntryRow: View {
    let entry: TimeEntry
    let tracker: Tracker
    let locked: Bool
    /// A split agent activity suggests for the entry, offered beside it.
    var suggestion: SplitSuggestion?
    var onSuggestion: () -> Void = {}
    /// Whether an agent proposed a change to the entry, waiting for review.
    var proposed = false
    var onProposal: () -> Void = {}
    let onContinue: () -> Void
    @State private var hovering = false

    var body: some View {
        let catalog = tracker.catalog
        let project = catalog.project(entry.projectId)
        let tags = entry.tagIds.compactMap { catalog.tag($0) }
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(entry.description.isEmpty ? "No description" : entry.description)
                    .foregroundStyle(entry.description.isEmpty ? .secondary : .primary)
                    .lineLimit(1)
                if project != nil || entry.isSplit || !tags.isEmpty {
                    HStack(spacing: 10) {
                        if entry.isSplit {
                            SplitLabel(catalog: catalog, parts: entry.parts)
                            SplitMeter(catalog: catalog, parts: entry.parts)
                                .frame(width: 56)
                        } else if let project {
                            ProjectLabel(project: project, task: catalog.task(entry.taskId), client: catalog.client(project.clientId))
                        }
                        ForEach(tags) { tag in
                            TagChip(name: tag.name)
                        }
                    }
                    .font(.callout)
                }
            }
            Spacer(minLength: 12)
            if locked {
                Image(systemName: "lock.fill")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .help("Locked")
                    .accessibilityLabel("Locked")
            }
            if proposed {
                ProposalChip(action: onProposal)
            }
            if let suggestion {
                SplitSuggestionChip(catalog: catalog, time: tracker.time, suggestion: suggestion, start: entry.start, stop: entry.stop, action: onSuggestion)
            }
            if entry.parts.contains(where: \.isBillable) {
                Image(systemName: "dollarsign")
                    .foregroundStyle(.secondary)
                    .help("Billable")
                    .accessibilityLabel("Billable")
            }
            Text("\(tracker.time.clock(entry.start)) – \(entry.stop.map(tracker.time.clock) ?? "now")")
                .foregroundStyle(.secondary)
                .monospacedDigit()
            Group {
                if entry.isRunning {
                    // A running timer always counts in seconds.
                    TimelineView(.periodic(from: .now, by: 1)) { context in
                        Text(Durations.clock(entry.seconds(at: context.date)))
                            .foregroundStyle(Color.brandText)
                            .fontWeight(.semibold)
                    }
                } else {
                    Text(tracker.time.duration(entry.seconds(at: Date())))
                        .fontWeight(.medium)
                }
            }
            .monospacedDigit()
            .frame(width: 76, alignment: .trailing)
            Button(action: onContinue) {
                Image(systemName: "play.fill")
                    .foregroundStyle(Color.brandText)
            }
            .buttonStyle(.plain)
            .help("Continue")
            .accessibilityLabel("Continue “\(entry.description.isEmpty ? "No description" : entry.description)”")
            .opacity(hovering && !entry.isRunning ? 1 : 0)
            .disabled(entry.isRunning)
        }
        .padding(.vertical, 4)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .accessibilityElement(children: .combine)
        .accessibilityAction(named: "Continue", onContinue)
    }
}
