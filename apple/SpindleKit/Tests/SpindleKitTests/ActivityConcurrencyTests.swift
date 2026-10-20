import Foundation
import Testing
@testable import SpindleKit

struct ActivityConcurrencyTests {
    private let day = DayKey("2026-09-28")!
    private let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)

    private actor SlowRead {
        var calls = 0
        private var released = false
        private var held: [CheckedContinuation<Void, Never>] = []
        func pause() async {
            calls += 1
            if released { return }
            await withCheckedContinuation { held.append($0) }
        }
        func release() { released = true; for continuation in held { continuation.resume() }; held = [] }
        func waitForStart() async throws {
            for _ in 0..<200 {
                if calls > 0 { return }
                try await Task.sleep(for: .milliseconds(5))
            }
            throw Failure.didNotStart
        }
    }
    private enum Failure: Error { case didNotStart }

    private func fixture() throws -> (root: URL, file: URL, ledger: URL) {
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        let folder = root.appending(path: "codex/sessions")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appending(path: "rollout-s.jsonl")
        try """
        {"type":"session_meta","payload":{"id":"s","cwd":"/repo"}}
        {"type":"event_msg","timestamp":"2026-09-28T07:00:00Z","payload":{"type":"task_started"}}

        """.write(to: file, atomically: true, encoding: .utf8)
        return (root, file, root.appending(path: "ledger"))
    }

    @Test func interactiveDaysFinishWhileAnUploadFileIsHeldAndUploadsJoin() async throws {
        let fixture = try fixture(), slow = SlowRead()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let loader = AgentActivityLoader(ledgerDirectory: fixture.ledger, beforeBackgroundFile: { await slow.pause() })
        let start = time.start(of: day)
        let first = Task { try await loader.sessions(since: start, sources: [.codex], fixtureRoot: fixture.root) }
        try await slow.waitForStart()
        let joined = Task { try await loader.sessions(since: start, sources: [.codex], fixtureRoot: fixture.root) }
        let clock = ContinuousClock(), began = clock.now
        let days = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        #expect(clock.now - began < .milliseconds(500))
        #expect(days.first?.sessions.first?.id == "codex:s")
        #expect(await slow.calls == 1)
        await slow.release()
        #expect(try await first.value.map(\.session.id) == ["codex:s"])
        #expect(try await joined.value.map(\.session.id) == ["codex:s"])
        #expect(await slow.calls == 1)
    }

    @Test func anOlderBackgroundParseCannotReplaceTheInteractiveLedgersAppend() async throws {
        let fixture = try fixture(), slow = SlowRead()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let loader = AgentActivityLoader(ledgerDirectory: fixture.ledger, beforeBackgroundFile: { await slow.pause() })
        let background = Task { try await loader.sessions(since: time.start(of: day), sources: [.codex], fixtureRoot: fixture.root) }
        try await slow.waitForStart()
        let handle = try FileHandle(forWritingTo: fixture.file)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data((#"{"type":"event_msg","timestamp":"2026-09-28T07:15:00Z","payload":{"type":"task_started"}}"# + "\n").utf8))
        try handle.close()
        let interactive = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        await slow.release()
        _ = try await background.value
        try FileManager.default.removeItem(at: fixture.file)
        let relaunched = try await AgentActivityLoader(ledgerDirectory: fixture.ledger).load(
            days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        #expect(relaunched.first?.sessions.map(\.events) == interactive.first?.sessions.map(\.events))
        #expect(relaunched.first?.sessions.first?.events.count == 2)
    }

    @Test func aNewBackgroundRequestDropsTheOldOne() async throws {
        let fixture = try fixture(), slow = SlowRead()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let loader = AgentActivityLoader(ledgerDirectory: fixture.ledger, beforeBackgroundFile: { await slow.pause() })
        let old = Task { try await loader.sessions(since: time.start(of: day), sources: [.codex], fixtureRoot: fixture.root) }
        try await slow.waitForStart()
        let replacement = try await loader.sessions(since: time.start(of: day), sources: [.claude], fixtureRoot: fixture.root)
        #expect(replacement.isEmpty)
        await slow.release()
        await #expect(throws: CancellationError.self) { try await old.value }
        #expect(!FileManager.default.fileExists(atPath: fixture.ledger.path))
    }

    @Test func clearingActivityDropsAnInFlightUploadAndCannotRecreateItsLedger() async throws {
        let fixture = try fixture(), slow = SlowRead()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let loader = AgentActivityLoader(ledgerDirectory: fixture.ledger, beforeBackgroundFile: { await slow.pause() })
        let background = Task { try await loader.sessions(since: time.start(of: day), sources: [.codex], fixtureRoot: fixture.root) }
        try await slow.waitForStart()
        await loader.clearCache()
        await slow.release()
        await #expect(throws: CancellationError.self) { try await background.value }
        #expect(!FileManager.default.fileExists(atPath: fixture.ledger.path))
        #expect(await loader.cacheUsage.bytes == 0)
    }
}
