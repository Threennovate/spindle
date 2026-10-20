import SwiftUI
import SpindleKit

/// The timer on top, and the person's entries below, a week at a time and grouped by day.
struct TimerTab: View {
    @Environment(AppModel.self) private var model
    let tracker: Tracker

    @State private var draft = EntryDetails()
    @FocusState private var descriptionFocused: Bool
    @State private var suggestions: [TimeEntrySuggestion] = []
    @State private var deleting: TimeEntry?
    @State private var loadingMore = false

    var body: some View {
        NavigationStack {
            List {
                if let organization = model.readOnlyOrganization {
                    Section {
                        ReadOnlyNotice(organization: organization)
                    }
                    .listRowBackground(Color.red.opacity(0.08))
                }
                Section {
                    TimerCard(tracker: tracker, draft: $draft, focused: $descriptionFocused, onStart: start)
                }
                if descriptionFocused && tracker.running == nil && !suggestions.isEmpty {
                    suggestionList
                } else {
                    entries
                }
            }
            .listStyle(.insetGrouped)
            .navigationTitle("Timer")
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    SyncStatusView()
                }
                ToolbarItem(placement: .topBarTrailing) {
                    Button {
                        model.editing = EntryEditing(newRange: ManualEntry.defaultRange(now: AppClock.now, entries: tracker.entries, time: tracker.time))
                    } label: {
                        Image(systemName: "plus")
                    }
                    .accessibilityLabel("Add a time entry")
                    .disabled(model.readOnlyOrganization != nil)
                }
            }
            .refreshable { await model.refresh() }
            .task(id: "\(draft.description)|\(descriptionFocused)") {
                guard descriptionFocused, tracker.running == nil else { return }
                try? await Task.sleep(for: .milliseconds(150))
                guard !Task.isCancelled, let found = try? await tracker.suggestions(matching: draft.description) else { return }
                suggestions = found
            }
            .confirmationDialog(deleteTitle, isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }), titleVisibility: .visible) {
                Button("Delete", role: .destructive) {
                    if let deleting { delete(deleting) }
                }
            } message: {
                Text(deleteMessage)
            }
            #if DEBUG
            .task { await applyLaunchOptions() }
            #endif
        }
    }

    // MARK: - Suggestions

    private var suggestionList: some View {
        Section("Previous entries") {
            ForEach(suggestions) { suggestion in
                Button {
                    start(suggestion.details)
                } label: {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(suggestion.description.isEmpty ? "No description" : suggestion.description)
                            .foregroundStyle(suggestion.description.isEmpty ? .secondary : .primary)
                            .lineLimit(1)
                        if let project = tracker.catalog.project(suggestion.projectId) {
                            ProjectLabel(project: project, task: tracker.catalog.task(suggestion.taskId), client: tracker.catalog.client(project.clientId))
                                .font(.subheadline)
                        }
                    }
                    .contentShape(Rectangle())
                }
                .tint(.primary)
                .accessibilityHint("Starts the timer with this entry's details")
            }
        }
    }

    // MARK: - Entries

    @ViewBuilder private var entries: some View {
        switch tracker.state {
        case .loading where tracker.weeksLoaded == 0:
            Section {
                ProgressView()
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 24)
            }
        case .failed(let message) where tracker.weeksLoaded == 0:
            Section {
                VStack(spacing: 10) {
                    Label("Couldn't load your time entries", systemImage: "exclamationmark.triangle")
                        .font(.headline)
                    Text(message)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                    Button("Try Again") {
                        Task { await tracker.load() }
                    }
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 12)
            }
        default:
            let days = DayGroup.grouping(tracker.entries, in: tracker.time)
            Section {
                if days.isEmpty {
                    VStack(spacing: 6) {
                        Text("Nothing tracked yet this week")
                            .font(.headline)
                        Text("Type what you're working on and tap the play button. To add time you forgot to track, tap +.")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                            .multilineTextAlignment(.center)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 16)
                }
            } header: {
                WeekTotal(tracker: tracker)
            }
            ForEach(days) { day in
                Section {
                    ForEach(day.entries) { entry in
                        row(entry)
                    }
                } header: {
                    DayHeader(day: day, time: tracker.time)
                }
            }
            Section {
                VStack(spacing: 6) {
                    Button(loadingMore ? "Loading…" : "Load the Week Before", action: loadWeekBefore)
                        .disabled(loadingMore)
                    if let since = tracker.loadedSince {
                        Text("Showing entries since \(tracker.time.longDay(tracker.time.day(of: since)))")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                .frame(maxWidth: .infinity)
            }
            .listRowBackground(Color.clear)
        }
    }

    private func row(_ entry: TimeEntry) -> some View {
        let locked = tracker.isLocked(entry.start)
        return Button {
            model.editing = EntryEditing(entry: entry)
        } label: {
            EntryRow(entry: entry, tracker: tracker, locked: locked)
                .contentShape(Rectangle())
        }
        .tint(.primary)
        .swipeActions(edge: .leading) {
            if !entry.isRunning {
                Button {
                    continueEntry(entry)
                } label: {
                    Label("Continue", systemImage: "play.fill")
                }
                .tint(.control)
            }
        }
        .swipeActions(edge: .trailing) {
            if !locked {
                Button(role: .destructive) {
                    deleting = entry
                } label: {
                    Label("Delete", systemImage: "trash")
                }
            }
        }
        .contextMenu {
            if !entry.isRunning {
                Button { continueEntry(entry) } label: { Label("Continue", systemImage: "play") }
            }
            Button { model.editing = EntryEditing(entry: entry) } label: {
                Label(locked ? "View" : "Edit", systemImage: locked ? "eye" : "pencil")
            }
            if !entry.isRunning && !locked {
                Button { duplicate(entry) } label: { Label("Duplicate", systemImage: "plus.square.on.square") }
            }
            if !locked {
                Button(role: .destructive) { deleting = entry } label: { Label("Delete", systemImage: "trash") }
            }
        }
    }

    // MARK: - Doing things

    private func start(_ details: EntryDetails) {
        let used = draft
        descriptionFocused = false
        Task {
            if await model.start(details), draft == used {
                draft = EntryDetails()
            }
        }
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

    private func delete(_ entry: TimeEntry) {
        deleting = nil
        Task {
            do {
                try await tracker.delete(entry)
                model.notices.show("Time entry deleted")
            } catch {
                model.notices.show(error, fallback: "Couldn't delete the time entry.")
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

    private var deleteTitle: String { "Delete this time entry?" }

    private var deleteMessage: String {
        guard let entry = deleting else { return "" }
        let duration = tracker.time.duration(entry.seconds(at: AppClock.now))
        return entry.description.isEmpty ? "The entry and its \(duration) will be removed." : "“\(entry.description)” and its \(duration) will be removed."
    }

    #if DEBUG
    private func applyLaunchOptions() async {
        let options = LaunchOptions.current
        if let text = options.description {
            draft.description = text
            descriptionFocused = true
        }
        guard let edit = options.edit else { return }
        // Waits for the entries, then opens what was asked for.
        for _ in 0..<20 where tracker.weeksLoaded == 0 {
            try? await Task.sleep(for: .milliseconds(250))
        }
        switch edit {
        case "new":
            model.editing = EntryEditing(newRange: ManualEntry.defaultRange(now: AppClock.now, entries: tracker.entries, time: tracker.time))
        case "running":
            model.editing = tracker.running.map { EntryEditing(entry: $0) }
        default:
            model.editing = tracker.entries.first(where: { !$0.isRunning }).map { EntryEditing(entry: $0) }
        }
    }
    #endif
}

/// "This week" and the time tracked in it.
private struct WeekTotal: View {
    let tracker: Tracker

    var body: some View {
        let week = tracker.time.week(weeksBack: 0, from: AppClock.now)
        let entries = tracker.entries.filter { week.contains($0.start) }
        HStack {
            Text("This week")
            Spacer()
            TimelineView(.periodic(from: .now, by: entries.contains(where: \.isRunning) ? 1 : 3600)) { _ in
                Text(tracker.time.duration(entries.reduce(0) { $0 + $1.seconds(at: AppClock.now) }))
                    .monospacedDigit()
            }
        }
    }
}

private struct DayHeader: View {
    let day: DayGroup
    let time: UserTime

    var body: some View {
        HStack {
            Text(time.dayHeading(day.day, now: AppClock.now))
            Spacer()
            TimelineView(.periodic(from: .now, by: day.entries.contains(where: \.isRunning) ? 1 : 3600)) { _ in
                Text(time.duration(day.totalSeconds(at: AppClock.now)))
                    .monospacedDigit()
            }
        }
    }
}

/// One entry: what it was, its project and tags, when, and for how long.
struct EntryRow: View {
    let entry: TimeEntry
    let tracker: Tracker
    let locked: Bool

    var body: some View {
        let catalog = tracker.catalog
        let project = catalog.project(entry.projectId)
        let tags = entry.tagIds.compactMap { catalog.tag($0) }
        VStack(alignment: .leading, spacing: 5) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(entry.description.isEmpty ? "No description" : entry.description)
                    .foregroundStyle(entry.description.isEmpty ? .secondary : .primary)
                    .lineLimit(2)
                Spacer(minLength: 8)
                if entry.isRunning {
                    // A running timer always counts in seconds.
                    TimelineView(.periodic(from: .now, by: 1)) { _ in
                        Text(Durations.clock(entry.seconds(at: AppClock.now)))
                            .foregroundStyle(Color.brandText)
                    }
                    .fontWeight(.semibold)
                    .monospacedDigit()
                } else {
                    Text(tracker.time.duration(entry.seconds(at: AppClock.now)))
                        .fontWeight(.medium)
                        .monospacedDigit()
                }
            }
            HStack(spacing: 8) {
                if entry.isSplit {
                    SplitLabel(catalog: catalog, parts: entry.parts)
                        .layoutPriority(1)
                } else if let project {
                    ProjectLabel(project: project, task: catalog.task(entry.taskId), client: catalog.client(project.clientId))
                        .layoutPriority(1)
                }
                if let tag = tags.first {
                    TagChip(name: tags.count > 1 ? "\(tag.name) +\(tags.count - 1)" : tag.name)
                }
                Spacer(minLength: 4)
                if locked {
                    Image(systemName: "lock.fill")
                        .accessibilityLabel("Locked")
                }
                if entry.parts.contains(where: \.isBillable) {
                    Image(systemName: "dollarsign")
                        .accessibilityLabel("Billable")
                }
                Text("\(tracker.time.clock(entry.start)) – \(entry.stop.map(tracker.time.clock) ?? "now")")
                    .monospacedDigit()
                    .fixedSize()
            }
            .font(.footnote)
            .foregroundStyle(.secondary)
        }
        .padding(.vertical, 2)
        .accessibilityElement(children: .combine)
    }
}

/// When a new entry added by hand starts and ends, unless the person changes it.
enum ManualEntry {
    /// It starts where the last entry today ended, or half an hour ago, and ends now, both on whole minutes.
    static func defaultRange(now: Date, entries: [TimeEntry], time: UserTime) -> DateInterval {
        let end = time.calendar.dateInterval(of: .minute, for: now)?.start ?? now
        let today = time.day(of: now)
        let lastStop = entries.compactMap(\.stop).filter { time.day(of: $0) == today && $0 <= end }.max()
        let start = lastStop.flatMap { end.timeIntervalSince($0) >= 60 ? $0 : nil } ?? end.addingTimeInterval(-30 * 60)
        return DateInterval(start: start, end: end)
    }
}
