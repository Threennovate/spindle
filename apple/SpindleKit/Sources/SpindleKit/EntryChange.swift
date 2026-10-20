import Foundation
import SpindleAPI

/// A change to an entry that the server hasn't confirmed yet. Changes wait in the outbox, in order, until they are
/// sent; the entries shown are the server's with these applied on top.
///
/// A change to an existing entry carries the entry's `version` as this device last had it from the server, so it
/// goes through only if nothing else changed the entry since. Nil for a change queued by an app that predates
/// versions, or an entry the server hasn't confirmed yet; such a change goes through as it is.
public enum EntryChange: Codable, Hashable, Sendable {
    /// A new entry, or a timer started, with the ID the device chose.
    case create(EntryRequest, id: String, workspaceId: String)
    case update(EntryRequest, id: String, workspaceId: String, version: String? = nil)
    case stop(at: Date, id: String, workspaceId: String, version: String? = nil)
    case delete(id: String, workspaceId: String, version: String? = nil)

    public var entryId: String {
        switch self {
        case .create(_, let id, _), .update(_, let id, _, _), .stop(_, let id, _, _), .delete(let id, _, _): id
        }
    }

    public var workspaceId: String {
        switch self {
        case .create(_, _, let workspaceId), .update(_, _, let workspaceId, _), .stop(_, _, let workspaceId, _), .delete(_, let workspaceId, _):
            workspaceId
        }
    }

    /// The version of the entry this change was made on.
    public var version: String? {
        switch self {
        case .create: nil
        case .update(_, _, _, let version), .stop(_, _, _, let version), .delete(_, _, let version): version
        }
    }

    /// The same change, made on another version of the entry: one saved since by an earlier change from this
    /// device, or the one the person chose to change anyway.
    func on(version: String?) -> EntryChange {
        switch self {
        case .create: self
        case .update(let request, let id, let workspaceId, _): .update(request, id: id, workspaceId: workspaceId, version: version)
        case .stop(let at, let id, let workspaceId, _): .stop(at: at, id: id, workspaceId: workspaceId, version: version)
        case .delete(let id, let workspaceId, _): .delete(id: id, workspaceId: workspaceId, version: version)
        }
    }

    /// This change followed by `next`, to the same entry, as one; nil when they cancel out, e.g. an entry created
    /// and deleted before either was sent. Returns `.some(next)` when they can't be joined. The joined change is made
    /// on this one's version, which `next` was made on top of.
    func merged(with next: EntryChange) -> EntryChange?? {
        switch (self, next) {
        case (.create, .delete):
            return .some(nil)
        case (.create(_, let id, let workspaceId), .update(let request, _, _, _)):
            return .create(request, id: id, workspaceId: workspaceId)
        case (.create(var request, let id, let workspaceId), .stop(let at, _, _, _)):
            request.stop = max(at, request.start)
            return .create(request, id: id, workspaceId: workspaceId)
        case (.update, .update), (.stop, .update), (.update, .delete), (.stop, .delete):
            return next.on(version: version)
        case (.update(var request, let id, let workspaceId, let version), .stop(let at, _, _, _)):
            request.stop = max(at, request.start)
            return .update(request, id: id, workspaceId: workspaceId, version: version)
        default:
            return nil
        }
    }

    /// Whether the entry as the server has it already shows this change, e.g. one saved before its answer was lost.
    func isApplied(to entry: TimeEntry) -> Bool {
        guard case .update = self else { return false }
        let applied = Self.apply([self], to: [entry], running: nil, workspaceId: entry.workspaceId) { _ in entry.isBillable }
        return applied.entries.first == entry
    }
}

extension EntryChange {
    /// The entries as they will be once these changes are through: `entries` and `running` as the server has them,
    /// with the changes applied in order. `workspaceId` picks the workspace whose entries are shown; the running
    /// entry may be in any.
    static func apply(
        _ changes: [EntryChange],
        to entries: [TimeEntry],
        running: TimeEntry?,
        workspaceId: String,
        billable: (String?) -> Bool
    ) -> (entries: [TimeEntry], running: TimeEntry?) {
        var byId: [String: TimeEntry] = [:]
        var order: [String] = []
        for entry in entries where byId[entry.id] == nil {
            byId[entry.id] = entry
            order.append(entry.id)
        }
        var running = running

        func put(_ entry: TimeEntry) {
            if byId[entry.id] == nil { order.append(entry.id) }
            byId[entry.id] = entry
        }

        for change in changes {
            switch change {
            case .create(let request, let id, let workspace):
                let entry = TimeEntry(
                    id: id, workspaceId: workspace, description: request.trimmedDescription,
                    projectId: request.details.projectId, taskId: request.details.taskId, tagIds: request.details.tagIds,
                    isBillable: request.details.isBillable ?? billable(request.details.projectId),
                    start: request.start, stop: request.stop, shares: request.shownShares,
                    // None until the server has it.
                    version: "")
                if entry.isRunning {
                    // The server stops the timer that is running where the new one starts.
                    if var previous = running, previous.id != id {
                        previous.stop = max(previous.start, request.start)
                        if byId[previous.id] != nil || previous.workspaceId == workspaceId { put(previous) }
                    }
                    running = entry
                }
                put(entry)
            case .update(let request, let id, _, _):
                guard var entry = byId[id] ?? (running?.id == id ? running : nil) else { continue }
                entry.description = request.trimmedDescription
                entry.projectId = request.details.projectId
                entry.taskId = request.details.taskId
                entry.tagIds = request.details.tagIds
                entry.isBillable = request.details.isBillable ?? entry.isBillable
                entry.shares = request.shownShares
                entry.start = request.start
                entry.stop = request.stop
                if byId[id] != nil { put(entry) }
                if entry.isRunning {
                    running = entry
                } else if running?.id == id {
                    running = nil
                }
            case .stop(let at, let id, _, _):
                if var entry = byId[id] ?? (running?.id == id ? running : nil), entry.isRunning {
                    entry.stop = max(at, entry.start)
                    if byId[id] != nil { put(entry) }
                }
                if running?.id == id { running = nil }
            case .delete(let id, _, _):
                byId[id] = nil
                if running?.id == id { running = nil }
            }
        }
        let shown = order.compactMap { byId[$0] }.filter { $0.workspaceId == workspaceId }
        return (shown, running)
    }
}

extension EntryRequest {
    /// The description the server will show: the parts' own joined, when they are saved with descriptions.
    var trimmedDescription: String {
        describeParts == true && details.isSplit
            ? SplitPart.joinedDescriptions(details.shares)
            : details.description.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// The parts as the server will keep them: with their own descriptions only when those are saved or kept.
    var shownShares: [Components.Schemas.TimeShareResponse] {
        details.shares.map { part in
            var part = part
            if describeParts == false { part.description = nil }
            if describeParts == true { part.description = part.description ?? "" }
            return part.response
        }
    }
}
