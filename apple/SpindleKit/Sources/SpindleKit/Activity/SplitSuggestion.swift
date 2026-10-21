import Foundation

/// Which project each repository's work belongs to: a branch rule the person set on the repository's link, the link
/// they chose, links learned from how they tracked time while working in a repository, and repositories named like
/// a project, in that order. A repository the person said goes to no project has none. Links are by the repository's
/// identity, not its path, so they are the same on every Mac and on the server, which has the same rules
/// (tests/vectors/split-suggestions.json).
public struct RepositoryLinks: Sendable, Equatable {
    /// Links the person chose, by repository identity.
    public var chosen: [String: String]
    /// Links learned from past entries, by repository identity.
    public var learned: [String: String]
    /// Branch rules on the links the person chose, by repository identity, in their order.
    public var rules: [String: [BranchRule]]
    /// Repositories the person said go to no project, by identity: neither a learned link nor a name takes their work.
    public var noProject: Set<String>

    public init(chosen: [String: String] = [:], learned: [String: String] = [:], rules: [String: [BranchRule]] = [:],
                noProject: Set<String> = []) {
        self.chosen = chosen
        self.learned = learned
        self.rules = rules
        self.noProject = noProject
    }

    /// Where a stretch of attention's work goes: a project, and the task a branch rule names.
    public struct Placement: Sendable, Hashable {
        public var projectId: String
        public var taskId: String?

        public init(projectId: String, taskId: String? = nil) {
            self.projectId = projectId
            self.taskId = taskId
        }
    }

    /// The project a repository's work goes to, if one is known.
    public func project(for repository: ActivityRepository, among projects: [Project]) -> String? {
        matcher(among: projects).project(for: repository)
    }

    /// Finds projects for any number of repositories among these projects, reading their names once.
    public func matcher(among projects: [Project]) -> Matcher {
        Matcher(links: self, projects: projects)
    }

    /// The links and the active projects, with each project's name already normalized: what finding a repository's
    /// project needs, worked out once for a set of projects rather than for every repository.
    public struct Matcher: Sendable {
        private let links: RepositoryLinks
        private let active: Set<String>
        /// Active projects' IDs and normalized names, in the catalog's order.
        private let names: [(id: String, name: String)]

        init(links: RepositoryLinks, projects: [Project]) {
            self.links = links
            let open = projects.filter { !$0.isArchived }
            active = Set(open.map(\.id))
            names = open.map { ($0.id, RepositoryLinks.normalized($0.name)) }
        }

        /// Where the work of a stretch of attention goes: the first branch rule of its repository that covers every
        /// branch its sessions were on, then the repository's project. A rule's project must be active too.
        public func place(_ segment: AttentionSegment) -> Placement? {
            place(segment.repository, branches: segment.branches)
        }

        public func place(_ repository: ActivityRepository, branches: Set<String>) -> Placement? {
            if let identity = repository.identity, let rules = links.rules[identity],
               let rule = rules.first(where: { active.contains($0.projectId) && $0.covers(branches) }) {
                return Placement(projectId: rule.projectId, taskId: rule.taskId)
            }
            return project(for: repository).map { Placement(projectId: $0) }
        }

        /// The project a repository's work goes to: none when the person said so, else the one they chose, then a
        /// learned one, then one named like it, as long as it isn't archived. Branch rules need the branches: see `place`.
        public func project(for repository: ActivityRepository) -> String? {
            if let identity = repository.identity {
                if links.noProject.contains(identity) { return nil }
                if let id = links.chosen[identity], active.contains(id) { return id }
                if let id = links.learned[identity], active.contains(id) { return id }
            }
            return RepositoryLinks.namedLike(RepositoryLinks.normalized(repository.name), among: names)
        }
    }

    /// A project whose name is the repository's, ignoring case and punctuation: "time-sheet" is "TimeSheet".
    /// One that contains it, or is contained in it, also counts when no other does. Both names come normalized.
    static func namedLike(_ name: String, among projects: [(id: String, name: String)]) -> String? {
        guard name.count >= 3 else { return nil }
        if let exact = projects.first(where: { $0.name == name }) { return exact.id }
        let close = projects.filter { $0.name.count >= 4 && ($0.name.contains(name) || name.contains($0.name)) }
        return close.count == 1 ? close[0].id : nil
    }

    static func normalized(_ text: String) -> String {
        text.lowercased().filter { $0.isLetter || $0.isNumber }
    }

