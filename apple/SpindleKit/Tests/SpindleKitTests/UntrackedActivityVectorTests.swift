import Foundation
import Testing
@testable import SpindleKit

/// The cases the Windows app runs too (`tests/vectors/untracked-activity.json`), so both find the same time nobody
/// tracked, offer the same earlier start, and name the same work in the reminder. Attention is built day by day, as the
/// Mac's loader does.
@Suite struct UntrackedActivityVectorTests {
    struct Vectors: Decodable {
        struct Named: Decodable { let id: String?; let identity: String?; let name: String }
        let timeZone: String
        let projects: [String: Named]
        let repositories: [String: Named]
        let chosen: [String: String]
        let stretches: [StretchCase]
        let earlierStart: [EarlierStartCase]
        let ongoing: [OngoingCase]
    }

    struct Session: Decodable {
        let id: String
        let repository: String?
        let touches: [String]
    }

    struct Expected: Decodable, Equatable, CustomStringConvertible {
        let start: String
        let end: String
        let projects: [String]
        let unlinked: Int
        var description: String { "\(start)–\(end) \(projects) unlinked \(unlinked)" }
    }

    struct StretchCase: Decodable, CustomTestStringConvertible {
        let name: String
        let day: String
        let now: String
        let sessions: [Session]
        let covered: [[String]]
        let expected: [Expected]
        var testDescription: String { name }
    }

    struct EarlierStartCase: Decodable, CustomTestStringConvertible {
        let name: String
        let sessions: [Session]
        let covered: [[String]]
        let start: String
        let notBefore: String
        let expected: Expected?
        var testDescription: String { name }
    }

    struct OngoingCase: Decodable, CustomTestStringConvertible {
        let name: String
        let sessions: [Session]
        let covered: [[String]]
        let notBefore: String
        let now: String
        let expected: Expected?
        var testDescription: String { name }
    }

    static let vectors: Vectors = {
        let url = URL(filePath: #filePath).deletingLastPathComponent()
            .appending(path: "../../../../tests/vectors/untracked-activity.json").standardized
        return try! JSONDecoder().decode(Vectors.self, from: Data(contentsOf: url))
    }()

    static let time = UserTime(timeZone: TimeZone(identifier: vectors.timeZone)!)

    static func formatter() -> DateFormatter {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = time.timeZone
        formatter.dateFormat = "yyyy-MM-dd HH:mm"
        return formatter
    }

    static func local(_ text: String) -> Date { formatter().date(from: text)! }

    static let projects: [Project] = vectors.projects.values.sorted { $0.name < $1.name }.map { named in
        Project(
            id: named.id!, name: named.name, clientId: nil, color: "#000000", isPrivate: false, isBillable: false,
            separateBilledTime: false, estimateSeconds: nil, archivedAt: nil, trackedSeconds: 0, tasks: [], hourlyRate: nil,
            canManage: true, billingMode: .asTracked, dailyHours: nil)
    }

    static let links = RepositoryLinks(chosen: Dictionary(uniqueKeysWithValues: vectors.chosen.map {
        (vectors.repositories[$0.key]!.identity!, vectors.projects[$0.value]!.id!)
    }))

    static func project(_ repository: ActivityRepository) -> String? { links.project(for: repository, among: projects) }

    static func key(_ id: String) -> String { vectors.projects.first { $0.value.id == id }!.key }

    /// The attention of the days the sessions' prompts are on, and the day after, as the loader keeps them.
    static func attention(_ sessions: [Session]) -> [AttentionSegment] {
        let parsed = sessions.map { session in
            AgentSession(id: session.id, source: .claude, parentID: nil, cwd: session.repository.map { "/code/\($0)" } ?? "/tmp/\(session.id)",
                         title: "", branches: [], events: session.touches.map { AgentEvent(date: local($0), isTouch: true) })
        }
        let repository = { (cwd: String) -> ActivityRepository in
            let key = URL(filePath: cwd).lastPathComponent
            if cwd.hasPrefix("/code/"), let repository = vectors.repositories[key] {
                return ActivityRepository(key: cwd, name: repository.name, identity: repository.identity)
            }
            return ActivityRepository(key: cwd, name: key, identity: nil)
        }
        let days = Set(sessions.flatMap(\.touches).map { time.day(of: local($0)) })
        return days.sorted().flatMap { ActivityTimeline.build(sessions: parsed, day: $0, time: time, repository: repository).attention }
    }

    static func covered(_ ranges: [[String]]) -> [DateInterval] {
        ranges.map { DateInterval(start: local($0[0]), end: local($0[1])) }
    }

    static func found(_ stretch: UntrackedActivity.Stretch) -> Expected {
        let formatter = formatter()
        return Expected(start: formatter.string(from: stretch.range.start), end: formatter.string(from: stretch.range.end),
                        projects: stretch.projects.map(key), unlinked: Int((stretch.unlinked / 60).rounded()))
    }

    @Test(arguments: vectors.stretches)
    func findsTheStretchesWindowsFinds(_ vector: StretchCase) {
        let day = DayKey(vector.day)!
        let bounds = DateInterval(start: Self.time.start(of: day), end: Self.time.start(of: Self.time.adding(1, to: day)))
        let found = UntrackedActivity.stretches(attention: Self.attention(vector.sessions), covered: Self.covered(vector.covered), within: bounds,
                                                now: Self.local(vector.now), project: Self.project)
        #expect(found.map(Self.found) == vector.expected)
    }

    @Test(arguments: vectors.earlierStart)
    func offersTheStartWindowsOffers(_ vector: EarlierStartCase) {
        let found = UntrackedActivity.earlierStart(before: Self.local(vector.start), attention: Self.attention(vector.sessions),
                                                   covered: Self.covered(vector.covered), notBefore: Self.local(vector.notBefore), project: Self.project)
        #expect(found.map(Self.found) == vector.expected)
    }

    @Test(arguments: vectors.ongoing)
    func namesTheWorkWindowsNames(_ vector: OngoingCase) {
        let found = UntrackedActivity.ongoing(attention: Self.attention(vector.sessions), covered: Self.covered(vector.covered),
                                              notBefore: Self.local(vector.notBefore), now: Self.local(vector.now), project: Self.project)
        #expect(found.map(Self.found) == vector.expected)
    }
}
