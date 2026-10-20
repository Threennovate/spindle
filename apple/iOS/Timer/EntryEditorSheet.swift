import SwiftUI
import SpindleKit

/// Edits an entry, or adds one for a stretch of time. Entries on locked days are shown, not edited.
struct EntryEditorSheet: View {
    @Environment(AppModel.self) private var model
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
    @State private var start = AppClock.now
    @State private var end: Date?
    @State private var errors = FormErrors()
    @State private var saving = false
    @State private var confirmingDelete = false
    @State private var filled = false
    @FocusState private var focus: DescriptionField?

    private enum DescriptionField: Hashable { case entry, part(Int) }

    private var entry: TimeEntry? { editing.entry }
    private var running: Bool { entry?.isRunning == true }
    private var stopping: Bool { editing.stopping && running }
    private var locked: Bool { entry.map { tracker.isLocked($0.start) } ?? false }
    private var time: UserTime { tracker.time }
    private var rules: EntryRules? { tracker.rules }
    private var project: Project? { tracker.catalog.project(projectId) }
    private var taskRequired: Bool { rules?.requiresTask(on: project) ?? false }
    /// Whether each part of the split has a description of its own, in place of the entry's one.
    private var perPart: Bool { parts.map(SplitPart.describesEach) ?? false }

    var body: some View {
        NavigationStack {
            Form {
                if locked, let rules {
                    Section {
                        FormBanner(text: rules.lockMessage(time), kind: .notice)
                    }
                    .listRowInsets(EdgeInsets())
                    .listRowBackground(Color.clear)
                }
                if stopping {
                    Section {
                        FormBanner(text: "Add the details this workspace requires, then stop the timer.", kind: .notice)
                    }
                    .listRowInsets(EdgeInsets())
                    .listRowBackground(Color.clear)
                }
                if let form = errors.form {
                    Section {
                        FormBanner(text: form)
                    }
                    .listRowInsets(EdgeInsets())
                    .listRowBackground(Color.clear)
                }

                descriptionSection

                if let partsBinding {
                    SplitSection(
                        tracker: tracker, parts: partsBinding,
                        seconds: (end ?? AppClock.now).timeIntervalSince(start), running: end == nil,
                        errors: errors)
                }

                Section {
                    NavigationLink {
                        ProjectPickerView(tracker: tracker, projectId: projectId, taskId: taskId, onChange: { newProject, newTask in
                            projectId = newProject
                            taskId = newTask
                            isBillable = tracker.catalog.project(newProject)?.isBillable ?? isBillable
                            parts = nil
                        }, choosingAnew: parts != nil)
                    } label: {
                        LabeledContent(required(taskRequired ? "Project and task" : "Project", rules?.settings.requireProject == true || taskRequired)) {
                            if parts != nil {
                                Text("Split")
                            } else if let project {
                                ProjectLabel(project: project, task: tracker.catalog.task(taskId))
                            } else {
                                Text("None")
                            }
                        }
                    }
                    if parts == nil {
                        NavigationLink {
                            ProjectPickerView(
                                tracker: tracker, projectId: nil, taskId: nil, onChange: beginSplit, title: "Split With",
                                taken: { $0 == projectId && $1 == taskId }, choosingAnew: true)
                        } label: {
                            Label("Split Between Projects", systemImage: "arrow.triangle.branch")
                        }
                    }
                    NavigationLink {
                        TagPickerView(tracker: tracker, tagIds: tagIds) { tagIds = $0 }
                    } label: {
                        LabeledContent(required("Tags", rules?.settings.requireTag == true)) {
                            Text(tagIds.isEmpty ? "None" : tagIds.compactMap { tracker.catalog.tag($0)?.name }.joined(separator: ", "))
                                .lineLimit(1)
                        }
                    }
                    // Billable applies to every part of a split entry.
                    Toggle("Billable", isOn: Binding(get: { parts?.contains(where: \.isBillable) ?? isBillable }, set: { value in
                        isBillable = value
                        if parts != nil { for index in parts!.indices { parts![index].isBillable = value } }
                    }))
                } footer: {
                    if let message = errors["projectId"] ?? errors["taskId"] ?? errors["tagIds"] {
                        errorText(message)
                    } else if parts != nil {
                        Text("Choosing a project puts all of the time on it.")
                    }
                }

                Section {
                    DatePicker("Start", selection: startBinding, in: earliest..., displayedComponents: [.date, .hourAndMinute])
                    if let end {
                        DatePicker("End", selection: Binding(get: { end }, set: { self.end = $0 }), in: start..., displayedComponents: [.date, .hourAndMinute])
                    } else {
                        LabeledContent("End", value: "Running")
                    }
                    LabeledContent("Duration") {
                        TimelineView(.periodic(from: .now, by: running ? 1 : 3600)) { _ in
                            Text(Durations.clock((end ?? AppClock.now).timeIntervalSince(start)))
                                .monospacedDigit()
                        }
                    }
                } footer: {
                    errorText(errors["start"] ?? errors["stop"])
                }
                .accountTime(time)

                if running && !stopping && !locked {
                    Section {
                        Button("Stop Timer") { save(stopNow: true) }
                            .disabled(saving)
                    }
                }
                if entry != nil && !locked {
                    Section {
                        Button("Delete Time Entry", role: .destructive) { confirmingDelete = true }
                    }
                }
            }
            .disabled(locked)
            .navigationTitle(title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                if locked {
                    ToolbarItem(placement: .confirmationAction) {
                        Button("Done") { dismiss() }
                    }
                } else {
                    ToolbarItem(placement: .cancellationAction) {
                        Button(stopping ? "Keep Running" : "Cancel") { dismiss() }
                    }
                    ToolbarItem(placement: .confirmationAction) {
                        Button(entry == nil ? "Add" : stopping ? "Stop" : "Save") { save(stopNow: stopping) }
                            .fontWeight(.semibold)
                            .disabled(saving)
                    }
                }
            }
            .confirmationDialog("Delete this time entry?", isPresented: $confirmingDelete, titleVisibility: .visible) {
                Button("Delete", role: .destructive, action: delete)
            } message: {
                Text("\(entry.map { $0.description.isEmpty ? "The entry" : "“\($0.description)”" } ?? "The entry") and its \(Durations.clock((end ?? AppClock.now).timeIntervalSince(start))) will be removed.")
            }
            .onAppear(perform: fill)
            .onChange(of: parts?.count) { _, count in
                if let count, count < 2 { endSplit() }
            }
        }
    }

