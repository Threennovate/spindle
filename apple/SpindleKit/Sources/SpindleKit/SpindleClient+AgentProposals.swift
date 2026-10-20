import Foundation
import SpindleAPI

/// The proposals waiting in a workspace, as the server lists them, with what stands in each one's way now in the
/// server's words: blocking issues keep it from being saved as it is, warnings are for the person to know first.
public struct ServerProposals: Sendable, Hashable {
    public var pending: [AgentProposal]
    public var issues: [String: [AgentProposals.Issue]]

    public init(pending: [AgentProposal] = [], issues: [String: [AgentProposals.Issue]] = [:]) {
        self.pending = pending
        self.issues = issues
    }

    init(_ response: Components.Schemas.AgentProposalsResponse) {
        pending = response.pending.map(AgentProposal.init)
        issues = Dictionary(uniqueKeysWithValues: response.pending.map { proposal in
            (proposal.id, proposal.blocking.map { AgentProposals.Issue(message: $0, blocking: true) }
                + proposal.warnings.map { AgentProposals.Issue(message: $0, blocking: false) })
        })
    }
}

extension AgentProposal {
    /// A proposal as the server has it.
    init(_ response: Components.Schemas.AgentProposalResponse) {
        func parts(_ shares: [Components.Schemas.TimeShareResponse]) -> [SplitPart] {
            shares.map { SplitPart(projectId: $0.projectId, taskId: $0.taskId, isBillable: $0.isBillable, percent: Int($0.percent), description: $0.description) }
        }
        let original = response.original.map { entry in
            EntryRequest(details: EntryDetails(description: entry.description, projectId: entry.projectId, taskId: entry.taskId,
                                               isBillable: entry.isBillable, shares: entry.shares.count > 1 ? parts(entry.shares) : []),
                         start: entry.start, stop: entry.stop)
        }
        let work = response.fields.work.map { work in
            Work(projectId: work.projectId, taskId: work.taskId, isBillable: work.isBillable, shares: work.shares.count > 1 ? parts(work.shares) : [])
        }
        self.init(id: response.id, workspaceId: response.workspaceId, entryId: response.entryId, original: original,
                  fields: Fields(start: response.fields.start, stop: response.fields.stop, description: response.fields.description, work: work),
                  reason: response.reason, client: response.client, proposedAt: response.proposedAt)
    }
}

extension SpindleClient {
    /// The proposals waiting in the workspace, oldest first.
    public func agentProposals(in workspaceId: String) async throws -> ServerProposals {
        try await call { ServerProposals(try await api.listAgentProposals(path: .init(workspaceId: workspaceId)).ok.body.json) }
    }

    /// Saves a proposal as it is, through the same checks as saving an entry; returns the entry it became.
    @discardableResult
    public func applyAgentProposal(_ id: String, in workspaceId: String) async throws -> TimeEntry {
        try await call { try await api.applyAgentProposal(path: .init(workspaceId: workspaceId, proposalId: id)).ok.body.json.entry }
    }

    /// The person saved the proposal after editing it, as the entry `entryId`.
    public func savedAgentProposal(_ id: String, as entryId: String?, in workspaceId: String) async throws {
        try await call {
            _ = try await api.resolveAgentProposal(path: .init(workspaceId: workspaceId, proposalId: id),
                                                   body: .json(.init(outcome: "saved", entryId: entryId))).noContent
        }
    }

    public func discardAgentProposal(_ id: String, in workspaceId: String) async throws {
        try await call {
            _ = try await api.resolveAgentProposal(path: .init(workspaceId: workspaceId, proposalId: id),
                                                   body: .json(.init(outcome: "discarded"))).noContent
        }
    }

    /// Puts a proposal back to wait, once the person took back what saving it did.
    public func reopenAgentProposal(_ id: String, in workspaceId: String) async throws {
        try await call { _ = try await api.reopenAgentProposal(path: .init(workspaceId: workspaceId, proposalId: id)).noContent }
    }
}
