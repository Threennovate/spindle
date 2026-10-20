import SwiftUI
import SpindleKit

/// Edits an entry, or adds one for a stretch of time. Entries on locked days are shown, not edited.
struct EntryEditor: View {
    @Environment(AppModel.self) private var model
    @Environment(Desktop.self) private var desktop
    @Environment(\.dismiss) private var dismiss
    let tracker: Tracker
    let editing: EntryEditing

    @State private var description = ""
    @State private var projectId: String?
    @State private var taskId: String?
    @State private var tagIds: [String] = []
    @State private var isBillable = false
    /// Two or more parts while the time is split between projects; nil while it all goes to one.
    @State private var parts: [SplitPart]?
    /// Two or more stretches while the time is to be saved as separate entries, one after another.
    @State private var stretches: [TimeStretch]?
    @State private var choosingSplit = false
    @State private var start = Date()
    @State private var end: Date?
    @State private var startText = ""
    @State private var endText = ""
    @State private var durationText: String?
    @State private var errors = FormErrors()
    @State private var saving = false
    @State private var confirmingDelete = false
    @State private var choosingProject = false
    @State private var choosingTags = false
    @FocusState private var focus: Field?
    /// The part whose own description has the keyboard, by position.
    @FocusState private var partFocus: Int?

    private enum Field: Hashable { case description, duration }

    private static let unreadableTime = "Enter a time such as 930 or 9:30."