    /// Learns links from entries on one project where attention mostly went to one repository: that repository
    /// goes with the project it was tracked on, when it was there for at least `minimum` and mostly on that project.
    /// Entries whose attention was spread between repositories teach nothing, as they may be the ones to split.
    public static func learn(
        from entries: [TimeEntry],
        activity: [ActivityDay],
        now: Date = Date(),
        minimum: TimeInterval = 20 * 60,
        dominance: Double = 0.6
    ) -> [String: String] {
        var seconds: [String: [String: TimeInterval]] = [:]
        let segments = activity.flatMap(\.attention).filter(\.repository.isRepository)
        for entry in entries where !entry.isSplit {
            guard let projectId = entry.projectId else { continue }
            let range = DateInterval(start: entry.start, end: max(entry.start, entry.stop ?? now))
            var byRepository: [String: TimeInterval] = [:]
            for segment in segments {
                guard let overlap = segment.range.intersection(with: range), overlap.duration > 0 else { continue }
                byRepository[segment.repository.identity!, default: 0] += overlap.duration
            }
            let total = byRepository.values.reduce(0, +)
            guard let (key, main) = byRepository.max(by: { $0.value < $1.value }), main / total >= dominance else { continue }
            seconds[key, default: [:]][projectId, default: 0] += main
        }
        var links: [String: String] = [:]
        for (key, byProject) in seconds {
            let total = byProject.values.reduce(0, +)
            guard let (projectId, best) = byProject.max(by: { $0.value < $1.value }),
                  best >= minimum, best / total >= dominance else { continue }
            links[key] = projectId
        }
        return links
    }
}

/// How an entry's time could be split, from where agent attention went while it ran.
public struct SplitSuggestion: Sendable, Equatable {
    /// Where part of the time went: a project, or a repository not yet linked to one.
    public enum Target: Sendable, Hashable {
        case project(String)
        case repository(ActivityRepository)
    }

    public struct Part: Sendable, Equatable {
        public var target: Target
        /// The repositories whose attention makes up this part.
        public var repositories: [ActivityRepository]
        /// Time the agent logs place here: attention, and the time between two prompts here.
        public var attention: TimeInterval
        /// Time without agent activity, which stays on the entry's own project.
        public var unattributed: TimeInterval
        public var percent: Int
        /// The task a branch rule names for every piece of the part's time; nil when none does, or they differ.
        public var taskId: String? = nil

        public var duration: TimeInterval { attention + unattributed }
    }

    /// One stretch of the entry, when the work moved from one project to the next rather than back and forth.
    public struct Stretch: Sendable, Equatable {
        public var target: Target
        public var range: DateInterval
    }

    /// Time the agent logs place with a target: an attention segment, or the time between two prompts for it.
    public struct Piece: Sendable, Equatable {
        public var target: Target
        public var range: DateInterval
        public var repository: ActivityRepository
        /// The task a branch rule names for it.
        public var taskId: String? = nil
    }

    /// A prompt the person sent within the entry, and where it went.
    public struct Prompt: Sendable, Equatable {
        public var target: Target
        public var date: Date
    }

    /// How firmly the logs back a suggestion, which decides how loudly it is offered.
    public enum Strength: Sendable, Equatable {
        /// The logs account for most of the time, and every part has prompts and time of its own: it may interrupt,
        /// with the card after a stop and a notification.
        case strong
        /// Anything else waits quietly beside the entry.
        case weak
    }

    public var parts: [Part]
    /// How much of the entry the agent logs account for, from 0 to 1. The rest stays on the entry's project.
    public var coverage: Double
    /// The entry as consecutive stretches, one per part, when the work moved between projects once or a few times.
    /// Nil when it went back and forth, so only sharing the time by percent describes it.
    public var stretches: [Stretch]?
    /// The time the suggestion divides.
    public var range: DateInterval
    /// What the logs place within it, in order, including targets too small to be a part.
    public var pieces: [Piece]
    /// The time within it the logs don't account for, in order, which stays on the entry's own project.
    public var open: [DateInterval]
    /// The person's prompts within it, in order.
    public var prompts: [Prompt]
    /// The entry's own project, which keeps the time the logs leave open.
    public var fallback: Target?

    /// How many prompts went to each target.
    public var touches: [Target: Int] {
        prompts.reduce(into: [:]) { $0[$1.target, default: 0] += 1 }
    }