    /// The entry's description, or one per project while the parts are described one by one. On a split, a switch
    /// says which, as one description shows on every project's reports and timesheet links.
    private var descriptionSection: some View {
        Section {
            if let parts, perPart {
                ForEach(parts.indices, id: \.self) { index in
                    VStack(alignment: .leading, spacing: 4) {
                        HStack(spacing: 6) {
                            Circle().fill(tracker.catalog.color(of: parts[index])).frame(width: 8, height: 8)
                            Text(tracker.catalog.name(of: parts[index]))
                                .lineLimit(1)
                        }
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .accessibilityHidden(true)
                        TextField(
                            "What did you do on \(tracker.catalog.project(parts[index].projectId)?.name ?? "this part")?",
                            text: partDescription(index), axis: .vertical)
                            .focused($focus, equals: .part(index))
                            .accessibilityLabel("Description for \(tracker.catalog.name(of: parts[index]))")
                        if let error = errors["shares[\(index)].description"] {
                            Text(error).font(.footnote).foregroundStyle(.red)
                        }
                    }
                }
            } else {
                TextField("Description", text: $description, axis: .vertical)
                    .focused($focus, equals: .entry)
            }
            if let parts, !locked {
                Toggle("Describe Each Project", isOn: Binding(get: { perPart }, set: { $0 ? describePerPart() : describeAllAsOne() }))
                    .disabled(parts.count < 2)
            }
        } header: {
            Text(required(perPart ? "Description per project" : "Description", rules?.settings.requireDescription == true))
        } footer: {
            if let message = errors["description"] {
                errorText(message)
            } else if let parts, !locked {
                Text(perPart
                    ? "Each project's reports and timesheet links show only its own description."
                    : "Shown on all \(parts.count) projects, timesheet links included.")
            }
        }
    }

    private func partDescription(_ index: Int) -> Binding<String> {
        Binding(
            get: { parts.flatMap { $0.indices.contains(index) ? $0[index].description : nil } ?? "" },
            set: { if parts?.indices.contains(index) == true { parts![index].description = $0 } })
    }

    /// Each project gets its own description, and the first one still to be written is ready for typing.
    private func describePerPart() {
        guard let current = parts else { return }
        let described = SplitPart.describingEach(current, from: description)
        parts = described
        let next = described.firstIndex { $0.description?.isEmpty != false } ?? 0
        // The field is there only once the change is drawn.
        DispatchQueue.main.async { focus = .part(next) }
    }

