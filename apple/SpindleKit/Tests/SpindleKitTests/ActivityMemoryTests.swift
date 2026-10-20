import Foundation
import Testing
@testable import SpindleKit

struct ActivityMemoryTests {
    private let day = DayKey(year: 2026, month: 9, day: 28)
    private let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)

    private func fixture(cwd: String = "/repo", extra: String = "") throws -> (root: URL, file: URL) {
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        let folder = root.appending(path: "codex/sessions/2026/09/11")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appending(path: "rollout-s.jsonl")
        let meta = try JSONSerialization.data(withJSONObject: ["type": "session_meta", "payload": ["id": "s", "cwd": cwd]])
        let text = String(decoding: meta, as: UTF8.self) + "\n"
            + #"{"type":"event_msg","timestamp":"2026-09-28T07:00:00Z","payload":{"type":"task_started"}}"# + "\n" + extra
        try text.write(to: file, atomically: true, encoding: .utf8)
        return (root, file)
    }

    @Test func clearingActivityReleasesEveryCacheAndDoesNotReadLogs() async throws {
        let fixture = try fixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let loader = AgentActivityLoader()
        _ = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        #expect(await loader.cacheUsage.files == 1)
        #expect(await loader.cacheUsage.requests == 1)
        let before = await loader.parsedBytes
        await loader.clearCache()
        #expect(await loader.cacheUsage.bytes == 0)
        #expect(await loader.parsedBytes == before)
        _ = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        #expect(await loader.parsedBytes == before * 2)
        _ = try await loader.load(days: [day], time: time, sources: [], fixtureRoot: fixture.root)
        #expect(await loader.cacheUsage.bytes == 0)
        #expect(await loader.parsedBytes == before * 2)
    }

    @Test func oversizedCacheValuesStillProduceCorrectDaysWithoutBeingRetained() async throws {
        let fixture = try fixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let loader = AgentActivityLoader(fileCacheBytes: 1, completedCacheBytes: 1)
        for _ in 0..<2 {
            let result = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
            #expect(result[0].attentionTotal == 900)
            #expect(await loader.cacheUsage.files == 0)
            #expect(await loader.cacheUsage.requests == 0)
        }
    }

    @Test func completedDaysKeepDependencyStampsAfterRepositoryCacheEviction() async throws {
        let repo = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: repo) }
        try FileManager.default.createDirectory(at: repo.appending(path: ".git"), withIntermediateDirectories: true)
        let config = repo.appending(path: ".git/config")
        try "[remote \"origin\"]\n url = original\n".write(to: config, atomically: true, encoding: .utf8)
        let fixture = try fixture(cwd: repo.path)
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let loader = AgentActivityLoader(fileCacheBytes: 1024 * 1024, repositoryCacheBytes: 0)
        let first = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        #expect(first[0].attention.first?.repository.origin == "original")
        #expect(await loader.cacheUsage.requests == 1)
        let bytes = await loader.parsedBytes
        try "[remote \"origin\"]\n url = changed\n".write(to: config, atomically: true, encoding: .utf8)
        let second = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        #expect(second[0].attention.first?.repository.origin == "changed")
        #expect(await loader.parsedBytes == bytes)
    }

    @Test func fragmentedRecordsReuseTheirBufferWithoutChangingDecodedStringsOrTail() throws {
        let url = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: url) }
        let first = String(repeating: "a", count: 2 * 1024 * 1024 - 2)
        let second = String(repeating: "b", count: 1024 * 1024 + 17)
        let tail = String(repeating: "c", count: 1024 * 1024 + 9)
        try ("\"" + first + "\"\n\"" + second + "\"\n" + tail).write(to: url, atomically: true, encoding: .utf8)
        let file = try FileHandle(forReadingFrom: url)
        defer { try? file.close() }
        var decoded: [String] = []
        let result = try ActivityLineReader.read(file, from: .init(), recordLimit: 2 * 1024 * 1024) {
            if let value = try? JSONDecoder().decode(String.self, from: $0) { decoded.append(value) }
        }
        #expect(decoded == [first, second])
        #expect(String(decoding: result.tail, as: UTF8.self) == tail)
        #expect(result.peakBufferedBytes == 2 * 1024 * 1024)
        #expect(result.state.offset == UInt64(first.utf8.count + second.utf8.count + 6))
    }

    @Test func displayedDaysKeepCarryInMetadataButOnlyTheirOwnEvents() async throws {
        let extra = #"{"type":"event_msg","timestamp":"2026-09-27T21:56:00Z","payload":{"type":"task_started"}}"# + "\n"
            + #"{"type":"response_item","timestamp":"2026-09-27T22:01:00Z","payload":{"role":"assistant"}}"# + "\n"
            + #"{"type":"response_item","timestamp":"2026-09-29T12:00:00Z","payload":{"role":"assistant"}}"# + "\n"
            + #"{"type":"session_meta","payload":{"id":"future","cwd":"/future"}}"# + "\n"
            + #"{"type":"event_msg","timestamp":"2026-09-30T12:00:00Z","payload":{"type":"task_started"}}"# + "\n"
        let fixture = try fixture(extra: extra)
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let result = try await AgentActivityLoader().load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        #expect(result[0].attentionTotal == 1560)
        #expect(result[0].sessions.map(\.id) == ["codex:s"])
        #expect(result[0].sessions[0].events.count == 2)
        #expect(result[0].sessions[0].events.allSatisfy {
            $0.date >= time.start(of: day) && $0.date < time.start(of: time.adding(1, to: day))
        })
    }

    @Test func deletedLogsAreRemovedFromTheParsedCache() async throws {
        let fixture = try fixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let loader = AgentActivityLoader()
        _ = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        #expect(await loader.cacheUsage.files == 1)
        try FileManager.default.removeItem(at: fixture.file)
        let result = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        #expect(!result[0].hasActivity)
        #expect(await loader.cacheUsage.files == 0)
    }

    @Test func oversizedUnterminatedRecordsDoNotGetReadAgainAfterAnAppend() async throws {
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appending(path: "codex/sessions/2026/09/11")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appending(path: "rollout-large.jsonl")
        FileManager.default.createFile(atPath: url.path, contents: nil)
        let writer = try FileHandle(forWritingTo: url)
        defer { try? writer.close() }
        try writer.truncate(atOffset: UInt64(ActivityLineReader.recordLimit + 1))
        let loader = AgentActivityLoader()
        _ = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        let before = await loader.parsedBytes
        let valid = "\n" + #"{"type":"session_meta","payload":{"id":"s","cwd":"/repo"}}"# + "\n"
            + #"{"type":"event_msg","timestamp":"2026-09-28T07:00:00Z","payload":{"type":"task_started"}}"# + "\n"
        try writer.seekToEnd()
        try writer.write(contentsOf: Data(valid.utf8))
        let result = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        #expect(result[0].attentionTotal == 900)
        #expect(await loader.parsedBytes - before == UInt64(valid.utf8.count))
    }

    private func ledgerDirectory() -> URL {
        FileManager.default.temporaryDirectory.appending(path: "ledger-" + UUID().uuidString)
    }

    @Test func aRelaunchWithALedgerReadsNoUnchangedLog() async throws {
        let fixture = try fixture()
        let ledger = ledgerDirectory()
        defer { try? FileManager.default.removeItem(at: fixture.root); try? FileManager.default.removeItem(at: ledger) }
        let first = try await AgentActivityLoader(ledgerDirectory: ledger).load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)

        let relaunched = AgentActivityLoader(ledgerDirectory: ledger)
        let again = try await relaunched.load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)

        #expect(again[0].attentionTotal == first[0].attentionTotal)
        #expect(again[0].attentionTotal == 900)
        #expect(await relaunched.parsedBytes == 0)
    }

    @Test func aRelaunchReadsOnlyWhatALogGainedSince() async throws {
        let fixture = try fixture()
        let ledger = ledgerDirectory()
        defer { try? FileManager.default.removeItem(at: fixture.root); try? FileManager.default.removeItem(at: ledger) }
        _ = try await AgentActivityLoader(ledgerDirectory: ledger).load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        let appended = #"{"type":"event_msg","timestamp":"2026-09-28T08:00:00Z","payload":{"type":"task_started"}}"# + "\n"
        let writer = try FileHandle(forWritingTo: fixture.file)
        try writer.seekToEnd()
        try writer.write(contentsOf: Data(appended.utf8))
        try writer.close()

        let relaunched = AgentActivityLoader(ledgerDirectory: ledger)
        let result = try await relaunched.load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)

        #expect(result[0].attentionTotal == 1800)
        #expect(await relaunched.parsedBytes == UInt64(appended.utf8.count))
    }

    @Test func anUnfinishedLastLineIsReadAgainAfterARelaunch() async throws {
        let fixture = try fixture(extra: #"{"type":"event_msg","timestamp":"2026-09-28T08:00:00Z","payload":{"type":"task_started"}}"#)
        let ledger = ledgerDirectory()
        defer { try? FileManager.default.removeItem(at: fixture.root); try? FileManager.default.removeItem(at: ledger) }
        let first = try await AgentActivityLoader(ledgerDirectory: ledger).load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)

        let relaunched = AgentActivityLoader(ledgerDirectory: ledger)
        let again = try await relaunched.load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)

        #expect(first[0].attentionTotal == 1800)
        #expect(again[0].attentionTotal == 1800)
        #expect(await relaunched.parsedBytes > 0)
    }

    @Test func aDeletedLogKeepsItsTimeFromTheLedger() async throws {
        let fixture = try fixture()
        let ledger = ledgerDirectory()
        defer { try? FileManager.default.removeItem(at: fixture.root); try? FileManager.default.removeItem(at: ledger) }
        _ = try await AgentActivityLoader(ledgerDirectory: ledger).load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        try FileManager.default.removeItem(at: fixture.file)

        let result = try await AgentActivityLoader(ledgerDirectory: ledger).load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)

        #expect(result[0].attentionTotal == 900)
        // Only while that source is read.
        let withoutCodex = try await AgentActivityLoader(ledgerDirectory: ledger).load(days: [day], time: time, sources: [.claude], fixtureRoot: fixture.root)
        #expect(!withoutCodex[0].hasActivity)
    }

    /// Rewrites a ledger as an older parser left it: older versions, and parsers without what was added since.
    private func makeOlder(_ ledger: URL) throws {
        for url in try FileManager.default.contentsOfDirectory(at: ledger, includingPropertiesForKeys: nil) {
            var plist = try #require(try PropertyListSerialization.propertyList(from: Data(contentsOf: url), format: nil) as? [String: Any])
            plist["version"] = 1
            if var parser = plist["parser"] as? [String: Any] {
                parser.removeValue(forKey: "turns")
                plist["parser"] = parser
            }
            try PropertyListSerialization.data(fromPropertyList: plist, format: .binary, options: 0).write(to: url)
        }
    }

    @Test func anOlderParsersRecordIsReadAgainWhileItsLogIsThere() async throws {
        let fixture = try fixture()
        let ledger = ledgerDirectory()
        defer { try? FileManager.default.removeItem(at: fixture.root); try? FileManager.default.removeItem(at: ledger) }
        _ = try await AgentActivityLoader(ledgerDirectory: ledger).load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        try makeOlder(ledger)

        let relaunched = AgentActivityLoader(ledgerDirectory: ledger)
        let result = try await relaunched.load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)

        #expect(result[0].attentionTotal == 900)
        #expect(await relaunched.parsedBytes == UInt64(try Data(contentsOf: fixture.file).count))
        // Written again by this parser, so the next launch reads nothing.
        let next = AgentActivityLoader(ledgerDirectory: ledger)
        _ = try await next.load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        #expect(await next.parsedBytes == 0)
    }

    @Test func anOlderParsersRecordStillCountsForADeletedLog() async throws {
        let fixture = try fixture()
        let ledger = ledgerDirectory()
        defer { try? FileManager.default.removeItem(at: fixture.root); try? FileManager.default.removeItem(at: ledger) }
        _ = try await AgentActivityLoader(ledgerDirectory: ledger).load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        try makeOlder(ledger)
        try FileManager.default.removeItem(at: fixture.file)

        let result = try await AgentActivityLoader(ledgerDirectory: ledger).load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)

        #expect(result[0].attentionTotal == 900)
    }

    @Test func clearingActivityDeletesTheLedger() async throws {
        let fixture = try fixture()
        let ledger = ledgerDirectory()
        defer { try? FileManager.default.removeItem(at: fixture.root); try? FileManager.default.removeItem(at: ledger) }
        let loader = AgentActivityLoader(ledgerDirectory: ledger)
        _ = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: fixture.root)
        #expect(FileManager.default.fileExists(atPath: ledger.path))

        await loader.clearCache()

        #expect(!FileManager.default.fileExists(atPath: ledger.path))
    }

    @Test func eachDayIsBuiltFromNearbyEventsAloneWithTheSameResult() {
        var generator = SystemRandomNumberGenerator()
        let base = time.start(of: day).addingTimeInterval(-86_400)
        for _ in 0..<40 {
            var sessions: [AgentSession] = []
            for index in 0..<8 {
                // Events over three days, bunched so runs and attention cross midnight.
                var date = base.addingTimeInterval(Double.random(in: 0..<(3 * 86_400), using: &generator))
                var events: [AgentEvent] = []
                for _ in 0..<Int.random(in: 1..<60, using: &generator) {
                    events.append(AgentEvent(date: date, isTouch: Bool.random(using: &generator)))
                    date = date.addingTimeInterval(Double.random(in: 10..<(Bool.random(using: &generator) ? 300 : 7_200), using: &generator))
                }
                let parent = index > 0 && Bool.random(using: &generator) ? "s\(Int.random(in: 0..<index, using: &generator))" : nil
                sessions.append(AgentSession(id: "s\(index)", source: .codex, parentID: parent, cwd: "/repo\(index % 3)",
                                             title: "", branches: [], events: events.sorted { $0.date < $1.date }))
            }
            let byID = Dictionary(uniqueKeysWithValues: sessions.map { ($0.id, $0) })
            let repository = { (cwd: String) in ActivityRepository(key: cwd, name: cwd, origin: nil, isRepository: true) }
            for offset in -1...2 {
                let current = time.adding(offset, to: day)
                let all = ActivityTimeline.build(sessions: sessions, day: current, time: time, repository: repository)
                let near = ActivityTimeline.build(sessions: AgentActivityLoader.sessions(byID, near: current, time: time),
                                                  day: current, time: time, repository: repository)
                #expect(all.attention.map { "\($0.repository.key) \($0.range) \($0.sessionIDs.sorted())" }
                        == near.attention.map { "\($0.repository.key) \($0.range) \($0.sessionIDs.sorted())" })
                #expect(all.runs.map(\.id) == near.runs.map(\.id))
                #expect(all.runs.map(\.range) == near.runs.map(\.range))
                #expect(all.parallel == near.parallel)
            }
        }
    }

    @Test func cacheEvictsLeastRecentlyUsedValuesAndRejectsOversizedValues() {
        var cache = ActivityCache<String, Int>(byteLimit: 100, countLimit: 2)
        cache.insert(1, for: "a", cost: 40)
        cache.insert(2, for: "b", cost: 40)
        #expect(cache.value(for: "a") == 1)
        cache.insert(3, for: "c", cost: 40)
        #expect(cache.value(for: "b") == nil)
        #expect(cache.value(for: "a") == 1)
        cache.insert(4, for: "a", cost: 101)
        #expect(cache.value(for: "a") == nil)
        #expect(cache.bytes == 40)
        cache.removeAll()
        #expect(cache.bytes == 0 && cache.count == 0)
    }

    @Test func readerBoundsRecordsAndResumesDiscardingThroughTheNextNewline() throws {
        let url = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: url) }
        try Data(repeating: 120, count: 2 * 1024 * 1024).write(to: url)
        var lines: [String] = []
        func read(_ state: ActivityLineReader.State) throws -> ActivityLineReader.Result {
            let file = try FileHandle(forReadingFrom: url)
            defer { try? file.close() }
            try file.seek(toOffset: state.offset)
            return try ActivityLineReader.read(file, from: state, recordLimit: 4096) {
                lines.append(String(decoding: $0, as: UTF8.self))
            }
        }
        let first = try read(.init())
        #expect(first.peakBufferedBytes <= 4096)
        #expect(first.state.discarding)
        #expect(first.state.offset == 2 * 1024 * 1024)
        #expect(first.tail.isEmpty && lines.isEmpty)
        let writer = try FileHandle(forWritingTo: url)
        try writer.seekToEnd()
        try writer.write(contentsOf: Data("more junk\nvalid 😀\npartial".utf8))
        try writer.close()
        let second = try read(first.state)
        #expect(!second.state.discarding)
        #expect(lines == ["valid 😀"])
        #expect(String(decoding: second.tail, as: UTF8.self) == "partial")
        #expect(second.bytes == UInt64("more junk\nvalid 😀\npartial".utf8.count))
    }

    @Test func parentMetadataCannotAccumulateAnOversizedFirstLine() throws {
        let url = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: url) }
        try Data(repeating: 120, count: 4097).write(to: url)
        #expect(throws: ActivityLineReader.Failure.self) {
            try ActivityLineReader.firstLine(url, limit: 4096)
        }
        try Data((String(repeating: "x", count: 4096) + "\nrest").utf8).write(to: url)
        #expect(try ActivityLineReader.firstLine(url, limit: 4096).count == 4096)
    }
}

