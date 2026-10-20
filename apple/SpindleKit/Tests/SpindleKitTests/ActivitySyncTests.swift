import Foundation
import Testing
@testable import SpindleKit

struct RepositoryIdentityTests {
    @Test(arguments: [
        "https://github.com/Acme/atlas.git",
        "https://github.com/Acme/atlas",
        "git@github.com:Acme/atlas.git",
        "ssh://git@github.com:22/Acme/atlas.git/",
        "https://x-access-token:ghp_secret@github.com/Acme/atlas.git",
        "https://ivan@github.com/acme/atlas",
        "  git@github.com:acme/atlas.git\n",
    ])
    func anOriginIsTheSameOverSSHAndHTTPSWithoutCredentials(_ url: String) {
        #expect(RepositoryIdentity.origin(url) == "origin:github.com/acme/atlas")
    }

    @Test func nestedGroupsAndPortsAreKeptApartFromTheHost() {
        #expect(RepositoryIdentity.origin("https://token@gitlab.example.com:8443/group/sub/repo.git?x=1")
            == "origin:gitlab.example.com/group/sub/repo")
        #expect(RepositoryIdentity.origin("user:password@git.example.com:team/repo") == "origin:git.example.com/team/repo")
    }

    @Test(arguments: ["/Users/ivan/git/repo.git", "file:///Users/ivan/repo", "../other", "C:/code/repo", "https://github.com/", "",
                      "git@host:my repo.git", "https://example.com/" + String(repeating: "a", count: 300)])
    func anOriginThatIsALocalPathIsNoOrigin(_ url: String) {
        #expect(RepositoryIdentity.origin(url) == nil)
    }

    @Test func aRepositoryWithoutAnOriginIsNamedByAHashOfItsPath() {
        let identity = RepositoryIdentity.of(path: "/Users/ivan/Repos/secret-client", origin: nil)

        #expect(identity.hasPrefix("path:"))
        #expect(identity.count == 5 + 32)
        #expect(!identity.contains("ivan") && !identity.contains("secret"))
        #expect(identity == RepositoryIdentity.of(path: "/Users/ivan/Repos/secret-client", origin: "/srv/git/secret-client.git"))
        #expect(identity != RepositoryIdentity.of(path: "/Users/ivan/Repos/other", origin: nil))
    }

    @Test func onlyRepositoriesHaveAnIdentity() {
        let repository = ActivityRepository(key: "/code/atlas", name: "atlas", origin: "git@github.com:acme/atlas.git", isRepository: true)
        let folder = ActivityRepository(key: "/tmp/scratch", name: "scratch", origin: nil, isRepository: false)

        #expect(repository.identity == "origin:github.com/acme/atlas")
        #expect(folder.identity == nil)
    }
}

struct ActivityUploadTests {
    let start = Date(timeIntervalSince1970: 1_790_000_000)
    let atlas = ActivityRepository(key: "/Users/ivan/code/atlas", name: "atlas", origin: "git@github.com:acme/atlas.git", isRepository: true)

    func session(_ id: String, minutes: [(Double, Bool)], title: String = "Fix the login page") -> AgentSession {
        AgentSession(id: id, source: .claude, parentID: nil, cwd: "/Users/ivan/code/atlas/sub", title: title, branches: ["main", "feat/x"],
                     events: minutes.map { AgentEvent(date: start.addingTimeInterval($0.0 * 60), isTouch: $0.1) })
    }

    @Test func aSessionGoesAsPromptTimesAndRunsWithoutItsPath() throws {
        let upload = AgentSessionUpload(
            session: session("claude:1", minutes: [(0, true), (1, false), (2, false), (10, true), (11, false)]), repository: atlas, titles: false)

        let ms = Int64(start.timeIntervalSince1970 * 1000)
        #expect(upload.touches == [ms, ms + 600_000])
        #expect(upload.runs == [.init(start: ms, end: ms + 120_000), .init(start: ms + 600_000, end: ms + 660_000)])
        #expect(upload.repository == .init(identity: "origin:github.com/acme/atlas", name: "atlas"))
        #expect(upload.title == nil)
        #expect(upload.branches == ["feat/x", "main"])
        let json = String(decoding: try JSONEncoder().encode(upload), as: UTF8.self)
        #expect(!json.contains("/Users"))
    }

