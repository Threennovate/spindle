import Foundation
import Observation

/// The changes to entries this device made that the server doesn't have yet, kept on disk and sent in order.
///
/// Online, a change is sent at once and whoever made it learns how it went, as if it were sent directly. Without a
/// connection it waits, the app goes on showing it, and it is sent when the server can be reached again.
///
/// A change to an entry that another device changed since this one saw it isn't saved over theirs: it stays, still
/// shown, as a conflict the person settles with ``keepMine(_:)`` or ``keepTheirs(_:)``. Other entries' changes go on.
@MainActor
@Observable
public final class Outbox {
    public struct Pending: Identifiable, Sendable {
        public let seq: Int64
        public var change: EntryChange
        /// The entry as the server has it, when it changed elsewhere since this device saw it and the change waits
        /// for the person to choose. Not kept on disk: sent again after a restart, the change finds it again.
        public var conflict: TimeEntry?
        public var id: Int64 { seq }
    }

    /// What became of a change.
    enum Outcome {
        case sent(TimeEntry?)
        /// It waits for the server to be reachable.
        case queued
        case rejected(Error)
    }

    /// The changes waiting, oldest first.
    public private(set) var pending: [Pending] = []
    /// The server couldn't be reached the last time a change was sent; the changes wait.
    public private(set) var isOffline = false

    /// A change went through: the entry as the server saved it, or nil for one deleted.
    @ObservationIgnored public var onSent: ((EntryChange, TimeEntry?) -> Void)?
    /// The server turned a change down, e.g. because its day is locked; it is dropped. `reported` says whether it
    /// was thrown to whoever made the change, or nobody was waiting any more and the app should say so itself.
    @ObservationIgnored public var onRejected: ((_ change: EntryChange, _ error: Error, _ reported: Bool) -> Void)?
    /// A change found its entry changed elsewhere: the entry as the server has it now. The change stays in
    /// ``conflicts`` until the person chooses.
    @ObservationIgnored public var onConflict: ((_ change: EntryChange, _ current: TimeEntry) -> Void)?

    private let api: SpindleClient
    private let store: LocalStore
    @ObservationIgnored private var sending: Int64?
    @ObservationIgnored private var isFlushing = false
    @ObservationIgnored private var waiters: [Int64: [CheckedContinuation<Outcome, Never>]] = [:]
    @ObservationIgnored private var retry: Task<Void, Never>?
    @ObservationIgnored private var failures = 0

    /// An outbox with the changes left in the store from before.
    public init(api: SpindleClient, store: LocalStore) async {
        self.api = api
        self.store = store
        pending = await store.changes().map { Pending(seq: $0.seq, change: $0.change) }
    }

    /// The changes for display, in order.
    public var changes: [EntryChange] {
        pending.map(\.change)
    }

    /// The changes waiting for the person to choose between them and what another device saved.
    public var conflicts: [Pending] {
        pending.filter { $0.conflict != nil }
    }

    /// Sends a change in conflict anyway, over what the other device saved.
    public func keepMine(_ seq: Int64) async {
        guard let index = pending.firstIndex(where: { $0.seq == seq }), let current = pending[index].conflict else { return }
        pending[index].change = pending[index].change.on(version: current.version)
        pending[index].conflict = nil
        try? await store.replace(seq, with: pending[index].change)
        await flush()
    }

    /// Drops a change in conflict, and this device's later changes to the same entry, keeping what the other device
    /// saved.
    public func keepTheirs(_ seq: Int64) async {
        guard let conflicted = pending.first(where: { $0.seq == seq }), conflicted.conflict != nil else { return }
        let dropped = pending.filter { $0.seq >= seq && $0.change.entryId == conflicted.change.entryId }
        pending.removeAll { item in dropped.contains { $0.seq == item.seq } }
        for item in dropped {
            resolve(item.seq, .queued)
            await store.removeChange(item.seq)
        }
    }

    /// Queues a change and sends it. Returns the entry as the server saved it once it is through, or nil when the
    /// server can't be reached and the change waits. Throws the server's reason when it turns the change down.
    @discardableResult
    public func submit(_ change: EntryChange) async throws -> TimeEntry? {
        let seq: Int64
        if let index = pending.lastIndex(where: { $0.change.entryId == change.entryId }), pending[index].seq != sending,
           let merged = pending[index].change.merged(with: change) {
            let earlier = pending[index]
            guard let merged else {
                // Created and deleted before either was sent: the server never needs to hear of it.
                pending.remove(at: index)
                await store.removeChange(earlier.seq)
                resolve(earlier.seq, .sent(nil))
                return nil
            }
            pending[index].change = merged
            try await store.replace(earlier.seq, with: merged)
            seq = earlier.seq
        } else {
            seq = try await store.append(change)
            pending.append(Pending(seq: seq, change: change))
        }

        if isOffline || pending.contains(where: { $0.conflict != nil && $0.change.entryId == change.entryId }) {
            // Known to be offline, or the entry waits for the person to settle a conflict: no need to wait.
            Task { await self.flush() }
            return nil
        }
        let outcome = await withCheckedContinuation { continuation in
            waiters[seq, default: []].append(continuation)
            Task { await self.flush() }
        }
        switch outcome {
        case .sent(let entry): return entry
        case .queued: return nil
        case .rejected(let error): throw error
        }
    }

