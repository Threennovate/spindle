import Foundation
import SpindleAPI

/// A change to an entry, in the shape the API takes it.
public struct EntryRequest: Sendable, Hashable, Codable {
    public var details: EntryDetails
    public var start: Date
    public var stop: Date?
    /// True to save the parts' own descriptions (see `SplitPart.description`), false for one description for all.
    /// Nil leaves them as they are while the entry's description is sent unchanged, as apps that predate them do.
    public var describeParts: Bool?

    public init(details: EntryDetails, start: Date, stop: Date?, describeParts: Bool? = nil) {
        self.details = details
        self.start = start
        self.stop = stop
        self.describeParts = describeParts
    }

    /// The request that saves an entry as it is, to change one part of it.
    public init(_ entry: TimeEntry) {
        self.init(details: entry.details, start: entry.start, stop: entry.stop)
    }

    var body: Components.Schemas.TimeEntryRequest {
        .init(
            description: details.description.trimmingCharacters(in: .whitespacesAndNewlines),
            projectId: details.projectId,
            taskId: details.taskId,
            tagIds: details.tagIds,
            isBillable: details.isBillable,
            shares: details.shares.map(\.request),
            describeParts: describeParts,
            start: start,
            stop: stop)
    }
}

extension SpindleClient {
    // MARK: - Time entries

    /// The person's entries that start in [from, to).
    public func timeEntries(in workspaceId: String, from: Date, to: Date) async throws -> [TimeEntry] {
        try await call {
            try await api.listTimeEntries(path: .init(workspaceId: workspaceId), query: .init(from: from, to: to)).ok.body.json
        }
    }

    /// The person's running entry in any workspace; a person has at most one.
    public func currentEntry() async throws -> TimeEntry? {
        try await call {
            switch try await api.getCurrentTimeEntry() {
            case .ok(let ok): try ok.body.json
            case .noContent: nil
            case .undocumented(let status, _): throw APIError(status: status)
            }
        }
    }

    /// Starts a timer (no stop) or adds a finished entry. The ID is chosen here, so sending the same entry again, e.g.
    /// after a lost answer, is safe: the server answers as it did the first time.
    public func createEntry(id: String, in workspaceId: String, _ request: EntryRequest) async throws -> TimeEntry {
        let body = request.body
        return try await call {
            let create = Components.Schemas.CreateTimeEntryRequest(
                id: id, description: body.description, projectId: body.projectId, taskId: body.taskId,
                tagIds: body.tagIds, isBillable: body.isBillable, shares: body.shares, describeParts: body.describeParts,
                start: body.start, stop: body.stop)
            switch try await api.createTimeEntry(path: .init(workspaceId: workspaceId), body: .json(create)) {
            case .created(let created): return try created.body.json
            case .ok(let again): return try again.body.json
            default: throw APIError(status: 500)
            }
        }
    }

    /// Saves an entry. With the `version` this device saw, only if nothing changed it since; otherwise it throws an
    /// ``APIError`` whose `current` is the entry as it is now.
    public func updateEntry(_ id: String, in workspaceId: String, _ request: EntryRequest, version: String? = nil) async throws -> TimeEntry {
        try await call {
            try await api.updateTimeEntry(
                path: .init(workspaceId: workspaceId, entryId: id), headers: .init(ifMatch: Self.ifMatch(version)), body: .json(request.body)
            ).ok.body.json
        }
    }

    /// Stops a running timer, now or at the given time. Stopping one that already stopped changes nothing. With the
    /// `version` this device saw, as `updateEntry`.
    public func stopEntry(_ id: String, in workspaceId: String, at stop: Date? = nil, version: String? = nil) async throws -> TimeEntry {
        try await call {
            try await api.stopTimeEntry(
                path: .init(workspaceId: workspaceId, entryId: id), headers: .init(ifMatch: Self.ifMatch(version)), body: .json(.init(stop: stop))
            ).ok.body.json
        }
    }

    /// Deletes an entry. With the `version` this device saw, as `updateEntry`.
    public func deleteEntry(_ id: String, in workspaceId: String, version: String? = nil) async throws {
        try await call {
            _ = try await api.deleteTimeEntry(
                path: .init(workspaceId: workspaceId, entryId: id), headers: .init(ifMatch: Self.ifMatch(version))
            ).noContent
        }
    }