    @Test func longNamesAndTitlesAreCutToWhatTheServerTakes() {
        let long = ActivityRepository(key: "/code/x", name: String(repeating: "🦊", count: 150), origin: nil, isRepository: true)
        let upload = AgentSessionUpload(session: session("claude:1", minutes: [(0, true)], title: String(repeating: "é🦊", count: 100)),
                                        repository: long, titles: true)

        #expect(upload.repository!.name.utf16.count <= 200)
        #expect(upload.title!.utf16.count <= 200)
    }

    @Test func titlesGoOnlyWhenAllowed() {
        #expect(AgentSessionUpload(session: session("claude:1", minutes: [(0, true)]), repository: atlas, titles: true).title == "Fix the login page")
    }

    /// Runs one upload against fake logs; `refuse` decides which batches the fake server refuses as invalid, and
    /// `fail` which ones break with an error that is no refusal.
    func run(_ uploader: ActivityUploader, _ logs: [AgentSession], at now: Date,
             refuse: @escaping @Sendable ([AgentSessionUpload]) -> Bool = { _ in false },
             fail: @escaping @Sendable ([AgentSessionUpload]) -> Bool = { _ in false },
             attempts: Box<[[String]]>) async throws -> (sent: Int, rejected: Int) {
        try await uploader.upload(syncedSince: Date(timeIntervalSince1970: 1_780_000_000), titles: false, now: now,
                                  sessions: { _ in logs.map { ($0, atlas) } },
                                  send: { batch in
            attempts.change { $0.append(batch.map(\.sessionID)) }
            if fail(batch) { throw URLError(.notConnectedToInternet) }
            if refuse(batch) { throw ActivityUploadRejected() }
        })
    }

