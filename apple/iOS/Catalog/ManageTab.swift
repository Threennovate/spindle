import SwiftUI
import SpindleKit

/// The workspace's projects, clients and tags.
struct ManageTab: View {
    let tracker: Tracker
    let client: SpindleClient

    enum Part: String, CaseIterable {
        case projects, clients, tags
        var label: String { rawValue.capitalized }
    }

    @State private var part = Part.projects
    @State private var path = NavigationPath()

    var body: some View {
        NavigationStack(path: $path) {
            Group {
                switch part {
                case .projects: ProjectList(tracker: tracker, client: client)
                case .clients: ClientList(tracker: tracker, client: client)
                case .tags: TagList(tracker: tracker, client: client)
                }
            }
            .safeAreaInset(edge: .top, spacing: 0) {
                Picker("Show", selection: $part) {
                    ForEach(Part.allCases, id: \.self) { Text($0.label).tag($0) }
                }
                .pickerStyle(.segmented)
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
                .background(.bar)
            }
            .navigationTitle(part.label)
            .navigationBarTitleDisplayMode(.inline)
            .navigationDestination(for: String.self) { projectId in
                ProjectDetailView(tracker: tracker, client: client, projectId: projectId)
            }
        }
        #if DEBUG
        .task {
            let options = LaunchOptions.current
            part = options.manage.flatMap(Part.init(rawValue:)) ?? part
            guard let name = options.project, name != "new" else { return }
            // Waits for the projects to load.
            for _ in 0..<40 where tracker.catalog.projects.isEmpty {
                try? await Task.sleep(for: .milliseconds(150))
            }
            if let project = tracker.catalog.projects.first(where: { $0.name == name }) {
                path.append(project.id)
            }
        }
        #endif
    }
}

// MARK: - Projects

private struct ProjectList: View {
    @Environment(AppModel.self) private var model
    let tracker: Tracker
    let client: SpindleClient

    enum Status: String, CaseIterable {
        case active, archived, all
        var label: String { rawValue.capitalized }
    }

