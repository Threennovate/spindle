import SwiftUI
import SpindleKit

/// The card at the top of the timer: what's running and a button to stop it, or what to start next.
struct TimerCard: View {
    @Environment(AppModel.self) private var model
    let tracker: Tracker
    @Binding var draft: EntryDetails
    var focused: FocusState<Bool>.Binding
    let onStart: (EntryDetails) -> Void

    /// The running entry's description while it's being typed; saved when the field is left.
    @State private var runningText = ""
    @State private var choosingProject = false
    @State private var choosingTags = false
    @State private var editingStart = false

    var body: some View {
        let running = tracker.running
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .center, spacing: 12) {
                if let running {
                    TextField("Add description", text: $runningText, axis: .vertical)
                        .focused(focused)
                        .submitLabel(.done)
                        .onSubmit { focused.wrappedValue = false }
                        .onAppear { runningText = running.description }
                        // Follows changes made elsewhere, e.g. on another device, unless the person is typing.
                        .onChange(of: running.description) {
                            if !focused.wrappedValue { runningText = running.description }
                        }
                        .onChange(of: running.id) { runningText = running.description }
                        .onChange(of: focused.wrappedValue) { _, isFocused in
                            if !isFocused { saveRunningDescription(running) }
                        }
                } else {
                    TextField("What are you working on?", text: $draft.description, axis: .vertical)
                        .focused(focused)
                        .submitLabel(.go)
                        .onSubmit { onStart(draft) }
                }
                Spacer(minLength: 0)
                if let running {
                    Button {
                        editingStart = true
                    } label: {
                        TimelineView(.periodic(from: .now, by: 1)) { _ in
                            Text(Durations.clock(running.seconds(at: AppClock.now)))
                                .font(.title2.weight(.semibold).monospacedDigit())
                                .foregroundStyle(.primary)
                        }
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Running time")
                    .accessibilityHint("Change when the timer started")
                    Button {
                        focused.wrappedValue = false
                        Task { await model.stop() }
                    } label: {
                        Image(systemName: "stop.fill")
                            .font(.system(size: 20, weight: .bold))
                            .foregroundStyle(.white)
                            .frame(width: 52, height: 52)
                            .background(Color.red, in: Circle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Stop timer")
                } else {
                    StartButton { onStart(draft) }
                        .disabled(model.readOnlyOrganization != nil)
                        .opacity(model.readOnlyOrganization != nil ? 0.4 : 1)
                }
            }
            .font(.body)

            HStack(spacing: 8) {
                fieldChips
                Spacer(minLength: 0)
                PomodoroChip(running: running)
            }
        }
        .padding(.vertical, 6)
        .sheet(isPresented: $choosingProject) {
            PickerSheet {
                ProjectPickerView(tracker: tracker, projectId: fields.projectId, taskId: fields.taskId, onChange: fields.setProject)
            }
        }
        .sheet(isPresented: $choosingTags) {
            PickerSheet {
                TagPickerView(tracker: tracker, tagIds: fields.tagIds, onChange: fields.setTags)
            }
        }
        .sheet(isPresented: $editingStart) {
            if let running {
                StartTimeSheet(entry: running, time: tracker.time) { start in
                    saveRunning { $0.start = start }
                }
            }
        }
        #if DEBUG
        .task {
            try? await Task.sleep(for: .seconds(1))
            choosingProject = LaunchOptions.current.picker == "project"
            choosingTags = LaunchOptions.current.picker == "tags"
        }
        #endif
    }

    @ViewBuilder private var fieldChips: some View {
        let fields = fields
        let project = tracker.catalog.project(fields.projectId)
        Button {
            choosingProject = true
        } label: {
            if let project {
                ProjectLabel(project: project, task: tracker.catalog.task(fields.taskId))
                    .font(.subheadline)
            } else {
                Label("Project", systemImage: "folder")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
        }
        .buttonStyle(ChipButtonStyle())
        .accessibilityLabel(project.map { "Project: \($0.name)" } ?? "Choose project")

        let tagNames = fields.tagIds.compactMap { tracker.catalog.tag($0)?.name }.joined(separator: ", ")
        Button {
            choosingTags = true
        } label: {
            if fields.tagIds.isEmpty {
                Image(systemName: "tag")
                    .foregroundStyle(.secondary)
            } else {
                Label(tagNames, systemImage: "tag.fill")
                    .font(.subheadline)
                    .foregroundStyle(Color.brandText)
            }
        }
        .buttonStyle(ChipButtonStyle())
        .accessibilityLabel(fields.tagIds.isEmpty ? "Choose tags" : "Tags: \(tagNames)")

        Button {
            fields.setBillable(!fields.isBillable)
        } label: {
            Image(systemName: "dollarsign")
                .fontWeight(fields.isBillable ? .bold : .regular)
                .foregroundStyle(fields.isBillable ? Color.brandText : .secondary)
        }
        .buttonStyle(ChipButtonStyle())
        .accessibilityLabel("Billable")
        .accessibilityValue(fields.isBillable ? "On" : "Off")
    }

    /// What the chips change: the running entry, or the draft of the next one.
    private var fields: EntryFields {
        if let running = tracker.running {
            return EntryFields(
                projectId: running.projectId,
                taskId: running.taskId,
                tagIds: running.tagIds,
                isBillable: running.isBillable,
                setProject: { projectId, taskId in
                    saveRunning {
                        $0.details.projectId = projectId
                        $0.details.taskId = taskId
                        $0.details.isBillable = tracker.catalog.project(projectId)?.isBillable ?? running.isBillable
                        // One project chosen ends a split.
                        $0.details.shares = []
                    }
                },
                setTags: { tagIds in saveRunning { $0.details.tagIds = tagIds } },
                setBillable: { isBillable in
                    saveRunning {
                        $0.details.isBillable = isBillable
                        for index in $0.details.shares.indices { $0.details.shares[index].isBillable = isBillable }
                    }
                })
        }
        return EntryFields(
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

    private func saveRunningDescription(_ running: TimeEntry) {
        let trimmed = runningText.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed != running.description {
            saveRunning { $0.details.description = trimmed }
        }
    }

    private func saveRunning(_ edit: @escaping (inout EntryRequest) -> Void) {
        Task {
            do {
                try await tracker.updateRunning(edit)
            } catch {
                model.notices.show(error, fallback: "Couldn't save the running entry.")
            }
        }
    }
}

/// An entry's project, tags and billable flag, and how to change them.
struct EntryFields {
    var projectId: String?
    var taskId: String?
    var tagIds: [String]
    var isBillable: Bool
    var setProject: (String?, String?) -> Void
    var setTags: ([String]) -> Void
    var setBillable: (Bool) -> Void
}

/// The round play button, in marigold with ink on it.
struct StartButton: View {
    var size: CGFloat = 52
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: "play.fill")
                .font(.system(size: size * 0.38, weight: .bold))
                .foregroundStyle(Color.ink)
                .frame(width: size, height: size)
                .background(Color.brand, in: Circle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Start timer")
    }
}

/// A field's value in a small capsule, tapped to change it.
struct ChipButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .lineLimit(1)
            .padding(.horizontal, 10)
            .frame(minWidth: 36, minHeight: 32)
            .background(Color(.tertiarySystemFill).opacity(configuration.isPressed ? 2 : 1), in: Capsule())
            .contentShape(Capsule())
    }
}

/// The Pomodoro's countdown: the focus left in this interval.
private struct PomodoroChip: View {
    @Environment(AppModel.self) private var model
    let running: TimeEntry?

