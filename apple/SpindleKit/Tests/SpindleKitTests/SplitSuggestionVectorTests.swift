import Foundation
import Testing
@testable import SpindleKit

/// The cases the API runs too (`tests/vectors/split-suggestions.json`), so the web suggests the split the Mac does.
/// Here attention is built day by day, as the Mac's loader does; the server builds it over a longer range.
@Suite struct SplitSuggestionVectorTests {
    struct Vectors: Decodable {
        struct Named: Decodable { let id: String?; let identity: String?; let name: String }
        let timeZone: String
        let projects: [String: Named]
        let repositories: [String: Named]
        let tasks: [String: Named]
        let suggestions: [Suggestion]
        let learning: [Learning]
        let branchPatterns: [BranchPattern]
    }

    struct BranchPattern: Decodable, CustomTestStringConvertible {
        let pattern: String
        let branch: String
        let matches: Bool
        var testDescription: String { "\(pattern) \(matches ? "matches" : "doesn't match") \(branch)" }
    }

    struct Rule: Decodable {
        let pattern: String
        let project: String
        let task: String?
    }

    struct Session: Decodable {
        let id: String
        let parent: String?
        let repository: String?
        let folder: String?
        let branches: [String]?
        let touches: [String]
    }

    struct Part: Decodable {
        let project: String?
        let task: String?
        let repository: String?
        let percent: Int
        let attention: Int
        let unattributed: Int
        let prompts: Int
    }

    struct Stretch: Decodable {
        let project: String?
        let repository: String?
        let start: String
        let end: String
    }

    struct Expected: Decodable {
        let parts: [Part]
        let coverage: Double
        let stretches: [Stretch]?
        let strength: String
    }

    struct Suggestion: Decodable, CustomTestStringConvertible {
        struct Entry: Decodable { let start: String; let stop: String; let project: String }
        let name: String
        let chosen: [String: String]?
        let learned: [String: String]?
        let noProject: [String]?
        let rules: [String: [Rule]]?
        let sessions: [Session]
        let entry: Entry
        let expected: Expected?
        var testDescription: String { name }
    }

    struct Learning: Decodable, CustomTestStringConvertible {
        struct Entry: Decodable { let start: String; let stop: String; let project: String; let split: Bool? }
        let name: String
        let sessions: [Session]
        let entries: [Entry]
        let expected: [String: String]
        var testDescription: String { name }
    }

