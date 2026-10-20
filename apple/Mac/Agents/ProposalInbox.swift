import Foundation
import Observation
import SpindleKit

/// Entries and changes the person's agents proposed, waiting on the server for the person to add, edit or discard
/// them, in the workspace shown. The server says what stands in each one's way, saves them through the same checks as
/// an entry, and lets them go after a week; this reads them again when they change on the server (sync), after each
/// answer, when the workspace changes and every few minutes.
@Observable
final class ProposalInbox {
    private let model: AppModel

    /// The proposals waiting in the workspace shown, oldest first.
    private(set) var all: [AgentProposal] = []
    /// Whether the review sheet is open.
    var reviewing = false
    /// Proposals added or applied since the review opened, which it can still take back.
    private(set) var done: [Done] = []
    /// What stands in each proposal's way, in the server's words.
    private var issuesByID: [String: [AgentProposals.Issue]] = [:]

    /// Proposals arrived from an agent: how many, and who sent them.
    @ObservationIgnored var onArrival: ((Int, String?) -> Void)?
    @ObservationIgnored private var loadedFor: Key?
    @ObservationIgnored private var hasLoaded = false
    @ObservationIgnored private var seen: Set<String> = []
    @ObservationIgnored private var revision = -1
    @ObservationIgnored private var loadedAt = Date.distantPast
    @ObservationIgnored private var loading = false

    /// Whose proposals, in which workspace.
    private struct Key: Equatable {
        let account: ObjectIdentifier
        let workspaceId: String
    }

    /// How often they are read again when nothing said they changed, e.g. while sync was disconnected.
    private static let interval: TimeInterval = 300
    /// How long to wait before trying again when they couldn't be read yet, e.g. offline or on a server without them.
    private static let retry: TimeInterval = 30

    struct Done: Identifiable {
        var id: String { proposal.id }
        let proposal: AgentProposal
        /// The entry added or changed.
        let entryId: String
        /// The entry before the change was applied; nil for an added entry.
        let before: EntryRequest?
    }

    struct Failure: LocalizedError {
        let errorDescription: String?
    }

    init(model: AppModel) {
        self.model = model
    }

    /// The proposals waiting in the tracker's workspace.
    func pending(in tracker: Tracker) -> [AgentProposal] {
        all.filter { $0.workspaceId == tracker.workspace.id }
    }

    /// The change waiting for an entry, if any.
    func change(for entryId: String) -> AgentProposal? {
        all.first { $0.entryId == entryId }
    }

    /// What stands in the way of a proposal now, as the server said when it was last read.
    func issues(_ proposal: AgentProposal, tracker: Tracker) -> [AgentProposals.Issue] {
        issuesByID[proposal.id] ?? []
    }

    /// The days the proposals fall on, for the entries already there to be loaded and compared with.
    func days(of proposals: [AgentProposal], tracker: Tracker) -> [DayKey] {
        let starts = proposals.compactMap { $0.fields.start ?? $0.original?.start }
        let stops = proposals.compactMap { $0.fields.stop ?? $0.original?.stop }
        guard let first = (starts + stops).min(), let last = (starts + stops).max() else { return [] }
        let from = tracker.time.day(of: first)
        let count = tracker.time.days(from: from, to: tracker.time.day(of: last))
        return (0...max(0, count)).map { tracker.time.adding($0, to: from) }
    }

    // MARK: - Reading them

    /// Reads the proposals again when the account or workspace changed, the server said they changed, or a while
    /// passed. Called every few seconds; never while a view draws.
    func sync(now: Date = Date()) {
        guard let account = model.account, let tracker = model.tracker else {
            if loadedFor != nil { reset(nil) }
            return
        }
        let key = Key(account: ObjectIdentifier(account), workspaceId: tracker.workspace.id)
        if key != loadedFor { reset(key) }
        let since = now.timeIntervalSince(loadedAt)
        guard !loading, hasLoaded ? revision != model.agentProposalsRevision || since > Self.interval : since > Self.retry
        else { return }
        Task { await refresh() }
    }

