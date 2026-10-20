import Foundation
import SpindleKit

/// What one click changed, so Undo can put it back: the entry as it was, and the entries added beside it.
struct SavedChange {
    /// The entry as it was before; nil when the change only added entries.
    let before: TimeEntry?
    /// The entries the change added, by the IDs chosen for them.
    let added: [String]

    /// Deletes the added entries, then saves the entry as it was, so the two never overlap.
    func undo(tracker: Tracker) async throws {
        func current(_ id: String) -> TimeEntry? {
            tracker.running?.id == id ? tracker.running : tracker.entries.first { $0.id == id }
        }
        let request = before.flatMap { restoring(current($0.id), added: added.compactMap(current)) }
        for id in added {
            try await tracker.delete(id: id)
        }
        guard let before else { return }
        try await tracker.update(before, to: request ?? EntryRequest(before))
    }

    /// What puts the entry back. One that was running keeps running, unless the timer stopped since, on it or on an
    /// entry the change added: then it keeps that stop, so Undo doesn't start the timer again.
    func restoring(_ current: TimeEntry?, added: [TimeEntry] = []) -> EntryRequest? {
        guard let before else { return nil }
        var request = EntryRequest(before)
        let since = ([current] + added).compactMap { $0 }
        if request.stop == nil, !since.isEmpty, since.allSatisfy({ $0.stop != nil }), let stop = since.compactMap(\.stop).max() {
            request.stop = max(stop, request.start)
        }
        return request
    }
}

/// Saves a split the way the person took it: in the editor, or with one click on a suggestion after stopping the
/// timer, in a notification or in the menu bar.
enum SplitApplier {
    enum Outcome {
        /// Saved; what changed, to undo it.
        case saved(SavedChange)
        /// The workspace requires details the split can't fill in on its own, e.g. a task; the editor asks for them.
        case needsDetails
    }

    /// The entries consecutive stretches of an entry's time become. Each takes `base`'s description and tags; a
    /// stretch on `base`'s project keeps its task and billable setting, the others follow their project. The last
    /// stretch runs on when `stop` is nil. Nil when a stretch would be empty, e.g. after the times were edited.
    static func requests(for stretches: [TimeStretch], base: EntryDetails, start: Date, stop: Date?, catalog: Catalog) -> [EntryRequest]? {
        let bounds = [start] + stretches.dropFirst().map(\.range.start) + [stop ?? Date()]
        guard stretches.count > 1, zip(bounds, bounds.dropFirst()).allSatisfy({ $0 < $1 }) else { return nil }
        return stretches.indices.map { index in
            let stretch = stretches[index]
            let own = stretch.projectId == base.projectId
            let details = EntryDetails(
                description: base.description, projectId: stretch.projectId, taskId: own ? base.taskId ?? stretch.taskId : stretch.taskId,
                tagIds: base.tagIds,
                isBillable: own ? base.isBillable : catalog.project(stretch.projectId)?.isBillable ?? false)
                .splitting([])
            return EntryRequest(details: details, start: bounds[index], stop: index == stretches.count - 1 ? stop : bounds[index + 1])
        }
    }

    /// What the first finished entry among `requests` still needs, as "Project: message".
    static func missing(_ requests: [EntryRequest], tracker: Tracker) -> String? {
        guard let rules = tracker.rules else { return nil }
        for request in requests where request.stop != nil {
            let project = tracker.catalog.project(request.details.projectId)
            if let missing = rules.missingDetails(request.details, project: project).first {
                return "\(project?.name ?? "An entry"): \(missing.message)"
            }
        }
        return nil
    }

    /// Saves `requests` as consecutive entries: `entry` becomes the last, so a running timer keeps running on it,
    /// and the ones before it are added. It shrinks first, so the entries never overlap.
    @discardableResult
    static func save(_ requests: [EntryRequest], replacing entry: TimeEntry?, tracker: Tracker) async throws -> SavedChange {
        guard let last = requests.last else { return SavedChange(before: nil, added: []) }
        var added: [String] = []
        let lastId = newID()
        try await tracker.save(entry, as: last, id: lastId)
        if entry == nil { added.append(lastId) }
        for request in requests.dropLast() {
            let id = newID()
            try await tracker.save(nil, as: request, id: id)
            added.append(id)
        }
        return SavedChange(before: entry, added: added)
    }

