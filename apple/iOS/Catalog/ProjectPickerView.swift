import SwiftUI
import SpindleKit

/// Picks a project or one of its tasks, grouped by client. Searching narrows the list, and a new project can be
/// created on the spot where the workspace allows it. In a split, what another part has is left out.
struct ProjectPickerView: View {
    let tracker: Tracker
    let projectId: String?
    let taskId: String?
    let onChange: (_ projectId: String?, _ taskId: String?) -> Void
    var title = "Project"
    /// Projects and tasks that can't be chosen here, as another part of a split has them.
    var taken: (_ projectId: String?, _ taskId: String?) -> Bool = { _, _ in false }
    /// Picking for something new, e.g. a project to add to a split, so nothing is checked as chosen.
    var choosingAnew = false

    @Environment(\.dismiss) private var dismiss
    @Environment(Notices.self) private var notices
    @State private var search = ""
    @State private var creating = false

    private var term: String { search.trimmingCharacters(in: .whitespaces) }

    private func matches(_ words: String...) -> Bool {
        term.isEmpty || words.contains { $0.localizedCaseInsensitiveContains(term) }
    }

    var body: some View {
        let groups = tracker.catalog.projectGroups(keeping: projectId)
        List {
            if matches("No project") && !taken(nil, nil) {
                row(checked: projectId == nil && !choosingAnew) {
                    Label("No project", systemImage: "folder.badge.minus")
                        .foregroundStyle(.secondary)
                } pick: {
                    onChange(nil, nil)
                }
            }
            ForEach(groups) { group in
                let clientName = group.client?.name ?? ""
                let projects = group.projects.filter { project in
                    (matches(project.name, clientName) && !taken(project.id, nil)) || !tasks(of: project, clientName: clientName).isEmpty
                }
                if !projects.isEmpty {
                    Section(group.client?.name ?? "No client") {
                        ForEach(projects) { project in
                            if !taken(project.id, nil) {
                                row(checked: project.id == projectId && taskId == nil) {
                                    HStack(spacing: 10) {
                                        ProjectDot(color: project.color, size: 11)
                                        Text(project.name)
                                    }
                                } pick: {
                                    onChange(project.id, nil)
                                }
                            } else {
                                // Only its tasks can be chosen; the project shows which they belong to.
                                HStack(spacing: 10) {
                                    ProjectDot(color: project.color, size: 11)
                                    Text(project.name)
                                }
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                            }
                            ForEach(tasks(of: project, clientName: clientName)) { task in
                                row(checked: task.id == taskId) {
                                    Text(task.name)
                                        .foregroundStyle(.secondary)
                                        .padding(.leading, 21)
                                } pick: {
                                    onChange(project.id, task.id)
                                }
                            }
                        }
                    }
                }
            }
            if canCreate && !term.isEmpty {
                Section {
                    Button {
                        create(named: term)
                    } label: {
                        Label("Create project “\(term)”", systemImage: "plus")
                    }
                    .disabled(creating)
                }
            }
        }
        .searchable(text: $search, placement: .navigationBarDrawer(displayMode: .always), prompt: "Search projects, tasks or clients")
        .autocorrectionDisabled()
        .navigationTitle(title)
        .navigationBarTitleDisplayMode(.inline)
        .overlay {
            if groups.allSatisfy({ $0.projects.isEmpty }) && term.isEmpty && !canCreate {
                ContentUnavailableView("No projects yet", systemImage: "folder", description: Text("An admin can add projects on the Projects tab."))
            }
        }
    }

    private var canCreate: Bool {
        guard let settings = tracker.settings else { return false }
        return tracker.isAdmin || !settings.onlyAdminsCreateProjects
    }

    private func tasks(of project: Project, clientName: String) -> [ProjectTask] {
        project.tasks.filter { (!$0.isDone || $0.id == taskId) && matches($0.name, project.name, clientName) && !taken(project.id, $0.id) }
    }