    @State private var status = Status.active
    @State private var clientFilter = ""
    @State private var search = ""
    @State private var creating = false
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
        let projects = projects
        List {
            ForEach(projects) { project in
                NavigationLink(value: project.id) {
                    ProjectRow(project: project, clientName: catalog.client(project.clientId)?.name)
                }
                .swipeActions(edge: .trailing) {
                    if project.canManage {
                        Button(role: .destructive) { deleting = project } label: { Label("Delete", systemImage: "trash") }
                        Button { archive(project, !project.isArchived) } label: {
                            Label(project.isArchived ? "Restore" : "Archive", systemImage: project.isArchived ? "tray.and.arrow.up" : "archivebox")
                        }
                        .tint(.orange)
                    }
                }
            }
        }
        .overlay {
            if projects.isEmpty {
                ContentUnavailableView {
                    Label(search.isEmpty && status == .active ? "No projects yet" : "No matching projects", systemImage: "folder")
                } description: {
                    Text(catalog.projects.isEmpty ? "Projects group your time, and tasks within them." : "Try another search or filter.")
                }
            }
        }
        .searchable(text: $search, prompt: "Search projects or clients")
        .refreshable { await tracker.reloadCatalog() }
        .toolbar {
            ToolbarItem(placement: .topBarLeading) {
                Menu {
                    Picker("Status", selection: $status) {
                        ForEach(Status.allCases, id: \.self) { Text($0.label).tag($0) }
                    }
                    Picker("Client", selection: $clientFilter) {
                        Text("All clients").tag(Self.allClients)
                        Text("No client").tag(Self.noClient)
                        ForEach(catalog.clients.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }) { client in
                            Text(client.name).tag(client.id)
                        }
                    }
                } label: {
                    Image(systemName: status != .active || !clientFilter.isEmpty ? "line.3.horizontal.decrease.circle.fill" : "line.3.horizontal.decrease.circle")
                }
                .accessibilityLabel("Filter projects")
            }
            if tracker.canCreateProjects {
                ToolbarItem(placement: .topBarTrailing) {
                    Button { creating = true } label: { Image(systemName: "plus") }
                        .accessibilityLabel("New project")
                }
            }
        }
        .sheet(isPresented: $creating) {
            ProjectEditorSheet(tracker: tracker, client: client, project: nil)
        }
        #if DEBUG
        .task { creating = LaunchOptions.current.project == "new" }
        #endif
        .confirmationDialog("Delete “\(deleting?.name ?? "")”?", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }), titleVisibility: .visible) {
            Button("Delete Project", role: .destructive) {
                if let deleting { delete(deleting) }
            }
        } message: {
            if let deleting {
                Text(deleting.trackedSeconds > 0
                    ? "Its tasks are deleted. The \(Durations.hoursMinutes(Double(deleting.trackedSeconds))) tracked on it stay, without a project. To keep the project for reports, archive it instead."
                    : "Its tasks are deleted too.")
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
        deleting = nil
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

private struct ProjectRow: View {
    let project: Project
    let clientName: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 8) {
                ProjectDot(color: project.color, size: 11)
                Text(project.name).fontWeight(.medium).lineLimit(1)
                if project.isPrivate {
                    Image(systemName: "lock").font(.caption).foregroundStyle(.secondary).accessibilityLabel("Private")
                }
                if project.isBillable {
                    Image(systemName: "dollarsign").font(.caption).foregroundStyle(.secondary).accessibilityLabel("Billable")
                }
                if project.isArchived {
                    Text("Archived").font(.caption2).padding(.horizontal, 6).padding(.vertical, 1).background(.quaternary, in: Capsule())
                }
                Spacer(minLength: 8)
                Text(Durations.hoursMinutes(Double(project.trackedSeconds)))
                    .font(.subheadline.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            if clientName != nil || project.estimateSeconds != nil {
                HStack {
                    Text(clientName ?? "No client")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                    Spacer(minLength: 8)
                    if project.estimateSeconds != nil {
                        EstimateProgress(tracked: project.trackedSeconds, estimate: project.estimateSeconds)
                    }
                }
                .padding(.leading, 19)
            }
        }
        .padding(.vertical, 2)
        .accessibilityElement(children: .combine)
    }
}

// MARK: - Clients

private struct ClientList: View {
    @Environment(AppModel.self) private var model
    let tracker: Tracker
    let client: SpindleClient

    @State private var showArchived = false
    @State private var naming: Naming?
    @State private var name = ""
    @State private var deleting: WorkspaceClient?

    /// A new client, or the one being renamed.
    private struct Naming: Identifiable {
        let id = UUID()
        let item: WorkspaceClient?
    }

    var body: some View {
        let clients = tracker.catalog.clients
            .filter { showArchived || $0.archivedAt == nil }
            .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        List {
            Section {
                ForEach(clients) { item in
                    let count = tracker.catalog.projects.filter { $0.clientId == item.id && !$0.isArchived }.count
                    HStack {
                        Text(item.name)
                        if item.archivedAt != nil {
                            Text("Archived").font(.caption2).padding(.horizontal, 6).padding(.vertical, 1).background(.quaternary, in: Capsule())
                        }
                        Spacer()
                        Text(count == 1 ? "1 project" : "\(count) projects")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                    }
                    .swipeActions(edge: .trailing) {
                        if tracker.isAdmin {
                            Button(role: .destructive) { deleting = item } label: { Label("Delete", systemImage: "trash") }
                            Button { archive(item, item.archivedAt == nil) } label: {
                                Label(item.archivedAt == nil ? "Archive" : "Restore", systemImage: item.archivedAt == nil ? "archivebox" : "tray.and.arrow.up")
                            }
                            .tint(.orange)
                            Button { rename(item) } label: { Label("Rename", systemImage: "pencil") }
                                .tint(.control)
                        }
                    }
                }
            } footer: {
                Text("The people and companies you work for. Assign them to projects to see and bill their time together.")
            }
        }
        .overlay {
            if clients.isEmpty {
                ContentUnavailableView("No clients yet", systemImage: "building.2", description: Text(tracker.isAdmin ? "Tap + to add the first one." : "An admin can add clients."))
            }
        }
        .refreshable { await tracker.reloadCatalog() }
        .toolbar {
            ToolbarItem(placement: .topBarLeading) {
                Toggle(isOn: $showArchived) { Image(systemName: "archivebox") }
                    .toggleStyle(.button)
                    .accessibilityLabel("Show archived clients")
            }
            if tracker.isAdmin {
                ToolbarItem(placement: .topBarTrailing) {
                    Button { rename(nil) } label: { Image(systemName: "plus") }
                        .accessibilityLabel("New client")
                }
            }
        }
        .alert(naming?.item == nil ? "New client" : "Rename client", isPresented: Binding(get: { naming != nil }, set: { if !$0 { naming = nil } })) {
            TextField("Client name", text: $name)
            Button("Cancel", role: .cancel) {}
            Button(naming?.item == nil ? "Add" : "Rename") { save(naming?.item) }
        }
        .confirmationDialog("Delete “\(deleting?.name ?? "")”?", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }), titleVisibility: .visible) {
            Button("Delete Client", role: .destructive) {
                if let deleting { delete(deleting) }
            }
        } message: {
            Text("Its projects stay, without a client.")
        }
    }

    private func rename(_ item: WorkspaceClient?) {
        name = item?.name ?? ""
        naming = Naming(item: item)
    }

    private func save(_ item: WorkspaceClient?) {
        let name = name.trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty else { return }
        Task {
            do {
                _ = try await client.saveClient(named: name, id: item?.id, in: tracker.workspace.id)
                await tracker.reloadCatalog()
                model.notices.show(item == nil ? "Client “\(name)” added" : "Client renamed")
            } catch {
                model.notices.show(error, fallback: "Couldn't save the client.")
            }
        }
    }

    private func archive(_ item: WorkspaceClient, _ archived: Bool) {
        Task {
            do {
                _ = try await client.setClientArchived(item.id, archived, in: tracker.workspace.id)
                await tracker.reloadCatalog()
            } catch {
                model.notices.show(error, fallback: "Couldn't change the client.")
            }
        }
    }

    private func delete(_ item: WorkspaceClient) {
        deleting = nil
        Task {
            do {
                try await client.deleteClient(item.id, in: tracker.workspace.id)
                await tracker.reloadCatalog()
                model.notices.show("“\(item.name)” deleted")
            } catch {
                model.notices.show(error, fallback: "Couldn't delete the client.")
            }
        }
    }
}

