import Foundation
import Testing
@testable import SpindleKit

struct AgentActivityTests {
    let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
    let day = DayKey("2026-09-28")!
    func date(_ text: String) -> Date { try! Date(text, strategy: .iso8601) }
    func repo(_ cwd: String) -> ActivityRepository {
        ActivityRepository(key: cwd, name: cwd, origin: nil, isRepository: true)
    }
    func session(_ id: String, repo: String = "a", parent: String? = nil, events: [(Double, Bool)]) -> AgentSession {
        AgentSession(id: id, source: .codex, parentID: parent, cwd: repo, title: id, branches: [],
                     events: events.map { AgentEvent(date: time.start(of: day).addingTimeInterval($0.0 * 60), isTouch: $0.1) })
    }
    func parse(_ lines: [String], source: AgentSource) -> [AgentSession] {
        var parser = AgentLogParser(source: source)
        for line in lines { parser.consume(Data(line.utf8)) }
        return parser.finish()
    }

    @Test(arguments: [
        (#""hello""#, "", true),
        (#"[{"type":"text","text":"hello"}]"#, "", true),
        (#"[{"type":"tool_result"}]"#, "", false),
        (#"[{"type":"text","text":"hello"},{"type":"tool_result"}]"#, "", false),
        (#""hello""#, #", "isMeta":true"#, false),
        (#""hello""#, #", "isSidechain":true"#, false),
        (#""hello""#, #", "promptSource":"system""#, false),
        (#""hello""#, #", "promptSource":"sdk""#, true),
        ("null", "", false),
    ])
    func claudeTouchRules(content: String, flags: String, expected: Bool) {
        let values = parse(["""
        {"type":"user","sessionId":"s","cwd":"/repo","timestamp":"2026-09-28T07:00:00.123Z","message":{"content":\(content)}\(flags)}
        """], source: .claude)
        #expect(values.count == 1)
        #expect(values.first?.events.first?.isTouch == expected)
        #expect(values.first?.events.first?.date != nil)
    }

    @Test func titlesWithoutTimestampAndMalformedLines() {
        let sessions = parse([
            "broken", #"{"type":"unknown"}"#,
            #"{"type":"user","sessionId":"s","cwd":"/repo","timestamp":"2026-09-28T07:00:00Z","message":{"content":"First line\nPrivate remainder"}}"#,
            #"{"type":"assistant","sessionId":"s","cwd":"/repo","timestamp":"bad"}"#,
            #"{"type":"custom-title","sessionId":"s","customTitle":"A chosen title"}"#
        ], source: .claude)
        #expect(sessions.count == 1)
        #expect(sessions[0].title == "A chosen title")
        #expect(sessions[0].events.count == 1)
    }

    @Test func claudePromptsThatOnlyAskSpindleAreNotTouches() {
        func line(_ minute: Int, _ type: String, _ content: String, sidechain: Bool = false) -> String {
            #"{"type":"\#(type)","sessionId":"s","cwd":"/repo","timestamp":"2026-09-28T07:\#(String(format: "%02d", minute)):00Z"\#(sidechain ? #","isSidechain":true"# : ""),"message":{"content":\#(content)}}"#
        }
        func call(_ name: String) -> String { #"[{"type":"tool_use","id":"t","name":"\#(name)","input":{}}]"# }
        let result = #"[{"tool_use_id":"t","type":"tool_result","content":"ok"}]"#
        let sessions = parse([
            line(0, "user", #""Describe today's entries\nin detail""#),
            line(1, "assistant", call("ToolSearch")),
            line(2, "assistant", call("mcp__spindle__list_entries")),
            line(3, "user", result),
            // A subagent's calls are the call that started it.
            line(4, "assistant", call("Bash"), sidechain: true),
            line(5, "assistant", call("mcp__spindle__update_entries")),
            line(10, "user", #""Fix the login bug""#),
            line(11, "assistant", call("mcp__spindle__get_timer")),
            line(12, "assistant", call("Edit")),
            line(13, "assistant", call("mcp__spindle__get_timer")),
            line(20, "user", #""What is the time?""#),
            line(21, "assistant", #"[{"type":"text","text":"Noon"}]"#),
            line(30, "user", #""Search the web, then my entries""#),
            line(31, "assistant", #"[{"type":"server_tool_use","id":"w","name":"web_search","input":{}}]"#),
            line(32, "assistant", call("mcp__spindle__list_entries")),
            line(40, "user", #""Somebody else's server""#),
            line(41, "assistant", call("mcp__spindlewood__list")),
        ], source: .claude)
        let touches = sessions[0].events.filter(\.isTouch).map { Int($0.date.timeIntervalSince(date("2026-09-28T07:00:00Z")) / 60) }
        #expect(touches == [10, 20, 30, 40])
        // The question didn't name the session; the first prompt that was work did.
        #expect(sessions[0].title == "Fix the login bug")
    }

    @Test func codexPromptsThatOnlyAskSpindleAreNotTouches() {
        let sessions = parse([
            #"{"type":"session_meta","payload":{"id":"s","cwd":"/repo","source":"cli"}}"#,
            #"{"type":"event_msg","timestamp":"2026-09-28T07:00:00Z","payload":{"type":"task_started"}}"#,
            #"{"type":"response_item","timestamp":"2026-09-28T07:00:01Z","payload":{"type":"function_call","name":"list_entries","namespace":"mcp__spindle","arguments":"{}"}}"#,
            #"{"type":"event_msg","timestamp":"2026-09-28T07:10:00Z","payload":{"type":"task_started"}}"#,
            // The older form, with the server in the name.
            #"{"type":"response_item","timestamp":"2026-09-28T07:10:01Z","payload":{"type":"function_call","name":"mcp__spindle__get_timer","arguments":"{}"}}"#,
            #"{"type":"event_msg","timestamp":"2026-09-28T07:20:00Z","payload":{"type":"task_started"}}"#,
            #"{"type":"response_item","timestamp":"2026-09-28T07:20:01Z","payload":{"type":"function_call","name":"list_entries","namespace":"mcp__spindle","arguments":"{}"}}"#,
            #"{"type":"response_item","timestamp":"2026-09-28T07:20:02Z","payload":{"type":"function_call","name":"exec_command","arguments":"{}"}}"#,
            #"{"type":"event_msg","timestamp":"2026-09-28T07:30:00Z","payload":{"type":"task_started"}}"#,
            #"{"type":"response_item","timestamp":"2026-09-28T07:30:01Z","payload":{"type":"message","role":"assistant"}}"#,
            // Code mode, as Codex 0.160 writes it: scripts that list tools, read the clock, then call Spindle's.
            #"{"type":"event_msg","timestamp":"2026-09-28T07:40:00Z","payload":{"type":"task_started"}}"#,
            #"{"type":"response_item","timestamp":"2026-09-28T07:40:01Z","payload":{"type":"custom_tool_call","status":"completed","call_id":"c","name":"exec","input":"const matches = ALL_TOOLS.filter(x => /spindle/i.test(x.name));\ntext(await tools.clock__curr_time({}));\n"}}"#,
            #"{"type":"response_item","timestamp":"2026-09-28T07:40:02Z","payload":{"type":"custom_tool_call_output","call_id":"c","output":[]}}"#,
            #"{"type":"response_item","timestamp":"2026-09-28T07:40:03Z","payload":{"type":"custom_tool_call","name":"exec","input":"const r = await Promise.allSettled([tools.mcp__spindle__get_timer({}), tools.mcp__spindle__list_entries({})]);"}}"#,
            #"{"type":"event_msg","timestamp":"2026-09-28T07:50:00Z","payload":{"type":"task_started"}}"#,
            #"{"type":"response_item","timestamp":"2026-09-28T07:50:01Z","payload":{"type":"custom_tool_call","name":"exec","input":"await tools.mcp__spindle__list_entries({}); await tools.exec_command({cmd: 'ls'});"}}"#,
        ], source: .codex)
        let touches = sessions[0].events.filter(\.isTouch).map { Int($0.date.timeIntervalSince(date("2026-09-28T07:00:00Z")) / 60) }
        #expect(touches == [20, 30, 50])
    }

    @Test(arguments: [
        ("mcp__spindle__list_entries", nil, nil, AgentLogParser.ToolCall.own),
        ("mcp__spindle__x", nil, nil, .own),
        ("list_entries", "mcp__spindle", "function_call", .own),
        ("list_entries", "mcp__spindle_other", "function_call", .other),
        ("mcp__spindle", nil, nil, .other),
        ("ToolSearch", nil, nil, .neutral),
        (nil, nil, "tool_search_call", .neutral),
        ("Bash", nil, nil, .other),
        (nil, nil, "web_search_call", .other),
        ("curr_time", "clock", "function_call", .neutral),
    ] as [(String?, String?, String?, AgentLogParser.ToolCall)])
    func toolCallsAreToldApart(name: String?, namespace: String?, type: String?, expected: AgentLogParser.ToolCall) {
        #expect(AgentLogParser.classify(name: name, namespace: namespace, type: type) == expected)
    }

    @Test(arguments: [
        ("text(ALL_TOOLS.length)", AgentLogParser.ToolCall.neutral),
        ("await tools.clock__curr_time({})", .neutral),
        ("await tools.mcp__spindle__get_timer({})", .own),
        ("await tools['mcp__spindle__list_entries']({})", .own),
        ("const { mcp__spindle__list_entries: list } = tools; await list({})", .own),
        ("await tools.mcp__spindle__get_timer({}); await tools.exec_command({cmd: 'make'})", .other),
        ("await tools.apply_patch({})", .other),
    ])
    func codeModeScriptsGoByTheToolsTheyCall(script: String, expected: AgentLogParser.ToolCall) {
        #expect(AgentLogParser.classify(name: "exec", namespace: nil, type: "custom_tool_call", input: script) == expected)
    }

    @Test func codexTopLevelAndChild() {
        for parent in ["", #", "parent_thread_id":"parent""#] {
            let sessions = parse([
                """
                {"type":"session_meta","payload":{"id":"child","cwd":"/child","source":"cli"\(parent)}}
                """,
                #"{"type":"event_msg","timestamp":"2026-09-28T07:00:00Z","payload":{"type":"task_started"}}"#,
                #"{"type":"event_msg","timestamp":"2026-09-28T07:01:00Z","payload":{"type":"user_message"}}"#,
                #"{"type":"response_item","timestamp":"2026-09-28T07:02:00Z","payload":{"type":"message","role":"assistant"}}"#
            ], source: .codex)
            #expect(sessions[0].events.filter(\.isTouch).count == (parent.isEmpty ? 1 : 0))
            #expect(sessions[0].events.last?.isTouch == false)
        }
        let result = ActivityTimeline.build(sessions: [
            session("parent", events: []), session("child", repo: "wrong", parent: "parent", events: [(60, true), (62, false)])
        ], day: day, time: time, repository: repo)
        #expect(result.attention.isEmpty)
        #expect(result.runs.first?.repository.key == "a")
    }

    @Test func attentionCapsHandsOverMergesAndLeavesGaps() {
        let result = ActivityTimeline.build(sessions: [
            session("one", events: [(60, true), (65, true), (100, true)]),
            session("two", repo: "b", events: [(70, true)])
        ], day: day, time: time, repository: repo)
        #expect(result.attention.map { $0.range.duration / 60 } == [10, 15, 15])
        #expect(result.attention.map { $0.repository.key } == ["a", "b", "a"])
        #expect(result.blocks.map { $0.duration / 60 } == [25, 15])
        #expect(result.attentionTotal == 40 * 60)
    }

    @Test func focusBlocksSumSharesInOrderOfFirstAttention() {
        let result = ActivityTimeline.build(sessions: [
            session("one", events: [(60, true), (80, true), (100, true)]),
            session("two", repo: "b", events: [(70, true), (90, true)])
        ], day: day, time: time, repository: repo)
        let blocks = result.focusBlocks
        #expect(blocks.count == 1)
        #expect(blocks[0].range.duration == 55 * 60)
        #expect(blocks[0].shares.map(\.repository.key) == ["a", "b"])
        #expect(blocks[0].shares.map { $0.duration / 60 } == [35, 20])
        #expect(blocks[0].shares[0].sessionIDs == ["one"])
        #expect(blocks[0].switches == 4)
    }

    @Test func simultaneousTouchesAreDeterministicAndBounded() {
        let sessions = (0..<30).map { id in
            session("s\(id)", repo: "r\(id)", events: stride(from: 0.0, to: 1440, by: 7).map { ($0, true) })
        }
        let result = ActivityTimeline.build(sessions: sessions, day: day, time: time, repository: repo)
        #expect(result.attentionTotal == 24 * 3600)
        #expect(zip(result.attention, result.attention.dropFirst()).allSatisfy { $0.range.end <= $1.range.start })
        let reversed = ActivityTimeline.build(sessions: sessions.reversed(), day: day, time: time, repository: repo)
        #expect(result.attention.map(\.id) == reversed.attention.map(\.id))
    }

    @Test func midnightAndRunGap() {
        let result = ActivityTimeline.build(sessions: [session("s", events: [(-5, true), (-2, false), (1, false), (5, false), (1435, true), (1438, false), (1441, false)])], day: day, time: time, repository: repo)
        #expect(result.attention.map { $0.range.duration / 60 } == [10, 5])
        #expect(result.runs.map { $0.range.duration / 60 } == [1, 0, 5])
        #expect(result.runs.first?.range.start == time.start(of: day))
    }

    @Test(arguments: [("2026-03-29", 23), ("2026-10-25", 25)])
    func daylightSavingUsesElapsedTime(text: String, hours: Int) {
        let day = DayKey(text)!, start = time.start(of: DayKey(text)!)
        let events = stride(from: 0, to: hours * 3600, by: 600).map { AgentEvent(date: start.addingTimeInterval(Double($0)), isTouch: true) }
        let s = AgentSession(id: "s", source: .claude, cwd: "a", title: "", branches: [], events: events)
        let result = ActivityTimeline.build(sessions: [s], day: day, time: time, repository: repo)
        #expect(result.attentionTotal == Double(hours * 3600))
    }

    @Test func parallelCountsRepositoriesNotSessions() {
        let result = ActivityTimeline.build(sessions: [
            session("a", events: [(60, true), (63, false)]),
            session("b", events: [(60, true), (63, false)]),
            session("c", repo: "b", events: [(61, true), (64, false)])
        ], day: day, time: time, repository: repo)
        #expect(result.parallel == 120)
        #expect(result.totals[repo("a")]?.agent == 360)
    }

    @Test func resolvesWorktreeScratchMissingAndNestedPaths() {
        let files = ["/repos/work/.git": "gitdir: ../main/.git/worktrees/work\n",
                     "/repos/main/.git/worktrees/work/commondir": "../..\n",
                     "/repos/main/.git/config": "[remote \"origin\"]\n url = git@example.com:team/main.git\n"]
        func resolve(_ cwd: String) -> ActivityRepository {
            ActivityRepositoryResolver.resolve(cwd: cwd, home: "/home/person", exists: { files[$0] != nil || ["/repos/work/sub", "/repos/work"].contains($0) }, read: { files[$0] })
        }
        #expect(resolve("/repos/work/sub").key == "/repos/main")
        #expect(resolve("/repos/work/sub").origin == "git@example.com:team/main.git")
        #expect(resolve("/missing/deleted").name == "deleted")
        #expect(!resolve("/missing/deleted").isRepository)
        #expect(resolve("/home/person/Library/Application Support/Claude/one/scratch/a").name == "Scratch")
        #expect(resolve("/home/person/Library/Application Support/Claude/one/scratch/a").key == resolve("/home/person/Library/Application Support/Claude/two/scratch/b").key)
    }

    @Test func codexTitleUsesTheRequestBeforeTheFirstReply() {
        let sessions = parse([
            #"{"type":"session_meta","payload":{"id":"s","cwd":"/repo"}}"#,
            #"{"type":"response_item","timestamp":"2026-09-28T07:00:00Z","payload":{"role":"user","content":[{"type":"input_text","text":"Environment instructions"}]}}"#,
            #"{"type":"response_item","timestamp":"2026-09-28T07:00:00Z","payload":{"role":"user","content":[{"type":"input_text","text":"Fix the search\nMore detail"}]}}"#,
            #"{"type":"response_item","timestamp":"2026-09-28T07:01:00Z","payload":{"role":"assistant"}}"#,
            #"{"type":"response_item","timestamp":"2026-09-28T07:02:00Z","payload":{"role":"user","content":"A later prompt"}}"#
        ], source: .codex)
        #expect(sessions.first?.title == "Fix the search")
    }

    @Test func loaderRechecksChangedFilesAndMissingSources() async throws {
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appending(path: "codex/sessions/2026/09/27")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appending(path: "rollout-test.jsonl")
        let initial = #"{"type":"session_meta","payload":{"id":"s","cwd":"/missing"}}"# + "\n"
            + #"{"type":"event_msg","timestamp":"2026-09-27T21:55:00Z","payload":{"type":"task_started"}}"# + "\n"
        try initial.write(to: file, atomically: true, encoding: .utf8)
        let loader = AgentActivityLoader()
        let first = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        #expect(first[0].attentionTotal == 10 * 60)
        let cached = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        #expect(cached[0].attentionTotal == first[0].attentionTotal)
        let changed = initial + "malformed\n" + #"{"type":"event_msg","timestamp":"2026-09-28T07:00:00Z","payload":{"type":"task_started"}}"#
        try changed.write(to: file, atomically: true, encoding: .utf8)
        let updated = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        #expect(updated[0].attentionTotal == 25 * 60)
        let missing = try await loader.load(days: [day], time: time, sources: [.claude], fixtureRoot: root)
        #expect(!missing[0].hasActivity)
        let disabled = try await loader.load(days: [day], time: time, sources: [], fixtureRoot: root)
        #expect(disabled.isEmpty)
    }

    @Test func loaderReadsCopilotSessionLogsAndNotTheFilesBesideThem() async throws {
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let session = root.appending(path: "copilot/session-state/s1")
        try FileManager.default.createDirectory(at: session.appending(path: "files"), withIntermediateDirectories: true)
        let start = #"{"type":"session.start","timestamp":"2026-09-28T07:00:00.000Z","data":{"sessionId":"s1","context":{"cwd":"/missing","branch":"main"}}}"#
        try [start, #"{"type":"user.message","timestamp":"2026-09-28T07:00:00.000Z","data":{"content":"Fix it"}}"#]
            .joined(separator: "\n").write(to: session.appending(path: "events.jsonl"), atomically: true, encoding: .utf8)
        // A copy of a file the agent worked on, which only looks like a log.
        try #"{"type":"user.message","timestamp":"2026-09-28T09:00:00.000Z","data":{"content":"Not a prompt"}}"#
            .write(to: session.appending(path: "files/notes.jsonl"), atomically: true, encoding: .utf8)
        // A ledger with records of the other sources, as an update finds it: Copilot's logs have none, so they are read.
        let ledger = root.appending(path: "ledger")
        _ = try await AgentActivityLoader(ledgerDirectory: ledger).load(days: [day], time: time, sources: [.claude, .codex], fixtureRoot: root)
        let loader = AgentActivityLoader(ledgerDirectory: ledger)
        let values = try await loader.load(days: [day], time: time, sources: [.copilot], fixtureRoot: root)
        #expect(values[0].attentionTotal == 15 * 60)
        #expect(values[0].sessions.map(\.id) == ["copilot:s1"])
        #expect(values[0].runs.map(\.source) == [.copilot])
        let others = try await loader.load(days: [day], time: time, sources: [.claude, .codex], fixtureRoot: root)
        #expect(!others[0].hasActivity)
    }

    @Test func loaderResolvesAParentOutsideTheSelectedFolders() async throws {
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        for (folder, name, text) in [
            ("2026/09/01", "parent", #"{"type":"session_meta","payload":{"id":"parent","cwd":"/parent"}}"#),
            ("2026/09/28", "child", #"{"type":"session_meta","payload":{"id":"child","cwd":"/child","parent_thread_id":"parent"}}"# + "\n" + #"{"type":"response_item","timestamp":"2026-09-28T07:00:00Z","payload":{"role":"assistant"}}"#)
        ] {
            let directory = root.appending(path: "codex/sessions/" + folder)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let file = directory.appending(path: "rollout-" + name + ".jsonl")
            try text.write(to: file, atomically: true, encoding: .utf8)
            if name == "parent" {
                try FileManager.default.setAttributes([.modificationDate: date("2026-09-01T00:00:00Z")], ofItemAtPath: file.path)
            }
        }
        let values = try await AgentActivityLoader().load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        #expect(values[0].runs.first?.repository.key == "/parent")
        #expect(values[0].attention.isEmpty)
    }

    @Test func growingFilesResumeAtTheLastCompleteLine() async throws {
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appending(path: "codex/sessions/2026/09/11")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appending(path: "rollout-growing.jsonl")
        let meta = #"{"type":"session_meta","payload":{"id":"s","cwd":"/repo"}}"# + "\n"
        func touch(_ minute: String) -> String {
            #"{"type":"event_msg","timestamp":"2026-09-28T07:"# + minute + #":00Z","payload":{"type":"task_started"}}"#
        }
        try (meta + touch("00") + "\n").write(to: file, atomically: true, encoding: .utf8)
        func append(_ text: String) throws {
            let handle = try FileHandle(forWritingTo: file)
            defer { try? handle.close() }
            try handle.seekToEnd(); try handle.write(contentsOf: Data(text.utf8))
        }
        let loader = AgentActivityLoader()
        let first = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        #expect(first[0].attentionTotal == 900)
        let bytes = await loader.parsedBytes
        _ = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        #expect(await loader.parsedBytes == bytes)
        // A valid record without a newline is visible but not part of the resumable parser prefix.
        try append(touch("10"))
        let tail = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        #expect(tail[0].attentionTotal == 1500)
        #expect(await loader.parsedBytes - bytes == UInt64(touch("10").utf8.count))
        try append("\n" + String(touch("20").prefix(40)))
        let partial = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        #expect(partial[0].sessions[0].events.count == 2)
        try append(String(touch("20").dropFirst(40)) + "\n")
        let complete = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        #expect(complete[0].sessions[0].events.count == 3)
        #expect(complete[0].attentionTotal == 2100)
    }

    @Test func shrinkingReplacingAndRewritingFilesInvalidateParserState() async throws {
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appending(path: "codex/sessions/2026/09/28")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appending(path: "rollout-replaced.jsonl")
        func content(_ id: String, padding: Int) -> String {
            "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"cwd\":\"/repo\"}}\n"
                + #"{"type":"event_msg","timestamp":"2026-09-28T07:00:00Z","payload":{"type":"task_started"}}"# + "\n"
                + "{\"type\":\"unknown\",\"padding\":\"" + String(repeating: "x", count: padding) + "\"}\n"
        }
        try content("original", padding: 6000).write(to: file, atomically: true, encoding: .utf8)
        let loader = AgentActivityLoader()
        _ = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        for (id, padding, atomic) in [("shrunk", 0, false), ("rewritten", 7000, false), ("replaced", 9000, true)] {
            if atomic { try content(id, padding: padding).write(to: file, atomically: true, encoding: .utf8) }
            else {
                let handle = try FileHandle(forWritingTo: file)
                try handle.truncate(atOffset: 0)
                try handle.write(contentsOf: Data(content(id, padding: padding).utf8)); try handle.close()
            }
            let value = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
            #expect(value[0].sessions.map(\.id) == ["codex:" + id])
            #expect(value[0].attentionTotal == 900)
        }
    }

    @Test func chunkBoundariesDoNotSplitLargeRecordsOrUTF8() async throws {
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appending(path: "claude/projects/repo")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let body = String(repeating: "😀 synthetic output ", count: 80000)
        let text = "{\"type\":\"assistant\",\"sessionId\":\"s\",\"cwd\":\"/repo\",\"timestamp\":\"2026-09-28T07:00:00Z\",\"message\":{\"content\":\"" + body + "\"}}\n"
            + #"{"type":"assistant","sessionId":"s","cwd":"/repo","timestamp":"2026-09-28T07:02:00Z"}"#
        try text.write(to: folder.appending(path: "s.jsonl"), atomically: true, encoding: .utf8)
        let result = try await AgentActivityLoader().load(days: [day], time: time, sources: [.claude], fixtureRoot: root)
        #expect(result[0].sessions.first?.events.count == 2)
        #expect(result[0].runs.first?.range.duration == 120)
    }

    @Test func cachedDaysNoticeNewFoldersTitleChangesAndRepositoryChanges() async throws {
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let repo = root.appending(path: "repo"), git = repo.appending(path: ".git")
        try FileManager.default.createDirectory(at: git, withIntermediateDirectories: true)
        let config = git.appending(path: "config")
        try "[remote \"origin\"]\n url = original\n".write(to: config, atomically: true, encoding: .utf8)
        let folder = root.appending(path: "codex/sessions/2026/09/11")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let meta = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"s\",\"cwd\":\"" + repo.path + "\"}}\n"
        let prompt = #"{"type":"event_msg","timestamp":"2026-09-28T07:00:00Z","payload":{"type":"task_started"}}"# + "\n"
        try (meta + prompt).write(to: folder.appending(path: "rollout-s.jsonl"), atomically: true, encoding: .utf8)
        let index = root.appending(path: "codex/session_index.jsonl")
        try #"{"id":"s","thread_name":"First title"}"#.write(to: index, atomically: true, encoding: .utf8)
        let loader = AgentActivityLoader()
        let first = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        #expect(first[0].sessions.first?.title == "First title")
        #expect(first[0].attention.first?.repository.origin == "original")
        let bytes = await loader.parsedBytes
        try #"{"id":"s","thread_name":"Renamed title"}"#.write(to: index, atomically: true, encoding: .utf8)
        let renamed = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        #expect(renamed[0].sessions.first?.title == "Renamed title")
        #expect(await loader.parsedBytes == bytes)
        try "[remote \"origin\"]\n url = updated\n".write(to: config, atomically: true, encoding: .utf8)
        let updated = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        #expect(updated[0].attention.first?.repository.origin == "updated")
        let newFolder = root.appending(path: "codex/sessions/2026/09/14")
        try FileManager.default.createDirectory(at: newFolder, withIntermediateDirectories: true)
        let other = #"{"type":"session_meta","payload":{"id":"new","cwd":"/new"}}"# + "\n"
            + #"{"type":"event_msg","timestamp":"2026-09-28T09:00:00Z","payload":{"type":"task_started"}}"# + "\n"
        try other.write(to: newFolder.appending(path: "rollout-new.jsonl"), atomically: true, encoding: .utf8)
        let added = try await loader.load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        #expect(added[0].attentionTotal == 1800)
        #expect(added[0].sessions.map(\.id) == ["codex:new", "codex:s"])
    }

    @Test func loaderIncludesAResumedTopLevelSessionFromAnOldFolder() async throws {
        let root = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appending(path: "codex/sessions/2026/09/11")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let text = #"{"type":"session_meta","payload":{"id":"resumed","cwd":"/repo"}}"# + "\n"
            + #"{"type":"event_msg","timestamp":"2026-09-28T07:00:00Z","payload":{"type":"task_started"}}"# + "\n"
            + #"{"type":"response_item","timestamp":"2026-09-28T07:02:00Z","payload":{"role":"assistant"}}"# + "\n"
        try text.write(to: folder.appending(path: "rollout-resumed.jsonl"), atomically: true, encoding: .utf8)
        let result = try await AgentActivityLoader().load(days: [day], time: time, sources: [.codex], fixtureRoot: root)
        #expect(result[0].attentionTotal == 15 * 60)
        #expect(result[0].runs.first?.range.duration == 120)
    }

    /// Opt-in local measurement. Prints only totals and timing, never session metadata.
    @Test func localMeasurements() async throws {
        guard ProcessInfo.processInfo.environment["SPINDLE_MEASURE_ACTIVITY"] == "1" else { return }
        let environment = ProcessInfo.processInfo.environment
        let root = environment["SPINDLE_MEASURE_ROOT"].map { URL(fileURLWithPath: $0) }
        let loader = AgentActivityLoader()
        let days = CalendarLayout.week(containing: day, in: time)
        for label in ["first", "cached", "appended"] {
            if label == "appended", let path = environment["SPINDLE_MEASURE_APPEND_FILE"] {
                // Only a disposable clone may be supplied. Never append to a person's live logs.
                precondition(path.hasPrefix(FileManager.default.temporaryDirectory.path) || path.hasPrefix("/tmp/"))
                let file = try FileHandle(forWritingTo: URL(fileURLWithPath: path))
                try file.seekToEnd()
                for _ in 0..<5 {
                    try file.write(contentsOf: Data((#"{"type":"response_item","timestamp":"2026-10-05T12:00:00Z","payload":{"role":"assistant","content":[]}}"# + "\n").utf8))
                }
                try file.close()
            }
            let start = Date()
            let values = try await loader.load(days: days, time: time, sources: Set(AgentSource.allCases), fixtureRoot: root)
            print("Activity \(label) week load: \(Date().timeIntervalSince(start)) seconds")
            for (value, expected) in zip(values.prefix(2), [36516.633, 40897.480]) {
                print("Activity \(value.day): \(String(format: "%.3f", value.attentionTotal))")
                #expect(abs(value.attentionTotal - expected) < 0.1)
            }
            if let ids = environment["SPINDLE_MEASURE_RESUMED_IDS"] {
                let resumed = Set(ids.split(separator: ",").map(String.init))
                let start = time.start(of: days[0]), end = time.start(of: time.adding(1, to: days.last!))
                let count = values[0].sessions.filter { resumed.contains($0.id) }.flatMap(\.events)
                    .filter { $0.isTouch && $0.date >= start && $0.date < end }.count
                print("Resumed top-level prompts in selected week: \(count)")
                #expect(count >= 1)
            }
        }
    }
}
