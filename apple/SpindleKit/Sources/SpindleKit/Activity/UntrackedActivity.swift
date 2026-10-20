import Foundation

/// When agents were busy and no entry covers it: the modest hint the vision allows ("agents were active 14:00–15:30
/// with no timer running"), for an agent to propose entries from when the person asks it to fill in a day. It never
/// says where attention "probably" was outside the logs; meetings, calls and reading stay the person's to tell.
public enum UntrackedActivity {
    /// Stretches closer than this are one stretch of work.
    public static let joinGap: TimeInterval = 20 * 60
    /// Shorter stretches are left out.
    public static let shortest: TimeInterval = 5 * 60

    /// The stretches of agent attention within `range` that nothing in `covered` (entries, proposals waiting)
    /// overlaps, up to `now`, joined when close and left out when short.
    public static func stretches(attention: [AttentionSegment], covered: [DateInterval], within range: DateInterval,
                                 now: Date) -> [DateInterval] {
        let end = min(range.end, now)
        guard end > range.start else { return [] }
        let bounds = DateInterval(start: range.start, end: end)
        let blocks = merge(covered.compactMap { $0.intersection(with: bounds) }, gap: 0)
        var pieces: [DateInterval] = []
        for segment in attention {
            guard let clipped = segment.range.intersection(with: bounds), clipped.duration > 0 else { continue }
            pieces += subtract(blocks, from: clipped)
        }
        return merge(pieces, gap: joinGap).filter { $0.duration >= shortest }
    }

    /// Where a stretch's attention went, repository by repository in time order, each run joined across short pauses,
    /// so parallel work shows as overlapping runs and sequential work as one after another.
    public static func runs(attention: [AttentionSegment], covered: [DateInterval], within stretch: DateInterval)
        -> [(repository: ActivityRepository, range: DateInterval)] {
        let blocks = merge(covered.compactMap { $0.intersection(with: stretch) }, gap: 0)
        var byRepository: [ActivityRepository: [DateInterval]] = [:]
        for segment in attention {
            guard let clipped = segment.range.intersection(with: stretch), clipped.duration > 0 else { continue }
            byRepository[segment.repository, default: []] += subtract(blocks, from: clipped)
        }
        return byRepository.flatMap { repository, ranges in merge(ranges, gap: runGap).map { (repository, $0) } }
            .sorted { $0.range.start != $1.range.start ? $0.range.start < $1.range.start : $0.repository.name < $1.repository.name }
    }

    /// Pauses in one repository's work shorter than this don't split its run.
    public static let runGap: TimeInterval = 5 * 60

    /// Intervals joined where they overlap or lie within `gap` of each other, in order.
    static func merge(_ intervals: [DateInterval], gap: TimeInterval) -> [DateInterval] {
        var result: [DateInterval] = []
        for interval in intervals.sorted(by: { $0.start < $1.start }) {
            if let last = result.last, interval.start.timeIntervalSince(last.end) <= gap {
                result[result.count - 1] = DateInterval(start: last.start, end: max(last.end, interval.end))
            } else {
                result.append(interval)
            }
        }
        return result
    }

    /// What is left of `interval` outside `blocks`, which are sorted and don't overlap.
    static func subtract(_ blocks: [DateInterval], from interval: DateInterval) -> [DateInterval] {
        var left: [DateInterval] = []
        var start = interval.start
        for block in blocks where block.end > start && block.start < interval.end {
            if block.start > start { left.append(DateInterval(start: start, end: block.start)) }
            start = max(start, block.end)
        }
        if start < interval.end { left.append(DateInterval(start: start, end: interval.end)) }
        return left.filter { $0.duration > 0 }
    }
}

// MARK: - What the apps offer for it

extension UntrackedActivity {
    /// A stretch of agent attention nobody tracked, and where it went.
    public struct Stretch: Sendable, Equatable {
        public var range: DateInterval
        /// The projects its attention went to, most first.
        public var projects: [String]
        /// Attention in repositories without a project, which no project can be filled in for.
        public var unlinked: TimeInterval

        public init(range: DateInterval, projects: [String], unlinked: TimeInterval) {
            self.range = range
            self.projects = projects
            self.unlinked = unlinked
        }
    }

    /// How close to a timer's start, or to now, untracked attention has to reach to belong to it.
    public static let reach: TimeInterval = 5 * 60
    /// How much attention in one project with no timer running makes the reminder to track name it.
    public static let ongoingAttention: TimeInterval = 15 * 60

