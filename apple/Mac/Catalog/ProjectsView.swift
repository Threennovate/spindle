import SwiftUI
import SpindleKit

/// The workspace's projects: the time tracked on each and against its estimate. Double-click one for its tasks.
struct ProjectsView: View {
    @Environment(AppModel.self) private var model
    let tracker: Tracker
    let client: SpindleClient

    enum Status: String, CaseIterable {
        case active, archived, all

        var label: String { rawValue.capitalized }
    }

    @State private var status = Status.active
    @State private var search = ""
    @State private var clientFilter = ""
    @State private var selection: String?
    @State private var editing: ProjectEditing?
    @State private var tasksOf: Project?
    @State private var deleting: Project?

    private static let allClients = ""
    private static let noClient = "none"

    private var catalog: Catalog { tracker.catalog }

    private var projects: [Project] {
        let term = search.trimmingCharacters(in: .whitespaces)
        return catalog.projects
            .filter { project in
                (status == .all || (status == .archived) == project.isArchived)
                    && (clientFilter == Self.allClients || (clientFilter == Self.noClient ? project.clientId == nil : project.clientId == clientFilter))
                    && (term.isEmpty || project.name.localizedCaseInsensitiveContains(term)
                        || catalog.client(project.clientId)?.name.localizedCaseInsensitiveContains(term) == true)
            }
            .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Picker("Status", selection: $status) {
                    ForEach(Status.allCases, id: \.self) { Text($0.label).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                TextField("Search", text: $search)
                    .textFieldStyle(.roundedBorder)
                    .frame(maxWidth: 240)
                    .accessibilityLabel("Search projects or clients")
                    .help("Search projects or clients")
                Picker("Client", selection: $clientFilter) {
                    Text("All clients").tag(Self.allClients)
                    Text("No client").tag(Self.noClient)
                    Divider()
                    ForEach(catalog.clients.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }) { client in
                        Text(client.name).tag(client.id)
                    }
                }
                .labelsHidden()
                .fixedSize()
                Spacer()
                if tracker.canCreateProjects {
                    Button {
                        editing = ProjectEditing(project: nil)
                    } label: {
                        Label("New Project", systemImage: "plus")
                    }
                    .buttonStyle(.brand)
                }
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
            Divider()
            if projects.isEmpty {
                ContentUnavailableView {
                    Label(catalog.projects.isEmpty ? "No projects yet" : "No projects match", systemImage: "folder")
                } description: {
                    Text(catalog.projects.isEmpty ? "Projects group your time, and tasks within them." : "Try another search or client.")
                }
            } else {
                table
            }
        }
        .navigationSubtitle("Group your time by project, and by task within a project")
        #if DEBUG
        .task {
            try? await Task.sleep(for: .seconds(1.5))
            let project = catalog.projects.first { $0.name == "Website redesign" } ?? catalog.projects.first
            switch SnapshotRun.current?.edit {
            case "project": editing = ProjectEditing(project: project)
            case "tasks": tasksOf = project
            default: break
            }
        }
        #endif
        .sheet(item: $editing) { editing in
            ProjectEditor(tracker: tracker, client: client, project: editing.project)
        }
        .sheet(item: $tasksOf) { project in
            ProjectTasksSheet(tracker: tracker, client: client, projectId: project.id)
        }
        .confirmationDialog("Delete “\(deleting?.name ?? "")”?", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } })) {
            Button("Delete Project", role: .destructive) {
                if let deleting { delete(deleting) }
            }
        } message: {
            if let deleting {
                Text(deleting.trackedSeconds > 0
                    ? "Its tasks are deleted. The \(Durations.hoursMinutes(Double(deleting.trackedSeconds))) tracked on it stay, without a project. To keep the project in reports, archive it instead."
                    : "Its tasks are deleted too.")
            }
        }
    }

    private var table: some View {
        Table(projects, selection: $selection) {
            TableColumn("Project") { project in
                HStack(spacing: 7) {
                    ProjectDot(color: project.color)
                    Text(project.name).lineLimit(1)
                    if project.isPrivate {
                        Image(systemName: "lock.fill").font(.caption).foregroundStyle(.secondary).help("Private")
                    }
                    if project.isBillable {
                        Image(systemName: "dollarsign").font(.caption).foregroundStyle(.secondary).help("Billable")
                    }
                    if project.isArchived {
                        Text("Archived").font(.caption).padding(.horizontal, 6).padding(.vertical, 1).background(.quaternary, in: Capsule())
                    }
                }
            }
            .width(min: 200, ideal: 280)
            TableColumn("Client") { project in
                Text(catalog.client(project.clientId)?.name ?? "—")
                    .foregroundStyle(project.clientId == nil ? .secondary : .primary)
            }
            .width(min: 100, ideal: 160)
            TableColumn("Tasks") { project in
                let open = project.tasks.filter { !$0.isDone }.count
                Text(project.tasks.isEmpty ? "—" : "\(open) open").foregroundStyle(.secondary)
            }
            .width(70)
            TableColumn("Tracked") { project in
                Text(Durations.hoursMinutes(Double(project.trackedSeconds))).monospacedDigit()
            }
            .width(90)
            TableColumn("Estimate") { project in
                EstimateProgress(tracked: project.trackedSeconds, estimate: project.estimateSeconds)
            }
            .width(min: 120, ideal: 170)
        }
        .contextMenu(forSelectionType: String.self) { ids in
            if let project = ids.first.flatMap(catalog.project) {
                Button("Tasks…") { tasksOf = project }
                if project.canManage {
                    Button("Edit…") { editing = ProjectEditing(project: project) }
                    Button(project.isArchived ? "Restore" : "Archive") { archive(project, !project.isArchived) }
                    Divider()
                    Button("Delete…", role: .destructive) { deleting = project }
                }
                Divider()
                Button("Members and Rates on the Web…") {
                    NSWorkspace.shared.open(model.webURL("projects/\(project.id)"))
                }
            }
        } primaryAction: { ids in
            tasksOf = ids.first.flatMap(catalog.project)
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
            } catch {
                model.notices.show(error, fallback: "Couldn't delete the project.")
            }
        }
    }
}

