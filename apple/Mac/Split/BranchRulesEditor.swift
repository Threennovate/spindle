import SwiftUI
import SpindleKit

/// A repository whose branches belong to different projects, to open "Different Projects by Branch…" for.
struct BranchRulesTarget: Identifiable {
    let repository: ActivityRepository
    var id: String { repository.identity ?? repository.key }
}

/// "Different Projects by Branch": rules that send a repository's work on some branches to another project, and
/// task, while the rest goes to the repository's own project. They go with the repository's link to the person's
/// account, so every Mac, Windows and the web place the work alike. Nothing already tracked changes: they only
/// decide where suggestions put the time.
struct BranchRulesEditor: View {
    let repository: ActivityRepository
    let tracker: Tracker

    @Environment(Desktop.self) private var desktop
    @Environment(\.dismiss) private var dismiss
    @State private var projectId: String?
    @State private var rules: [Draft]
    @State private var picking: String?
    @FocusState private var focused: UUID?

    /// A rule as it is being written.
    struct Draft: Identifiable, Equatable {
        let id = UUID()
        var pattern: String
        var projectId: String?
        var taskId: String?

        var trimmed: String { pattern.trimmingCharacters(in: .whitespaces) }
    }

    /// Opens on the repository's link and rules, or on `rules` to show how it looks with some.
    init(repository: ActivityRepository, tracker: Tracker, splits: SplitAdvisor, rules: [BranchRule]? = nil) {
        self.repository = repository
        self.tracker = tracker
        let identity = repository.identity ?? ""
        let own = splits.chosen[identity] ?? splits.links.matcher(among: tracker.catalog.projects).project(for: repository)
        _projectId = State(initialValue: own)
        let existing = rules ?? splits.rules[identity] ?? []
        _rules = State(initialValue: existing.isEmpty
            ? [Draft(pattern: "", projectId: nil)]
            : existing.map { Draft(pattern: $0.pattern, projectId: $0.projectId, taskId: $0.taskId) })
    }

    private var catalog: Catalog { tracker.catalog }

    /// The rules to save: those with a pattern. Each needs a project, and a pattern only once.
    private var written: [Draft] { rules.filter { !$0.trimmed.isEmpty } }

    private var problem: String? {
        if projectId == nil { return "Choose the project for the other branches." }
        if written.contains(where: { $0.projectId == nil }) { return "Choose a project for each rule." }
        let patterns = written.map { $0.trimmed.lowercased() }
        if Set(patterns).count != patterns.count { return "Each pattern can have one rule." }
        return nil
    }