    /// The logs must account for at least this share of the time for a suggestion to be strong.
    public static let strongCoverage = 0.6
    /// Each part of a strong suggestion has at least this many prompts.
    public static let strongPrompts = 2
    /// Each part of a strong suggestion has at least this much time; the entry's own project counts what the logs
    /// leave open.
    public static let strongPartDuration: TimeInterval = 10 * 60

    public var strength: Strength {
        let touches = touches
        let firm = coverage >= Self.strongCoverage && parts.allSatisfy {
            touches[$0.target, default: 0] >= Self.strongPrompts && $0.duration >= Self.strongPartDuration
        }
        return firm ? .strong : .weak
    }

    /// Repositories that still need a project before the split can be used.
    public var unlinked: [ActivityRepository] {
        parts.compactMap { if case .repository(let repository) = $0.target { repository } else { nil } }
    }

    /// The parts as an entry takes them, once every repository has a project. Billable follows each project.
    public func splitParts(billable: (String) -> Bool) -> [SplitPart]? {
        var result: [SplitPart] = []
        for part in parts {
            guard case .project(let id) = part.target else { return nil }
            result.append(SplitPart(projectId: id, taskId: part.taskId, isBillable: billable(id), percent: part.percent))
        }
        return result
    }

    /// The stretches as separate entries' projects and times, once every repository has a project, with the task a
    /// branch rule names for the project's part.
    public var projectStretches: [(projectId: String, range: DateInterval, taskId: String?)]? {
        guard let stretches else { return nil }
        var result: [(projectId: String, range: DateInterval, taskId: String?)] = []
        for stretch in stretches {
            guard case .project(let id) = stretch.target else { return nil }
            result.append((id, stretch.range, parts.first { $0.target == stretch.target }?.taskId))
        }
        return result
    }

    /// Whether an entry already shows this split, give or take `tolerance` percent on each project.
    public func matches(_ entry: TimeEntry, tolerance: Int = 5) -> Bool {
        let shown = Dictionary(entry.parts.map { ($0.projectId ?? "", $0.percent) }, uniquingKeysWith: +)
        guard shown.count == parts.count else { return false }
        return parts.allSatisfy { part in
            guard case .project(let id) = part.target, let percent = shown[id] else { return false }
            return abs(percent - part.percent) <= tolerance
        }
    }

    /// A part smaller than this share of the entry is left out as noise.
    public static let minimumShare = 0.1

    /// Less time placed by the agent logs inside an entry is too little to suggest anything.
    public static let minimumAttention: TimeInterval = 10 * 60

    /// A shorter stretch of one project between others is a detour, not a move to that project.
    public static let minimumStretch: TimeInterval = 10 * 60

    /// The split that agent activity suggests for the time from `start` to `end`, when it went to two or more
    /// projects (or unlinked repositories). Nil when it all went to one, or there was too little to tell.
    ///
    /// Attention goes where each prompt was, as the timeline has it. The time between two prompts for the same
    /// project goes to that project however long it was, as the timer says the person was working. Time the logs
    /// don't account for goes to `fallback`, the project the entry was tracked on, when it has one.
    public static func suggest(
        from start: Date,
        to end: Date,
        activity: [ActivityDay],
        fallback: String? = nil,
        project: (ActivityRepository) -> String?
    ) -> SplitSuggestion? {
        suggest(from: start, to: end, activity: activity, fallback: fallback, placing: { project($0.repository).map { .init(projectId: $0) } })
    }

