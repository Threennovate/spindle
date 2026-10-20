import Foundation
import SpindleKit

/// One thing the day's review offers, in its place on the day: everything the app would otherwise offer one by one,
/// with a chip, a card or a notification, so a day can be closed in one go.
enum ReviewItem: Identifiable {
    /// A finished entry on one project whose time agent activity suggests splitting (with its evidence).
    case split(SplitOffer)
    /// A finished split entry whose work changed after its split was set.
    case drift(DriftOffer)
    /// Agent attention with nothing tracked.
    case untracked(UntrackedBlock)
    // What agents proposed (`ProposalInbox`) joins here later, so the day has one inbox; it has its own review today.

    var id: String {
        switch self {
        case .split(let offer): "split|\(offer.entry.id)"
        case .drift(let offer): "drift|\(offer.entry.id)|\(offer.statement)"
        case .untracked(let block): "untracked|\(block.id)"
        }
    }

    /// The time it is about, where it sits on the day.
    var range: DateInterval {
        switch self {
        case .split(let offer): offer.suggestion.range
        case .drift(let offer): DateInterval(start: offer.entry.start, end: max(offer.entry.start, offer.entry.stop ?? offer.entry.start))
        case .untracked(let block): block.range
        }
    }

    /// The entry it changes; nil for time nobody tracked.
    var entry: TimeEntry? {
        switch self {
        case .split(let offer): offer.entry
        case .drift(let offer): offer.entry
        case .untracked: nil
        }
    }

    /// Whether one click can take it, as "Take All" does: every repository it names has a project.
    var isTakeable: Bool {
        switch self {
        case .split(let offer): offer.suggestion.splitParts(billable: { _ in false }) != nil
        case .drift: true
        case .untracked(let block): !block.stretch.projects.isEmpty
        }
    }

    /// Its button: "Split", "Split at 10:42", "End Split at 11:05" or "Add Entry".
    func action(catalog: Catalog, time: UserTime) -> String {
        switch self {
        case .split(let offer): offer.suggestion.stretches == nil ? "Split" : SplitSuggestionChip.separateTitle(offer.suggestion, time: time)
        case .drift(let offer): offer.title(catalog: catalog, time: time)
        case .untracked: "Add Entry"
        }
    }
}

extension ReviewItem {
    /// What a set of items is, counted by kind: "2 splits, 1 split change, 3 untracked stretches".
    static func breakdown(_ items: [ReviewItem]) -> String {
        var splits = 0, changes = 0, untracked = 0
        for item in items {
            switch item {
            case .split: splits += 1
            case .drift: changes += 1
            case .untracked: untracked += 1
            }
        }
        return [
            splits == 0 ? nil : splits == 1 ? "1 split" : "\(splits) splits",
            changes == 0 ? nil : changes == 1 ? "1 split change" : "\(changes) split changes",
            untracked == 0 ? nil : untracked == 1 ? "1 untracked stretch" : "\(untracked) untracked stretches",
        ].compactMap { $0 }.joined(separator: ", ")
    }
}

/// One entry an item of the review leaves behind once taken, as its row shows it: "Atlas 09:00–09:40", or one entry
/// shared as "Atlas 0:50 · Beacon 0:40".
struct ReviewResult: Equatable {
    struct Piece: Equatable {
        /// The project or repository; nil for no project, e.g. untracked time only in repositories without one.
        var target: SplitSuggestion.Target?
        var seconds: TimeInterval
    }

    var range: DateInterval
    /// Each project's share of it; one piece for an entry on one project.
    var pieces: [Piece]
}