// MARK: - Tags

private struct TagList: View {
    @Environment(AppModel.self) private var model
    let tracker: Tracker
    let client: SpindleClient

    @State private var naming: Naming?
    @State private var name = ""
    @State private var deleting: Tag?

    private struct Naming: Identifiable {
        let id = UUID()
        let tag: Tag?
    }

    var body: some View {
        let tags = tracker.catalog.sortedTags
        List {
            Section {
                ForEach(tags) { tag in
                    Label(tag.name, systemImage: "tag")
                        .swipeActions(edge: .trailing) {
                            if tracker.isAdmin {
                                Button(role: .destructive) { deleting = tag } label: { Label("Delete", systemImage: "trash") }
                                Button { rename(tag) } label: { Label("Rename", systemImage: "pencil") }
                                    .tint(.control)
                            }
                        }
                }
            } footer: {
                Text("Labels that cut across projects, such as meeting, review or overtime.")
            }
        }
        .overlay {
            if tags.isEmpty {
                ContentUnavailableView("No tags yet", systemImage: "tag", description: Text(tracker.canCreateTags ? "Tap + to add the first one." : "An admin can add tags."))
            }
        }
        .refreshable { await tracker.reloadCatalog() }
        .toolbar {
            if tracker.canCreateTags {
                ToolbarItem(placement: .topBarTrailing) {
                    Button { rename(nil) } label: { Image(systemName: "plus") }
                        .accessibilityLabel("New tag")
                }
            }
        }
        .alert(naming?.tag == nil ? "New tag" : "Rename tag", isPresented: Binding(get: { naming != nil }, set: { if !$0 { naming = nil } })) {
            TextField("Tag name", text: $name)
            Button("Cancel", role: .cancel) {}
            Button(naming?.tag == nil ? "Add" : "Rename") { save(naming?.tag) }
        }
        .confirmationDialog("Delete the tag “\(deleting?.name ?? "")”?", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }), titleVisibility: .visible) {
            Button("Delete Tag", role: .destructive) {
                if let deleting { delete(deleting) }
            }
        } message: {
            Text("It is removed from every time entry that has it. The time itself stays.")
        }
    }

    private func rename(_ tag: Tag?) {
        name = tag?.name ?? ""
        naming = Naming(tag: tag)
    }

    private func save(_ tag: Tag?) {
        let name = name.trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty else { return }
        Task {
            do {
                if let tag {
                    _ = try await client.renameTag(tag.id, to: name, in: tracker.workspace.id)
                    await tracker.reloadCatalog(entriesChanged: true)
                    model.notices.show("Tag renamed")
                } else {
                    _ = try await tracker.createTag(named: name)
                    model.notices.show("Tag “\(name)” added")
                }
            } catch {
                model.notices.show(error, fallback: "Couldn't save the tag.")
            }
        }
    }

    private func delete(_ tag: Tag) {
        deleting = nil
        Task {
            do {
                try await client.deleteTag(tag.id, in: tracker.workspace.id)
                await tracker.reloadCatalog(entriesChanged: true)
                model.notices.show("Tag “\(tag.name)” deleted")
            } catch {
                model.notices.show(error, fallback: "Couldn't delete the tag.")
            }
        }
    }
}
