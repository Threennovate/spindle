import SwiftUI
import SpindleKit

/// Picks a project or one of its tasks, grouped by client; typing narrows the list, the arrow keys move through it
/// and Return picks. A new project can be created on the spot.
struct ProjectPicker: View {
    let tracker: Tracker
    let projectId: String?
    let taskId: String?
    let onChange: (_ projectId: String?, _ taskId: String?) -> Void
    /// Choices another part of a split already has, left out.
    var taken: (_ projectId: String?, _ taskId: String?) -> Bool = { _, _ in false }
    /// Choosing another part of a split rather than changing a choice, so nothing is checked.
    var choosingAnew = false
    var placeholder = "Search projects, tasks or clients"
    /// Offers to split the time between projects instead of choosing one.
    var onSplit: (() -> Void)?
    /// Choosing a project for a repository: offers to ignore the repository instead, beside "No project".
    var onIgnore: (() -> Void)?

    @Environment(\.dismiss) private var dismiss
    @Environment(Notices.self) private var notices
    /// Missing where the picker isn't in a window of the Mac's desktop app, e.g. in a test.
    @Environment(Desktop.self) private var desktop: Desktop?
    @State private var search = ""
    @State private var searching = true
    @State private var highlighted: Row.ID?
    @State private var creating = false

    enum Row: Identifiable, Equatable {
        case noProject
        case ignore
        case agentsHeader
        /// A project agents are at work in now, with the task a branch rule names for that work.
        case agentsNow(Project, task: ProjectTask?)
        /// The line under "Agents now", before the whole list.
        case agentsEnd
        case header(WorkspaceClient?)
        case project(Project)
        case task(ProjectTask, in: Project)
        case create(String)
        case split

        var id: String {
            switch self {
            case .split: "split"
            case .noProject: "none"
            case .ignore: "ignore"
            case .agentsHeader: "agents"
            case .agentsNow(let project, let task): "agents:\(project.id):\(task?.id ?? "")"
            case .agentsEnd: "agents-end"
            case .header(let client): "client:\(client?.id ?? "")"
            case .project(let project): project.id
            case .task(let task, _): task.id
            case .create: "create"
            }
        }

        var isChoice: Bool {
            switch self {
            case .header, .agentsHeader, .agentsEnd: false
            default: true
            }
        }

        static func == (lhs: Row, rhs: Row) -> Bool { lhs.id == rhs.id }
    }

    private var canCreate: Bool {
        guard let settings = tracker.settings else { return false }
        return tracker.isAdmin || !settings.onlyAdminsCreateProjects
    }

    private var rows: [Row] {
        let term = search.trimmingCharacters(in: .whitespaces)
        func matches(_ words: String...) -> Bool {
            term.isEmpty || words.contains { $0.localizedCaseInsensitiveContains(term) }
        }
        var rows: [Row] = []
        // What agents are at work in now comes first, while nothing is typed.
        if term.isEmpty, let desktop {
            let now = desktop.splits.agentsNow(time: tracker.time, projects: tracker.catalog.projects).compactMap { placement -> Row? in
                guard let project = tracker.catalog.project(placement.projectId), !project.isArchived else { return nil }
                let task = tracker.catalog.task(placement.taskId).flatMap { $0.projectId == project.id && !$0.isDone ? $0 : nil }
                return taken(project.id, task?.id) ? nil : .agentsNow(project, task: task)
            }
            if !now.isEmpty { rows += [.agentsHeader] + now + [.agentsEnd] }
        }
        if matches("No project") && !taken(nil, nil) {
            rows.append(.noProject)
        }
        if onIgnore != nil && matches("Ignore repository") {
            rows.append(.ignore)
        }
        for group in tracker.catalog.projectGroups(keeping: projectId) {
            let clientName = group.client?.name ?? ""
            var groupRows: [Row] = []
            for project in group.projects {
                let tasks = project.tasks.filter {
                    (!$0.isDone || $0.id == taskId) && matches($0.name, project.name, clientName) && !taken(project.id, $0.id)
                }
                let projectFree = !taken(project.id, nil)
                if (matches(project.name, clientName) && projectFree) || !tasks.isEmpty {
                    if projectFree { groupRows.append(.project(project)) }
                    groupRows += tasks.map { .task($0, in: project) }
                }
            }
            if !groupRows.isEmpty {
                rows.append(.header(group.client))
                rows += groupRows
            }
        }
        if onSplit != nil && term.isEmpty {
            rows.append(.split)
        }
        if canCreate {
            rows.append(.create(term))
        }
        return rows
    }