    static let vectors: Vectors = {
        // tests/vectors at the repository's root, from apple/SpindleKit/Tests/SpindleKitTests.
        let url = URL(filePath: #filePath).deletingLastPathComponent()
            .appending(path: "../../../../tests/vectors/split-suggestions.json").standardized
        return try! JSONDecoder().decode(Vectors.self, from: Data(contentsOf: url))
    }()

    static let time = UserTime(timeZone: TimeZone(identifier: vectors.timeZone)!)

    static func local(_ text: String) -> Date {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = time.timeZone
        formatter.dateFormat = "yyyy-MM-dd HH:mm"
        return formatter.date(from: text)!
    }

    static let projects: [Project] = vectors.projects.values.sorted { $0.name < $1.name }.map { named in
        Project(
            id: named.id!, name: named.name, clientId: nil, color: "#000000", isPrivate: false, isBillable: false,
            separateBilledTime: false, estimateSeconds: nil, archivedAt: nil, trackedSeconds: 0, tasks: [], hourlyRate: nil,
            canManage: true, billingMode: .asTracked, dailyHours: nil)
    }

    static func projectKey(_ id: String) -> String { vectors.projects.first { $0.value.id == id }!.key }

    /// The sessions as the parser leaves them, and the repository of each working directory.
    static func sessions(_ sessions: [Session]) -> (sessions: [AgentSession], repository: (String) -> ActivityRepository) {
        let parsed = sessions.map { session in
            AgentSession(
                id: session.id, source: .claude, parentID: session.parent,
                cwd: session.repository.map { "/code/\($0)" } ?? "/tmp/\(session.folder ?? session.id)",
                title: "", branches: Set(session.branches ?? []), events: session.touches.map { AgentEvent(date: local($0), isTouch: true) })
        }
        return (parsed, { cwd in
            let key = URL(filePath: cwd).lastPathComponent
            if cwd.hasPrefix("/code/"), let repository = vectors.repositories[key] {
                return ActivityRepository(key: cwd, name: repository.name, identity: repository.identity)
            }
            return ActivityRepository(key: cwd, name: key, identity: nil)
        })
    }

    /// Each day's timeline, as the Mac's loader builds it.
    static func days(_ sessions: [Session], from start: Date, to end: Date) -> [ActivityDay] {
        let (parsed, repository) = Self.sessions(sessions)
        let days = Set([time.day(of: start), time.day(of: end)])
        return days.sorted().map { ActivityTimeline.build(sessions: parsed, day: $0, time: time, repository: repository) }
    }

    static func links(_ keys: [String: String]?) -> [String: String] {
        Dictionary(uniqueKeysWithValues: (keys ?? [:]).map { (vectors.repositories[$0.key]!.identity!, vectors.projects[$0.value]!.id!) })
    }

    static func rules(_ keys: [String: [Rule]]?) -> [String: [BranchRule]] {
        Dictionary(uniqueKeysWithValues: (keys ?? [:]).map { key, rules in
            (vectors.repositories[key]!.identity!, rules.map {
                BranchRule(pattern: $0.pattern, projectId: vectors.projects[$0.project]!.id!, taskId: $0.task.map { vectors.tasks[$0]!.id! })
            })
        })
    }

    static func taskKey(_ id: String?) -> String? { id.map { id in vectors.tasks.first { $0.value.id == id }!.key } }

    static func name(_ target: SplitSuggestion.Target) -> String {
        switch target {
        case .project(let id): projectKey(id)
        case .repository(let repository): "repository " + vectors.repositories.first { $0.value.identity == repository.identity }!.key
        }
    }

    @Test(arguments: vectors.suggestions)
    func suggestsWhatTheServerSuggests(_ vector: Suggestion) throws {
        let start = Self.local(vector.entry.start), stop = Self.local(vector.entry.stop)
        let links = RepositoryLinks(chosen: Self.links(vector.chosen), learned: Self.links(vector.learned), rules: Self.rules(vector.rules),
                                    noProject: Set((vector.noProject ?? []).map { Self.vectors.repositories[$0]!.identity! }))
        let matcher = links.matcher(among: Self.projects)

        let suggestion = SplitSuggestion.suggest(
            from: start, to: stop, activity: Self.days(vector.sessions, from: start, to: stop),
            fallback: Self.vectors.projects[vector.entry.project]!.id, placing: matcher.place)

        guard let expected = vector.expected else {
            #expect(suggestion == nil)
            return
        }
        let found = try #require(suggestion)
        #expect(found.parts.map { "\(Self.name($0.target)) \($0.percent)% \(Int($0.attention.rounded()))s+\(Int($0.unattributed.rounded()))s" }
            == expected.parts.map { "\($0.project ?? "repository " + $0.repository!) \($0.percent)% \($0.attention)s+\($0.unattributed)s" })
        #expect(abs(found.coverage - expected.coverage) < 1e-9)
        #expect(found.parts.map { Self.taskKey($0.taskId) } == expected.parts.map(\.task))
        let touches = found.touches
        #expect(found.parts.map { "\(Self.name($0.target)) \(touches[$0.target, default: 0])" }
            == expected.parts.map { "\($0.project ?? "repository " + $0.repository!) \($0.prompts)" })
        #expect("\(found.strength)" == expected.strength)
        #expect(found.stretches?.map { "\(Self.name($0.target)) \($0.range.start)–\($0.range.end)" }
            == expected.stretches?.map { "\($0.project ?? "repository " + $0.repository!) \(Self.local($0.start))–\(Self.local($0.end))" })
    }

    @Test(arguments: vectors.branchPatterns)
    func readsBranchPatternsAsTheServerDoes(_ vector: BranchPattern) {
        #expect(BranchRule.matches(pattern: vector.pattern, branch: vector.branch) == vector.matches)
    }

    @Test(arguments: vectors.learning)
    func learnsWhatTheServerLearns(_ vector: Learning) {
        let entries = vector.entries.enumerated().map { index, entry in
            let projectId = Self.vectors.projects[entry.project]!.id!
            let shares = entry.split == true
                ? [SplitPart(projectId: projectId, isBillable: false, percent: 50).response, SplitPart(projectId: Self.projects[0].id, isBillable: false, percent: 50).response]
                : []
            return TimeEntry(id: "e\(index)", workspaceId: "w", description: "", projectId: projectId, tagIds: [], isBillable: false,
                             start: Self.local(entry.start), stop: Self.local(entry.stop), shares: shares, version: "1")
        }
        let start = entries.map(\.start).min()!, end = entries.compactMap(\.stop).max()!

        let learned = RepositoryLinks.learn(from: entries, activity: Self.days(vector.sessions, from: start, to: end), now: end)

        #expect(learned == Self.links(vector.expected))
    }
}
