import Foundation
import SpindleAPI

/// A project as it is edited: everything the API takes.
public struct ProjectFields: Hashable, Sendable {
    public var name: String
    public var clientId: String?
    public var color: String
    public var isPrivate: Bool
    public var isBillable: Bool
    public var estimateSeconds: Int64?
    public var hourlyRate: Double?

    public init(name: String = "", clientId: String? = nil, color: String, isPrivate: Bool = false, isBillable: Bool = false, estimateSeconds: Int64? = nil, hourlyRate: Double? = nil) {
        self.name = name
        self.clientId = clientId
        self.color = color
        self.isPrivate = isPrivate
        self.isBillable = isBillable
        self.estimateSeconds = estimateSeconds
        self.hourlyRate = hourlyRate
    }

    public init(_ project: Project) {
        self.init(
            name: project.name, clientId: project.clientId, color: project.color, isPrivate: project.isPrivate,
            isBillable: project.isBillable, estimateSeconds: project.estimateSeconds, hourlyRate: project.hourlyRate)
    }

    var body: Components.Schemas.ProjectRequest {
        .init(
            name: name.trimmingCharacters(in: .whitespacesAndNewlines), clientId: clientId, color: color, isPrivate: isPrivate,
            isBillable: isBillable, estimateSeconds: estimateSeconds, hourlyRate: hourlyRate)
    }
}

extension SpindleClient {
    // MARK: - Projects and tasks

    public func project(_ id: String, in workspaceId: String) async throws -> Project {
        try await call { try await api.getProject(path: .init(workspaceId: workspaceId, projectId: id)).ok.body.json }
    }

    /// Creates a project, or saves one when `id` is given. Every field is saved as it is; a rate left out means none.
    public func saveProject(_ fields: ProjectFields, id: String? = nil, in workspaceId: String) async throws -> Project {
        try await call {
            if let id {
                try await api.updateProject(path: .init(workspaceId: workspaceId, projectId: id), body: .json(fields.body)).ok.body.json
            } else {
                try await api.createProject(path: .init(workspaceId: workspaceId), body: .json(fields.body)).created.body.json
            }
        }
    }

    /// Archives a project, so it isn't offered for new time but stays in reports, or restores it.
    public func setProjectArchived(_ id: String, _ archived: Bool, in workspaceId: String) async throws -> Project {
        try await call {
            if archived {
                try await api.archiveProject(path: .init(workspaceId: workspaceId, projectId: id)).ok.body.json
            } else {
                try await api.restoreProject(path: .init(workspaceId: workspaceId, projectId: id)).ok.body.json
            }
        }
    }

    /// Deletes a project and its tasks; the time tracked on it stays, without a project.
    public func deleteProject(_ id: String, in workspaceId: String) async throws {
        try await call { _ = try await api.deleteProject(path: .init(workspaceId: workspaceId, projectId: id)).noContent }
    }

    public func saveTask(named name: String, estimateSeconds: Int64? = nil, isDone: Bool = false, id: String? = nil, project projectId: String, in workspaceId: String) async throws -> ProjectTask {
        let body = Components.Schemas.TaskRequest(name: name.trimmingCharacters(in: .whitespacesAndNewlines), estimateSeconds: estimateSeconds, isDone: isDone)
        return try await call {
            if let id {
                try await api.updateTask(path: .init(workspaceId: workspaceId, projectId: projectId, taskId: id), body: .json(body)).ok.body.json
            } else {
                try await api.createTask(path: .init(workspaceId: workspaceId, projectId: projectId), body: .json(body)).created.body.json
            }
        }
    }

    /// Deletes a task; the time tracked on it stays on its project.
    public func deleteTask(_ id: String, project projectId: String, in workspaceId: String) async throws {
        try await call { _ = try await api.deleteTask(path: .init(workspaceId: workspaceId, projectId: projectId, taskId: id)).noContent }
    }

    // MARK: - Clients

    public func saveClient(named name: String, id: String? = nil, in workspaceId: String) async throws -> WorkspaceClient {
        let body = Components.Schemas.ClientRequest(name: name.trimmingCharacters(in: .whitespacesAndNewlines))
        return try await call {
            if let id {
                try await api.updateClient(path: .init(workspaceId: workspaceId, clientId: id), body: .json(body)).ok.body.json
            } else {
                try await api.createClient(path: .init(workspaceId: workspaceId), body: .json(body)).created.body.json
            }
        }
    }

    public func setClientArchived(_ id: String, _ archived: Bool, in workspaceId: String) async throws -> WorkspaceClient {
        try await call {
            if archived {
                try await api.archiveClient(path: .init(workspaceId: workspaceId, clientId: id)).ok.body.json
            } else {
                try await api.restoreClient(path: .init(workspaceId: workspaceId, clientId: id)).ok.body.json
            }
        }
    }

    public func deleteClient(_ id: String, in workspaceId: String) async throws {
        try await call { _ = try await api.deleteClient(path: .init(workspaceId: workspaceId, clientId: id)).noContent }
    }

    // MARK: - Tags

    public func renameTag(_ id: String, to name: String, in workspaceId: String) async throws -> Tag {
        try await call {
            try await api.updateTag(path: .init(workspaceId: workspaceId, tagId: id), body: .json(.init(name: name.trimmingCharacters(in: .whitespacesAndNewlines)))).ok.body.json
        }
    }

    /// Deletes a tag; entries lose it.
    public func deleteTag(_ id: String, in workspaceId: String) async throws {
        try await call { _ = try await api.deleteTag(path: .init(workspaceId: workspaceId, tagId: id)).noContent }
    }
}