    var body: some View {
        let rows = rows
        VStack(spacing: 0) {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass")
                    .foregroundStyle(.secondary)
                CommandTextField(
                    text: $search,
                    placeholder: placeholder,
                    isFocused: $searching,
                    onCommand: { command in handle(command, rows: rows) })
            }
            .padding(10)
            Divider()
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 1) {
                        ForEach(rows) { row in
                            rowView(row)
                                .id(row.id)
                        }
                        if rows.filter(\.isChoice).isEmpty {
                            Text("No matching projects.")
                                .foregroundStyle(.secondary)
                                .padding(12)
                        }
                    }
                    .padding(5)
                }
                .onChange(of: highlighted) { _, id in
                    if let id { proxy.scrollTo(id) }
                }
            }
        }
        .frame(width: 340, height: 380)
        .onChange(of: search) {
            highlighted = nil
        }
    }

    @ViewBuilder private func rowView(_ row: Row) -> some View {
        switch row {
        case .header(let client):
            Text(client?.name ?? "No client")
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
                .padding(.horizontal, 8)
                .padding(.top, 8)
                .padding(.bottom, 2)
        case .agentsHeader:
            Label("Agents now", systemImage: ActivityStyle.symbol)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
                .padding(.horizontal, 8)
                .padding(.top, 6)
                .padding(.bottom, 2)
                .help("Projects agents worked in during the last 15 minutes, from your linked repositories")
        case .agentsEnd:
            Divider().padding(.horizontal, 8).padding(.vertical, 4)
        case .agentsNow(let project, let task):
            choice(row, checked: false) {
                iconRow(ProjectDot(color: project.color), HStack(spacing: 4) {
                    Text(project.name).lineLimit(1)
                    if let task { Text("· \(task.name)").foregroundStyle(.secondary).lineLimit(1) }
                })
            }
        case .noProject:
            choice(row, checked: projectId == nil && !choosingAnew) {
                iconRow(Image(systemName: "folder.badge.minus"), Text("No project"))
                    .foregroundStyle(.secondary)
            }
        case .ignore:
            choice(row, checked: false) {
                iconRow(Image(systemName: "eye.slash"), Text("Ignore repository"))
                    .foregroundStyle(.secondary)
            }
            .help("Leave its agent work out of Spindle on all your devices: no suggestions, calendar or uploads")
        case .project(let project):
            choice(row, checked: project.id == projectId && taskId == nil && !choosingAnew) {
                iconRow(ProjectDot(color: project.color), Text(project.name).lineLimit(1))
            }
        case .task(let task, _):
            choice(row, checked: task.id == taskId && !choosingAnew) {
                // Under its project's name.
                Text(task.name)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .padding(.leading, Self.iconWidth + Self.iconSpacing)
            }
        case .split:
            choice(row, checked: false) {
                iconRow(Image(systemName: "arrow.triangle.branch"), Text("Split time between projects…"))
            }
        case .create(let name):
            choice(row, checked: false) {
                iconRow(Image(systemName: "plus"), Text(name.isEmpty ? "Create a new project" : "Create project “\(name)”"))
            }
            .disabled(creating)
        }
    }

    private static let iconWidth: CGFloat = 18
    private static let iconSpacing: CGFloat = 7

    /// An icon in a column of its own width, so every row's text starts in the same place.
    private func iconRow(_ icon: some View, _ text: some View) -> some View {
        HStack(spacing: Self.iconSpacing) {
            icon.frame(width: Self.iconWidth)
            text
        }
    }

    private func choice(_ row: Row, checked: Bool, @ViewBuilder label: () -> some View) -> some View {
        Button {
            pick(row)
        } label: {
            HStack {
                label()
                Spacer(minLength: 8)
                if checked {
                    Image(systemName: "checkmark")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(Color.brandText)
                }
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .contentShape(Rectangle())
            .background(
                highlighted == row.id ? Color.highlight : .clear,
                in: RoundedRectangle(cornerRadius: 5))
        }
        .buttonStyle(.plain)
        .onHover { inside in
            if inside { highlighted = row.id }
        }
    }

    private func handle(_ command: CommandTextField.Command, rows: [Row]) -> Bool {
        let choices = rows.filter(\.isChoice)
        let index = choices.firstIndex { $0.id == highlighted }
        switch command {
        case .down where !choices.isEmpty:
            highlighted = choices[min((index ?? -1) + 1, choices.count - 1)].id
        case .up where !choices.isEmpty:
            highlighted = choices[max((index ?? choices.count) - 1, 0)].id
        case .up, .down:
            break
        case .submit:
            // Return picks the highlighted row, or the only match.
            if let row = index.map({ choices[$0] }) ?? (choices.filter({ $0.id != "create" }).count == 1 ? choices.first : nil) {
                pick(row)
            }
        case .cancel:
            dismiss()
        }
        return true
    }

    private func pick(_ row: Row) {
        switch row {
        case .noProject:
            onChange(nil, nil)
            dismiss()
        case .ignore:
            dismiss()
            onIgnore?()
        case .project(let project):
            onChange(project.id, nil)
            dismiss()
        case .task(let task, let project):
            onChange(project.id, task.id)
            dismiss()
        case .agentsNow(let project, let task):
            onChange(project.id, task?.id)
            dismiss()
        case .create(let name):
            create(named: name)
        case .split:
            dismiss()
            onSplit?()
        case .header, .agentsHeader, .agentsEnd:
            break
        }
    }

    private func create(named name: String) {
        guard !name.isEmpty else {
            notices.show("Type the new project's name in the search field first.", kind: .error)
            return
        }
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
