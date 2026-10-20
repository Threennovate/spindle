import SwiftUI
import SpindleKit

enum EntryMode: String {
    case timer, manual
}

/// The bar at the top of the timer: start and stop the timer, or add time manually.
struct TimerBar: View {
    @Environment(AppModel.self) private var model
    @Environment(Desktop.self) private var desktop
    let tracker: Tracker

    @AppStorage("entryMode") private var mode = EntryMode.timer
    @State private var draft = EntryDetails()
    @State private var descriptionFocused = false

    var body: some View {
        let _ = Performance.event("Timer bar drawn")
        HStack(spacing: 10) {
            if let running = tracker.running {
                RunningDescription(entry: running, isFocused: $descriptionFocused) { description in
                    save { $0.details.description = description }
                }
            } else {
                DescriptionField(
                    tracker: tracker,
                    text: $draft.description,
                    isFocused: $descriptionFocused,
                    onSubmit: {
                        if mode == .timer { start(draft) }
                    },
                    onPick: { suggestion in start(suggestion.details) })
            }

            EntryFieldButtons(tracker: tracker, fields: fields)
            if let running = tracker.running, Date().timeIntervalSince(running.start) < SplitAdvisor.earlierStartFor {
                // Redrawn each minute, so it goes away on its own.
                TimelineView(.everyMinute) { context in
                    if let offer = desktop.splits.earlierStart(for: running, tracker: tracker, now: context.date) {
                        EarlierStartChip(offer: offer, tracker: tracker)
                    }
                }
            }
            if let running = tracker.running, let suggestion = desktop.splits.suggestion(for: running, tracker: tracker) {
                SplitSuggestionChip(catalog: tracker.catalog, time: tracker.time, suggestion: suggestion, start: running.start, stop: nil) {
                    model.editing = SplitAdvisor.editing(running, as: suggestion, catalog: tracker.catalog)
                }
            } else if let running = tracker.running, let drift = desktop.splits.drift(for: running, tracker: tracker) {
                DriftChip(offer: drift, tracker: tracker)
            }

            PomodoroStatus(running: tracker.running)
            if let running = tracker.running {
                RunningDuration(entry: running, tracker: tracker) { start in
                    try await tracker.updateRunning { $0.start = start }
                }
                Button {
                    Task { await model.stop() }
                } label: {
                    Image(systemName: "stop.fill")
                        .font(.system(size: 14, weight: .bold))
                        .foregroundStyle(.white)
                        .frame(width: 38, height: 38)
                        .background(Color.red, in: Circle())
                }
                .buttonStyle(.plain)
                .help("Stop timer")
                .accessibilityLabel("Stop timer")
            } else if mode == .manual {
                ManualTimes(tracker: tracker, draft: draft) { used in
                    clear(used)
                }
            } else {
                Text("0:00:00")
                    .font(.system(size: 17, weight: .semibold).monospacedDigit())
                    .foregroundStyle(.secondary)
                    .frame(width: 86, alignment: .trailing)
                Button {
                    start(draft)
                } label: {
                    Image(systemName: "play.fill")
                        .font(.system(size: 14, weight: .bold))
                        .foregroundStyle(Color.ink)
                        .frame(width: 38, height: 38)
                        .background(Color.brand, in: Circle())
                }
                .buttonStyle(.plain)
                .disabled(model.readOnlyOrganization != nil)
                .opacity(model.readOnlyOrganization != nil ? 0.4 : 1)
                .help(model.readOnlyOrganization != nil ? APIError.readOnlyMessage : "Start timer")
                .accessibilityLabel("Start timer")
            }

            if tracker.running == nil {
                Picker("Entry mode", selection: $mode) {
                    Image(systemName: "timer").help("Timer").tag(EntryMode.timer)
                    Image(systemName: "text.badge.plus").help("Add time manually").tag(EntryMode.manual)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
            }
        }
        .padding(.leading, 14)
        .padding(.trailing, 10)
        .padding(.vertical, 9)
        .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.separator))
        .shadow(color: .black.opacity(0.04), radius: 2, y: 1)
        #if DEBUG
        .task {
            if let text = SnapshotRun.current?.description, tracker.running == nil {
                draft.description = text
                descriptionFocused = true
            }
        }
        #endif
        .onChange(of: model.wantsDescriptionFocus, initial: true) {
            if model.wantsDescriptionFocus {
                descriptionFocused = true
                model.wantsDescriptionFocus = false
            }
        }
    }

    /// The fields the buttons change: the running entry's, or the draft's for the next one.
    private var fields: EntryFieldButtons.Fields {
        if let running = tracker.running {
            return .init(
                projectId: running.projectId,
                taskId: running.taskId,
                tagIds: running.tagIds,
                isBillable: running.parts.contains(where: \.isBillable),
                setProject: { projectId, taskId in
                    save {
                        // One project chosen ends a split.
                        $0.details.projectId = projectId
                        $0.details.taskId = taskId
                        $0.details.isBillable = tracker.catalog.project(projectId)?.isBillable ?? running.isBillable
                        $0.details.shares = []
                    }
                },
                setTags: { tagIds in save { $0.details.tagIds = tagIds } },
                // Billable applies to every part of a split entry.
                setBillable: { isBillable in
                    save {
                        $0.details.isBillable = isBillable
                        for index in $0.details.shares.indices { $0.details.shares[index].isBillable = isBillable }
                    }
                },
                parts: running.isSplit ? running.parts : nil,
                onEditSplit: { model.editing = EntryEditing(entry: running) },
                onSplit: { model.editing = EntryEditing(entry: running, startSplit: true) })
        }
        return .init(
            projectId: draft.projectId,
            taskId: draft.taskId,
            tagIds: draft.tagIds,
            isBillable: draft.isBillable ?? tracker.catalog.project(draft.projectId)?.isBillable ?? false,
            setProject: { projectId, taskId in
                draft.projectId = projectId
                draft.taskId = taskId
                draft.isBillable = nil
            },
            setTags: { draft.tagIds = $0 },
            setBillable: { draft.isBillable = $0 })
    }

    private func start(_ details: EntryDetails) {
        let used = draft
        Task {
            if await model.start(details) {
                clear(used)
            }
        }
    }

    /// Clears the draft after it was used, unless it was changed while the request was on its way.
    private func clear(_ used: EntryDetails) {
        if draft == used {
            draft = EntryDetails()
        }
    }

    private func save(_ edit: @escaping (inout EntryRequest) -> Void) {
        Task {
            do {
                try await tracker.updateRunning(edit)
            } catch {
                model.notices.show(error, fallback: "Couldn't save the running entry.")
            }
        }
    }
}

