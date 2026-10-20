import SwiftUI
import SpindleKit

/// The workspace's clients, whom projects are for. Admins add, rename, archive and delete them.
struct ClientsView: View {
    @Environment(AppModel.self) private var model
    let tracker: Tracker
    let client: SpindleClient

    @State private var showArchived = false
    @State private var selection: String?
    @State private var adding = false
    @State private var renaming: WorkspaceClient?
    @State private var deleting: WorkspaceClient?

    private var clients: [WorkspaceClient] {
        tracker.catalog.clients
            .filter { showArchived || $0.archivedAt == nil }
            .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Toggle("Show archived", isOn: $showArchived)
                    .toggleStyle(.checkbox)
                Spacer()
                if tracker.isAdmin {
                    Button {
                        adding = true
                    } label: {
                        Label("New Client", systemImage: "plus")
                    }
                    .buttonStyle(.brand)
                }
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
            Divider()
            if clients.isEmpty {
                ContentUnavailableView {
                    Label("No clients yet", systemImage: "building.2")
                } description: {
                    Text("The people and companies you work for. Assign them to projects to see and bill their time together.")
                }
            } else {
                Table(clients, selection: $selection) {
                    TableColumn("Client") { item in
                        HStack(spacing: 6) {
                            Text(item.name)
                            if item.archivedAt != nil {
                                Text("Archived").font(.caption).padding(.horizontal, 6).padding(.vertical, 1).background(.quaternary, in: Capsule())
                            }
                        }
                    }
                    TableColumn("Projects") { item in
                        Text(item.activeProjectCount == 1 ? "1 active project" : "\(item.activeProjectCount) active projects")
                            .foregroundStyle(.secondary)
                    }
                    .width(min: 120, ideal: 160)
                }
                .contextMenu(forSelectionType: String.self) { ids in
                    if tracker.isAdmin, let item = clients.first(where: { $0.id == ids.first }) {
                        Button("Rename…") { renaming = item }
                        Button(item.archivedAt == nil ? "Archive" : "Restore") { archive(item, item.archivedAt == nil) }
                        Divider()
                        Button("Delete…", role: .destructive) { deleting = item }
                    }
                } primaryAction: { ids in
                    if tracker.isAdmin { renaming = clients.first { $0.id == ids.first } }
                }
            }
        }
        .navigationSubtitle("The people and companies you work for. Assign them to projects.")
        .sheet(isPresented: $adding) {
            NameSheet(title: "New client", placeholder: "e.g. Acme", saveLabel: "Add Client") { name in
                _ = try await client.saveClient(named: name, in: tracker.workspace.id)
                await tracker.reloadCatalog()
                model.notices.show("Client “\(name)” added")
            }
        }
        .sheet(item: $renaming) { item in
            NameSheet(title: "Rename client", initial: item.name) { name in
                _ = try await client.saveClient(named: name, id: item.id, in: tracker.workspace.id)
                await tracker.reloadCatalog()
            }
        }
        .confirmationDialog("Delete “\(deleting?.name ?? "")”?", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } })) {
            Button("Delete Client", role: .destructive) {
                if let deleting { delete(deleting) }
            }
        } message: {
            Text("Its projects stay, without a client.")
        }
    }

    private func archive(_ item: WorkspaceClient, _ archived: Bool) {
        Task {
            do {
                _ = try await client.setClientArchived(item.id, archived, in: tracker.workspace.id)
                await tracker.reloadCatalog()
                model.notices.show(archived ? "“\(item.name)” archived" : "“\(item.name)” restored")
            } catch {
                model.notices.show(error, fallback: "Couldn't change the client.")
            }
        }
    }

    private func delete(_ item: WorkspaceClient) {
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

/// The workspace's tags, to label time across projects.
struct TagsView: View {
    @Environment(AppModel.self) private var model
    let tracker: Tracker
    let client: SpindleClient

    @State private var selection: String?
    @State private var adding = false
    @State private var renaming: Tag?
    @State private var deleting: Tag?

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(tracker.catalog.tags.count == 1 ? "1 tag" : "\(tracker.catalog.tags.count) tags").foregroundStyle(.secondary)
                Spacer()
                if tracker.canCreateTags {
                    Button {
                        adding = true
                    } label: {
                        Label("New Tag", systemImage: "plus")
                    }
                    .buttonStyle(.brand)
                }
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
            Divider()
            if tracker.catalog.tags.isEmpty {
                ContentUnavailableView {
                    Label("No tags yet", systemImage: "tag")
                } description: {
                    Text("Labels that cut across projects, such as meeting, review or overtime.")
                }
            } else {
                Table(tracker.catalog.sortedTags, selection: $selection) {
                    TableColumn("Tag") { tag in
                        Label(tag.name, systemImage: "tag")
                    }
                }
                .contextMenu(forSelectionType: String.self) { ids in
                    if tracker.isAdmin, let tag = ids.first.flatMap(tracker.catalog.tag) {
                        Button("Rename…") { renaming = tag }
                        Divider()
                        Button("Delete…", role: .destructive) { deleting = tag }
                    }
                } primaryAction: { ids in
                    if tracker.isAdmin { renaming = ids.first.flatMap(tracker.catalog.tag) }
                }
            }
        }
        .navigationSubtitle("Labels that cut across projects, such as meeting, review or overtime.")
        .sheet(isPresented: $adding) {
            NameSheet(title: "New tag", placeholder: "e.g. meeting", saveLabel: "Add Tag") { name in
                _ = try await tracker.createTag(named: name)
                model.notices.show("Tag “\(name)” added")
            }
        }
        .sheet(item: $renaming) { tag in
            NameSheet(title: "Rename tag", initial: tag.name) { name in
                _ = try await client.renameTag(tag.id, to: name, in: tracker.workspace.id)
                await tracker.reloadCatalog()
            }
        }
        .confirmationDialog("Delete the tag “\(deleting?.name ?? "")”?", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } })) {
            Button("Delete Tag", role: .destructive) {
                if let deleting { delete(deleting) }
            }
        } message: {
            Text("It is removed from every time entry that has it. The time itself stays.")
        }
    }

    private func delete(_ tag: Tag) {
        Task {
            do {
                try await client.deleteTag(tag.id, in: tracker.workspace.id)
                await tracker.reloadCatalog(entriesChanged: true)
                model.notices.show("“\(tag.name)” deleted")
            } catch {
                model.notices.show(error, fallback: "Couldn't delete the tag.")
            }
        }
    }
}