    /// Sends the waiting changes in order, until they are through or the server can't be reached.
    public func flush() async {
        guard !isFlushing else { return }
        isFlushing = true
        defer { isFlushing = false }
        retry?.cancel()

        // Changes after one in conflict, to the same entry, wait for it; the others go on.
        while let next = pending.first(where: { item in !pending.contains { $0.conflict != nil && $0.change.entryId == item.change.entryId } }) {
            sending = next.seq
            do {
                let saved = try await send(next.change)
                sending = nil
                await didSend(next, saved)
            } catch {
                sending = nil
                let error = error.asSpindleError
                if let changed = error as? APIError, changed.isEntryChanged, let current = changed.current {
                    isOffline = false
                    failures = 0
                    if next.change.isApplied(to: current) {
                        await didSend(next, current)
                    } else if case .stop = next.change, current.isRunning, next.change.version != current.version {
                        // Changed while it ran, e.g. renamed elsewhere: stopping it overwrites nothing of that.
                        await rebase(next.change.entryId, from: next.seq, to: current.version)
                    } else if let index = pending.firstIndex(where: { $0.seq == next.seq }) {
                        pending[index].conflict = current
                        onConflict?(next.change, current)
                        // Whoever made the change learns it waits, as it does for the person to choose.
                        resolve(next.seq, .queued)
                    }
                    continue
                }
                if Self.isWorthRetrying(error) {
                    isOffline = error is ConnectionError
                    failures += 1
                    // Everyone waiting learns that their change waits; it goes when the server is back.
                    for seq in Array(waiters.keys) {
                        resolve(seq, .queued)
                    }
                    scheduleRetry()
                    return
                }
                remove(next.seq)
                isOffline = false
                onRejected?(next.change, error, waiters[next.seq]?.isEmpty == false)
                resolve(next.seq, .rejected(error))
                await store.removeChange(next.seq)
            }
        }
    }

    /// No connection, the server failing, or signing in again first: the change is fine, the moment isn't.
    static func isWorthRetrying(_ error: Error) -> Bool {
        if error is ConnectionError { return true }
        guard let error = error as? APIError else { return true }
        return error.status >= 500 || error.status == 401 || error.status == 408 || error.status == 429
    }

    /// The server has a change: its answer becomes what is shown, and the entry's later changes are made on it.
    private func didSend(_ sent: Pending, _ saved: TimeEntry?) async {
        // The server's answer goes in before the change comes out, so what is shown doesn't flicker.
        onSent?(sent.change, saved)
        remove(sent.seq)
        isOffline = false
        failures = 0
        resolve(sent.seq, .sent(saved))
        await store.removeChange(sent.seq)
        if let saved {
            await rebase(sent.change.entryId, from: sent.seq, to: saved.version)
        }
    }

    /// The entry's changes from `seq` on were made on top of what the server now has as `version`.
    private func rebase(_ entryId: String, from seq: Int64, to version: String) async {
        for index in pending.indices where pending[index].seq >= seq && pending[index].change.entryId == entryId {
            pending[index].change = pending[index].change.on(version: version)
            try? await store.replace(pending[index].seq, with: pending[index].change)
        }
    }

    private func send(_ change: EntryChange) async throws -> TimeEntry? {
        switch change {
        case .create(let request, let id, let workspaceId):
            return try await api.createEntry(id: id, in: workspaceId, request)
        case .update(let request, let id, let workspaceId, let version):
            return try await api.updateEntry(id, in: workspaceId, request, version: version)
        case .stop(let at, let id, let workspaceId, let version):
            return try await api.stopEntry(id, in: workspaceId, at: at, version: version)
        case .delete(let id, let workspaceId, let version):
            do {
                try await api.deleteEntry(id, in: workspaceId, version: version)
            } catch let error as APIError where error.status == 404 {
                // Gone already, e.g. deleted on another device or this delete was sent before.
            }
            return nil
        }
    }

    /// Tries again after a while, waiting longer after each failure, up to a minute.
    private func scheduleRetry() {
        retry?.cancel()
        let delay = [2.0, 5, 15, 30, 60][min(failures - 1, 4)]
        retry = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled else { return }
            await self?.flush()
        }
    }

    private func remove(_ seq: Int64) {
        pending.removeAll { $0.seq == seq }
    }

    private func resolve(_ seq: Int64, _ outcome: Outcome) {
        for waiter in waiters.removeValue(forKey: seq) ?? [] {
            waiter.resume(returning: outcome)
        }
    }
}