    /// Why one click can't save something, so the editor opens for it instead.
    enum Blocker: Error, Equatable {
        /// A repository it names has no project yet.
        case unlinked
        /// The workspace requires details one click can't fill in, or the times no longer fit: "Needs a task on
        /// Beacon".
        case details(String)
    }

    /// What one click saves for a suggestion: separate entries, the entry itself becoming the last, or the entry
    /// with shares.
    enum Prepared {
        case separate([EntryRequest])
        case shares(EntryRequest)
    }

    /// The words for an entry whose times changed since its suggestion was made.
    static let timesChanged = "Its times changed since it was suggested"

    /// What taking a suggestion for an entry saves: separate entries when the work moved from one project to the
    /// next, or shares of one entry when it went back and forth. Fails with why the editor is needed instead.
    static func prepare(_ suggestion: SplitSuggestion, for entry: TimeEntry, rules: EntryRules?, catalog: Catalog) -> Result<Prepared, Blocker> {
        var base = entry.details
        base.isBillable = entry.isBillable
        if suggestion.stretches != nil {
            guard let stretches = stretches(suggestion, catalog: catalog) else { return .failure(.unlinked) }
            guard let requests = requests(for: stretches, base: base, start: entry.start, stop: entry.stop, catalog: catalog) else {
                return .failure(.details(timesChanged))
            }
            if let need = need(requests, rules: rules, catalog: catalog) { return .failure(.details(need)) }
            return .success(.separate(requests))
        }
        guard var parts = parts(suggestion, catalog: catalog) else { return .failure(.unlinked) }
        // The part on the entry's own project keeps its task, if it has one, and billable setting.
        for index in parts.indices where parts[index].projectId == entry.projectId {
            parts[index].taskId = entry.taskId ?? parts[index].taskId
            parts[index].isBillable = entry.isBillable
        }
        let request = EntryRequest(details: base.splitting(parts), start: entry.start, stop: entry.stop)
        if let need = need([request], rules: rules, catalog: catalog) { return .failure(.details(need)) }
        return .success(.shares(request))
    }

    /// Takes a suggestion for an entry with one click: separate entries when the work moved from one project to the
    /// next, or shares of one entry when it went back and forth.
    static func apply(_ suggestion: SplitSuggestion, to entry: TimeEntry, tracker: Tracker) async throws -> Outcome {
        switch prepare(suggestion, for: entry, rules: tracker.rules, catalog: tracker.catalog) {
        case .success(.separate(let requests)):
            return .saved(try await save(requests, replacing: entry, tracker: tracker))
        case .success(.shares(let request)):
            try await tracker.save(entry, as: request)
            return .saved(SavedChange(before: entry, added: []))
        case .failure:
            return .needsDetails
        }
    }

    /// What the first finished entry among `requests` still needs that the workspace requires, its own details or
    /// a part's, in a few words: "Needs a task on Beacon", "Needs a description". Nil when nothing is missing.
    static func need(_ requests: [EntryRequest], rules: EntryRules?, catalog: Catalog) -> String? {
        guard let rules else { return nil }
        for request in requests where request.stop != nil {
            let details = request.details
            let project = catalog.project(details.projectId)
            if let missing = rules.missingDetails(details, project: project).first {
                return need(missing.field, on: project)
            }
            guard details.isSplit else { continue }
            let parts = details.shares
            if let (index, message) = rules.missingInParts(parts, project: catalog.project).min(by: { $0.key < $1.key }) {
                let part = catalog.project(parts[index].projectId)
                return message == EntryRules.descriptionRequired ? "Needs a description for \(part?.name ?? "a part")"
                    : need(parts[index].projectId == nil ? .projectId : .taskId, on: part)
            }
        }
        return nil
    }

    private static func need(_ field: DetailField, on project: Project?) -> String {
        switch field {
        case .description: "Needs a description"
        case .projectId: "Needs a project"
        case .taskId: "Needs a task on \(project?.name ?? "its project")"
        case .tagIds: "Needs a tag"
        }
    }

