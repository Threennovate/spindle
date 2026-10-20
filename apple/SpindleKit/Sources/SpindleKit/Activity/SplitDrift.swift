import Foundation

/// Whether the work changed after the person split a running entry: one of its projects went quiet, or agents began
/// working in a project outside it. The person's split itself is never judged against the agents' prompts; only the
/// time after it was set (its statement time) is looked at, to offer once to end the entry there and carry on in a
/// new one. Windows has the same rules (tests/vectors/split-drift.json).
public enum SplitDrift {
    public enum Drift: Sendable, Equatable {
        /// These projects of the split had agent attention earlier in the entry and none since `at`, while the
        /// others went on. The entry could end at `at` and continue on the rest.
        case quiet([String], at: Date)
        /// Agents have been working in this linked project, outside the split, since `at`.
        case joined(String, at: Date)

        /// Where the entry would end and the next one begin, at a whole minute.
        public var at: Date {
            switch self {
            case .quiet(_, let at), .joined(_, let at): at
            }
        }
    }

    /// A project of the split with no agent attention for this long has gone quiet...
    public static let quietAfter: TimeInterval = 30 * 60
    /// ...when the split's other projects had at least this much in that time, some of it after the first
    /// `ActivityTimeline.idleCap` of it.
    public static let othersActive: TimeInterval = 10 * 60
    /// A project outside the split joined it once it had this much attention since the statement time.
    public static let joinedAfter: TimeInterval = 15 * 60

    /// How the work changed since `statedAt`, when the split on `parts` (project IDs) was set, in an entry that began
    /// at `start` and runs until `now`. Attention and repository links are those of `SplitSuggestion.suggest`.
    ///
    /// A part that never had agent attention in the entry is never judged: it may be work the logs can't see. When
    /// both a project went quiet and another joined, the earlier change is the one to offer.
    public static func detect(
        parts: [String],
        from start: Date,
        statedAt: Date,
        now: Date,
        activity: [ActivityDay],
        project: (ActivityRepository) -> String?
    ) -> Drift? {
        detect(parts: parts, from: start, statedAt: statedAt, now: now, activity: activity,
               placing: { project($0.repository).map { .init(projectId: $0) } })
    }

    /// The same, with each stretch of attention placed by `placing`, which reads branch rules.
    public static func detect(
        parts: [String],
        from start: Date,
        statedAt: Date,
        now: Date,
        activity: [ActivityDay],
        placing: (AttentionSegment) -> RepositoryLinks.Placement?
    ) -> Drift? {
        guard now > start, !parts.isEmpty else { return nil }
        let range = DateInterval(start: start, end: now)
        let stated = min(max(statedAt, start), now)
        let pieces = SplitSuggestion.pieces(within: range, activity: activity, placing: placing).compactMap { piece -> (String, DateInterval)? in
            if case .project(let id) = piece.target { (id, piece.range) } else { nil }
        }
        func attention(_ projects: Set<String>, in window: DateInterval) -> TimeInterval {
            pieces.reduce(0) { total, piece in
                guard projects.contains(piece.0), let overlap = piece.1.intersection(with: window) else { return total }
                return total + overlap.duration
            }
        }
        var ordered: [String] = []
        for id in parts where !ordered.contains(id) { ordered.append(id) }
        let split = Set(ordered)

        var quiet: [(id: String, since: Date)] = []
        for id in ordered {
            guard let last = pieces.filter({ $0.0 == id }).map(\.1.end).max() else { continue }
            let since = max(last, stated), others = split.subtracting([id])
            // The others went on: one last prompt before everyone stopped leaves up to `idleCap` of attention, so
            // some of theirs must come later than that.
            guard now.timeIntervalSince(since) >= quietAfter,
                  attention(others, in: DateInterval(start: since, duration: quietAfter)) >= othersActive,
                  attention(others, in: DateInterval(start: since + ActivityTimeline.idleCap, end: since + quietAfter)) > 0 else { continue }
            quiet.append((id, since))
        }
        var found: [Drift] = []
        // The latest of them, so no quiet project's work after its own last prompt is cut off from it.
        if !quiet.isEmpty, quiet.count < ordered.count, let since = quiet.map(\.since).max(),
           let at = cut(since, within: range) {
            found.append(.quiet(quiet.map(\.id), at: at))
        }

        let window = DateInterval(start: stated, end: now)
        var outside: [String: (attention: TimeInterval, first: Date)] = [:]
        for (id, piece) in pieces where !split.contains(id) {
            guard let overlap = piece.intersection(with: window), overlap.duration > 0 else { continue }
            var value = outside[id] ?? (0, overlap.start)
            value.attention += overlap.duration
            value.first = min(value.first, overlap.start)
            outside[id] = value
        }
        if split.count < SplitMath.maxParts, let (id, value) = outside.filter({ $0.value.attention >= joinedAfter })
            .min(by: { $0.value.first != $1.value.first ? $0.value.first < $1.value.first : $0.value.attention > $1.value.attention }),
           let at = cut(value.first, within: range) {
            found.append(.joined(id, at: at))
        }
        return found.min { $0.at < $1.at }
    }

    /// `date` at the nearest whole minute, which is how the entries will read, strictly inside the entry.
    private static func cut(_ date: Date, within range: DateInterval) -> Date? {
        let at = Date(timeIntervalSinceReferenceDate: (date.timeIntervalSinceReferenceDate / 60).rounded() * 60)
        return at > range.start && at < range.end ? at : nil
    }

    /// The parts the entry continues with after `drift`: without the quiet projects, the rest keeping their ratio as
    /// the person set it (one project when one is left), or with the project that joined at an equal share, as
    /// `@Project` mentions add one. Each part keeps its task, billable setting and description; a joined one starts
    /// with an empty description when the parts are described one by one.
    public static func continuing(_ parts: [SplitPart], after drift: Drift, billable: (String) -> Bool) -> [SplitPart] {
        switch drift {
        case .quiet(let projects, _):
            var rest = parts.filter { !projects.contains($0.projectId ?? "") }
            let percents = SplitMath.apportion(rest.map(\.percent), total: 100)
            for index in rest.indices { rest[index].percent = percents[index] }
            return rest
        case .joined(let projectId, _):
            let percents = SplitMath.adding(to: parts.map(\.percent))
            var next = parts
            for index in next.indices { next[index].percent = percents[index] }
            next.append(SplitPart(projectId: projectId, isBillable: billable(projectId), percent: percents[parts.count],
                                  description: SplitPart.describesEach(parts) ? "" : nil))
            return next
        }
    }
}
