import Foundation
import SpindleAPI

public typealias AgentActivitySettings = Components.Schemas.AgentActivitySettingsResponse
public typealias RepositoryLink = Components.Schemas.RepositoryLinkResponse

extension RepositoryLink {
    /// The link's branch rules, in order.
    public var rules: [BranchRule] {
        branchRules.map { BranchRule(pattern: $0.pattern, projectId: $0.projectId, taskId: $0.taskId) }
    }
}

extension SpindleClient {
    // MARK: - Agent activity, the person's own

    /// Turns uploading agent activity on or off on all the person's Macs. Off deletes what was uploaded.
    public func updateAgentActivitySettings(sync: Bool, titles: Bool) async throws -> AgentActivitySettings {
        try await call {
            try await api.updateAgentActivitySettings(body: .json(.init(sync: sync, titles: titles))).ok.body.json
        }
    }

    /// Sends sessions this Mac read from agent logs, each replacing what this Mac sent for it before.
    public func uploadAgentSessions(_ sessions: [AgentSessionUpload], device: String) async throws {
        let body = Components.Schemas.AgentActivityUploadRequest(deviceId: device, sessions: sessions.map { session in
            .init(
                source: session.source.rawValue,
                sessionId: session.sessionID,
                parentSessionId: session.parentSessionID,
                repository: session.repository.map { .init(identity: $0.identity, name: $0.name) },
                title: session.title,
                branches: session.branches,
                touches: session.touches,
                runs: session.runs.map { .init(start: $0.start, end: $0.end) },
                parserVersion: Int32(session.parserVersion))
        })
        try await call { _ = try await api.uploadAgentSessions(body: .json(body)).ok }
    }

    /// The projects the person chose for their repositories, in every workspace.
    /// Repositories the person said go to no project come along without a project or workspace.
    public func repositoryLinks() async throws -> [RepositoryLink] {
        try await call { try await api.listRepositoryLinks(query: .init(includeNoProject: true)).ok.body.json }
    }

    /// Links a repository to a project of the workspace, or with no project forgets the link, unless `noProject` says
    /// its work goes to no project from now on. Branch rules, when given, replace the link's; nil keeps them.
    public func linkRepository(identity: String, name: String, to projectId: String?, noProject: Bool = false, in workspaceId: String,
                               branchRules: [BranchRule]? = nil) async throws {
        try await call {
            _ = try await api.linkRepository(
                path: .init(workspaceId: workspaceId),
                body: .json(.init(repository: .init(identity: identity, name: name), projectId: projectId, noProject: noProject ? true : nil,
                                  branchRules: branchRules?.map { .init(pattern: $0.pattern, projectId: $0.projectId, taskId: $0.taskId) })))
                .noContent
        }
    }

    /// The repositories the person ignores, as the hashes their devices sent (`IgnoredRepositories.hash`).
    public func ignoredRepositories() async throws -> [String] {
        try await call { try await api.listIgnoredRepositories().ok.body.json }
    }

    /// Ignores a repository on all the person's devices, named by its hash alone. The server deletes what was uploaded
    /// or linked for it.
    public func ignoreRepository(hash: String) async throws {
        try await call { _ = try await api.ignoreRepository(path: .init(hash: hash)).noContent }
    }

    /// Stops ignoring a repository on all the person's devices.
    public func unignoreRepository(hash: String) async throws {
        try await call { _ = try await api.unignoreRepository(path: .init(hash: hash)).noContent }
    }

    /// Entries the person said not to suggest a split for, on any device, newest first.
    public func dismissedSplitSuggestions() async throws -> [String] {
        try await call { try await api.listSplitSuggestionDismissals().ok.body.json }
    }

    /// Stops suggesting a split for one of the person's entries, on all their devices.
    public func dismissSplitSuggestion(entryId: String, in workspaceId: String) async throws {
        try await call {
            _ = try await api.dismissSplitSuggestion(path: .init(workspaceId: workspaceId, entryId: entryId)).noContent
        }
    }
}
