import Foundation

/// The local tools whose session logs can provide evidence of work. Where each keeps its logs, and what it is
/// called, is its `format`; how their lines read is in `AgentLogParser`.
public enum AgentSource: String, CaseIterable, Sendable, Hashable, Codable {
    case claude, codex, copilot
}

/// A repository shared by its working trees, or a folder outside version control.
public struct ActivityRepository: Hashable, Sendable {
    /// Its local path, which never leaves the Mac.
    public let key: String
    public let name: String
    public let origin: String?
    public let isRepository: Bool
    /// How it is named without its path, for links that sync and for uploaded activity; nil outside a repository.
    public let identity: String?

    public init(key: String, name: String, origin: String?, isRepository: Bool) {
        self.key = key
        self.name = name
        self.origin = origin
        self.isRepository = isRepository
        identity = isRepository ? RepositoryIdentity.of(path: key, origin: origin) : nil
    }

    /// A linked repository known by its identity alone, e.g. one agents haven't worked in lately.
    public init(linked identity: String, name: String) {
        key = identity
        self.name = name
        origin = identity.hasPrefix("origin:") ? String(identity.dropFirst("origin:".count)) : nil
        isRepository = true
        self.identity = identity
    }

    /// A repository with a given identity, for tests.
    init(key: String, name: String, identity: String?) {
        self.key = key
        self.name = name
        origin = nil
        isRepository = identity != nil
        self.identity = identity
    }
}

/// A timestamp retained without its prompt or model output.
public struct AgentEvent: Sendable, Equatable {
    public let date: Date
    public let isTouch: Bool
}

/// Metadata and timestamps held in memory for one session. IDs include the source.
public struct AgentSession: Sendable, Identifiable {
    public var id: String
    public var source: AgentSource
    public var parentID: String?
    public var cwd: String
    public var title: String
    public var branches: Set<String>
    public var events: [AgentEvent]
}

/// An interval attributed to one repository and the sessions that contributed to it.
public struct AttentionSegment: Sendable, Identifiable {
    public var id: String { "\(repository.key)|\(range.start.timeIntervalSince1970)" }
    public let repository: ActivityRepository
    public var range: DateInterval
    public var sessionIDs: Set<String>
    /// When the person prompted within it, in order: the prompt that began it, and those that kept it going.
    public var prompts: [Date] = []
    /// The branches its sessions were on, which branch rules read.
    public var branches: Set<String> = []
}

/// A cluster of nearby events in one session; concurrent runs may overlap.
public struct AgentRun: Sendable, Identifiable {
    public var id: String { "\(sessionID)|\(range.start.timeIntervalSince1970)" }
    public let sessionID: String
    public let source: AgentSource
    public let repository: ActivityRepository
    public let range: DateInterval
}

/// One repository's part of a focus block.
public struct FocusShare: Sendable, Identifiable {
    public var id: String { repository.key }
    public let repository: ActivityRepository
    public var duration: TimeInterval
    public var sessionIDs: Set<String>

    public init(repository: ActivityRepository, duration: TimeInterval, sessionIDs: Set<String>) {
        self.repository = repository
        self.duration = duration
        self.sessionIDs = sessionIDs
    }
}

/// Attention without a gap, and how it divided between repositories. Shares keep the order in which each
/// repository first had attention, so a busy hour reads as a few bands instead of dozens of slivers.
public struct FocusBlock: Sendable, Identifiable {
    public var id: String { "\(range.start.timeIntervalSince1970)" }
    public let range: DateInterval
    public let shares: [FocusShare]
    /// How often attention moved to another repository within the block.
    public let switches: Int
}

public struct ActivityTotals: Sendable {
    public var attention: TimeInterval = 0
    /// Sum of session run durations; parallel sessions intentionally count separately.
    public var agent: TimeInterval = 0
}

/// Local evidence for a day, clipped using the person's calendar and time zone.
public struct ActivityDay: Sendable {
    public let day: DayKey
    public var attention: [AttentionSegment]
    public var blocks: [DateInterval]
    public var runs: [AgentRun]
    public var sessions: [AgentSession]
    public var totals: [ActivityRepository: ActivityTotals]
    public var parallel: TimeInterval
    public var attentionTotal: TimeInterval { attention.reduce(0) { $0 + $1.range.duration } }
    public var hasActivity: Bool { !attention.isEmpty || !runs.isEmpty }

    public var focusBlocks: [FocusBlock] {
        blocks.map { block in
            let segments = attention.filter { $0.range.start >= block.start && $0.range.start < block.end }
            var shares: [FocusShare] = []
            for segment in segments {
                if let index = shares.firstIndex(where: { $0.repository == segment.repository }) {
                    shares[index].duration += segment.range.duration
                    shares[index].sessionIDs.formUnion(segment.sessionIDs)
                } else {
                    shares.append(FocusShare(repository: segment.repository, duration: segment.range.duration, sessionIDs: segment.sessionIDs))
                }
            }
            // Adjacent segments of one repository are merged unless their branches differ, which is no switch.
            let switches = zip(segments, segments.dropFirst()).filter { $0.repository != $1.repository }.count
            return FocusBlock(range: block, shares: shares, switches: switches)
        }
    }
}

/// Tunable rules, independent of file loading and drawing.
public enum ActivityTimeline {
    public static let idleCap: TimeInterval = 15 * 60
    public static let runGap: TimeInterval = 3 * 60