    var body: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 0) {
                Text("Different Projects by Branch")
                    .font(.title2.weight(.semibold))
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(repository.name).fontWeight(.medium)
                    if let origin = repository.origin {
                        Text(origin).font(.caption).foregroundStyle(.tertiary).lineLimit(1).truncationMode(.middle)
                    }
                }
                .padding(.top, 4)
                Text("Work on branches that match a rule goes to its project. A session counts when all of its branches match; the first rule that matches wins. Use * for any characters, as in acme/* or ACME-*.")
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 10)

                VStack(alignment: .leading, spacing: 8) {
                    ForEach($rules) { $rule in
                        ruleRow($rule)
                    }
                    Button {
                        let draft = Draft(pattern: "", projectId: nil)
                        rules.append(draft)
                        focused = draft.id
                    } label: {
                        Label("Add Rule", systemImage: "plus")
                    }
                    .buttonStyle(.brandLink)
                    .disabled(rules.count >= BranchRule.maxRules)
                    Divider().padding(.vertical, 4)
                    HStack(spacing: 10) {
                        Text("Other branches")
                            .foregroundStyle(.secondary)
                            .frame(width: Self.patternWidth, alignment: .leading)
                        Image(systemName: "arrow.right").foregroundStyle(.tertiary)
                        projectButton(id: "other", projectId: projectId, taskId: nil, tasks: false) { project, _ in projectId = project }
                    }
                }
                .padding(.top, 16)

                let branches = desktop.splits.branches(of: repository)
                if !branches.isEmpty {
                    recent(branches)
                        .padding(.top, 18)
                }
            }
            .padding(.horizontal, 28)
            .padding(.top, 24)
            .padding(.bottom, 20)
            .frame(maxWidth: .infinity, alignment: .topLeading)
            Divider()
            HStack {
                // Only a repository linked to a project has a link to take away.
                if let identity = repository.identity, desktop.splits.chosen[identity] != nil {
                    Button("Unlink Repository", role: .destructive) {
                        desktop.unlink(repository)
                        dismiss()
                    }
                    .help("\(repository.name)'s work goes to no project, and its branch rules go")
                }
                if let problem, !written.isEmpty || projectId == nil {
                    Text(problem).font(.callout).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Cancel") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Save") { save() }
                    .buttonStyle(.brand)
                    .keyboardShortcut(.defaultAction)
                    .disabled(problem != nil)
            }
            .controlSize(.large)
            .padding(.horizontal, 20)
            .padding(.vertical, 14)
        }
        .frame(width: 560)
        .fixedSize(horizontal: false, vertical: true)
    }

    private static let patternWidth: CGFloat = 170

    private func ruleRow(_ rule: Binding<Draft>) -> some View {
        HStack(spacing: 10) {
            TextField("acme/*", text: rule.pattern)
                .textFieldStyle(.roundedBorder)
                .font(.body.monospaced())
                .frame(width: Self.patternWidth)
                .focused($focused, equals: rule.wrappedValue.id)
                .accessibilityLabel("Branches")
            Image(systemName: "arrow.right").foregroundStyle(.tertiary)
            projectButton(id: rule.wrappedValue.id.uuidString, projectId: rule.wrappedValue.projectId, taskId: rule.wrappedValue.taskId,
                          tasks: true) { project, task in
                rule.wrappedValue.projectId = project
                rule.wrappedValue.taskId = task
            }
            Spacer(minLength: 0)
            Button {
                rules.removeAll { $0.id == rule.wrappedValue.id }
                if rules.isEmpty { rules = [Draft(pattern: "", projectId: nil)] }
            } label: {
                Image(systemName: "minus.circle")
            }
            .buttonStyle(.borderless)
            .foregroundStyle(.secondary)
            .help("Remove this rule")
            .accessibilityLabel("Remove the rule for \(rule.wrappedValue.trimmed.isEmpty ? "these branches" : rule.wrappedValue.trimmed)")
        }
    }

    /// The project (and task) a rule sends work to, chosen in the usual picker.
    private func projectButton(id: String, projectId: String?, taskId: String?, tasks: Bool,
                               onChange: @escaping (String?, String?) -> Void) -> some View {
        let project = catalog.project(projectId)
        let task = catalog.task(taskId)
        return Button {
            picking = id
        } label: {
            HStack(spacing: 6) {
                if let project {
                    ProjectDot(color: project.color)
                    Text(project.name).lineLimit(1)
                    if let task { Text("· \(task.name)").foregroundStyle(.secondary).lineLimit(1) }
                } else {
                    Text("Choose Project").foregroundStyle(.secondary)
                }
                Image(systemName: "chevron.up.chevron.down").font(.caption2).foregroundStyle(.tertiary)
            }
            .frame(minWidth: 180, alignment: .leading)
        }
        .buttonStyle(.bordered)
        .popover(isPresented: Binding(get: { picking == id }, set: { if !$0 { picking = nil } }), arrowEdge: .bottom) {
            ProjectPicker(
                tracker: tracker, projectId: projectId, taskId: taskId,
                onChange: { project, task in
                    // "No project" is no rule; the other branches need one too.
                    guard let project else { return }
                    onChange(project, tasks ? task : nil)
                },
                taken: { project, task in project == nil || (!tasks && task != nil) },
                placeholder: tasks ? "Which project and task are these branches for?" : "Which project are the other branches for?")
        }
    }

    /// The branches agents worked on here lately, and where each would go with the rules as written.
    private func recent(_ branches: [String]) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Recent branches")
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
            ForEach(branches.prefix(6), id: \.self) { branch in
                let rule = written.first { $0.projectId != nil && BranchRule.matches(pattern: $0.trimmed, branch: branch) }
                let project = catalog.project(rule?.projectId ?? projectId)
                HStack(spacing: 6) {
                    Text(branch).font(.callout.monospaced()).lineLimit(1).truncationMode(.middle)
                    Image(systemName: "arrow.right").font(.caption).foregroundStyle(.tertiary)
                    if let project {
                        ProjectDot(color: project.color, size: 8)
                        Text(project.name).font(.callout).lineLimit(1)
                        if let task = catalog.task(rule?.taskId) {
                            Text("· \(task.name)").font(.callout).foregroundStyle(.secondary).lineLimit(1)
                        }
                    } else {
                        Text("No project").font(.callout).foregroundStyle(.secondary)
                    }
                    if rule != nil {
                        Text("rule").font(.caption2.weight(.semibold)).foregroundStyle(Color.brandText)
                    }
                }
                .accessibilityElement(children: .combine)
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.primary.opacity(0.03), in: RoundedRectangle(cornerRadius: 8))
    }

    private func save() {
        guard let projectId, problem == nil else { return }
        desktop.splits.link(repository, to: projectId, rules: written.map {
            BranchRule(pattern: $0.trimmed, projectId: $0.projectId!, taskId: $0.taskId)
        })
        dismiss()
    }
}

/// "Different Projects by Branch…", for a repository that has, or is about to have, a project.
struct BranchRulesButton: View {
    let repository: ActivityRepository
    /// "Branches…" where room is short.
    var short = false
    @Binding var editing: BranchRulesTarget?
    @Environment(Desktop.self) private var desktop

    var body: some View {
        let count = desktop.splits.rules[repository.identity ?? ""]?.count ?? 0
        Button {
            editing = BranchRulesTarget(repository: repository)
        } label: {
            if short {
                Label(count > 0 ? "Branches (\(count))…" : "Branches…", systemImage: "arrow.triangle.branch")
            } else {
                Label(count > 0 ? "Different Projects by Branch (\(count))…" : "Different Projects by Branch…", systemImage: "arrow.triangle.branch")
            }
        }
        .disabled(repository.identity == nil)
        .help("Send the work on some branches of \(repository.name) to another project, e.g. acme/* to the client's project")
    }
}
