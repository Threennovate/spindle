import Foundation

/// What agent logs say about time already tracked, in a sentence or two: what turning them on shows first, so the
/// point of reading them is seen before the first parallel entry stops. Worked out by the same rules as the
/// suggestions, so every minute it counts is one a suggestion offers.
public enum ActivityFindings {
    /// Tracked time that went somewhere other than the project on the timer.
    public struct Elsewhere: Equatable, Sendable {
        /// Time in projects other than the entry's own.
        public var otherProjects: TimeInterval = 0
        /// Time in repositories without a project yet, which may still turn out to be the entry's own.
        public var unlinked: TimeInterval = 0
        /// The entries a split is suggested for.
        public var entries = 0

        public init(otherProjects: TimeInterval = 0, unlinked: TimeInterval = 0, entries: Int = 0) {
            self.otherProjects = otherProjects
            self.unlinked = unlinked
            self.entries = entries
        }
    }

    /// How much of the finished entries' time on one project the logs place in other projects, by their suggestions.
    /// Entries the person split already, and those without a project, aren't counted: there is nothing to compare.
    public static func elsewhere(
        entries: [TimeEntry],
        activity: [ActivityDay],
        project: (ActivityRepository) -> String?
    ) -> Elsewhere {
        elsewhere(entries: entries, activity: activity, placing: { project($0.repository).map { .init(projectId: $0) } })
    }

    /// The same, with each stretch of attention placed by `placing`, which reads branch rules.
    public static func elsewhere(
        entries: [TimeEntry],
        activity: [ActivityDay],
        placing: (AttentionSegment) -> RepositoryLinks.Placement?
    ) -> Elsewhere {
        var found = Elsewhere()
        for entry in entries where !entry.isSplit {
            guard let stop = entry.stop, let own = entry.projectId,
                  let suggestion = SplitSuggestion.suggest(from: entry.start, to: stop, activity: activity, fallback: own, placing: placing)
            else { continue }
            found.entries += 1
            for part in suggestion.parts {
                switch part.target {
                case .project(let id) where id == own: break
                case .project: found.otherProjects += part.duration
                case .repository: found.unlinked += part.duration
                }
            }
        }
        return found
    }

    /// A repository agents worked in, and for how long.
    public struct Repository: Equatable, Sendable {
        public var repository: ActivityRepository
        public var attention: TimeInterval
    }

    /// The repositories with agent attention in these days, most first. Folders outside Git don't count, as
    /// suggestions leave them out too; worktrees are their repository already.
    public static func repositories(in activity: [ActivityDay]) -> [Repository] {
        var byKey: [String: Repository] = [:]
        for segment in activity.flatMap(\.attention) where segment.repository.isRepository {
            let key = segment.repository.identity ?? segment.repository.key
            byKey[key, default: Repository(repository: segment.repository, attention: 0)].attention += segment.range.duration
        }
        return byKey.values.sorted { $0.attention != $1.attention ? $0.attention > $1.attention : $0.repository.name < $1.repository.name }
    }
}