    /// One description for all parts again: theirs joined.
    private func describeAllAsOne() {
        guard let current = parts else { return }
        let shared = SplitPart.sharingOneDescription(current)
        parts = shared.parts
        description = shared.description
        errors.fields = errors.fields.filter { !$0.key.hasSuffix("].description") }
    }

    private var title: String {
        guard let entry else { return "New Time Entry" }
        if locked { return "Time Entry" }
        if stopping { return "Stop Timer" }
        return entry.isRunning ? "Running Entry" : "Edit Time Entry"
    }

    /// The first moment an entry may start: the first day that isn't locked, when some are.
    private var earliest: Date {
        guard !locked, let day = rules?.firstOpenDay else { return .distantPast }
        return time.start(of: day)
    }

    /// A new start time keeps the end where it is, so the duration changes, as on the web and the Mac. Another day
    /// moves the whole entry, keeping its length, and so does a start past the end.
    private var startBinding: Binding<Date> {
        Binding(get: { start }, set: { newStart in
            if let end, time.day(of: newStart) != time.day(of: start) || newStart > end {
                self.end = newStart.addingTimeInterval(end.timeIntervalSince(start))
            }
            start = newStart
        })
    }

    private var partsBinding: Binding<[SplitPart]>? {
        guard parts != nil else { return nil }
        return Binding(get: { parts ?? [] }, set: { parts = $0 })
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
            if let own = only.description { description = own }
        }
        parts = nil
    }

    private func required(_ label: String, _ isRequired: Bool) -> String {
        isRequired && !locked ? "\(label) (required)" : label
    }

    @ViewBuilder private func errorText(_ message: String?) -> some View {
        if let message {
            Text(message).foregroundStyle(.red)
        }
    }

    private func fill() {
        guard !filled else { return }
        filled = true
        if let entry {
            description = entry.description
            projectId = entry.projectId
            taskId = entry.taskId
            tagIds = entry.tagIds
            isBillable = entry.isBillable
            parts = entry.isSplit ? entry.parts : nil
            start = entry.start
            end = entry.stop
            if stopping, let rules {
                errors = FormErrors(missing: rules.missingDetails(entry.details, project: tracker.catalog.project(entry.projectId)))
                if entry.isSplit {
                    errors.fields.merge(SplitSection.errorFields(rules.missingInParts(entry.parts, project: tracker.catalog.project))) { $1 }
                }
            }
        } else if let range = editing.newRange {
            start = range.start
            end = range.end
        }
    }

    /// Saves the entry; `stopNow` also stops a running timer, keeping the changes made here.
    private func save(stopNow: Bool) {
        let stop = stopNow ? (editing.stopAt ?? AppClock.now) : end
        if let stop, stop < start {
            errors = FormErrors(fields: ["stop": "The end must not be before the start."])
            return
        }
        // Parts described one by one make the entry's description of theirs, as the server does.
        let shownDescription = perPart ? SplitPart.joinedDescriptions(parts ?? []) : description.trimmingCharacters(in: .whitespacesAndNewlines)
        let details = EntryDetails(description: shownDescription, projectId: projectId, taskId: taskId, tagIds: tagIds, isBillable: isBillable)
            .splitting(parts ?? [])
        // A running entry may lack details for now; a finished one must have what the workspace requires.
        if stop != nil, let rules {
            let missing = rules.missingDetails(details, project: project)
            let missingParts = parts.map { rules.missingInParts($0, project: tracker.catalog.project) } ?? [:]
            errors = FormErrors(missing: missing)
            errors.fields.merge(SplitSection.errorFields(missingParts)) { $1 }
            if !missing.isEmpty || !missingParts.isEmpty { return }
        } else {
            errors = FormErrors()
        }

        saving = true
        Task {
            defer { saving = false }
            do {
                // A split says whether its parts are described one by one, so going back to one description sticks.
                try await tracker.save(entry, as: EntryRequest(details: details, start: start, stop: stop, describeParts: parts.map { _ in perPart }))
                if stopNow {
                    model.notices.show("Timer stopped")
                }
                dismiss()
            } catch {
                errors = FormErrors(error, fields: Set(["description", "projectId", "taskId", "tagIds", "start", "stop"]).union(SplitSection.knownErrorFields))
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