    /// The untracked stretches within `range`, up to `now`, with the projects their attention went to.
    public static func stretches(attention: [AttentionSegment], covered: [DateInterval], within range: DateInterval, now: Date,
                                 project: (ActivityRepository) -> String?) -> [Stretch] {
        stretches(attention: attention, covered: covered, within: range, now: now, placing: { project($0.repository) })
    }

    /// The same, with each stretch of attention's project from `placing`, which reads branch rules.
    public static func stretches(attention: [AttentionSegment], covered: [DateInterval], within range: DateInterval, now: Date,
                                 placing: (AttentionSegment) -> String?) -> [Stretch] {
        stretches(attention: attention, covered: covered, within: range, now: now).map { range in
            let attended = attended(attention, covered: covered, within: range, placing: placing)
            let projects = attended.compactMap { key, value in key.map { ($0, value) } }
                .sorted { $0.1 != $1.1 ? $0.1 > $1.1 : $0.0 < $1.0 }.map(\.0)
            return Stretch(range: range, projects: projects, unlinked: attended[nil] ?? 0)
        }
    }

    /// Each project's attention within `range` outside `covered`; nil for repositories without a project.
    static func attended(_ attention: [AttentionSegment], covered: [DateInterval], within range: DateInterval,
                         placing: (AttentionSegment) -> String?) -> [String?: TimeInterval] {
        let blocks = merge(covered.compactMap { $0.intersection(with: range) }, gap: 0)
        var byProject: [String?: TimeInterval] = [:]
        for segment in attention {
            guard let clipped = segment.range.intersection(with: range), clipped.duration > 0 else { continue }
            byProject[placing(segment), default: 0] += subtract(blocks, from: clipped).reduce(0) { $0 + $1.duration }
        }
        return byProject
    }

    /// Where a timer started at `start` could have started instead: the untracked stretch just before it, when that
    /// reaches within `reach` of the start. Never before `floor` (the previous entry's stop, the day's start, a
    /// locked day), and on a whole minute.
    public static func earlierStart(before start: Date, attention: [AttentionSegment], covered: [DateInterval], notBefore floor: Date,
                                    project: (ActivityRepository) -> String?) -> Stretch? {
        earlierStart(before: start, attention: attention, covered: covered, notBefore: floor, placing: { project($0.repository) })
    }

    public static func earlierStart(before start: Date, attention: [AttentionSegment], covered: [DateInterval], notBefore floor: Date,
                                    placing: (AttentionSegment) -> String?) -> Stretch? {
        guard floor < start,
              var last = stretches(attention: attention, covered: covered, within: DateInterval(start: floor, end: start), now: start,
                                   placing: placing).last,
              start.timeIntervalSince(last.range.end) <= reach else { return nil }
        let from = max(floor, wholeMinute(last.range.start))
        guard start.timeIntervalSince(from) >= 60 else { return nil }
        last.range = DateInterval(start: from, end: last.range.end)
        return last
    }

    /// The untracked work going on now, with no timer running: the last stretch, when it reaches within `reach` of
    /// `now` and one project has had at least `least` of its attention. Starts on a whole minute, never before `floor`.
    public static func ongoing(attention: [AttentionSegment], covered: [DateInterval], notBefore floor: Date, now: Date,
                               least: TimeInterval = ongoingAttention, project: (ActivityRepository) -> String?) -> Stretch? {
        ongoing(attention: attention, covered: covered, notBefore: floor, now: now, least: least, placing: { project($0.repository) })
    }

    public static func ongoing(attention: [AttentionSegment], covered: [DateInterval], notBefore floor: Date, now: Date,
                               least: TimeInterval = ongoingAttention, placing: (AttentionSegment) -> String?) -> Stretch? {
        guard floor < now,
              var last = stretches(attention: attention, covered: covered, within: DateInterval(start: floor, end: now), now: now,
                                   placing: placing).last,
              now.timeIntervalSince(last.range.end) <= reach, let first = last.projects.first else { return nil }
        guard (attended(attention, covered: covered, within: last.range, placing: placing)[first] ?? 0) >= least else { return nil }
        last.range = DateInterval(start: max(floor, wholeMinute(last.range.start)), end: last.range.end)
        return last
    }

    private static func wholeMinute(_ date: Date) -> Date {
        Date(timeIntervalSinceReferenceDate: (date.timeIntervalSinceReferenceDate / 60).rounded(.down) * 60)
    }
}
