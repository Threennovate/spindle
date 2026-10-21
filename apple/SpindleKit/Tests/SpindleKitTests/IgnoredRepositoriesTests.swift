import Foundation
import Testing
@testable import SpindleKit

/// Repositories the person ignores: hashed as the server and Windows hash them, and left out of everything but the
/// place of their prompts.
struct IgnoredRepositoriesTests {
    struct Vectors: Decodable {
        struct Case: Decodable, CustomTestStringConvertible {
            let account: String
            let identity: String
            let hash: String
            var testDescription: String { "\(identity) for \(account)" }
        }
        let cases: [Case]
    }

    static let vectors: Vectors = {
        let url = URL(filePath: #filePath).deletingLastPathComponent()
            .appending(path: "../../../../tests/vectors/ignored-repositories.json").standardized
        return try! JSONDecoder().decode(Vectors.self, from: Data(contentsOf: url))
    }()

    @Test(arguments: vectors.cases)
    func hashesARepositoryAsTheServerDoes(_ vector: Vectors.Case) {
        #expect(IgnoredRepositories.hash(vector.identity, account: vector.account) == vector.hash)
        #expect(IgnoredRepositories(account: vector.account, hashes: [vector.hash]).contains(identity: vector.identity))
    }

    let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
    let day = DayKey("2026-09-28")!
    let account = "0c000000-0000-4000-8000-000000000001"

    func repository(_ name: String) -> ActivityRepository {
        ActivityRepository(key: "/code/\(name)", name: name, identity: "origin:github.com/acme/\(name)")
    }

    func session(_ id: String, _ name: String, parent: String? = nil, events: [(Double, Bool)]) -> AgentSession {
        AgentSession(id: id, source: .claude, parentID: parent, cwd: "/code/\(name)", title: "Private work", branches: ["secret"],
                     events: events.map { AgentEvent(date: time.start(of: day).addingTimeInterval($0.0 * 60), isTouch: $0.1) })
    }

    @Test func anIgnoredRepositoryLeavesOnlyThePlaceOfItsPrompts() {
        let ignoring = IgnoredRepositories(account: account, hashes: [IgnoredRepositories.hash("origin:github.com/acme/zeta", account: account)])
        let sessions = [
            session("a", "atlas", events: [(540, true), (545, false), (570, true)]),
            session("z", "zeta", events: [(550, true), (551, false), (555, false)]),
            session("zz", "zeta", parent: "z", events: [(552, false)]),
        ]

        let result = ActivityTimeline.build(sessions: sessions, day: day, time: time, repository: { repository(URL(filePath: $0).lastPathComponent) },
                                            ignoring: ignoring.contains)

        // Atlas's attention ends at zeta's prompt; zeta's is kept apart, and nothing else of it shows.
        #expect(result.attention.map { "\($0.repository.name) \(Int($0.range.duration / 60))" } == ["atlas 10", "atlas 15"])
        #expect(result.ignored.map { $0.duration / 60 } == [15])
        #expect(result.blocks.map { $0.duration / 60 } == [10, 15])
        #expect(Set(result.runs.map(\.repository.name)) == ["atlas"])
        #expect(result.sessions.map(\.id) == ["a"])
        #expect(result.totals.keys.map(\.name) == ["atlas"])
    }

    @Test func anIgnoredSessionGoesAsItsPromptsTimesAloneAndASubagentsAsNothing() throws {
        let zeta = repository("zeta")
        let upload = AgentSessionUpload(session: session("claude:z", "zeta", events: [(540, true), (545, false), (560, true)]),
                                        repository: zeta, titles: true, ignored: true)
        let subagent = AgentSessionUpload(session: session("claude:zz", "zeta", parent: "claude:z", events: [(546, false)]),
                                          repository: zeta, titles: true, ignored: true)

        #expect(upload.touches.count == 2)
        #expect(upload.runs.isEmpty && upload.branches.isEmpty && upload.repository == nil && upload.title == nil && upload.parentSessionID == nil)
        let json = String(decoding: try JSONEncoder().encode(upload), as: UTF8.self)
        #expect(!json.contains("zeta") && !json.contains("secret") && !json.contains("Private"))
        #expect(subagent.touches.isEmpty && subagent.runs.isEmpty)
    }

    @Test func ignoringAndUnignoringSendsTheRepositorysSessionsAgain() async throws {
        let zeta = repository("zeta"), atlas = repository("atlas")
        let logs: [(AgentSession, ActivityRepository)] = [
            (session("claude:a", "atlas", events: [(540, true)]), atlas),
            (session("claude:z", "zeta", events: [(550, true), (551, false)]), zeta),
            (session("claude:zz", "zeta", parent: "claude:z", events: [(552, false)]), zeta),
        ]
        let sent = Box<[[String]]>([]), looks = Box<[Date]>([])
        let uploader = ActivityUploader(file: nil)
        let ignored = IgnoredRepositories(account: account, hashes: [IgnoredRepositories.hash(zeta.identity!, account: account)])
        let start = time.start(of: day).addingTimeInterval(24 * 3600)
        func upload(_ ignoring: IgnoredRepositories, at now: Date) async throws {
            try await uploader.upload(syncedSince: Date(timeIntervalSince1970: 1_780_000_000), titles: false, ignoring: ignoring, now: now,
                                      sessions: { from in looks.change { $0.append(from) }; return logs },
                                      send: { batch in
                sent.change { $0.append(batch.map { "\($0.sessionID) \($0.repository?.name ?? "-")" }) }
            })
        }

        try await upload(.none, at: start)
        try await upload(ignored, at: start.addingTimeInterval(300))
        try await upload(ignored, at: start.addingTimeInterval(600))
        try await upload(.none, at: start.addingTimeInterval(900))

        #expect(sent.value == [
            ["claude:a atlas", "claude:z zeta", "claude:zz zeta"],
            // Its prompts' times alone; the subagent, whose runs the server deleted with the rest, says nothing now.
            ["claude:z -"],
            ["claude:z zeta", "claude:zz zeta"],
        ])
        // Each change of the list looks at the whole history again.
        #expect(looks.value[1] == start.addingTimeInterval(300 - ActivityUploader.history))
        #expect(looks.value[3] == start.addingTimeInterval(900 - ActivityUploader.history))
        #expect(looks.value[2] == start.addingTimeInterval(240))
    }
}