    /// The split for the time from `start` to `end`, with each stretch of attention placed by `placing`, which may
    /// read its branches (`RepositoryLinks.Matcher.place`). A part gets the task its pieces all have.
    public static func suggest(
        from start: Date,
        to end: Date,
        activity: [ActivityDay],
        fallback: String? = nil,
        placing: (AttentionSegment) -> RepositoryLinks.Placement?
    ) -> SplitSuggestion? {
        let range = DateInterval(start: start, end: max(start, end))
        guard range.duration > 0 else { return nil }
        let pieces = pieces(within: range, activity: activity, placing: placing)
        let attributed = pieces.reduce(0) { $0 + $1.range.duration }
        guard attributed >= minimumAttention else { return nil }

        var byTarget: [Target: (attention: TimeInterval, unattributed: TimeInterval, repositories: [ActivityRepository], first: Date)] = [:]
        var tasks: [Target: Set<String?>] = [:]
        for piece in pieces {
            var value = byTarget[piece.target] ?? (0, 0, [], piece.range.start)
            value.attention += piece.range.duration
            if !value.repositories.contains(piece.repository) { value.repositories.append(piece.repository) }
            value.first = min(value.first, piece.range.start)
            byTarget[piece.target] = value
            tasks[piece.target, default: []].insert(piece.taskId)
        }
        // What the logs leave open, in order, to stay on the entry's project.
        var open: [DateInterval] = []
        var cursor = range.start
        for piece in pieces {
            if piece.range.start > cursor { open.append(DateInterval(start: cursor, end: piece.range.start)) }
            cursor = max(cursor, piece.range.end)
        }
        if cursor < range.end { open.append(DateInterval(start: cursor, end: range.end)) }
        let fallbackTarget = fallback.map(Target.project)
        if let fallbackTarget, !open.isEmpty {
            // Its work began with the first time left open, unless a prompt for it came earlier.
            var value = byTarget[fallbackTarget] ?? (0, 0, [], open[0].start)
            value.unattributed = open.reduce(0) { $0 + $1.duration }
            value.first = min(value.first, open[0].start)
            byTarget[fallbackTarget] = value
        }

        let total = byTarget.values.reduce(0) { $0 + $1.attention + $1.unattributed }
        // In the order work on each began, as the focus lane shows it.
        let kept = byTarget
            .filter { ($0.value.attention + $0.value.unattributed) / total >= minimumShare }
            .sorted { $0.value.first < $1.value.first }
        guard kept.count >= 2, kept.count <= SplitMath.maxParts else { return nil }
        let percents = SplitMath.apportion(kept.map { Int(($0.value.attention + $0.value.unattributed).rounded()) }, total: 100)
        let parts = zip(kept, percents).map { item, percent in
            let task = tasks[item.key].flatMap { $0.count == 1 ? $0.first! : nil }
            return Part(target: item.key, repositories: item.value.repositories, attention: item.value.attention,
                        unattributed: item.value.unattributed, percent: percent, taskId: task)
        }
        // As stretches, the time before any prompt is the entry's project, where the person said they began. Later
        // time the logs leave open continues whatever came before it, until a prompt elsewhere.
        var timeline: [(target: Target?, range: DateInterval)] = pieces.map { ($0.target, $0.range) }
        timeline += open.map { ($0.start == range.start ? fallbackTarget : nil, $0) }
        let stretches = Self.stretches(
            timeline.sorted { $0.range.start < $1.range.start }, keeping: Set(kept.map(\.key)),
            requiring: Set(kept.filter { $0.value.attention > 0 }.map(\.key)), within: range)
        return SplitSuggestion(parts: parts, coverage: min(1, attributed / range.duration), stretches: stretches,
                               range: range, pieces: pieces, open: open,
                               prompts: prompts(within: range, activity: activity, placing: placing), fallback: fallbackTarget)
    }

    /// What a stretch of attention is looked up by: its repository and its branches. Each is looked up once.
    private struct Lookup: Hashable {
        let repository: ActivityRepository
        let branches: Set<String>
    }

    /// The target and task of each stretch of attention in a repository, looked up once for each repository and set
    /// of branches. A folder outside a repository, e.g. a scratch folder, says nothing about which project it was for.
    private struct Targets {
        let placing: (AttentionSegment) -> RepositoryLinks.Placement?
        var found: [Lookup: (target: Target, taskId: String?)] = [:]

        mutating func of(_ segment: AttentionSegment) -> (target: Target, taskId: String?)? {
            guard segment.repository.isRepository else { return nil }
            let key = Lookup(repository: segment.repository, branches: segment.branches)
            if let known = found[key] { return known }
            let placement = placing(segment)
            let value = (placement.map { Target.project($0.projectId) } ?? .repository(segment.repository), placement?.taskId)
            found[key] = value
            return value
        }
    }

    /// The prompts within `range` sent in a repository, and where each went.
    static func prompts(within range: DateInterval, activity: [ActivityDay], placing: (AttentionSegment) -> RepositoryLinks.Placement?) -> [Prompt] {
        withoutActuallyEscaping(placing) { placing in
            var targets = Targets(placing: placing)
            var prompts: [Prompt] = []
            for segment in activity.flatMap(\.attention) {
                let inside = segment.prompts.filter { $0 >= range.start && $0 < range.end }
                guard !inside.isEmpty, let target = targets.of(segment)?.target else { continue }
                prompts += inside.map { Prompt(target: target, date: $0) }
            }
            return prompts.sorted { $0.date < $1.date }
        }
    }