/// Lines read without decoding must leave the parser exactly as decoding them would.
struct QuickParsingTests {
    private func compare(_ source: AgentSource, _ lines: [String]) {
        var quick = AgentLogParser(source: source), full = AgentLogParser(source: source)
        full.readsQuickly = false
        for line in lines {
            quick.consume(Data(line.utf8))
            full.consume(Data(line.utf8))
        }
        func describe(_ parser: AgentLogParser) -> [String] {
            parser.finish().map { "\($0.id)|\($0.parentID ?? "")|\($0.cwd)|\($0.title)|\($0.branches.sorted())|\($0.events)" }
        }
        #expect(describe(quick) == describe(full))
        #expect(!describe(full).isEmpty)
    }

    @Test func claudeLinesReadQuicklyAsTheyDecode() {
        compare(.claude, [
            #"{"type":"user","sessionId":"a","cwd":"/r","gitBranch":"main","timestamp":"2026-09-28T07:00:00Z","message":{"content":"Fix the \"login\" bug\nthen tests"}}"#,
            #"{"type":"assistant","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:01:00.123Z","message":{"content":[{"type":"text","text":"ok"}]}}"#,
            #"{"parentUuid":null,"type":"user","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:02:00Z","toolUseResult":{"stdout":"x\\\"y"},"message":{"role":"user","content":[{"tool_use_id":"t","type":"tool_result","content":"a \"quoted\" {not json] value"}]}}"#,
            #"{ "type" : "user" , "sessionId": "a", "cwd": "/r", "timestamp": "2026-09-28T07:03:00Z", "message": { "content": [ { "type": "text", "text": "spaced prompt" } ] } }"#,
            #"{"type":"user","sessionId":"a","cwd":"/r\/other","timestamp":"2026-09-28T07:04:00Z","isSidechain":true,"message":{"content":"side"}}"#,
            #"{"type":"user","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:05:00Z","isMeta":"yes","message":{"content":"bad type"}}"#,
            #"{"type":"user","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:06:00Z","message":{"content":[{"type":"tool_result","text":5}]}}"#,
            #"{"type":"attachment","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:07:00Z"}"#,
            #"{"type":"custom-title","sessionId":"a","customTitle":"Named"}"#,
            #"{"type":"assistant","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:08:00Z"} trailing"#,
            #"{"type":"assistant","sessionId":"a","cwd":"/r","timestamp":"not a date"}"#,
            #"{"type":"user","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:09:00Z","message":{"content":"Describe today's entries"}}"#,
            #"{"type":"assistant","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:09:01Z","message":{"content":[{"type":"text","text":"Sure"}]}}"#,
            #"{"type":"assistant","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:09:02Z","message":{"content":[{"type":"tool_use","id":"t1","name":"ToolSearch","input":{}}]}}"#,
            #"{"type":"assistant","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:09:03Z","message":{"content":[{"type":"tool_use","id":"t2","name":"mcp__spindle__list_entries","input":{"from":"x"}}]}}"#,
            #"{"type":"user","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:09:04Z","message":{"content":[{"tool_use_id":"t2","type":"tool_result","content":"[]"}]}}"#,
            #"{"type":"assistant","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:09:05Z","isSidechain":true,"message":{"content":[{"type":"tool_use","id":"t3","name":"Bash","input":{}}]}}"#,
            #"{"type":"user","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:10:00Z","message":{"content":"Now fix the bug"}}"#,
            #"{"type":"assistant","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:10:01Z","message":{"content":[{"type":"tool_use","id":"t4","name":"mcp__spindle__get_timer","input":{}}]}}"#,
            #"{"type":"assistant","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:10:02Z","message":{"content":[{"type":"tool_use","id":"t5","name":"Edit","input":{}}]}}"#,
            #"{"type":"assistant","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:10:03Z","message":{"content":[{"type":"tool_use","id":"t6","name":5}]}}"#,
            #"{"type":"assistant","sessionId":"a","cwd":"/r","timestamp":"2026-09-28T07:10:04Z","message":{"content":[{"type":7}]}}"#,
        ])
    }

