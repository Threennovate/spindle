import SwiftUI
import SpindleKit

/// A project: what it is, the time on it, and its tasks.
struct ProjectDetailView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let tracker: Tracker
    let client: SpindleClient
    let projectId: String

    @State private var editing = false
    @State private var confirmingDelete = false
    @State private var newTask = ""
    @State private var renaming: ProjectTask?
    @State private var taskName = ""
    @State private var deletingTask: ProjectTask?

    private var project: Project? { tracker.catalog.project(projectId) }

    var body: some View {
        if let project {
            List {
                Section {
                    LabeledContent("Client", value: tracker.catalog.client(project.clientId)?.name ?? "No client")
                    LabeledContent("Tracked", value: Durations.hoursMinutes(Double(project.trackedSeconds)))
                    if project.estimateSeconds != nil {
                        LabeledContent("Estimate") {
                            EstimateProgress(tracked: project.trackedSeconds, estimate: project.estimateSeconds)
                        }
                    }
                    LabeledContent("Billable", value: project.isBillable ? "Yes" : "No")
                    if project.isPrivate {
                        LabeledContent("Private", value: "Members and admins only")
                    }
                    if project.isArchived {
                        LabeledContent("Status", value: "Archived")
                    }
                }

                Section {
                    ForEach(project.tasks) { task in
                        HStack {
                            Image(systemName: task.isDone ? "checkmark.circle.fill" : "circle")
                                .foregroundStyle(task.isDone ? Color.brandText : .secondary)
                            Text(task.name)
                                .strikethrough(task.isDone)
                                .foregroundStyle(task.isDone ? .secondary : .primary)
                            Spacer(minLength: 8)
                            if task.estimateSeconds != nil {
                                EstimateProgress(tracked: task.trackedSeconds, estimate: task.estimateSeconds)
                            } else {
                                Text(Durations.hoursMinutes(Double(task.trackedSeconds)))
                                    .font(.subheadline.monospacedDigit())
                                    .foregroundStyle(.secondary)
                            }
                        }
                        .swipeActions(edge: .leading) {
                            if project.canManage {
                                Button { save(task, name: task.name, done: !task.isDone) } label: {
                                    Label(task.isDone ? "Not Done" : "Done", systemImage: task.isDone ? "arrow.uturn.backward" : "checkmark")
                                }
                                .tint(.control)
                            }
                        }
                        .swipeActions(edge: .trailing) {
                            if project.canManage {
                                Button(role: .destructive) { deletingTask = task } label: { Label("Delete", systemImage: "trash") }
                                Button {
                                    taskName = task.name
                                    renaming = task
                                } label: {
                                    Label("Rename", systemImage: "pencil")
                                }
                            }
                        }
                    }
                    if project.canManage {
                        HStack {
                            TextField("Add a task", text: $newTask)
                                .submitLabel(.done)
                                .onSubmit(addTask)
                            Button("Add", action: addTask)
                                .disabled(newTask.trimmingCharacters(in: .whitespaces).isEmpty)
                        }
                    }
                } header: {
                    Text("Tasks")
                } footer: {
                    if project.tasks.isEmpty {
                        Text("Tasks split a project's time, e.g. design and development. Done tasks can't get new time.")
                    }
                }
            }
            .navigationTitle(project.name)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                if project.canManage {
                    ToolbarItem(placement: .topBarTrailing) {
                        Menu {
                            Button { editing = true } label: { Label("Edit", systemImage: "pencil") }
                            Button { archive(project, !project.isArchived) } label: {
                                Label(project.isArchived ? "Restore" : "Archive", systemImage: project.isArchived ? "tray.and.arrow.up" : "archivebox")
                            }
                            Button(role: .destructive) { confirmingDelete = true } label: { Label("Delete", systemImage: "trash") }
                        } label: {
                            Image(systemName: "ellipsis.circle")
                        }
                        .accessibilityLabel("Project actions")
                    }
                }
            }
            .refreshable { await tracker.reloadCatalog() }
            .sheet(isPresented: $editing) {
                ProjectEditorSheet(tracker: tracker, client: client, project: project)
            }
            .alert("Rename task", isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } })) {
                TextField("Task name", text: $taskName)
                Button("Cancel", role: .cancel) {}
                Button("Rename") {
                    if let renaming, !taskName.trimmingCharacters(in: .whitespaces).isEmpty {
                        save(renaming, name: taskName.trimmingCharacters(in: .whitespaces), done: renaming.isDone)
                    }
                }
            }
            .confirmationDialog("Delete “\(project.name)”?", isPresented: $confirmingDelete, titleVisibility: .visible) {
                Button("Delete Project", role: .destructive) { delete(project) }
            } message: {
                Text(project.trackedSeconds > 0
                    ? "Its tasks are deleted. The \(Durations.hoursMinutes(Double(project.trackedSeconds))) tracked on it stay, without a project. To keep the project for reports, archive it instead."
                    : "Its tasks are deleted too.")
            }
            .confirmationDialog("Delete the task “\(deletingTask?.name ?? "")”?", isPresented: Binding(get: { deletingTask != nil }, set: { if !$0 { deletingTask = nil } }), titleVisibility: .visible) {
                Button("Delete Task", role: .destructive) {
                    if let deletingTask { delete(deletingTask) }
                }
            } message: {
                Text("The time tracked on it stays on the project, without a task.")
            }
        } else {
            ContentUnavailableView("This project is gone", systemImage: "folder.badge.questionmark")
        }
    }

    private func addTask() {
        let name = newTask.trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty else { return }
        newTask = ""
        Task {
            do {
                _ = try await client.saveTask(named: name, project: projectId, in: tracker.workspace.id)
                await tracker.reloadCatalog()
            } catch {
                newTask = name
                model.notices.show(error, fallback: "Couldn't add the task.")
            }
        }
    }

    private func save(_ task: ProjectTask, name: String, done: Bool) {
        Task {
            do {
                _ = try await client.saveTask(named: name, estimateSeconds: task.estimateSeconds, isDone: done, id: task.id, project: projectId, in: tracker.workspace.id)
                await tracker.reloadCatalog()
            } catch {
                model.notices.show(error, fallback: "Couldn't change the task.")
            }
        }
    }

    private func delete(_ task: ProjectTask) {
        deletingTask = nil
        Task {
            do {
                try await client.deleteTask(task.id, project: projectId, in: tracker.workspace.id)
                await tracker.reloadCatalog(entriesChanged: true)
            } catch {
                model.notices.show(error, fallback: "Couldn't delete the task.")
            }
        }
    }

    private func archive(_ project: Project, _ archived: Bool) {
        Task {
            do {
                _ = try await client.setProjectArchived(project.id, archived, in: tracker.workspace.id)
                await tracker.reloadCatalog()
                model.notices.show(archived ? "“\(project.name)” archived" : "“\(project.name)” restored")
            } catch {
                model.notices.show(error, fallback: "Couldn't change the project.")
            }
        }
    }

    private func delete(_ project: Project) {
        Task {
            do {
                try await client.deleteProject(project.id, in: tracker.workspace.id)
                await tracker.reloadCatalog(entriesChanged: true)
                model.notices.show("“\(project.name)” deleted")
                dismiss()
            } catch {
                model.notices.show(error, fallback: "Couldn't delete the project.")
            }
        }
    }
}