    static func pieces(within range: DateInterval, activity: [ActivityDay], project: (ActivityRepository) -> String?) -> [Piece] {
        pieces(within: range, activity: activity, placing: { project($0.repository).map { .init(projectId: $0) } })
    }

    /// The time within `range` the agent logs place with a target: each attention segment, and the time between two
    /// prompts for the same target, in order.
    static func pieces(within range: DateInterval, activity: [ActivityDay], placing: (AttentionSegment) -> RepositoryLinks.Placement?) -> [Piece] {
        let segments = activity.flatMap(\.attention).sorted { $0.range.start < $1.range.start }
        let ignored = activity.flatMap(\.ignored)
        let placed = withoutActuallyEscaping(placing) { placing in
            var targets = Targets(placing: placing)
            return segments.map { targets.of($0) }
        }
        var pieces: [Piece] = []
        for (index, segment) in segments.enumerated() {
            guard let (target, taskId) = placed[index] else { continue }
            if let overlap = segment.range.intersection(with: range), overlap.duration > 0 {
                pieces.append(Piece(target: target, range: overlap, repository: segment.repository, taskId: taskId))
            }
            // The time between two prompts for the same target is its; its task only when both name the same. Not
            // over an ignored repository's attention, which the person said is none of the target's.
            if index + 1 < segments.count, let next = placed[index + 1], next.target == target,
               segment.range.end < segments[index + 1].range.start,
               case let between = DateInterval(start: segment.range.end, end: segments[index + 1].range.start),
               !ignored.contains(where: { $0.start < between.end && $0.end > between.start }),
               let gap = between.intersection(with: range),
               gap.duration > 0 {
                pieces.append(Piece(target: target, range: gap, repository: segment.repository, taskId: next.taskId == taskId ? taskId : nil))
            }
        }
        return pieces.sorted { $0.range.start < $1.range.start }
    }

    /// The timeline as one stretch per target, when each target's time is in one place once detours shorter than
    /// `minimumStretch`, and time without a kept target, join a neighbouring stretch. Every target the logs place
    /// time in (`required`) needs its stretch. Nil when the work alternates between them.
    static func stretches(_ timeline: [(target: Target?, range: DateInterval)], keeping kept: Set<Target>,
                          requiring required: Set<Target>, within range: DateInterval) -> [Stretch]? {
        var runs: [(target: Target?, start: Date, end: Date)] = []
        func merge() {
            var merged: [(target: Target?, start: Date, end: Date)] = []
            for run in runs {
                if let last = merged.last, last.target == run.target {
                    merged[merged.count - 1].end = run.end
                } else { merged.append(run) }
            }
            runs = merged
        }
        runs = timeline.map { item in (item.target.flatMap { kept.contains($0) ? $0 : nil }, item.range.start, item.range.end) }
        merge()
        // Absorb unknown runs first, then the shortest detour, into a neighbour, until every run is a kept target
        // long enough to be a stretch of its own.
        func rank(_ index: Int) -> (Int, TimeInterval) {
            (runs[index].target == nil ? 0 : 1, runs[index].end.timeIntervalSince(runs[index].start))
        }
        while runs.count > 1, let index = runs.indices
            .filter({ runs[$0].target == nil || runs[$0].end.timeIntervalSince(runs[$0].start) < minimumStretch })
            .min(by: { rank($0) < rank($1) }) {
            let neighbour = index > 0 ? index - 1 : index + 1
            if neighbour < index { runs[neighbour].end = runs[index].end } else { runs[neighbour].start = runs[index].start }
            runs.remove(at: index)
            merge()
        }
        let targets = runs.compactMap(\.target)
        guard runs.count >= 2, targets.count == runs.count, Set(targets).count == targets.count,
              required.isSubset(of: targets) else { return nil }
        // Cut at whole minutes, which is how the entries will read.
        var cuts = runs.dropFirst().map { Date(timeIntervalSinceReferenceDate: ($0.start.timeIntervalSinceReferenceDate / 60).rounded() * 60) }
        cuts = cuts.map { min(max($0, range.start), range.end) }
        let bounds = [range.start] + cuts + [range.end]
        guard zip(bounds, bounds.dropFirst()).allSatisfy({ $0 < $1 }) else { return nil }
        return runs.indices.map { Stretch(target: runs[$0].target!, range: DateInterval(start: bounds[$0], end: bounds[$0 + 1])) }
    }
}