/// The project, tags and billable buttons of an entry.
struct EntryFieldButtons: View {
    struct Fields {
        var projectId: String?
        var taskId: String?
        var tagIds: [String]
        var isBillable: Bool
        var setProject: (String?, String?) -> Void
        var setTags: ([String]) -> Void
        var setBillable: (Bool) -> Void
        /// A split entry's parts, shown instead of one project; changing them opens the entry.
        var parts: [SplitPart]?
        var onEditSplit: (() -> Void)?
        /// Offers to split the time between projects from the project picker.
        var onSplit: (() -> Void)?
    }

    let tracker: Tracker
    let fields: Fields
    @State private var choosingProject = false
    @State private var choosingTags = false

    var body: some View {
        let project = tracker.catalog.project(fields.projectId)
        HStack(spacing: 4) {
            if let parts = fields.parts {
                Button {
                    fields.onEditSplit?()
                } label: {
                    SplitLabel(catalog: tracker.catalog, parts: parts)
                        .frame(maxWidth: 260, alignment: .leading)
                }
                .buttonStyle(FieldButtonStyle())
                .help("Split: \(tracker.catalog.describe(parts)). Click to change it.")
            } else {
            Button {
                choosingProject = true
            } label: {
                if let project {
                    ProjectLabel(project: project, task: tracker.catalog.task(fields.taskId))
                        .frame(maxWidth: 220, alignment: .leading)
                } else {
                    Image(systemName: "folder")
                        .foregroundStyle(.secondary)
                }
            }
            .buttonStyle(FieldButtonStyle())
            .help(project.map { "Project: \($0.name)" } ?? "Choose project")
            .accessibilityLabel(project.map { "Project: \($0.name)" } ?? "Choose project")
            .popover(isPresented: $choosingProject, arrowEdge: .bottom) {
                ProjectPicker(
                    tracker: tracker, projectId: fields.projectId, taskId: fields.taskId, onChange: fields.setProject,
                    onSplit: fields.onSplit)
            }
            }

            Button {
                choosingTags = true
            } label: {
                if fields.tagIds.isEmpty {
                    Image(systemName: "tag")
                        .foregroundStyle(.secondary)
                } else {
                    Label(tagNames, systemImage: "tag.fill")
                        .lineLimit(1)
                        .frame(maxWidth: 160)
                        .foregroundStyle(Color.brandText)
                }
            }
            .buttonStyle(FieldButtonStyle())
            .help(fields.tagIds.isEmpty ? "Choose tags" : "Tags: \(tagNames)")
            .accessibilityLabel(fields.tagIds.isEmpty ? "Choose tags" : "Tags: \(tagNames)")
            .popover(isPresented: $choosingTags, arrowEdge: .bottom) {
                TagPicker(tracker: tracker, tagIds: fields.tagIds, onChange: fields.setTags)
            }

            Button {
                fields.setBillable(!fields.isBillable)
            } label: {
                Image(systemName: "dollarsign")
                    .fontWeight(fields.isBillable ? .bold : .regular)
                    .foregroundStyle(fields.isBillable ? Color.brandText : .secondary)
            }
            .buttonStyle(FieldButtonStyle())
            .help(fields.isBillable ? "Billable" : "Non-billable")
            .accessibilityLabel("Billable")
            .accessibilityValue(fields.isBillable ? "On" : "Off")
        }
        #if DEBUG
        .task {
            try? await Task.sleep(for: .seconds(1))
            choosingProject = snapshotPicker == "project"
            choosingTags = snapshotPicker == "tags"
        }
        #endif
    }