    /// The `If-Match` header for a version; none for an entry this device hasn't had from the server. Sent bare, which
    /// the server takes too, as the OpenAPI runtime would percent-encode an entity tag's quotes.
    private static func ifMatch(_ version: String?) -> String? {
        guard let version, !version.isEmpty else { return nil }
        return version
    }

    /// Changes several entries at once, all or none. Nil leaves that part of each entry as it is.
    public func updateEntries(
        _ ids: [String],
        in workspaceId: String,
        description: String? = nil,
        project: (projectId: String?, taskId: String?)? = nil,
        isBillable: Bool? = nil,
        addTagIds: [String]? = nil,
        removeTagIds: [String]? = nil
    ) async throws -> [TimeEntry] {
        let body = Components.Schemas.BulkUpdateTimeEntriesRequest(
            ids: ids,
            description: description,
            project: project.map { .init(projectId: $0.projectId, taskId: $0.taskId) },
            isBillable: isBillable,
            addTagIds: addTagIds,
            removeTagIds: removeTagIds)
        return try await call {
            try await api.bulkUpdateTimeEntries(path: .init(workspaceId: workspaceId), body: .json(body)).ok.body.json
        }
    }

    public func deleteEntries(_ ids: [String], in workspaceId: String) async throws {
        try await call {
            _ = try await api.bulkDeleteTimeEntries(path: .init(workspaceId: workspaceId), body: .json(.init(ids: ids))).noContent
        }
    }

    /// Earlier entries that match what is typed, to start again.
    public func suggestions(in workspaceId: String, search: String, limit: Int = 8) async throws -> [TimeEntrySuggestion] {
        try await call {
            try await api.suggestTimeEntries(
                path: .init(workspaceId: workspaceId),
                query: .init(search: search.isEmpty ? nil : search, limit: Int32(limit))
            ).ok.body.json
        }
    }

    /// Descriptions written before that contain what is typed, those on `projectId` first, to complete it.
    public func descriptions(in workspaceId: String, search: String, projectId: String?, limit: Int = 8) async throws -> [String] {
        try await call {
            try await api.suggestDescriptions(
                path: .init(workspaceId: workspaceId),
                query: .init(search: search.isEmpty ? nil : search, projectId: projectId, limit: Int32(limit))
            ).ok.body.json
        }
    }

    // MARK: - Projects, clients and tags

    /// The workspace's projects the person can see, archived ones included when asked.
    public func projects(in workspaceId: String, includeArchived: Bool = false) async throws -> [Project] {
        try await call {
            try await api.listProjects(path: .init(workspaceId: workspaceId), query: .init(includeArchived: includeArchived)).ok.body.json
        }
    }

    public func clients(in workspaceId: String, includeArchived: Bool = false) async throws -> [WorkspaceClient] {
        try await call {
            try await api.listClients(path: .init(workspaceId: workspaceId), query: .init(includeArchived: includeArchived)).ok.body.json
        }
    }

    public func tags(in workspaceId: String) async throws -> [Tag] {
        try await call { try await api.listTags(path: .init(workspaceId: workspaceId)).ok.body.json }
    }

    public func createTag(named name: String, in workspaceId: String) async throws -> Tag {
        try await call { try await api.createTag(path: .init(workspaceId: workspaceId), body: .json(.init(name: name))).created.body.json }
    }

    /// Creates a project with just a name and a color; the rest can be set on the web app or in the project's page.
    public func createProject(named name: String, color: String, in workspaceId: String) async throws -> Project {
        try await call {
            try await api.createProject(path: .init(workspaceId: workspaceId), body: .json(.init(name: name, color: color))).created.body.json
        }
    }

    // MARK: - Workspace

    public func settings(of workspaceId: String) async throws -> WorkspaceSettings {
        try await call { try await api.getWorkspaceSettings(path: .init(workspaceId: workspaceId)).ok.body.json }
    }
}

/// Project colors to pick from, the same as the web app's, chosen to stay readable as dots and labels in light and
/// dark mode.
public enum ProjectColors {
    public static let all = [
        "#0B83D9", "#9E5BD9", "#D94182", "#E36A00", "#BF7000", "#2DA608", "#06A893", "#C9806B",
        "#465BB3", "#990099", "#C7AF14", "#566614", "#D92B2B", "#525266", "#991102",
    ]

    /// A color for a new project, varied so consecutive projects are told apart.
    public static func next(afterCount count: Int) -> String {
        all[count % all.count]
    }
}