    var body: some View {
        if let preferences = model.me?.preferences, preferences.pomodoroEnabled, let running {
            TimelineView(.periodic(from: .now, by: 1)) { _ in
                let left = Pomodoro.focusLeft(elapsed: AppClock.now.timeIntervalSince(running.start), focusMinutes: Int(preferences.pomodoroMinutes))
                Label(Pomodoro.countdown(left), systemImage: "timer")
                    .font(.subheadline.monospacedDigit())
                    .foregroundStyle(.secondary)
                    .accessibilityLabel("Focus left in this interval: \(Pomodoro.countdown(left))")
            }
        }
    }
}

/// Corrects when the running timer started, e.g. after forgetting to start it.
private struct StartTimeSheet: View {
    @Environment(\.dismiss) private var dismiss
    let entry: TimeEntry
    let time: UserTime
    let onSave: (Date) -> Void
    @State private var start = AppClock.now

    var body: some View {
        NavigationStack {
            Form {
                DatePicker("Started", selection: $start, in: ...AppClock.now, displayedComponents: [.date, .hourAndMinute])
                    .datePickerStyle(.graphical)
                    .accountTime(time)
            }
            .navigationTitle("Started at")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Save") {
                        onSave(start)
                        dismiss()
                    }
                }
            }
            .onAppear { start = entry.start }
        }
        .presentationDetents([.large])
    }
}