struct ProjectEditing: Identifiable {
    let id = UUID()
    let project: Project?
}

/// Creates a project or changes one: its name, client, color, estimate, rate, and whether it is billable and private.
struct ProjectEditor: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let tracker: Tracker
    let client: SpindleClient
    let project: Project?
    var initialName = ""
    var onSaved: (Project) -> Void = { _ in }

    @State private var fields = ProjectFields(color: ProjectColors.all[0])
    @State private var estimateText = ""
    @State private var rateText = ""
    @State private var newClient: String?
    @State private var errors = FormErrors()
    @State private var saving = false

    private var currency: String { tracker.settings?.currency ?? "EUR" }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(project == nil ? "New project" : "Edit project").font(.title2.weight(.semibold))
            if let form = errors.form {
                FormBanner(text: form)
            }
            FormField(label: "Name", error: errors["name"]) {
                TextField("", text: $fields.name)
            }
            FormField(label: "Client", error: errors["clientId"]) {
                if let name = newClient {
                    HStack {
                        TextField("Client name", text: Binding(get: { name }, set: { newClient = $0 }))
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
                            Divider()
                            Text("New Client…").tag("new")
                        }
                    }
                    .labelsHidden()
                }
            }
            VStack(alignment: .leading, spacing: 6) {
                Text("Color").font(.callout.weight(.medium))
                HStack(spacing: 6) {
                    ForEach(ProjectColors.all, id: \.self) { color in
                        Button {
                            fields.color = color
                        } label: {
                            Circle()
                                .fill(Color(hex: color))
                                .frame(width: 20, height: 20)
                                .overlay {
                                    if fields.color.caseInsensitiveCompare(color) == .orderedSame {
                                        Image(systemName: "checkmark").font(.caption2.weight(.bold)).foregroundStyle(.white)
                                    }
                                }
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(color)
                        .accessibilityAddTraits(fields.color == color ? .isSelected : [])
                    }
                }
            }
            HStack(alignment: .top, spacing: 16) {
                FormField(label: "Estimate (hours)", error: errors["estimateSeconds"]) {
                    TextField("Optional", text: $estimateText)
                }
                FormField(label: "Hourly rate (\(currency))", error: errors["hourlyRate"]) {
                    TextField(tracker.settings?.defaultHourlyRate.map { "Member rates, or \(ReportFormat.money($0, currency: currency))" } ?? "Member rates", text: $rateText)
                }
            }
            Toggle(isOn: $fields.isBillable) {
                VStack(alignment: .leading, spacing: 1) {
                    Text("Billable")
                    Text("New time on this project is billable by default.").font(.caption).foregroundStyle(.secondary)
                }
            }
            .toggleStyle(.switch)
            Toggle(isOn: $fields.isPrivate) {
                VStack(alignment: .leading, spacing: 1) {
                    Text("Private")
                    Text("Only project members and admins can see it.").font(.caption).foregroundStyle(.secondary)
                }
            }
            .toggleStyle(.switch)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(saving ? "Saving…" : project == nil ? "Create Project" : "Save") { save() }
                    .buttonStyle(.brand)
                    .keyboardShortcut(.defaultAction)
                    .disabled(saving)
            }
        }
        .padding(24)
        .frame(width: 480)
        .textFieldStyle(.roundedBorder)
        .onAppear(perform: fill)
    }

    private func fill() {
        if let project {
            fields = ProjectFields(project)
            estimateText = project.estimateSeconds.map { hours(Double($0) / 3600) } ?? ""
            rateText = project.hourlyRate.map { hours($0) } ?? ""
        } else {
            fields = ProjectFields(name: initialName, color: ProjectColors.next(afterCount: tracker.catalog.projects.count))
        }
    }

    private func hours(_ value: Double) -> String {
        value == value.rounded() ? String(Int(value)) : String(value)
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
                    fields.clientId = try await client.saveClient(named: newClient, in: workspace).id
                    self.newClient = nil
                }
                let saved = try await client.saveProject(fields, id: project?.id, in: workspace)
                await tracker.reloadCatalog()
                model.notices.show(project == nil ? "Project “\(saved.name)” created" : "Project saved")
                onSaved(saved)
                dismiss()
            } catch {
                errors = FormErrors(error, fields: ["name", "clientId", "color", "estimateSeconds", "hourlyRate"])
            }
        }
    }
}