extension ReviewItem {
    /// What the time becomes once the item is taken: one entry split between projects, separate entries cut where
    /// the work moved, the split entry ended and continued, or a new entry for untracked time. `untracked` is the
    /// suggestion for untracked time, as one click saves it (`SplitAdvisor.requests(for:)`).
    func results(untracked suggestion: SplitSuggestion? = nil) -> [ReviewResult] {
        func whole(_ range: DateInterval, _ target: SplitSuggestion.Target?) -> ReviewResult {
            ReviewResult(range: range, pieces: [.init(target: target, seconds: range.duration)])
        }
        func shared(_ suggestion: SplitSuggestion, _ range: DateInterval) -> ReviewResult {
            ReviewResult(range: range, pieces: zip(suggestion.parts, SplitApplier.durations(suggestion, seconds: range.duration))
                .map { .init(target: $0.target, seconds: $1) })
        }
        func separate(_ stretches: [SplitSuggestion.Stretch], _ range: DateInterval) -> [ReviewResult] {
            let bounds = [range.start] + stretches.dropFirst().map(\.range.start) + [range.end]
            return stretches.indices.map { whole(DateInterval(start: bounds[$0], end: max(bounds[$0], bounds[$0 + 1])), stretches[$0].target) }
        }
        func split(_ parts: [SplitPart], _ range: DateInterval) -> ReviewResult {
            let minutes = SplitMath.allocate(Int((range.duration / 60).rounded()), parts.map(\.percent))
            return ReviewResult(range: range, pieces: zip(parts, minutes).map { .init(target: $0.projectId.map(SplitSuggestion.Target.project), seconds: TimeInterval($1 * 60)) })
        }
        switch self {
        case .split(let offer):
            if let stretches = offer.suggestion.stretches { return separate(stretches, range) }
            return [shared(offer.suggestion, range)]
        case .drift(let offer):
            let at = min(max(offer.drift.at, range.start), range.end)
            let next = SplitDrift.continuing(offer.entry.parts, after: offer.drift) { _ in false }
            return [split(offer.entry.parts, DateInterval(start: range.start, end: at)), split(next, DateInterval(start: at, end: range.end))]
        case .untracked(let block):
            if let suggestion, suggestion.projectStretches != nil, let stretches = suggestion.stretches { return separate(stretches, range) }
            if let suggestion, suggestion.splitParts(billable: { _ in false }) != nil { return [shared(suggestion, range)] }
            return [whole(range, block.stretch.projects.first.map(SplitSuggestion.Target.project))]
        }
    }
}

/// What the person did with an item while its review is open. The item keeps its place with this outcome, and a
/// take or "Not Now" can be taken back until the review closes.
struct ReviewOutcome {
    enum Kind {
        /// Saved with one click: what its notice said, and what Undo puts back.
        case taken(String, SavedChange)
        /// Saved from the editor, opened with Change….
        case changed
        /// Left out of the review.
        case notNow
    }

    let item: ReviewItem
    let kind: Kind

    /// "Split saved", "Added Atlas 14:00–14:47", "Saved in the editor" or "Not Now".
    var title: String {
        switch kind {
        case .taken(let text, _): text
        case .changed: "Saved in the editor"
        case .notNow: "Not Now"
        }
    }

    /// Whether Undo can take it back from its row; what was saved in the editor is changed there.
    var canUndo: Bool {
        if case .changed = kind { return false }
        return true
    }
}

/// The days a review covers: one day, or a week (or the last seven days, after turning agent logs on).
struct ReviewScope: Identifiable, Equatable {
    var days: [DayKey]
    var id: String { days.map(\.description).joined(separator: ",") }
    var isWeek: Bool { days.count > 1 }

    static func day(_ day: DayKey) -> ReviewScope { ReviewScope(days: [day]) }

    /// The calendar week the day is in.
    static func week(of day: DayKey, time: UserTime) -> ReviewScope {
        ReviewScope(days: CalendarLayout.week(containing: day, in: time))
    }

    /// The seven days up to and including `day`.
    static func lastSevenDays(to day: DayKey, time: UserTime) -> ReviewScope {
        ReviewScope(days: (0..<7).reversed().map { time.adding(-$0, to: day) })
    }
}

extension SplitAdvisor {
    /// What the review offers on these days, in order: splits of finished entries on one project, changes after a
    /// split, and agent time nobody tracked. Leaves out running entries (the timer bar offers theirs), locked days,
    /// what the person kept as it is, and what they put off with "Not Now". Weak suggestions are here too; this is
    /// where they wait.
    func review(on days: [DayKey], tracker: Tracker, covered: [DateInterval] = [], now: Date = Date()) -> [ReviewItem] {
        review(on: days, entries: tracker.entries(overlapping: days), time: tracker.time, projects: tracker.catalog.projects,
               covered: covered, isDayLocked: { tracker.rules?.isLocked($0) == true }, now: now)
    }