    public static func build(sessions: [AgentSession], day: DayKey, time: UserTime,
                             repository: (String) -> ActivityRepository) -> ActivityDay {
        let bounds = DateInterval(start: time.start(of: day), end: time.start(of: time.adding(1, to: day)))
        let byID = Dictionary(sessions.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        func root(_ session: AgentSession) -> AgentSession {
            var current = session
            var seen: Set<String> = [session.id]
            while let parent = current.parentID, seen.insert(parent).inserted, let next = byID[parent] {
                current = next
            }
            return current
        }
        let repos = Dictionary(sessions.map { ($0.id, repository(root($0).cwd)) }, uniquingKeysWith: { first, _ in first })
        func clip(_ start: Date, _ end: Date) -> DateInterval? {
            let a = max(start, bounds.start), b = min(end, bounds.end)
            return a < b ? DateInterval(start: a, end: b) : nil
        }
        let branches = Dictionary(sessions.map { ($0.id, $0.branches) }, uniquingKeysWith: { first, _ in first })
        let touches = sessions.filter { $0.parentID == nil }.flatMap { session in
            session.events.filter(\.isTouch).map { (date: $0.date, id: session.id) }
        }.sorted { ($0.date, $0.id) < ($1.date, $1.id) }
        var attention: [AttentionSegment] = []
        for (index, touch) in touches.enumerated() {
            let end = min(touch.date.addingTimeInterval(idleCap), index + 1 < touches.count ? touches[index + 1].date : .distantFuture)
            guard let range = clip(touch.date, end), let repo = repos[touch.id] else { continue }
            // A prompt before the day is the previous day's, though its attention carries into this one.
            let prompts = touch.date >= bounds.start ? [touch.date] : []
            // One repository's attention goes on across prompts, as long as its branches stay the same, so a branch
            // rule can tell two sessions on different branches apart.
            let on = branches[touch.id] ?? []
            if let last = attention.last, last.repository == repo, last.branches == on, last.range.end == range.start {
                attention[attention.count - 1].range = DateInterval(start: last.range.start, end: range.end)
                attention[attention.count - 1].sessionIDs.insert(touch.id)
                attention[attention.count - 1].prompts += prompts
            } else {
                attention.append(AttentionSegment(repository: repo, range: range, sessionIDs: [touch.id], prompts: prompts,
                                                  branches: on))
            }
        }
        var blocks: [DateInterval] = []
        for segment in attention {
            if let last = blocks.last, last.end == segment.range.start {
                blocks[blocks.count - 1] = DateInterval(start: last.start, end: segment.range.end)
            } else { blocks.append(segment.range) }
        }
        var runs: [AgentRun] = []
        for session in sessions {
            let events = session.events.sorted { $0.date < $1.date }
            guard let first = events.first, let repo = repos[session.id] else { continue }
            var start = first.date, end = first.date
            func appendRun() {
                if let range = clip(start, end) {
                    runs.append(AgentRun(sessionID: session.id, source: session.source, repository: repo, range: range))
                } else if start == end, start >= bounds.start, start < bounds.end {
                    runs.append(AgentRun(sessionID: session.id, source: session.source, repository: repo,
                                         range: DateInterval(start: start, duration: 0)))
                }
            }
            for event in events.dropFirst() {
                if event.date.timeIntervalSince(end) > runGap { appendRun(); start = event.date }
                end = event.date
            }
            appendRun()
        }
        runs.sort { ($0.range.start, $0.id) < ($1.range.start, $1.id) }
        var totals: [ActivityRepository: ActivityTotals] = [:]
        for segment in attention { totals[segment.repository, default: ActivityTotals()].attention += segment.range.duration }
        for run in runs { totals[run.repository, default: ActivityTotals()].agent += run.range.duration }
        // Sweep distinct repositories, rather than counting overlapping sessions in the same repo twice.
        let edges = runs.filter { $0.range.duration > 0 }.flatMap { run in
            [(run.range.start, run.repository.key, 1), (run.range.end, run.repository.key, -1)]
        }.sorted { $0.0 < $1.0 }
        var active: [String: Int] = [:], parallel: TimeInterval = 0
        var previous = bounds.start
        for (date, key, delta) in edges {
            if active.count >= 2 { parallel += date.timeIntervalSince(previous) }
            active[key, default: 0] += delta
            if active[key] == 0 { active.removeValue(forKey: key) }
            previous = date
        }
        return ActivityDay(day: day, attention: attention, blocks: blocks, runs: runs, sessions: sessions,
                           totals: totals, parallel: parallel)
    }
}

/// Kept compactly on disk: event dates in one list and the positions of the person's prompts in another.
extension AgentSession: Codable {
    private enum CodingKeys: String, CodingKey { case id, source, parentID, cwd, title, branches, dates, touches }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(String.self, forKey: .id)
        source = try container.decode(AgentSource.self, forKey: .source)
        parentID = try container.decodeIfPresent(String.self, forKey: .parentID)
        cwd = try container.decode(String.self, forKey: .cwd)
        title = try container.decode(String.self, forKey: .title)
        branches = try container.decode(Set<String>.self, forKey: .branches)
        let dates = try container.decode([Double].self, forKey: .dates)
        let touches = Set(try container.decode([Int].self, forKey: .touches))
        events = dates.enumerated().map { AgentEvent(date: Date(timeIntervalSinceReferenceDate: $0.element), isTouch: touches.contains($0.offset)) }
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(id, forKey: .id)
        try container.encode(source, forKey: .source)
        try container.encodeIfPresent(parentID, forKey: .parentID)
        try container.encode(cwd, forKey: .cwd)
        try container.encode(title, forKey: .title)
        try container.encode(branches, forKey: .branches)
        try container.encode(events.map(\.date.timeIntervalSinceReferenceDate), forKey: .dates)
        try container.encode(events.indices.filter { events[$0].isTouch }, forKey: .touches)
    }
}