    #if DEBUG
    private var snapshotPicker: String? { SnapshotRun.current?.picker }
    #endif

    private var tagNames: String {
        fields.tagIds.compactMap { tracker.catalog.tag($0)?.name }.joined(separator: ", ")
    }
}

/// A quiet button that shows a field's value, with a background on hover.
struct FieldButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        HoverableLabel(configuration: configuration)
    }

    private struct HoverableLabel: View {
        let configuration: Configuration
        @State private var hovering = false

        var body: some View {
            configuration.label
                .font(.callout)
                .padding(.horizontal, 8)
                .frame(minWidth: 30, minHeight: 30)
                .background(
                    configuration.isPressed ? Color.primary.opacity(0.12) : hovering ? Color.primary.opacity(0.06) : .clear,
                    in: RoundedRectangle(cornerRadius: 7))
                .contentShape(Rectangle())
                .onHover { hovering = $0 }
        }
    }
}

/// The running entry's description, saved when the field is left or Return is pressed.
private struct RunningDescription: View {
    let entry: TimeEntry
    @Binding var isFocused: Bool
    let onSave: (String) -> Void
    @State private var text = ""

    var body: some View {
        CommandTextField(
            text: $text,
            placeholder: "Add description",
            font: .systemFont(ofSize: 15),
            isFocused: $isFocused,
            onCommand: { command in
                if command == .submit || command == .cancel {
                    if command == .cancel { text = entry.description }
                    isFocused = false
                    return true
                }
                return false
            },
            onEndEditing: save)
            .frame(maxWidth: .infinity, minHeight: 24)
            .accessibilityLabel("Description")
            .onAppear { text = entry.description }
            // Follows changes made elsewhere, e.g. on another device, unless the person is typing.
            .onChange(of: entry.description) {
                if !isFocused { text = entry.description }
            }
            .onChange(of: entry.id) {
                text = entry.description
            }
    }

    private func save() {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed != entry.description {
            onSave(trimmed)
        }
    }
}

/// The ticking duration of the running timer. Clicking it lets the person correct when the timer started.
private struct RunningDuration: View {
    let entry: TimeEntry
    let tracker: Tracker
    let onChangeStart: (Date) async throws -> Void
    @State private var editing = false

    var body: some View {
        Button {
            editing = true
        } label: {
            TimelineView(.periodic(from: .now, by: 1)) { context in
                Text(Durations.clock(entry.seconds(at: context.date)))
                    .font(.system(size: 17, weight: .semibold).monospacedDigit())
                    .frame(width: 86, alignment: .trailing)
            }
        }
        .buttonStyle(FieldButtonStyle())
        .help("Change start time")
        .accessibilityLabel("Change start time")
        .popover(isPresented: $editing, arrowEdge: .bottom) {
            RunningStart(entry: entry, tracker: tracker, onSave: onChangeStart) {
                editing = false
            }
        }
    }
}

/// The Pomodoro's countdown: the focus left in this interval, or the break left.
private struct PomodoroStatus: View {
    @Environment(AppModel.self) private var model
    @Environment(Desktop.self) private var desktop
    let running: TimeEntry?

    var body: some View {
        if let preferences = model.me?.preferences, preferences.pomodoroEnabled {
            TimelineView(.periodic(from: .now, by: 1)) { context in
                if let running {
                    let left = Pomodoro.focusLeft(elapsed: context.date.timeIntervalSince(running.start), focusMinutes: Int(preferences.pomodoroMinutes))
                    chip("timer", Pomodoro.countdown(left), help: "Focus left in this interval")
                } else if let ends = desktop.breakEnds, ends > context.date {
                    chip("cup.and.saucer", "Break \(Pomodoro.countdown(ends.timeIntervalSince(context.date)))", help: "Break left")
                }
            }
        }
    }

    private func chip(_ symbol: String, _ text: String, help: String) -> some View {
        Label(text, systemImage: symbol)
            .font(.callout.monospacedDigit())
            .foregroundStyle(.secondary)
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(.quaternary.opacity(0.6), in: Capsule())
            .help(help)
            .accessibilityLabel("\(help): \(text)")
    }
}