    func review(on days: [DayKey], entries: [TimeEntry], time: UserTime, projects: [Project], covered: [DateInterval] = [],
                isDayLocked: (DayKey) -> Bool = { _ in false }, now: Date = Date()) -> [ReviewItem] {
        guard isEnabled else { return [] }
        let wanted = Set(days.filter { !isDayLocked($0) })
        var items: [ReviewItem] = []
        for entry in entries where entry.stop != nil && wanted.contains(time.day(of: entry.start)) {
            if entry.isSplit {
                if let drift = drift(for: entry, time: time, projects: projects, now: now) { items.append(.drift(drift)) }
            } else if let suggestion = suggestion(for: entry, time: time, projects: projects, now: now) {
                items.append(.split(SplitOffer(entry: entry, suggestion: suggestion)))
            }
        }
        items += untracked(on: days, entries: entries, time: time, projects: projects, covered: covered, isDayLocked: isDayLocked, now: now)
            .map(ReviewItem.untracked)
        return items.filter { !putOff.contains($0.id) }.sorted { $0.range.start < $1.range.start }
    }

    /// The entries one click saves for untracked time: on its project, its split, or separate entries where the work
    /// moved, with no description. Nil while agents only worked in repositories without a project.
    func requests(for block: UntrackedBlock, tracker: Tracker) -> [EntryRequest]? {
        guard let projectId = block.stretch.projects.first else { return nil }
        let catalog = tracker.catalog
        let suggestion = suggestion(from: block.range.start, to: block.range.end, on: projectId, tracker: tracker)
        let base = EntryDetails(projectId: projectId, isBillable: catalog.project(projectId)?.isBillable ?? false)
        if let stretches = suggestion.flatMap({ SplitApplier.stretches($0, catalog: catalog) }),
           let cut = SplitApplier.requests(for: stretches, base: base, start: block.range.start, stop: block.range.end, catalog: catalog) {
            return cut
        }
        if let parts = suggestion.flatMap({ SplitApplier.parts($0, catalog: catalog) }) {
            return [EntryRequest(details: base.splitting(parts), start: block.range.start, stop: block.range.end)]
        }
        return [EntryRequest(details: base, start: block.range.start, stop: block.range.end)]
    }

    /// What a review item becomes once taken, for its row.
    func results(_ item: ReviewItem, tracker: Tracker) -> [ReviewResult] {
        guard case .untracked(let block) = item, let projectId = block.stretch.projects.first else { return item.results() }
        return item.results(untracked: suggestion(from: block.range.start, to: block.range.end, on: projectId, tracker: tracker))
    }

    /// Why one click can't take a review item, so its main button is "Change…"; nil when it can. Checked on the
    /// entry as it is now.
    func blocker(_ item: ReviewItem, tracker: Tracker) -> SplitApplier.Blocker? {
        switch item {
        case .split(let offer):
            let entry = tracker.entry(id: offer.entry.id) ?? offer.entry
            if case .failure(let blocker) = SplitApplier.prepare(offer.suggestion, for: entry, rules: tracker.rules, catalog: tracker.catalog) {
                return blocker
            }
            return nil
        case .drift(let offer):
            let entry = tracker.entry(id: offer.entry.id) ?? offer.entry
            let parts = SplitDrift.continuing(entry.parts, after: offer.drift) { tracker.catalog.project($0)?.isBillable ?? false }
            guard let (ended, next) = SplitApplier.continuing(entry, at: offer.drift.at, on: parts) else { return .details(SplitApplier.timesChanged) }
            return SplitApplier.need([ended, next], rules: tracker.rules, catalog: tracker.catalog).map(SplitApplier.Blocker.details)
        case .untracked(let block):
            guard let requests = requests(for: block, tracker: tracker) else { return .unlinked }
            return SplitApplier.need(requests, rules: tracker.rules, catalog: tracker.catalog).map(SplitApplier.Blocker.details)
        }
    }
}