    private func row(checked: Bool, @ViewBuilder label: () -> some View, pick: @escaping () -> Void) -> some View {
        Button {
            pick()
            dismiss()
        } label: {
            HStack {
                label()
                    .lineLimit(1)
                Spacer(minLength: 8)
                if checked {
                    Image(systemName: "checkmark")
                        .fontWeight(.semibold)
                        .foregroundStyle(Color.brandText)
                }
            }
            .contentShape(Rectangle())
        }
        .tint(.primary)
    }

    private func create(named name: String) {
        creating = true
        Task {
            defer { creating = false }
            do {
                let project = try await tracker.createProject(named: name)
                onChange(project.id, nil)
                dismiss()
            } catch {
                notices.show(error, fallback: "Couldn't create the project.")
            }
        }
    }
}

/// Picks any number of tags; each tap adds or removes one at once. A name that doesn't exist yet can be created.
struct TagPickerView: View {
    let tracker: Tracker
    let onChange: ([String]) -> Void

    @Environment(Notices.self) private var notices
    @State private var tagIds: [String]
    @State private var search = ""
    @State private var creating = false

    init(tracker: Tracker, tagIds: [String], onChange: @escaping ([String]) -> Void) {
        self.tracker = tracker
        self.onChange = onChange
        _tagIds = State(initialValue: tagIds)
    }

    private var name: String { search.trimmingCharacters(in: .whitespaces) }

    private var canCreate: Bool {
        guard let settings = tracker.settings else { return false }
        return tracker.isAdmin || !settings.onlyAdminsCreateTags
    }

    var body: some View {
        let tags = tracker.catalog.sortedTags.filter { name.isEmpty || $0.name.localizedCaseInsensitiveContains(name) }
        let offersCreate = canCreate && !name.isEmpty
            && !tracker.catalog.tags.contains { $0.name.caseInsensitiveCompare(name) == .orderedSame }
        List {
            ForEach(tags) { tag in
                Button {
                    toggle(tag.id)
                } label: {
                    HStack {
                        Label(tag.name, systemImage: "tag")
                            .lineLimit(1)
                        Spacer(minLength: 8)
                        if tagIds.contains(tag.id) {
                            Image(systemName: "checkmark")
                                .fontWeight(.semibold)
                                .foregroundStyle(Color.brandText)
                        }
                    }
                    .contentShape(Rectangle())
                }
                .tint(.primary)
            }
            if offersCreate {
                Section {
                    Button {
                        create(named: name)
                    } label: {
                        Label("Create tag “\(name)”", systemImage: "plus")
                    }
                    .disabled(creating)
                }
            }
        }
        .searchable(text: $search, placement: .navigationBarDrawer(displayMode: .always), prompt: canCreate ? "Search or create tags" : "Search tags")
        .autocorrectionDisabled()
        .navigationTitle("Tags")
        .navigationBarTitleDisplayMode(.inline)
        .overlay {
            if tracker.catalog.tags.isEmpty && !offersCreate {
                ContentUnavailableView(
                    "No tags yet", systemImage: "tag",
                    description: Text(canCreate ? "Type a name to create the first one." : "An admin can add tags on the Projects tab."))
            }
        }
    }

    private func toggle(_ id: String) {
        if let index = tagIds.firstIndex(of: id) {
            tagIds.remove(at: index)
        } else {
            tagIds.append(id)
        }
        onChange(tagIds)
    }

    private func create(named name: String) {
        creating = true
        Task {
            defer { creating = false }
            do {
                let tag = try await tracker.createTag(named: name)
                search = ""
                toggle(tag.id)
            } catch {
                notices.show(error, fallback: "Couldn't create the tag.")
            }
        }
    }
}

/// A picker in a sheet of its own, with a Done button, for places that aren't in a navigation stack already.
struct PickerSheet<Content: View>: View {
    @Environment(\.dismiss) private var dismiss
    @ViewBuilder var content: Content

    var body: some View {
        NavigationStack {
            content
                .toolbar {
                    ToolbarItem(placement: .confirmationAction) {
                        Button("Done") { dismiss() }
                    }
                }
        }
        .presentationDetents([.medium, .large])
    }
}