    /// Reads the workspace's proposals from the server now. Offline, what was read last stays.
    func refresh() async {
        guard let account = model.account, let tracker = model.tracker else { return }
        let key = Key(account: ObjectIdentifier(account), workspaceId: tracker.workspace.id)
        if key != loadedFor { reset(key) }
        loading = true
        defer { loading = false }
        revision = model.agentProposalsRevision
        loadedAt = Date()
        guard let list = try? await model.client.agentProposals(in: key.workspaceId), loadedFor == key else { return }
        let arrived = list.pending.filter { !seen.contains($0.id) }
        all = list.pending
        issuesByID = list.issues
        seen.formUnion(list.pending.map(\.id))
        // Those waiting when the app first reads them aren't news.
        if hasLoaded, !arrived.isEmpty { onArrival?(arrived.count, arrived.last?.client) }
        hasLoaded = true
    }

    private func reset(_ key: Key?) {
        loadedFor = key
        hasLoaded = false
        loadedAt = .distantPast
        seen = []
        all = []
        issuesByID = [:]
        done = []
        reviewing = false
    }

    // MARK: - The person's answer

    /// Saves the proposal as it is, on the server, which checks it as an entry.
    func accept(_ proposal: AgentProposal, tracker: Tracker) async throws {
        try await apply(proposal, tracker: tracker)
        await tracker.refresh()
        await refresh()
    }

    private func apply(_ proposal: AgentProposal, tracker: Tracker) async throws {
        let before = proposal.entryId.flatMap(tracker.entry(id:)).map(EntryRequest.init)
        let entry = try await model.client.applyAgentProposal(proposal.id, in: tracker.workspace.id)
        all.removeAll { $0.id == proposal.id }
        done.append(Done(proposal: proposal, entryId: entry.id, before: before))
    }

    /// Adds every proposal of the workspace that nothing stands in the way of; says how many it added and how many
    /// it couldn't.
    func acceptAll(in tracker: Tracker) async -> (added: Int, failed: Int) {
        let ready = pending(in: tracker).filter { proposal in !issues(proposal, tracker: tracker).contains(where: \.blocking) }
        var added = 0
        var failed = 0
        for proposal in ready {
            do {
                try await apply(proposal, tracker: tracker)
                added += 1
            } catch {
                failed += 1
            }
        }
        await tracker.refresh()
        await refresh()
        return (added, failed)
    }

    /// The person saved the proposal in the editor, as they changed it, as the entry `entryId`.
    func saved(_ proposal: AgentProposal, as entryId: String?) {
        all.removeAll { $0.id == proposal.id }
        answer { try await $0.savedAgentProposal(proposal.id, as: entryId ?? proposal.entryId, in: proposal.workspaceId) }
    }

    func discard(_ proposal: AgentProposal) {
        all.removeAll { $0.id == proposal.id }
        answer { try await $0.discardAgentProposal(proposal.id, in: proposal.workspaceId) }
    }

    func discardAll(in tracker: Tracker) {
        for proposal in pending(in: tracker) { discard(proposal) }
    }

    /// Tells the server, then reads the proposals again; a refusal shows, and the proposal comes back if it still waits.
    private func answer(_ call: @escaping (SpindleClient) async throws -> Void) {
        let client = model.client
        Task {
            do { try await call(client) } catch {
                model.notices.show(error, fallback: "Couldn't answer the proposal.")
            }
            await refresh()
        }
    }

    /// Takes back an entry added or a change applied in this review, and puts the proposal back.
    func undo(_ item: Done, tracker: Tracker) async throws {
        if let before = item.before {
            guard let entry = tracker.entry(id: item.entryId) else { throw Failure(errorDescription: "The entry is gone.") }
            try await tracker.update(entry, to: before)
        } else {
            try await tracker.delete(id: item.entryId)
        }
        done.removeAll { $0.id == item.id }
        try await model.client.reopenAgentProposal(item.proposal.id, in: item.proposal.workspaceId)
        await refresh()
    }

    /// The review closed: what was done there stays done.
    func endReview() {
        reviewing = false
        done = []
    }
}
