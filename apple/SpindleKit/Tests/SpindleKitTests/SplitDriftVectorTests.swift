import Foundation
import Testing
@testable import SpindleKit

/// The cases the Windows app runs too (`tests/vectors/split-drift.json`), so both offer to end a split at the same
/// moment. Attention is built day by day, as the Mac's loader does.
@Suite struct SplitDriftVectorTests {
    struct Vectors: Decodable {
        struct Named: Decodable { let id: String?; let identity: String?; let name: String }
        let timeZone: String
        let projects: [String: Named]
        let repositories: [String: Named]
        let cases: [Case]
    }

    struct Session: Decodable {
        let id: String
        let repository: String?
        let touches: [String]
    }

    struct Part: Decodable { let project: String; let percent: Int }

    struct Case: Decodable, CustomTestStringConvertible {
        struct Entry: Decodable { let start: String; let statedAt: String; let now: String; let parts: [Part] }
        struct Expected: Decodable { let quiet: [String]?; let joined: String?; let at: String; let next: [Part] }
        let name: String
        let chosen: [String: String]?
        let sessions: [Session]
        let entry: Entry
        let expected: Expected?
        var testDescription: String { name }
    }

    static let vectors: Vectors = {
        let url = URL(filePath: #filePath).deletingLastPathComponent()
            .appending(path: "../../../../tests/vectors/split-drift.json").standardized
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

    static func id(_ key: String) -> String { vectors.projects[key]!.id! }
    static func key(_ id: String) -> String { vectors.projects.first { $0.value.id == id }!.key }

    static func days(_ sessions: [Session], from start: Date, to end: Date) -> [ActivityDay] {
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
        return Set([time.day(of: start), time.day(of: end)]).sorted().map {
            ActivityTimeline.build(sessions: parsed, day: $0, time: time, repository: repository)
        }
    }

    @Test(arguments: vectors.cases)
    func offersWhatWindowsOffers(_ vector: Case) throws {
        let start = Self.local(vector.entry.start), now = Self.local(vector.entry.now)
        let chosen = Dictionary(uniqueKeysWithValues: (vector.chosen ?? [:]).map { (Self.vectors.repositories[$0.key]!.identity!, Self.id($0.value)) })
        let links = RepositoryLinks(chosen: chosen)
        let parts = vector.entry.parts.map { SplitPart(projectId: Self.id($0.project), isBillable: false, percent: $0.percent) }

        let drift = SplitDrift.detect(
            parts: parts.compactMap(\.projectId), from: start, statedAt: Self.local(vector.entry.statedAt), now: now,
            activity: Self.days(vector.sessions, from: start, to: now)) { links.project(for: $0, among: Self.projects) }

        guard let expected = vector.expected else {
            #expect(drift == nil)
            return
        }
        let found = try #require(drift)
        if let quiet = expected.quiet {
            #expect(found == .quiet(quiet.map(Self.id), at: Self.local(expected.at)))
        } else {
            #expect(found == .joined(Self.id(expected.joined!), at: Self.local(expected.at)))
        }
        let next = SplitDrift.continuing(parts, after: found) { _ in false }
        #expect(next.map { "\(Self.key($0.projectId!)) \($0.percent)" } == expected.next.map { "\($0.project) \($0.percent)" })
    }
}

struct SplitDriftTests {
    let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
    let day = DayKey("2026-09-29")!
    func at(_ minutes: Double) -> Date { time.start(of: day).addingTimeInterval(minutes * 60) }
    let atlas = ActivityRepository(key: "/a", name: "atlas", origin: nil, isRepository: true)
    let beacon = ActivityRepository(key: "/b", name: "beacon", origin: nil, isRepository: true)

    func days(_ spans: [(ActivityRepository, Double, Double)]) -> [ActivityDay] {
        [ActivityDay(day: day, attention: spans.map { AttentionSegment(repository: $0.0, range: DateInterval(start: at($0.1), end: at($0.2)), sessionIDs: []) },
                     blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)]
    }

    @Test func theCutIsAtTheNearestWholeMinute() {
        let activity = days([(atlas, 540, 570.4), (beacon, 570.4, 640)])

        let drift = SplitDrift.detect(parts: ["atlas", "beacon"], from: at(540), statedAt: at(540), now: at(640), activity: activity) { $0.name }

        #expect(drift == .quiet(["atlas"], at: at(570)))
    }

    @Test func aSplitOfSixPartsHasNoRoomForAProjectToJoin() {
        let activity = days([(atlas, 550, 600)])
        let six = ["b", "c", "d", "e", "f", "g"]

        #expect(SplitDrift.detect(parts: six, from: at(540), statedAt: at(540), now: at(600), activity: activity) { $0.name } == nil)
        #expect(SplitDrift.detect(parts: Array(six.prefix(5)), from: at(540), statedAt: at(540), now: at(600), activity: activity) { $0.name }
            == .joined("atlas", at: at(550)))
    }

    @Test func continuingKeepsEachPartsTaskBillableAndDescription() {
        let parts = [
            SplitPart(projectId: "atlas", taskId: "t1", isBillable: true, percent: 50, description: "Login"),
            SplitPart(projectId: "beacon", isBillable: false, percent: 30, description: "Docs"),
            SplitPart(projectId: "copper", isBillable: false, percent: 20, description: ""),
        ]

        let quiet = SplitDrift.continuing(parts, after: .quiet(["copper"], at: at(600))) { _ in false }
        #expect(quiet == [
            SplitPart(projectId: "atlas", taskId: "t1", isBillable: true, percent: 63, description: "Login"),
            SplitPart(projectId: "beacon", isBillable: false, percent: 37, description: "Docs"),
        ])

        let joined = SplitDrift.continuing(Array(parts.prefix(2)), after: .joined("dune", at: at(600))) { $0 == "dune" }
        #expect(joined.map(\.percent) == [42, 25, 33])
        #expect(joined.last == SplitPart(projectId: "dune", isBillable: true, percent: 33, description: ""))
        #expect(SplitDrift.continuing([parts[0], parts[1]].map { var part = $0; part.description = nil; return part },
                                      after: .joined("dune", at: at(600))) { _ in false }.last?.description == nil)
    }
}
