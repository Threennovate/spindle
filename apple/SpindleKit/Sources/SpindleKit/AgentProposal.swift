import Foundation

/// Entry or change an agent proposes through the server's MCP, waiting there until the person adds it or throws it away.
/// Agents may write descriptions directly, but time, projects and splits are the person's to confirm, so a new
/// entry or a change to those is only ever a proposal.
public struct AgentProposal: Identifiable, Codable, Sendable, Hashable {
    public let id: String
    public let workspaceId: String
    /// The entry to change, or nil for a new entry.
    public let entryId: String?
    /// The entry as it was when the change was proposed, to tell whether it changed since.
    public let original: EntryRequest?
    public var fields: Fields
    /// Why the agent proposes it, in its words, e.g. "Commits on fix/apns between 14:02 and 15:40".
    public let reason: String
    /// Who proposed it, as the agent named itself.
    public let client: String?
    public let proposedAt: Date

    public init(id: String = newID(), workspaceId: String, entryId: String?, original: EntryRequest?, fields: Fields,
                reason: String, client: String?, proposedAt: Date) {
        self.id = id
        self.workspaceId = workspaceId
        self.entryId = entryId
        self.original = original
        self.fields = fields
        self.reason = reason
        self.client = client
        self.proposedAt = proposedAt
    }

    public var isNew: Bool { entryId == nil }

    /// What the proposal sets; nil leaves that part of the entry as it is. A new entry has all of them.
    public struct Fields: Codable, Sendable, Hashable {
        public var start: Date?
        public var stop: Date?
        public var description: String?
        public var work: Work?

        public init(start: Date? = nil, stop: Date? = nil, description: String? = nil, work: Work? = nil) {
            self.start = start
            self.stop = stop
            self.description = description
            self.work = work
        }
    }

    /// Where the time goes: one project (and task), or parts of several.
    public struct Work: Codable, Sendable, Hashable {
        public var projectId: String?
        public var taskId: String?
        public var isBillable: Bool
        /// Two or more parts when split, otherwise empty.
        public var shares: [SplitPart]

        public init(projectId: String?, taskId: String?, isBillable: Bool, shares: [SplitPart] = []) {
            self.projectId = projectId
            self.taskId = taskId
            self.isBillable = isBillable
            self.shares = shares
        }

        public var parts: [SplitPart] {
            shares.count > 1 ? shares : [SplitPart(projectId: projectId, taskId: taskId, isBillable: isBillable, percent: 100)]
        }
    }

    /// What saving the proposal sends: the entry as it is now with the proposed parts, or the new entry. A split whose
    /// parts have their own descriptions is saved with them, the entry's description made of them; one description
    /// proposed for a split is one for all of its parts again.
    public func request(current: TimeEntry?) -> EntryRequest {
        var request = current.map(EntryRequest.init) ?? EntryRequest(details: .empty, start: fields.start ?? Date(), stop: fields.stop)
        if let start = fields.start { request.start = start }
        if let stop = fields.stop { request.stop = stop }
        if let description = fields.description {
            request.details.description = description
            request.details.shares = request.details.shares.map { part in
                var part = part
                part.description = nil
                return part
            }
        }
        if let work = fields.work {
            var details = request.details
            details.projectId = work.projectId
            details.taskId = work.taskId
            details.isBillable = work.isBillable
            details.shares = []
            request.details = work.shares.count > 1 ? details.splitting(work.shares) : details
        }
        if request.details.isSplit {
            let described = request.details.shares.contains { $0.description != nil }
            request.describeParts = described
            if described {
                request.details.shares = request.details.shares.map { part in
                    var part = part
                    part.description = part.description ?? ""
                    return part
                }
                request.details.description = SplitPart.joinedDescriptions(request.details.shares)
            }
        }
        return request
    }
}

/// Something about a proposal the person should know before adding it, as the server words it. A blocking one keeps it
/// from being added as it is; it can still be edited or thrown away.
public enum AgentProposals {
    public struct Issue: Sendable, Hashable {
        public let message: String
        public let blocking: Bool

        public init(message: String, blocking: Bool) {
            self.message = message
            self.blocking = blocking
        }
    }
}