    private var entry: TimeEntry? { editing.entry }
    private var running: Bool { entry?.isRunning == true }
    private var stopping: Bool { editing.stopping && running }
    private var locked: Bool { entry.map { tracker.isLocked($0.start) } ?? false }
    private var time: UserTime { tracker.time }
    private var rules: EntryRules? { tracker.rules }
    private var project: Project? { tracker.catalog.project(projectId) }
    private var day: DayKey { time.day(of: start) }
    /// Whether each part of the split has a description of its own, in place of the entry's one.
    private var perPart: Bool { parts.map(SplitPart.describesEach) ?? false }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(.title2.weight(.semibold))
                if stopping {
                    Text("Add the details this workspace requires, then stop the timer.")
                        .foregroundStyle(.secondary)
                }
            }

            if locked, let rules {
                FormBanner(text: rules.lockMessage(time), kind: .notice)
            }
            if let form = errors.form {
                FormBanner(text: form)
            }

            Group {
                if !perPart {
                    VStack(alignment: .leading, spacing: 6) {
                        FormField(label: label("Description", required: rules?.settings.requireDescription == true), error: errors["description"]) {
                            TextField("Add description", text: $description)
                                .focused($focus, equals: .description)
                                .descriptionSuggestions(tracker, text: description, projectId: projectId)
                        }
                        // One description on a split shows on every project's reports and timesheet links, so it says so.
                        if let parts, !locked {
                            DescriptionReach(projects: parts.count, onDescribePerProject: describePerPart)
                        }
                    }
                }

                if let suggestion, !locked, stretches == nil {
                    SplitSuggestionBanner(
                        tracker: tracker, suggestion: suggestion,
                        onApply: { parts = $0 },
                        onSeparate: { stretches = $0; parts = nil },
                        onDismiss: entry.map { entry in { desktop.splits.dismiss(entry.id, workspaceId: entry.workspaceId) } })
                }

                if let stretches {
                    FormField(label: "Separate entries", error: errors["stretches"]) {
                        StretchList(tracker: tracker, stretches: stretches, start: start, end: end) {
                            self.stretches = nil
                        }
                    }
                }

                if let partsBinding {
                    FormField(label: label(
                        perPart ? "Split and described per project" : "Split between projects",
                        required: rules?.settings.requireProject == true || (perPart && rules?.settings.requireDescription == true))
                    ) {
                        SplitEditor(
                            tracker: tracker, parts: partsBinding,
                            seconds: (end ?? Date()).timeIntervalSince(start), running: end == nil,
                            errors: SplitEditor.partErrors(errors), splitError: errors["shares"],
                            descriptionFocus: $partFocus, onDescribeAllAsOne: describeAllAsOne)
                    }
                    .onChange(of: parts?.count) { _, count in
                        if let count, count < 2 { endSplit() }
                    }
                }

                HStack(alignment: .top, spacing: 16) {
                    if parts == nil && stretches == nil {
                    FormField(
                        label: label(taskRequired ? "Project and task" : "Project", required: rules?.settings.requireProject == true || taskRequired),
                        error: errors["projectId"] ?? errors["taskId"]
                    ) {
                        pickerButton(open: $choosingProject) {
                            if let project {
                                ProjectLabel(project: project, task: tracker.catalog.task(taskId))
                            } else {
                                Text("No project").foregroundStyle(.secondary)
                            }
                        }
                        .accessibilityLabel(project.map { "Project: \($0.name)" } ?? "Choose project")
                        .popover(isPresented: $choosingProject, arrowEdge: .bottom) {
                            ProjectPicker(tracker: tracker, projectId: projectId, taskId: taskId, onChange: { newProject, newTask in
                                projectId = newProject
                                taskId = newTask
                                isBillable = tracker.catalog.project(newProject)?.isBillable ?? isBillable
                            }, onSplit: { choosingSplit = true })
                        }
                        .overlay(alignment: .topTrailing) {
                            if !locked {
                                Button {
                                    choosingSplit = true
                                } label: {
                                    Label("Split", systemImage: "arrow.triangle.branch")
                                        .font(.caption)
                                }
                                .buttonStyle(.borderless)
                                .foregroundStyle(.secondary)
                                .help("Split this time between projects")
                                .offset(y: -22)
                                .popover(isPresented: $choosingSplit, arrowEdge: .bottom) {
                                    ProjectPicker(
                                        tracker: tracker, projectId: nil, taskId: nil, onChange: beginSplit,
                                        taken: { $0 == projectId && $1 == taskId }, choosingAnew: true,
                                        placeholder: "Split the time with which project?")
                                }
                            }
                        }
                    }
                    }
                    FormField(label: label("Tags", required: rules?.settings.requireTag == true), error: errors["tagIds"]) {
                        pickerButton(open: $choosingTags) {
                            if tagIds.isEmpty {
                                Text("No tags").foregroundStyle(.secondary)
                            } else {
                                HStack(spacing: 4) {
                                    ForEach(tagIds, id: \.self) { id in
                                        TagChip(name: tracker.catalog.tag(id)?.name ?? "…")
                                    }
                                }
                            }
                        }
                        .accessibilityLabel("Choose tags")
                        .popover(isPresented: $choosingTags, arrowEdge: .bottom) {
                            TagPicker(tracker: tracker, tagIds: tagIds) { tagIds = $0 }
                        }
                    }
                }

                if parts == nil && stretches == nil {
                    Toggle("Billable", isOn: $isBillable)
                        .toggleStyle(.switch)
                }

                HStack(alignment: .top, spacing: 10) {
                    FormField(label: "Date") {
                        DayPicker(day: Binding(get: { day }, set: { changeDay($0) }), time: time, earliest: locked ? nil : rules?.firstOpenDay)
                    }
                    FormField(label: "Start") {
                        ClockField(text: $startText, time: time, onCommit: { _ in commitStart() })
                            .accessibilityLabel("Start")
                            .frame(width: time.hour12 ? 84 : 70)
                    }
                    FormField(label: "End") {
                        if end == nil {
                            TextField("", text: .constant("Running"))
                                .disabled(true)
                                .frame(width: time.hour12 ? 84 : 70)
                        } else {
                            ClockField(text: $endText, time: time, onCommit: { _ in commitEnd() })
                                .accessibilityLabel("End")
                                .frame(width: time.hour12 ? 84 : 70)
                        }
                    }
                    FormField(label: "Duration") {
                        TimelineView(.periodic(from: .now, by: running ? 1 : 3600)) { context in
                            TextField("", text: Binding(
                                get: { durationText ?? Durations.clock((end ?? context.date).timeIntervalSince(start)) },
                                set: { durationText = $0 }))
                                .focused($focus, equals: .duration)
                                .onSubmit(commitDuration)
                                .disabled(end == nil)
                                .font(.body.monospacedDigit())
                                .frame(width: 90)
                        }
                    }
                }
                if let message = errors["start"] ?? errors["stop"] {
                    Text(message)
                        .font(.callout)
                        .foregroundStyle(.red)
                        .padding(.top, -10)
                }
            }
            .disabled(locked)

            buttons
        }
        .padding(24)
        .frame(width: 600)
        .onAppear(perform: fill)
        .task(id: desktop.splits.request([time.day(of: start), time.day(of: end ?? Date())], time: time)) {
            let days = Set([time.day(of: start), time.day(of: end ?? Date())])
            await desktop.splits.load(Array(days), tracker: tracker)
        }
        .onChange(of: focus) { old, _ in
            if old == .duration {
                commitDuration()
            }
        }
        .confirmationDialog("Delete this time entry?", isPresented: $confirmingDelete) {
            Button("Delete", role: .destructive, action: delete)
        } message: {
            Text(entry.map { $0.description.isEmpty ? "The entry" : "“\($0.description)”" }.map { "\($0) and its \(Durations.clock((end ?? Date()).timeIntervalSince(start))) will be removed." } ?? "")
        }
    }

    @ViewBuilder private var buttons: some View {
        if locked {
            HStack {
                Spacer()
                Button("Done") { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
        } else {
            HStack {
                if entry != nil {
                    Button(role: .destructive) {
                        confirmingDelete = true
                    } label: {
                        Label("Delete", systemImage: "trash")
                    }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.red)
                }
                Spacer()
                if running && !stopping {
                    Button("Stop Timer") { save(stopNow: true) }
                        .disabled(saving)
                }
                Button(stopping ? "Keep Running" : "Cancel") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(entry == nil ? "Add Entry" : stopping ? "Save and Stop" : "Save") {
                    save(stopNow: stopping)
                }
                .buttonStyle(.brand)
                .keyboardShortcut(.defaultAction)
                .disabled(saving)
            }
        }
    }

    private var title: String {
        guard let entry else { return "New time entry" }
        if locked { return "Time entry" }
        if stopping { return "Stop timer" }
        return entry.isRunning ? "Edit running entry" : "Edit time entry"
    }

    private var partsBinding: Binding<[SplitPart]>? {
        guard parts != nil else { return nil }
        return Binding(get: { parts ?? [] }, set: { parts = $0 })
    }

    /// What agent activity suggests for this time, unless the split already shows it.
    private var suggestion: SplitSuggestion? {
        // The time the logs don't account for stays where the entry is tracked: its project, or its largest part.
        let tracked = parts.flatMap { $0.max(by: { $0.percent < $1.percent })?.projectId } ?? projectId
        guard let suggestion = desktop.splits.suggestion(from: start, to: end ?? Date(), on: tracked, tracker: tracker) else { return nil }
        if let entry, desktop.splits.dismissed.contains(entry.id) { return nil }
        let current = parts ?? [SplitPart(projectId: projectId, taskId: taskId, isBillable: isBillable, percent: 100)]
        let shown = Dictionary(current.map { ($0.projectId ?? "", $0.percent) }, uniquingKeysWith: +)
        let matches = shown.count == suggestion.parts.count && suggestion.parts.allSatisfy { part in
            guard case .project(let id) = part.target, let percent = shown[id] else { return false }
            return abs(percent - part.percent) <= 5
        }
        return matches ? nil : suggestion
    }

    /// Begins a split: what was chosen keeps half the time, the project picked gets the other half.
    private func beginSplit(_ newProject: String?, _ newTask: String?) {
        parts = [
            SplitPart(projectId: projectId, taskId: taskId, isBillable: isBillable, percent: 50),
            SplitPart(projectId: newProject, taskId: newTask, isBillable: tracker.catalog.project(newProject)?.isBillable ?? false, percent: 50),
        ]
    }

    /// A split left with one part is that part's project again, described as that part was.
    private func endSplit() {
        if let only = parts?.first {
            projectId = only.projectId
            taskId = only.taskId
            isBillable = only.isBillable
            if let own = only.description {
                description = own
                focusSoon(.description)
            }
        }
        parts = nil
    }

    /// Each project gets its own description, and the first one still to be written is ready for typing.
    private func describePerPart() {
        guard let current = parts else { return }
        let described = SplitPart.describingEach(current, from: description)
        parts = described
        let next = described.firstIndex { $0.description?.isEmpty != false } ?? 0
        // The field is there only once the change is drawn.
        DispatchQueue.main.async { partFocus = next }
    }

    /// One description for all parts again: theirs joined.
    private func describeAllAsOne() {
        guard let current = parts else { return }
        let shared = SplitPart.sharingOneDescription(current)
        parts = shared.parts
        description = shared.description
        errors.fields = errors.fields.filter { !$0.key.hasSuffix("].description") }
        focusSoon(.description)
    }

    private func focusSoon(_ field: Field) {
        DispatchQueue.main.async { focus = field }
    }

    private var taskRequired: Bool {
        rules?.requiresTask(on: project) ?? false
    }

    private func label(_ text: String, required: Bool) -> String {
        required && !locked ? "\(text) (required)" : text
    }

    private func pickerButton(open: Binding<Bool>, @ViewBuilder label: () -> some View) -> some View {
        Button {
            open.wrappedValue = true
        } label: {
            HStack {
                label()
                    .lineLimit(1)
                Spacer(minLength: 4)
                Image(systemName: "chevron.up.chevron.down")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 8)
            .frame(maxWidth: .infinity, minHeight: 28)
            .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
            .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(.separator))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private func fill() {
        if let entry {
            description = entry.description
            projectId = entry.projectId
            taskId = entry.taskId
            tagIds = entry.tagIds
            isBillable = entry.isBillable
            parts = entry.isSplit ? entry.parts : nil
            show(entry.start, entry.stop)
            if stopping, let rules {
                errors = FormErrors(missing: rules.missingDetails(entry.details, project: tracker.catalog.project(entry.projectId)))
                if entry.isSplit {
                    errors.fields.merge(SplitEditor.errorFields(rules.missingInParts(entry.parts, project: tracker.catalog.project))) { $1 }
                }
            }
        } else if let range = editing.newRange {
            show(range.start, range.end)
            if let details = editing.details {
                description = details.description
                projectId = details.projectId
                taskId = details.taskId
                tagIds = details.tagIds
                isBillable = details.isBillable ?? tracker.catalog.project(details.projectId)?.isBillable ?? false
            }
        }
        if let proposed = editing.proposed {
            let details = proposed.details
            description = details.description
            projectId = details.projectId
            taskId = details.taskId
            tagIds = details.tagIds
            isBillable = details.isBillable ?? tracker.catalog.project(details.projectId)?.isBillable ?? false
            parts = details.isSplit ? details.shares : nil
            show(proposed.start, proposed.stop)
        }
        if let split = editing.split, split.count > 1 {
            parts = split
        }
        if let separate = editing.stretches, separate.count > 1 {
            stretches = separate
            parts = nil
        }
        if let parts, perPart {
            // The first part still to be described, as the entry's own description isn't shown.
            partFocus = parts.firstIndex { $0.description?.isEmpty != false } ?? 0
        } else {
            focus = .description
        }
        if editing.startSplit && parts == nil {
            choosingSplit = true
        }
    }

    private func show(_ newStart: Date, _ newEnd: Date?) {
        start = newStart
        end = newEnd
        startText = time.clockField(newStart)
        endText = newEnd.map(time.clockField) ?? ""
    }

    /// The start a typed time gives, on the entry's day; the start as it was if the time wasn't changed.
    private func startFrom(_ text: String) -> Date? {
        if text == time.clockField(start) { return start }
        return time.readClockField(text).map { time.date(day, hours: $0.hours, minutes: $0.minutes) }
    }

    /// The end a typed time gives; an end before the start means the entry ran past midnight.
    private func endFrom(_ text: String, start: Date) -> Date? {
        if let end, text == time.clockField(end) { return end }
        guard let clock = time.readClockField(text) else { return nil }
        let newEnd = time.date(day, hours: clock.hours, minutes: clock.minutes)
        return newEnd < start ? time.date(time.adding(1, to: day), hours: clock.hours, minutes: clock.minutes) : newEnd
    }

    /// A new start keeps the end where it is, so the duration changes. Each field only rewrites itself, so a time
    /// that can't be read stays there to be corrected.
    private func commitStart() {
        guard let newStart = startFrom(startText) else {
            errors["start"] = Self.unreadableTime
            return
        }
        errors["start"] = nil
        start = newStart
        startText = time.clockField(newStart)
    }

    private func commitEnd() {
        guard end != nil else { return }
        guard let newEnd = endFrom(endText, start: start) else {
            errors["stop"] = Self.unreadableTime
            return
        }
        errors["stop"] = nil
        end = newEnd
        endText = time.clockField(newEnd)
    }

    private func commitDuration() {
        if let text = durationText, let seconds = Durations.parse(text), end != nil {
            end = start.addingTimeInterval(TimeInterval(seconds))
            endText = time.clockField(end!)
        }
        durationText = nil
    }

    /// Moving to another day keeps the clock times, counted in calendar days.
    private func changeDay(_ newDay: DayKey) {
        let offset = time.days(from: day, to: newDay)
        let startClock = time.clockTime(of: start)
        let newStart = time.date(newDay, hours: startClock.hours, minutes: startClock.minutes)
        var newEnd: Date?
        if let end {
            let endClock = time.clockTime(of: end)
            newEnd = time.date(time.adding(offset, to: time.day(of: end)), hours: endClock.hours, minutes: endClock.minutes)
        }
        show(newStart, newEnd)
    }

    /// Saves the entry; `stopNow` also stops a running timer, keeping the changes made here.
    private func save(stopNow: Bool) {
        commitStart()
        commitEnd()
        commitDuration()
        if errors["start"] == Self.unreadableTime || errors["stop"] == Self.unreadableTime { return }
        let stop = stopNow ? (editing.stopAt ?? Date()) : end
        if let stop, stop < start {
            errors = FormErrors(fields: ["stop": "The end must not be before the start."])
            return
        }
        if let stretches {
            saveSeparately(stretches, stop: stop, stopNow: stopNow)
            return
        }
        // Parts described one by one make the entry's description of theirs, as the server does.
        let shownDescription = perPart ? SplitPart.joinedDescriptions(parts ?? []) : description
        let details = EntryDetails(description: shownDescription, projectId: projectId, taskId: taskId, tagIds: tagIds, isBillable: isBillable)
            .splitting(parts ?? [])
        // A running entry may lack details for now; a finished one must have what the workspace requires.
        if stop != nil, let rules {
            let missing = rules.missingDetails(details, project: project)
            let missingParts = parts.map { rules.missingInParts($0, project: tracker.catalog.project) } ?? [:]
            errors = FormErrors(missing: missing)
            errors.fields.merge(SplitEditor.errorFields(missingParts)) { $1 }
            if !missing.isEmpty || !missingParts.isEmpty { return }
        } else {
            errors = FormErrors()
        }

        saving = true
        Task {
            defer { saving = false }
            do {
                // A split says whether its parts are described one by one, so going back to one description sticks.
                let id = entry?.id ?? newID()
                try await tracker.save(entry, as: EntryRequest(details: details, start: start, stop: stop, describeParts: parts.map { _ in perPart }),
                                       id: id)
                if stopNow {
                    model.notices.show("Timer stopped")
                }
                editing.onSaved?()
                editing.onSavedEntry?(id)
                dismiss()
            } catch {
                errors = FormErrors(error, fields: Set(["description", "projectId", "taskId", "tagIds", "start", "stop"]).union(SplitEditor.knownErrorFields))
            }
        }
    }

    /// Saves the time as consecutive entries, cut where the stretches meet: this entry becomes the last stretch, so a
    /// running timer keeps running on it, and the earlier stretches are added before it.
    private func saveSeparately(_ stretches: [TimeStretch], stop: Date?, stopNow: Bool) {
        let base = EntryDetails(description: description, projectId: projectId, taskId: taskId, tagIds: tagIds, isBillable: isBillable)
        guard let requests = SplitApplier.requests(for: stretches, base: base, start: start, stop: stop, catalog: tracker.catalog) else {
            errors = FormErrors(fields: ["stretches": "The start or end moved past where the entries meet. Go back to one entry and split it again."])
            return
        }
        if let missing = SplitApplier.missing(requests, tracker: tracker) {
            errors = FormErrors(fields: ["stretches": missing])
            return
        }
        errors = FormErrors()
        saving = true
        Task {
            defer { saving = false }
            do {
                try await SplitApplier.save(requests, replacing: entry, tracker: tracker)
                model.notices.show(stopNow ? "Timer stopped" : "Split into \(requests.count) entries")
                editing.onSaved?()
                dismiss()
            } catch {
                errors = FormErrors(error, fields: ["stretches"])
            }
        }
    }

    private func delete() {
        guard let entry else { return }
        Task {
            do {
                try await tracker.delete(entry)
                model.notices.show("Time entry deleted")
                dismiss()
            } catch {
                model.notices.show(error, fallback: "Couldn't delete the time entry.")
            }
        }
    }
}