/// A project's tasks: add them, rename them, mark them done, or delete them.
struct ProjectTasksSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let tracker: Tracker
    let client: SpindleClient
    let projectId: String
    @State private var newTask = ""
    @State private var renaming: ProjectTask?
    @State private var working = false

    private var project: Project? { tracker.catalog.project(projectId) }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if let project {
                HStack(spacing: 8) {
                    ProjectDot(color: project.color, size: 12)
                    Text(project.name).font(.title2.weight(.semibold))
                    Spacer()
                    Text("\(Durations.hoursMinutes(Double(project.trackedSeconds))) tracked").foregroundStyle(.secondary)
                }
                if project.tasks.isEmpty {
                    Text("No tasks yet. Tasks split a project's time, e.g. into design and development.")
                        .foregroundStyle(.secondary)
                        .padding(.vertical, 8)
                }
                List {
                    ForEach(project.tasks.sorted { ($0.isDone ? 1 : 0, $0.name) < ($1.isDone ? 1 : 0, $1.name) }) { task in
                        HStack(spacing: 10) {
                            Toggle("", isOn: Binding(get: { task.isDone }, set: { done in save(task, name: task.name, done: done) }))
                                .toggleStyle(.checkbox)
                                .labelsHidden()
                                .disabled(!project.canManage)
                                .help(task.isDone ? "Done" : "Mark done")
                            Text(task.name)
                                .strikethrough(task.isDone)
                                .foregroundStyle(task.isDone ? .secondary : .primary)
                            Spacer()
                            if task.estimateSeconds != nil {
                                EstimateProgress(tracked: task.trackedSeconds, estimate: task.estimateSeconds)
                            } else {
                                Text(Durations.hoursMinutes(Double(task.trackedSeconds))).monospacedDigit().foregroundStyle(.secondary)
                            }
                        }
                        .contextMenu {
                            if project.canManage {
                                Button("Rename…") { renaming = task }
                                Button(task.isDone ? "Mark Not Done" : "Mark Done") { save(task, name: task.name, done: !task.isDone) }
                                Divider()
                                Button("Delete", role: .destructive) { delete(task) }
                            }
                        }
                    }
                }
                .frame(minHeight: 200)
                if project.canManage {
                    HStack {
                        TextField("New task", text: $newTask)
                            .textFieldStyle(.roundedBorder)
                            .onSubmit(add)
                        Button("Add Task", action: add)
                            .disabled(newTask.trimmingCharacters(in: .whitespaces).isEmpty || working)
                    }
                }
            }
            HStack {
                Spacer()
                Button("Done") { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(24)
        .frame(width: 520, height: 460)
        .sheet(item: $renaming) { task in
            NameSheet(title: "Rename task", initial: task.name) { name in
                try await saveThrowing(task, name: name, done: task.isDone)
            }
        }
    }

    private func add() {
        let name = newTask.trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty else { return }
        working = true
        Task {
            defer { working = false }
            do {
                _ = try await client.saveTask(named: name, project: projectId, in: tracker.workspace.id)
                newTask = ""
                await tracker.reloadCatalog()
            } catch {
                model.notices.show(error, fallback: "Couldn't add the task.")
            }
        }
    }

    private func save(_ task: ProjectTask, name: String, done: Bool) {
        Task {
            do {
                try await saveThrowing(task, name: name, done: done)
            } catch {
                model.notices.show(error, fallback: "Couldn't change the task.")
            }
        }
    }

    private func saveThrowing(_ task: ProjectTask, name: String, done: Bool) async throws {
        _ = try await client.saveTask(named: name, estimateSeconds: task.estimateSeconds, isDone: done, id: task.id, project: projectId, in: tracker.workspace.id)
        await tracker.reloadCatalog()
    }

    private func delete(_ task: ProjectTask) {
        Task {
            do {
                try await client.deleteTask(task.id, project: projectId, in: tracker.workspace.id)
                await tracker.reloadCatalog(entriesChanged: true)
                model.notices.show("Task “\(task.name)” deleted")
            } catch {
                model.notices.show(error, fallback: "Couldn't delete the task.")
            }
        }
    }
}