    /// Ends `entry` at `cut` with its split as it is, and continues from there in a new entry on `parts`: running
    /// when `entry` was, otherwise until its stop. The new entry keeps the description, tags and the parts' own
    /// descriptions; when one project is left, its own description, if it had one, is the entry's.
    static func endAndContinue(_ entry: TimeEntry, at cut: Date, on parts: [SplitPart], tracker: Tracker) async throws -> Outcome {
        guard let (ended, next) = continuing(entry, at: cut, on: parts), missing([ended, next], tracker: tracker) == nil else { return .needsDetails }
        try await tracker.save(entry, as: ended)
        let id = newID()
        try await tracker.save(nil, as: next, id: id)
        return .saved(SavedChange(before: entry, added: [id]))
    }

    /// The entry ended at `cut`, and the one that continues from there. Nil when `cut` isn't inside the entry.
    static func continuing(_ entry: TimeEntry, at cut: Date, on parts: [SplitPart]) -> (ended: EntryRequest, next: EntryRequest)? {
        guard !parts.isEmpty, cut > entry.start, cut < entry.stop ?? .distantFuture else { return nil }
        var ended = EntryRequest(entry)
        ended.stop = cut
        var details = entry.details
        details.isBillable = entry.isBillable
        details = details.splitting(parts)
        let described = SplitPart.describesEach(entry.parts)
        if parts.count == 1, described, let own = parts[0].description?.trimmingCharacters(in: .whitespacesAndNewlines), !own.isEmpty {
            details.description = own
        }
        let next = EntryRequest(details: details, start: cut, stop: entry.stop,
                                describeParts: parts.count > 1 && described ? true : nil)
        return (ended, next)
    }

    /// A suggestion's parts as an entry takes them, once every repository has a project: billable follows each
    /// project, and a part gets the task a branch rule names while that is an open task of its project.
    static func parts(_ suggestion: SplitSuggestion, catalog: Catalog) -> [SplitPart]? {
        suggestion.splitParts { catalog.project($0)?.isBillable ?? false }?.map { part in
            var part = part
            part.taskId = openTask(part.taskId, of: part.projectId, catalog: catalog)
            return part
        }
    }

    /// A suggestion's stretches as separate entries take them, with the task a branch rule names, as `parts`.
    static func stretches(_ suggestion: SplitSuggestion, catalog: Catalog) -> [TimeStretch]? {
        suggestion.projectStretches?.map {
            TimeStretch(projectId: $0.projectId, range: $0.range, taskId: openTask($0.taskId, of: $0.projectId, catalog: catalog))
        }
    }

    /// `taskId` while it is an open task of the project; a rule's task may have been finished or moved since.
    private static func openTask(_ taskId: String?, of projectId: String?, catalog: Catalog) -> String? {
        guard let task = catalog.task(taskId), task.projectId == projectId, !task.isDone else { return nil }
        return task.id
    }

    /// How a suggestion reads in one line: "Atlas until 10:42, then Beacon", or "Atlas 1:20 · Beacon 0:40".
    static func summary(_ suggestion: SplitSuggestion, entry: TimeEntry, catalog: Catalog, time: UserTime) -> String {
        func name(_ target: SplitSuggestion.Target) -> String {
            switch target {
            case .project(let id): catalog.project(id)?.name ?? "a project"
            case .repository(let repository): repository.name
            }
        }
        if let stretches = suggestion.stretches {
            let moves = stretches.dropFirst().map { "\(time.clock($0.range.start)), then \(name($0.target))" }
            return "\(name(stretches[0].target)) until " + moves.joined(separator: " until ")
        }
        let durations = durations(suggestion, seconds: (entry.stop ?? Date()).timeIntervalSince(entry.start))
        return zip(suggestion.parts, durations).map { "\(name($0.target)) \(Durations.hoursMinutes($1))" }
            .joined(separator: " · ")
    }

    /// How much of `seconds` each part would get, in whole minutes that add up to the whole.
    static func durations(_ suggestion: SplitSuggestion, seconds: TimeInterval) -> [TimeInterval] {
        let minutes = Int((max(0, seconds) / 60).rounded())
        return SplitMath.allocate(minutes, suggestion.parts.map(\.percent)).map { TimeInterval($0 * 60) }
    }
}