/// Creates a project or changes one: its name, client, color, estimate, rate, and whether it is billable and private.
struct ProjectEditorSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let tracker: Tracker
    let client: SpindleClient
    let project: Project?

    @State private var fields = ProjectFields(color: ProjectColors.all[0])
    @State private var estimateText = ""
    @State private var rateText = ""
    @State private var newClient: String?
    @State private var errors = FormErrors()
    @State private var saving = false
    @State private var filled = false

    private var currency: String { tracker.settings?.currency ?? "EUR" }

    var body: some View {
        NavigationStack {
            Form {
                if let form = errors.form {
                    Section { FormBanner(text: form) }
                        .listRowInsets(EdgeInsets())
                        .listRowBackground(Color.clear)
                }
                Section {
                    TextField("Name", text: $fields.name)
                } header: {
                    Text("Name")
                } footer: {
                    error("name")
                }
                Section {
                    if let name = newClient {
                        HStack {
                            TextField("New client's name", text: Binding(get: { name }, set: { newClient = $0 }))
                            Button("Cancel") { newClient = nil }
                        }
                    } else {
                        Picker("Client", selection: Binding(get: { fields.clientId ?? "" }, set: { value in
                            if value == "new" { newClient = "" } else { fields.clientId = value.isEmpty ? nil : value }
                        })) {
                            Text("No client").tag("")
                            ForEach(tracker.catalog.clients.filter { $0.archivedAt == nil || $0.id == fields.clientId }) { client in
                                Text(client.name).tag(client.id)
                            }
                            if tracker.isAdmin {
                                Text("New Client…").tag("new")
                            }
                        }
                    }
                } footer: {
                    error("clientId")
                }
                Section("Color") {
                    LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 10), count: 8), spacing: 10) {
                        ForEach(ProjectColors.all, id: \.self) { color in
                            Button {
                                fields.color = color
                            } label: {
                                Circle()
                                    .fill(Color(hex: color))
                                    .frame(width: 30, height: 30)
                                    .overlay {
                                        if fields.color.caseInsensitiveCompare(color) == .orderedSame {
                                            Image(systemName: "checkmark").font(.caption.weight(.bold)).foregroundStyle(.white)
                                        }
                                    }
                            }
                            .buttonStyle(.plain)
                            .accessibilityLabel(color)
                            .accessibilityAddTraits(fields.color.caseInsensitiveCompare(color) == .orderedSame ? .isSelected : [])
                        }
                    }
                    .padding(.vertical, 4)
                }
                Section {
                    LabeledContent("Estimate (hours)") {
                        TextField("Optional", text: $estimateText)
                            .keyboardType(.decimalPad)
                            .multilineTextAlignment(.trailing)
                    }
                    LabeledContent("Hourly rate (\(currency))") {
                        TextField(tracker.settings?.defaultHourlyRate.map { "or \(ReportFormat.money($0, currency: currency))" } ?? "Member rates", text: $rateText)
                            .keyboardType(.decimalPad)
                            .multilineTextAlignment(.trailing)
                    }
                } footer: {
                    error("estimateSeconds") ?? error("hourlyRate") ?? Text("Without a rate, time is billed at each member's rate.")
                }
                Section {
                    Toggle("Billable", isOn: $fields.isBillable)
                    Toggle("Private", isOn: $fields.isPrivate)
                } footer: {
                    Text("New time on a billable project is billable. Only project members and admins see a private project.")
                }
            }
            .tint(.control)
            .navigationTitle(project == nil ? "New Project" : "Edit Project")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(project == nil ? "Create" : "Save", action: save)
                        .fontWeight(.semibold)
                        .disabled(saving)
                }
            }
            .onAppear(perform: fill)
        }
    }

    private func error(_ field: String) -> Text? {
        errors[field].map { Text($0).foregroundStyle(.red) }
    }

    private func fill() {
        guard !filled else { return }
        filled = true
        if let project {
            fields = ProjectFields(project)
            estimateText = project.estimateSeconds.map { hours(Double($0) / 3600) } ?? ""
            rateText = project.hourlyRate.map { hours($0) } ?? ""
        } else {
            fields = ProjectFields(color: ProjectColors.next(afterCount: tracker.catalog.projects.count))
        }
    }

    private func hours(_ value: Double) -> String {
        value.rounded() == value ? String(Int(value)) : String(format: "%.2f", value)
    }

    private func save() {
        var fields = fields
        var validation = FormErrors()
        if fields.name.trimmingCharacters(in: .whitespaces).isEmpty {
            validation["name"] = "Enter a name."
        }
        let estimate = estimateText.trimmingCharacters(in: .whitespaces)
        if estimate.isEmpty {
            fields.estimateSeconds = nil
        } else if let hours = Double(estimate.replacingOccurrences(of: ",", with: ".")), hours > 0 {
            fields.estimateSeconds = Int64((hours * 3600).rounded())
        } else {
            validation["estimateSeconds"] = "Enter the hours, e.g. 40."
        }
        let rate = rateText.trimmingCharacters(in: .whitespaces)
        if rate.isEmpty {
            fields.hourlyRate = nil
        } else if let amount = Double(rate.replacingOccurrences(of: ",", with: ".")), amount >= 0 {
            fields.hourlyRate = amount
        } else {
            validation["hourlyRate"] = "Enter an amount, e.g. 80."
        }
        if let newClient, newClient.trimmingCharacters(in: .whitespaces).isEmpty {
            validation["clientId"] = "Enter the new client's name."
        }
        errors = validation
        guard validation.isEmpty else { return }

        saving = true
        Task {
            defer { saving = false }
            do {
                let workspace = tracker.workspace.id
                if let newClient {
                    fields.clientId = try await client.saveClient(named: newClient.trimmingCharacters(in: .whitespaces), in: workspace).id
                    self.newClient = nil
                }
                let saved = try await client.saveProject(fields, id: project?.id, in: workspace)
                await tracker.reloadCatalog()
                model.notices.show(project == nil ? "Project “\(saved.name)” created" : "Project saved")
                dismiss()
            } catch {
                errors = FormErrors(error, fields: ["name", "clientId", "color", "estimateSeconds", "hourlyRate"])
            }
        }
    }
}