    @Test func codexLinesReadQuicklyAsTheyDecode() {
        compare(.codex, [
            #"{"type":"response_item","timestamp":"2026-09-28T06:59:00Z","payload":{"type":"message","role":"assistant"}}"#,
            #"{"timestamp":"2026-09-28T07:00:00Z","type":"session_meta","payload":{"id":"s","cwd":"/r","git":{"branch":"main"}}}"#,
            #"{"timestamp":"2026-09-28T07:00:01Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment>"}]}}"#,
            #"{"timestamp":"2026-09-28T07:00:02Z","type":"event_msg","payload":{"type":"user_message","message":"Make it \"faster\""}}"#,
            #"{"timestamp":"2026-09-28T07:00:03Z","type":"event_msg","payload":{"type":"task_started"}}"#,
            #"{"timestamp":"2026-09-28T07:00:04Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}}"#,
            #"{"timestamp":"2026-09-28T07:00:05Z","type":"response_item","payload":{"type":"function_call_output","output":"{\"a\":[1,2,{\"b\":\"]\"}]}"}}"#,
            #"{"timestamp":"2026-09-28T07:00:06Z","type":"event_msg","payload":{"type":"token_count","info":{"total":1}}}"#,
            #"{"timestamp":"2026-09-28T07:00:07Z","type":"response_item","payload":{"type":"message","role":"user","git":{"branch":3}}}"#,
            #"{"timestamp":"2026-09-28T07:00:08Z","type":"turn_context","payload":{"cwd":"/r"}}"#,
            #"{"timestamp":"2026-09-28T07:01:00Z","type":"event_msg","payload":{"type":"task_started"}}"#,
            #"{"timestamp":"2026-09-28T07:01:01Z","type":"response_item","payload":{"type":"function_call","name":"list_entries","namespace":"mcp__spindle","arguments":"{}","call_id":"c1"}}"#,
            #"{"timestamp":"2026-09-28T07:01:02Z","type":"response_item","payload":{"type":"function_call_output","call_id":"c1","output":"[]"}}"#,
            #"{"timestamp":"2026-09-28T07:02:00Z","type":"event_msg","payload":{"type":"task_started"}}"#,
            #"{"timestamp":"2026-09-28T07:02:01Z","type":"response_item","payload":{"type":"function_call","name":"mcp__spindle__get_timer","arguments":"{}"}}"#,
            #"{"timestamp":"2026-09-28T07:02:02Z","type":"response_item","payload":{"type":"function_call","name":"exec_command","namespace":null,"arguments":"{}"}}"#,
            #"{"timestamp":"2026-09-28T07:02:03Z","type":"response_item","payload":{"type":"custom_tool_call","name":{"odd":1}}}"#,
            #"{"timestamp":"2026-09-28T07:03:00Z","type":"event_msg","payload":{"type":"task_started"}}"#,
            #"{"timestamp":"2026-09-28T07:03:01Z","type":"response_item","payload":{"type":"custom_tool_call","name":"exec","input":"await tools.mcp__spindle__get_timer({})"}}"#,
            #"{"timestamp":"2026-09-28T07:03:02Z","type":"response_item","payload":{"type":"custom_tool_call","name":"exec","input":["not","a","script"]}}"#,
        ])
    }
}
