import Foundation
import Observation

/// A workspace's projects, tasks, clients and tags, with lookups by ID.
@MainActor
@Observable
public final class Catalog {
    /// All projects the person can see, archived ones included, since old entries may still point at them.
    public private(set) var projects: [Project] = [] {
        didSet { index() }
    }
    public private(set) var clients: [WorkspaceClient] = [] {
        didSet { clientsById = Dictionary(clients.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first }) }
    }
    public private(set) var tags: [Tag] = [] {
        didSet { tagsById = Dictionary(tags.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first }) }
    }

    // Observed like the lists, so views that look things up by ID follow when the catalog loads or changes.
    private var projectsById: [String: Project] = [:]
    private var tasksById: [String: ProjectTask] = [:]
    private var clientsById: [String: WorkspaceClient] = [:]
    private var tagsById: [String: Tag] = [:]

    public init(projects: [Project] = [], clients: [WorkspaceClient] = [], tags: [Tag] = []) {
        self.projects = projects
        self.clients = clients
        self.tags = tags
        index()
        clientsById = Dictionary(clients.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        tagsById = Dictionary(tags.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
    }

    public func project(_ id: String?) -> Project? { id.flatMap { projectsById[$0] } }
    public func task(_ id: String?) -> ProjectTask? { id.flatMap { tasksById[$0] } }
    public func client(_ id: String?) -> WorkspaceClient? { id.flatMap { clientsById[$0] } }
    public func tag(_ id: String) -> Tag? { tagsById[id] }

    /// Tags sorted by name, as pickers list them.
    public var sortedTags: [Tag] {
        tags.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    /// Projects to choose from, grouped by client: projects without a client first, then clients A to Z. Archived
    /// projects are left out, except the one already chosen.
    public func projectGroups(keeping chosen: String? = nil) -> [ProjectGroup] {
        let open = projects.filter { !$0.isArchived || $0.id == chosen }
        return Dictionary(grouping: open) { $0.clientId }
            .map { ProjectGroup(client: client($0.key), projects: $0.value.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }) }
            .sorted { first, second in
                switch (first.client, second.client) {
                case (nil, _): true
                case (_, nil): false
                case let (a?, b?): a.name.localizedCaseInsensitiveCompare(b.name) == .orderedAscending
                }
            }
    }

    func load(from api: SpindleClient, workspaceId: String) async throws {
        async let projects = api.projects(in: workspaceId, includeArchived: true)
        async let clients = api.clients(in: workspaceId, includeArchived: true)
        async let tags = api.tags(in: workspaceId)
        (self.projects, self.clients, self.tags) = try await (projects, clients, tags)
    }

    func replace(projects: [Project], clients: [WorkspaceClient], tags: [Tag]) {
        self.projects = projects
        self.clients = clients
        self.tags = tags
    }

    func add(_ project: Project) {
        projects.removeAll { $0.id == project.id }
        projects.append(project)
    }

    func add(_ tag: Tag) {
        tags.removeAll { $0.id == tag.id }
        tags.append(tag)
    }

    private func index() {
        projectsById = Dictionary(projects.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        tasksById = Dictionary(projects.flatMap(\.tasks).map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
    }
}

/// A client's projects, or the projects without a client.
public struct ProjectGroup: Identifiable, Sendable {
    public let client: WorkspaceClient?
    public let projects: [Project]

    public var id: String { client?.id ?? "" }
}