    @Test func aRefusedBatchGoesOneSessionAtATimeAndTheRefusedOneWaitsUntilItChanges() async throws {
        let file = FileManager.default.temporaryDirectory.appending(path: "uploads-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: file) }
        var logs = [session("claude:A", minutes: [(1, true)]), session("claude:B", minutes: [(2, true)]), session("claude:C", minutes: [(3, true)])]
        let attempts = Box<[[String]]>([])
        let refuseB: @Sendable ([AgentSessionUpload]) -> Bool = { $0.contains { $0.sessionID == "claude:B" } }

        let first = try await run(ActivityUploader(file: file), logs, at: start, refuse: refuseB, attempts: attempts)
        #expect(first.sent == 2 && first.rejected == 1)
        #expect(attempts.value == [["claude:A", "claude:B", "claude:C"], ["claude:A"], ["claude:B"], ["claude:C"]])

        // Handled, so nothing goes while nothing changed.
        attempts.change { $0 = [] }
        let quiet = try await run(ActivityUploader(file: file), logs, at: start.addingTimeInterval(360), refuse: refuseB, attempts: attempts)
        #expect(quiet.sent == 0 && quiet.rejected == 0 && attempts.value.isEmpty)

        // B changes: it is sent again, and refused again.
        logs[1].events.append(AgentEvent(date: start.addingTimeInterval(540), isTouch: true))
        let changed = try await run(ActivityUploader(file: file), logs, at: start.addingTimeInterval(720), refuse: refuseB, attempts: attempts)
        #expect(changed.sent == 0 && changed.rejected == 1)
        #expect(attempts.value == [["claude:B"]])
    }

    @Test func batchesAfterARefusedOneAreStillSent() async throws {
        var logs = (0..<150).map { session("claude:\(String(format: "%03d", $0))", minutes: [(Double(150 - $0), true)]) }
        logs[0] = session("claude:B", minutes: [(-500, true)])
        let attempts = Box<[[String]]>([])

        let result = try await run(ActivityUploader(file: nil), logs, at: start, refuse: { $0.contains { $0.sessionID == "claude:B" } }, attempts: attempts)

        #expect(result.sent == 149 && result.rejected == 1)
        // B is the oldest, so it sits in the first batch; the second batch of 50 went whole.
        #expect(attempts.value.last?.count == 50)
    }

    @Test func anotherErrorWhileSendingOneAtATimeStopsTheUploadAndKeepsTheLookOpen() async throws {
        let file = FileManager.default.temporaryDirectory.appending(path: "uploads-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: file) }
        let logs = [session("claude:A", minutes: [(1, true)]), session("claude:B", minutes: [(2, true)]), session("claude:C", minutes: [(3, true)])]
        let attempts = Box<[[String]]>([])

        await #expect(throws: URLError.self) {
            try await run(ActivityUploader(file: file), logs, at: start,
                          refuse: { $0.count > 1 }, fail: { $0.map(\.sessionID) == ["claude:B"] }, attempts: attempts)
        }
        // A went; B and C are not recorded, and the next look reads from before this one.
        attempts.change { $0 = [] }
        let again = try await run(ActivityUploader(file: file), logs, at: start.addingTimeInterval(300), attempts: attempts)
        #expect(again.sent == 2)
        #expect(attempts.value == [["claude:B", "claude:C"]])
    }

    @Test func onlyChangedSessionsGoAgainUntilTheSettingsChange() async throws {
        let file = FileManager.default.temporaryDirectory.appending(path: "uploads-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: file) }
        let logs = Box([(session("claude:1", minutes: [(0, true)]), atlas), (session("claude:2", minutes: [(5, true)]), atlas)])
        let sent = Box<[[String]]>([])
        let since = Date(timeIntervalSince1970: 1_780_000_000)
        func upload(_ uploader: ActivityUploader, titles: Bool = false, at now: Date) async throws {
            try await uploader.upload(syncedSince: since, titles: titles, now: now, sessions: { _ in logs.value.map { ($0.0, $0.1) } },
                                      send: { batch in sent.change { $0.append(batch.map(\.sessionID)) } })
        }

        let uploader = ActivityUploader(file: file)
        try await upload(uploader, at: start)
        try await upload(uploader, at: start.addingTimeInterval(60))
        let later = AgentEvent(date: start.addingTimeInterval(600), isTouch: true)
        logs.change { $0[1].0.events.append(later) }
        // A new launch reads what was sent from the file.
        try await upload(ActivityUploader(file: file), at: start.addingTimeInterval(120))
        try await upload(ActivityUploader(file: file), titles: true, at: start.addingTimeInterval(180))

        #expect(sent.value == [["claude:1", "claude:2"], ["claude:2"], ["claude:1", "claude:2"]])
    }

    @Test func aSourceTurnedOnSendsItsEarlierSessions() async throws {
        var copilot = session("copilot:1", minutes: [(5, true)])
        copilot.source = .copilot
        let logs: [(AgentSession, ActivityRepository)] = [(session("claude:1", minutes: [(0, true)]), atlas), (copilot, atlas)]
        let looks = Box<[Date]>([]), sent = Box<[[String]]>([])
        let since = Date(timeIntervalSince1970: 1_780_000_000)
        let uploader = ActivityUploader(file: nil)
        func upload(_ sources: Set<AgentSource>, at now: Date) async throws {
            try await uploader.upload(syncedSince: since, titles: false, sources: sources, now: now, sessions: { from in
                looks.change { $0.append(from) }
                return logs.filter { sources.contains($0.0.source) }
            }, send: { batch in sent.change { $0.append(batch.map(\.sessionID)) } })
        }
        try await upload([.claude], at: start)
        try await upload([.claude], at: start.addingTimeInterval(300))
        try await upload([.claude, .copilot], at: start.addingTimeInterval(600))
        try await upload([.claude, .copilot], at: start.addingTimeInterval(900))
        // Only the new source's session goes, from as far back as the first look.
        #expect(sent.value == [["claude:1"], ["copilot:1"]])
        #expect(looks.value[1] == start.addingTimeInterval(-60))
        #expect(looks.value[2] == looks.value[0].addingTimeInterval(600))
        #expect(looks.value[3] == start.addingTimeInterval(540))
    }

    @Test func stateFromBeforeOtherSourcesReadsTheirHistoryOnce() async throws {
        let file = FileManager.default.temporaryDirectory.appending(path: "uploads-\(UUID().uuidString).json")
        defer { try? FileManager.default.removeItem(at: file) }
        let since = Date(timeIntervalSince1970: 1_780_000_000)
        // What an earlier version wrote: no sources.
        try Data(#"{"syncedSince":\#(since.timeIntervalSinceReferenceDate),"titles":false,"parserVersion":\#(AgentSessionUpload.parserVersion),"scannedAt":\#(start.timeIntervalSinceReferenceDate),"sent":{}}"#.utf8).write(to: file)
        let looks = Box<[Date]>([])
        func upload(_ sources: Set<AgentSource>, at now: Date) async throws {
            try await ActivityUploader(file: file).upload(syncedSince: since, titles: false, sources: sources, now: now,
                                                          sessions: { from in looks.change { $0.append(from) }; return [] }, send: { _ in })
        }
        try await upload([.claude, .codex], at: start.addingTimeInterval(300))
        try await upload([.claude, .codex, .copilot], at: start.addingTimeInterval(600))
        #expect(looks.value == [start.addingTimeInterval(-60), start.addingTimeInterval(600 - ActivityUploader.history)])
    }
}

/// A value closures can share in a test.
final class Box<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: Value
    init(_ value: Value) { stored = value }
    var value: Value { lock.withLock { stored } }
    func change(_ body: (inout Value) -> Void) { lock.withLock { body(&stored) } }
}

struct ActivityWholeSessionTests {
    private func write(_ lines: [String], to file: URL, modified: Date) throws {
        try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try (lines.joined(separator: "\n") + "\n").write(to: file, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: file.path)
    }

    private func user(_ session: String, _ time: String, sidechain: Bool = false) -> String {
        #"{"type":"user","sessionId":"\#(session)","cwd":"/r","timestamp":"\#(time)","isSidechain":\#(sidechain),"message":{"content":"Fix the bug"}}"#
    }

    private func upload(mainChanged: Bool, subagentChanged: Bool, other: Bool = false) async throws -> [AgentSession] {
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let projects = root.appending(path: "claude/projects/p")
        let since = Date(timeIntervalSince1970: 1_790_000_000), before = since.addingTimeInterval(-3600), after = since.addingTimeInterval(3600)
        try write([user("S", "2026-09-28T07:00:00Z"), #"{"type":"assistant","sessionId":"S","cwd":"/r","timestamp":"2026-09-28T07:01:00Z","message":{"content":[]}}"#],
                  to: projects.appending(path: "S.jsonl"), modified: mainChanged ? after : before)
        try write([user("S", "2026-09-28T07:02:00Z", sidechain: true), user("S", "2026-09-28T07:03:00Z", sidechain: true)],
                  to: projects.appending(path: "S/subagents/agent-1.jsonl"), modified: subagentChanged ? after : before)
        if other {
            try write([user("T", "2026-09-28T08:00:00Z")], to: projects.appending(path: "T.jsonl"), modified: before)
        }
        // A fresh loader and no ledger, so nothing from another test is remembered.
        let loader = AgentActivityLoader()
        return try await loader.sessions(since: since, sources: [.claude], fixtureRoot: root).map(\.session)
    }

    @Test(arguments: [(true, false), (false, true), (true, true)])
    func aSessionComesWholeWhicheverOfItsLogsChanged(mainChanged: Bool, subagentChanged: Bool) async throws {
        let sessions = try await upload(mainChanged: mainChanged, subagentChanged: subagentChanged, other: true)

        let session = try #require(sessions.first)
        #expect(sessions.map(\.id) == ["claude:S"])
        // The prompt of the main log and the two events of the subagent's.
        #expect(session.events.filter(\.isTouch).count == 1)
        #expect(session.events.count == 4)
    }

    @Test func aSessionWhoseLogsAllPredateTheLookIsNotSent() async throws {
        #expect(try await upload(mainChanged: false, subagentChanged: false).isEmpty)
    }

    @Test func aCodexLogBeforeTheLookIsStillNotSent() async throws {
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let since = Date(timeIntervalSince1970: 1_790_000_000)
        try write([#"{"type":"session_meta","payload":{"id":"c","cwd":"/r"}}"#, #"{"type":"event_msg","timestamp":"2026-09-28T07:00:00Z","payload":{"type":"task_started"}}"#],
                  to: root.appending(path: "codex/sessions/rollout-c.jsonl"), modified: since.addingTimeInterval(-3600))
        let sessions = try await AgentActivityLoader().sessions(since: since, sources: [.codex], fixtureRoot: root)
        #expect(sessions.isEmpty)
    }
}
