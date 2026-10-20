import Foundation

/// A streaming accumulator. Only titles and metadata survive decoding; prompt bodies are discarded.
public struct AgentLogParser: Codable, Sendable {
    private let source: AgentSource
    private var sessions: [String: AgentSession] = [:]
    private var currentID: String?
    private var titleFinished: Set<String> = []
    private var customTitles: [String: String] = [:]
    /// Each session's latest prompt while it may still turn out to be only a question for Spindle's MCP tools.
    private var turns: [String: Turn] = [:]
    private var decoder = JSONDecoder()
    /// Whether lines that only add a timestamp skip full decoding; tests compare both ways.
    var readsQuickly = true
    private enum CodingKeys: String, CodingKey { case source, sessions, currentID, titleFinished, customTitles, turns }

    /// A resumed parser gets its own decoder before a file is read on another task.
    mutating func prepareForRead() { decoder = JSONDecoder() }

    public init(source: AgentSource) { self.source = source }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        source = try container.decode(AgentSource.self, forKey: .source)
        sessions = try container.decode([String: AgentSession].self, forKey: .sessions)
        currentID = try container.decodeIfPresent(String.self, forKey: .currentID)
        titleFinished = try container.decode(Set<String>.self, forKey: .titleFinished)
        customTitles = try container.decode([String: String].self, forKey: .customTitles)
        // Records from before prompts to Spindle were left out have none.
        turns = try container.decodeIfPresent([String: Turn].self, forKey: .turns) ?? [:]
    }

    // MARK: - Prompts to Spindle itself

    /// The names Spindle's MCP server is added under (Windows: `AgentLogParser.OwnServers`). A prompt whose turn only
    /// calls its tools, such as "describe today's entries", is the person asking about their time, not work in the
    /// repository it was sent from.
    static let ownServers = ["spindle"]

    /// What a tool call says about the turn it is in.
    enum ToolCall: Equatable, Sendable { case own, neutral, other }

    /// Claude Code names an MCP tool `mcp__<server>__<tool>`. Codex did too, and now writes the tool's name with a
    /// `namespace` of `mcp__<server>`; in its code mode a call is a script (`exec`) that calls tools by those names.
    /// Looking tools up first (Claude Code's `ToolSearch`) or reading the clock says nothing either way.
    static func classify(name: String?, namespace: String?, type: String? = nil, input: String? = nil) -> ToolCall {
        if type == "custom_tool_call", name == "exec", namespace == nil, let input { return classify(script: input) }
        if let namespace, ownServers.contains(where: { namespace == "mcp__" + $0 }) { return .own }
        if let name, ownServers.contains(where: { name.hasPrefix("mcp__" + $0 + "__") }) { return .own }
        if name == "ToolSearch" || type == "tool_search_call" || namespace == "clock" || name?.hasPrefix("clock__") == true {
            return .neutral
        }
        return .other
    }

    /// A code-mode script, by the tools it calls: `tools.mcp__spindle__list_entries(…)`, or a tool's full name
    /// anywhere in it. One that calls none only computes.
    private static func classify(script: String) -> ToolCall {
        var names = script.matches(of: /tools\s*(?:\.\s*|\[\s*["'`])([A-Za-z0-9_-]+)/).map { String($0.1) }
        names += script.matches(of: /mcp__[A-Za-z0-9_-]+?__[A-Za-z0-9_-]+/).map { String($0.0) }
        let calls = names.map { classify(name: $0, namespace: nil) }
        if calls.contains(.other) { return .other }
        return calls.contains(.own) ? .own : .neutral
    }

    /// A prompt and what its turn has called so far. It stops counting as a touch while Spindle's tools are all
    /// it called, and counts again once anything else is called.
    private struct Turn: Codable, Sendable {
        var touch: Int
        var callsOnlyOwn = false
        /// The session's title, when this prompt gave it.
        var title: String?
    }

    private mutating func began(turnAt index: Int, in key: String, title: String?) {
        turns[key] = Turn(touch: index, title: title)
    }

    private mutating func noted(_ call: ToolCall, in key: String) {
        guard call != .neutral, var turn = turns[key], let events = sessions[key]?.events, turn.touch < events.count else { return }
        // Once something else was called the prompt is work, whatever follows.
        if call == .other { turns.removeValue(forKey: key) } else { turn.callsOnlyOwn = true; turns[key] = turn }
        let isTouch = call == .other
        guard events[turn.touch].isTouch != isTouch else { return }
        sessions[key]?.events[turn.touch] = AgentEvent(date: events[turn.touch].date, isTouch: isTouch)
        if let title = turn.title { sessions[key]?.title = isTouch ? title : "" }
    }

    /// Whether a line's tool calls could change how its turn's prompt counts, so it must be decoded.
    private func callsMatter(in key: String) -> Bool { turns[key] != nil }

    public mutating func consume(_ line: Data) {
        if source == .copilot { return consumeCopilot(line) }
        // Most lines are tool output and model turns that only add a timestamp; read those without decoding them.
        if readsQuickly, line.withUnsafeBytes({ consumeQuickly($0) }) { return }
        guard let record = try? decoder.decode(Record.self, from: line) else { return }
        if source == .claude {
            guard let id = record.sessionId else { return }
            let key = "claude:" + id
            if record.type == "custom-title", let title = record.customTitle {
                customTitles[key] = String(title.prefix(80))
                return
            }
            guard record.type == "user" || record.type == "assistant",
                  let date = Self.date(record.timestamp), let cwd = record.cwd else { return }
            let touch = record.type == "user" && record.isSidechain != true && record.isMeta != true
                && record.promptSource != "system" && record.message?.content?.isPrompt == true
            ensureClaude(key: key, cwd: cwd)
            updateClaude(key: key, cwd: cwd, branch: record.gitBranch, sidechain: record.isSidechain == true)
            var title: String?
            if touch && sessions[key]?.title.isEmpty == true {
                title = record.message?.content?.title ?? ""
                sessions[key]?.title = title!
            }
            if touch { began(turnAt: sessions[key]?.events.count ?? 0, in: key, title: title) }
            sessions[key]?.events.append(AgentEvent(date: date, isTouch: touch))
            // A subagent's own calls belong to the call that started it.
            if record.type == "assistant", record.isSidechain != true {
                for call in record.message?.content?.calls ?? [] { noted(call, in: key) }
            }
        } else {
            if record.type == "session_meta", let payload = record.payload, let id = payload.id, let cwd = payload.cwd {
                let key = "codex:" + id
                currentID = key
                let parent = payload.parent_thread_id ?? payload.source?.subagent?.thread_spawn?.parent_thread_id
                if sessions[key] == nil {
                    sessions[key] = AgentSession(id: key, source: source, parentID: parent.map { "codex:" + $0 }, cwd: cwd,
                                                 title: "", branches: Set([payload.git?.branch].compactMap { $0 }), events: [])
                }
                return
            }
            guard let key = currentID, sessions[key] != nil else { return }
            // event_msg.user_message is the actual prompt; response_item user messages may be injected instructions.
            if sessions[key]?.title.isEmpty == true, record.type == "event_msg", record.payload?.type == "user_message",
               let prompt = try? decoder.decode(PromptRecord.self, from: line) {
                sessions[key]?.title = String((prompt.payload.message.split(separator: "\n").first ?? "").prefix(80))
            }
            if !titleFinished.contains(key), record.type == "response_item" {
                if record.payload?.role == "user" {
                    // The initial turn can contain several user messages, including environment instructions.
                    // Keep the last before the first assistant response, which is the submitted request.
                    if let title = record.payload?.content?.title { sessions[key]?.title = title }
                } else if record.payload?.role == "assistant" { titleFinished.insert(key) }
            }
            let touch = record.type == "event_msg" && record.payload?.type == "task_started" && sessions[key]?.parentID == nil
            if (touch || record.type == "response_item"), let date = Self.date(record.timestamp) {
                if touch { began(turnAt: sessions[key]?.events.count ?? 0, in: key, title: nil) }
                sessions[key]?.events.append(AgentEvent(date: date, isTouch: touch))
            }
            if record.type == "response_item", let type = record.payload?.type, type.hasSuffix("_call") {
                let payload = record.payload
                noted(Self.classify(name: payload?.name?.value, namespace: payload?.namespace?.value, type: type, input: payload?.input?.value), in: key)
            }
        }
    }

    /// A row of a source's title index (`AgentLogFormat.titleIndex`): the session it names and its title.
    static func indexTitle(_ line: Data, source: AgentSource) -> (id: String, title: String)? {
        switch source {
        case .codex:
            guard let row = try? JSONDecoder().decode(CodexIndexRow.self, from: line) else { return nil }
            return ("codex:" + row.id, String(row.thread_name.prefix(80)))
        case .claude, .copilot: return nil
        }
    }
    private struct CodexIndexRow: Decodable { let id: String; let thread_name: String }

    // MARK: - GitHub Copilot CLI

    /// One event of `session-state/<session>/events.jsonl` (github/copilot-sdk, `session-events.ts`). The session is
    /// named by its first event, `session.start`; later events carry no session ID.
    private mutating func consumeCopilot(_ line: Data) {
        guard let event = try? decoder.decode(CopilotEvent.self, from: line), let type = event.type else { return }
        let data = event.data
        if type == "session.start", let id = data?.sessionId?.value {
            let key = "copilot:" + id
            currentID = key
            if sessions[key] == nil {
                sessions[key] = AgentSession(id: key, source: source, cwd: data?.context?.cwd ?? "", title: "", branches: [], events: [])
            }
        }
        guard let key = currentID, sessions[key] != nil else { return }
        // Where it works: at the start, on resuming, and when the folder or branch changes.
        if let context = type == "session.context_changed" ? data?.directory : data?.context,
           type == "session.start" || type == "session.resume" || type == "session.context_changed" {
            if let cwd = context.cwd { sessions[key]?.cwd = cwd }
            if let branch = context.branch { sessions[key]?.branches.insert(branch) }
        }
        guard type == "user.message" || type.hasPrefix("assistant.") || type.hasPrefix("tool.") || type.hasPrefix("subagent."),
              let date = Self.date(event.timestamp) else { return }
        // A subagent's events are the agent's work, never a prompt; autopilot's continuations, skills' hidden text and
        // other agents' messages (`source`) aren't the person's, and neither is a slash command.
        let content = data?.content?.value ?? ""
        let touch = type == "user.message" && event.agentId == nil && data?.source == nil
            && data?.isAutopilotContinuation != true && !content.isEmpty && !Self.isCommand(content)
        var title: String?
        if touch && sessions[key]?.title.isEmpty == true {
            title = String((content.split(separator: "\n").first ?? "").prefix(80))
            sessions[key]?.title = title!
        }
        if touch { began(turnAt: sessions[key]?.events.count ?? 0, in: key, title: title) }
        sessions[key]?.events.append(AgentEvent(date: date, isTouch: touch))
        if type == "tool.execution_start", event.agentId == nil { noted(Self.classify(copilot: data), in: key) }
    }

    /// A slash command such as `/model` or `/mcp add`: a word after one slash. A prompt that starts with a path
    /// (`/Users/me/app fails`) is a prompt.
    static func isCommand(_ text: String) -> Bool {
        guard text.hasPrefix("/") else { return false }
        let word = text.dropFirst().prefix { !$0.isWhitespace }
        return !word.isEmpty && !word.contains("/")
    }

    /// Copilot names the MCP server of a call. It reports what it is about to do (`report_intent`) alongside any
    /// call, which says nothing either way.
    private static func classify(copilot data: CopilotData?) -> ToolCall {
        if let server = data?.mcpServerName?.value { return ownServers.contains(server) ? .own : .other }
        if data?.toolName?.value == "report_intent" { return .neutral }
        return classify(name: data?.toolName?.value, namespace: nil)
    }

    private struct CopilotEvent: Decodable {
        var type: String?
        var timestamp: String?
        var agentId: String?
        var data: CopilotData?
    }
    private struct CopilotData: Decodable {
        var sessionId: Loose?
        var context: CopilotContext?
        /// `session.context_changed` is the context itself.
        var directory: CopilotContext?
        var content: Loose?
        var source: Loose?
        var isAutopilotContinuation: Bool?
        var toolName: Loose?
        var mcpServerName: Loose?

        private enum CodingKeys: String, CodingKey {
            case sessionId, context, content, source, isAutopilotContinuation, toolName, mcpServerName, cwd
        }
        init(from decoder: any Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            sessionId = try? container.decodeIfPresent(Loose.self, forKey: .sessionId)
            context = try? container.decodeIfPresent(CopilotContext.self, forKey: .context)
            directory = container.contains(.cwd) ? try? CopilotContext(from: decoder) : nil
            content = try? container.decodeIfPresent(Loose.self, forKey: .content)
            source = try? container.decodeIfPresent(Loose.self, forKey: .source)
            if source?.value == nil { source = nil }
            isAutopilotContinuation = try? container.decodeIfPresent(Bool.self, forKey: .isAutopilotContinuation)
            toolName = try? container.decodeIfPresent(Loose.self, forKey: .toolName)
            mcpServerName = try? container.decodeIfPresent(Loose.self, forKey: .mcpServerName)
            if mcpServerName?.value == nil { mcpServerName = nil }
        }
    }
    private struct CopilotContext: Decodable {
        var cwd: String?
        var branch: String?
        init(from decoder: any Decoder) throws {
            let container = try decoder.container(keyedBy: Keys.self)
            cwd = try? container.decodeIfPresent(String.self, forKey: .cwd)
            branch = try? container.decodeIfPresent(String.self, forKey: .branch)
        }
        private enum Keys: String, CodingKey { case cwd, branch }
    }

    /// Handles a line the way decoding it would, when its outcome can be read without decoding it: anything but a
    /// prompt, a title or a session's start. False leaves the line to be decoded in full.
    private mutating func consumeQuickly(_ bytes: UnsafeRawBufferPointer) -> Bool {
        let scanner = JSONScanner(bytes: bytes)
        let start = scanner.skipSpace(0)
        guard let end = scanner.skipValue(at: start), scanner.skipSpace(end) == bytes.count else { return false }
        // Only lines that `Record` decodes: each field it knows must have its type.
        var type: String?, timestamp: String?, sessionId: String?, cwd: String?, branch: String?, promptSource: String?
        var sidechain: Bool?, meta: Bool?, message: Range<Int>?, payload: Range<Int>?, decodable = true
        let wellFormed = scanner.members(of: start..<end) { key, value in
            func string(_ target: inout String?) {
                switch scanner.kind(of: value) {
                case .string: if let text = scanner.text(value) { target = text } else { decodable = false }
                case .null: break
                default: decodable = false
                }
            }
            func bool(_ target: inout Bool?) {
                switch scanner.kind(of: value) {
                case .bool(let flag): target = flag
                case .null: break
                default: decodable = false
                }
            }
            func object(_ target: inout Range<Int>?) {
                switch scanner.kind(of: value) {
                case .object: target = value
                case .null: break
                default: decodable = false
                }
            }
            guard !key.escaped else { decodable = false; return }
            let name = key.contents
            if scanner.equals(name, "type") { string(&type) }
            else if scanner.equals(name, "timestamp") { string(&timestamp) }
            else if scanner.equals(name, "sessionId") { string(&sessionId) }
            else if scanner.equals(name, "cwd") { string(&cwd) }
            else if scanner.equals(name, "gitBranch") { string(&branch) }
            else if scanner.equals(name, "promptSource") { string(&promptSource) }
            else if scanner.equals(name, "isSidechain") { bool(&sidechain) }
            else if scanner.equals(name, "isMeta") { bool(&meta) }
            else if scanner.equals(name, "message") { object(&message) }
            else if scanner.equals(name, "payload") { object(&payload) }
            else if scanner.equals(name, "customTitle") { decodable = false }
        }
        guard wellFormed, decodable, let type else { return false }

        if source == .claude {
            if type == "custom-title" { return false }
            guard type == "user" || type == "assistant" else { return true }
            guard let id = sessionId, payload == nil else { return false }
            guard let date = Self.date(timestamp), let cwd else { return true }
            if type == "user" && sidechain != true && meta != true && promptSource != "system" {
                // A prompt needs its title; only a tool result, which is not one, is settled here.
                guard let message, isToolResult(message, scanner) else { return false }
            }
            if let message, !messageDecodes(message, scanner) { return false }
            let key = "claude:" + id
            if type == "assistant", sidechain != true, callsMatter(in: key), let message, callsTools(message, scanner) { return false }
            ensureClaude(key: key, cwd: cwd)
            updateClaude(key: key, cwd: cwd, branch: branch, sidechain: sidechain == true)
            sessions[key]?.events.append(AgentEvent(date: date, isTouch: false))
            return true
        }

        if type == "session_meta" { return false }
        guard let key = currentID, sessions[key] != nil else { return true }
        if message != nil || sessionId != nil || cwd != nil { return false }
        var payloadType: String?, role: String?
        if let payload {
            var payloadDecodes = true
            let ok = scanner.members(of: payload) { key, value in
                guard !key.escaped else { payloadDecodes = false; return }
                let name = key.contents
                let kind = scanner.kind(of: value)
                if scanner.equals(name, "type") || scanner.equals(name, "role") {
                    switch kind {
                    case .string:
                        guard let text = scanner.text(value) else { payloadDecodes = false; return }
                        if scanner.equals(name, "type") { payloadType = text } else { role = text }
                    case .null: break
                    default: payloadDecodes = false
                    }
                } else if scanner.equals(name, "id") || scanner.equals(name, "cwd") || scanner.equals(name, "parent_thread_id") {
                    if case .string = kind {} else if case .null = kind {} else { payloadDecodes = false }
                } else if scanner.equals(name, "git") {
                    if case .object = kind {
                        // `Git` decodes only with a string branch, if any.
                        let ok = scanner.members(of: value) { key, branch in
                            guard !key.escaped else { payloadDecodes = false; return }
                            if scanner.equals(key.contents, "branch") {
                                if case .string = scanner.kind(of: branch) {} else if case .null = scanner.kind(of: branch) {} else { payloadDecodes = false }
                            }
                        }
                        if !ok { payloadDecodes = false }
                    } else if case .null = kind {} else { payloadDecodes = false }
                }
            }
            guard ok, payloadDecodes else { return false }
        }
        if type == "event_msg" && payloadType == "user_message" && sessions[key]?.title.isEmpty == true { return false }
        if type == "response_item" && !titleFinished.contains(key) {
            if role == "user" { return false }
            if role == "assistant" { titleFinished.insert(key) }
        }
        if type == "response_item", payloadType?.hasSuffix("_call") == true, callsMatter(in: key) { return false }
        let touch = type == "event_msg" && payloadType == "task_started" && sessions[key]?.parentID == nil
        if touch || type == "response_item", let date = Self.date(timestamp) {
            if touch { began(turnAt: sessions[key]?.events.count ?? 0, in: key, title: nil) }
            sessions[key]?.events.append(AgentEvent(date: date, isTouch: touch))
        }
        return true
    }

    /// Whether a Claude message's content has a block that calls a tool. Read loosely: a false yes only means the
    /// line is decoded in full.
    private func callsTools(_ message: Range<Int>, _ scanner: JSONScanner) -> Bool {
        var content: Range<Int>?
        _ = scanner.members(of: message) { key, value in
            if !key.escaped, scanner.equals(key.contents, "content") { content = value }
        }
        guard let content, case .array = scanner.kind(of: content) else { return false }
        var calls = false
        let ok = scanner.elements(of: content) { block in
            let sound = scanner.members(of: block) { key, value in
                if key.escaped { calls = true }
                else if scanner.equals(key.contents, "type") {
                    let type = scanner.text(value)
                    if type == nil || type == "tool_use" || type == "server_tool_use" { calls = true }
                }
            }
            if !sound { calls = true }
        }
        return calls || !ok
    }

    /// Whether a Claude message's content is blocks that include a tool result, each block decodable as one.
    private func isToolResult(_ message: Range<Int>, _ scanner: JSONScanner) -> Bool {
        var content: Range<Int>?
        guard scanner.members(of: message, { key, value in
            if !key.escaped, scanner.equals(key.contents, "content") { content = value }
        }), let content, case .array = scanner.kind(of: content) else { return false }
        var blocks = true, result = false
        guard scanner.elements(of: content, { block in
            var type: String?
            let ok = scanner.members(of: block) { key, value in
                guard !key.escaped else { blocks = false; return }
                if scanner.equals(key.contents, "type") {
                    if let text = scanner.text(value) { type = text } else { blocks = false }
                } else if scanner.equals(key.contents, "text") {
                    if case .string = scanner.kind(of: value) {} else if case .null = scanner.kind(of: value) {} else { blocks = false }
                }
            }
            if !ok || type == nil { blocks = false }
            if type == "tool_result" { result = true }
        }) else { return false }
        return blocks && result
    }

    /// Whether `Message` decodes from this object; its content never fails to, so only the object must be sound.
    private func messageDecodes(_ message: Range<Int>, _ scanner: JSONScanner) -> Bool {
        var keysPlain = true
        return scanner.members(of: message) { key, _ in if key.escaped { keysPlain = false } } && keysPlain
    }

    private mutating func ensureClaude(key: String, cwd: String) {
        if sessions[key] == nil {
            sessions[key] = AgentSession(id: key, source: source, cwd: cwd, title: "", branches: [], events: [])
        }
    }

    private mutating func updateClaude(key: String, cwd: String, branch: String?, sidechain: Bool) {
        if !sidechain { sessions[key]?.cwd = cwd }
        if let branch { sessions[key]?.branches.insert(branch) }
    }

    var activityCacheCost: Int {
        512 + sessions.values.reduce(0) { $0 + $1.activityCacheCost }
            + customTitles.reduce(0) { $0 + 128 + ($1.key.utf8.count + $1.value.utf8.count) * 2 }
            + titleFinished.reduce(0) { $0 + 64 + $1.utf8.count * 2 }
            + turns.reduce(0) { $0 + 96 + ($1.key.utf8.count + ($1.value.title?.utf8.count ?? 0)) * 2 }
    }

    public func finish() -> [AgentSession] {
        sessions.values.map { session in
            var result = session
            result.title = customTitles[session.id] ?? session.title
            return result
        }.sorted { $0.id < $1.id }
    }

    private static func date(_ value: String?) -> Date? {
        guard let value else { return nil }
        if value.contains(".") {
            return (try? Date(value, strategy: Date.ISO8601FormatStyle(includingFractionalSeconds: true)))
                ?? (try? Date(value, strategy: .iso8601))
        }
        return try? Date(value, strategy: .iso8601)
    }

    private struct Record: Decodable {
        var type: String
        var timestamp: String?
        var sessionId: String?
        var cwd: String?
        var gitBranch: String?
        var isSidechain: Bool?
        var isMeta: Bool?
        var promptSource: String?
        var customTitle: String?
        var message: Message?
        var payload: Payload?
    }
    private struct PromptRecord: Decodable { var payload: Prompt }
    private struct Prompt: Decodable { var message: String }
    private struct Message: Decodable { var content: Content? }
    private struct Payload: Decodable {
        var id: String?
        var cwd: String?
        var parent_thread_id: String?
        var type: String?
        var role: String?
        var content: Content?
        var git: Git?
        var source: Source?
        var name: Loose?
        var namespace: Loose?
        /// A code-mode script.
        var input: Loose?
    }
    /// A string that doesn't fail the record when it is something else, so lines decode as they did without it.
    private struct Loose: Decodable {
        let value: String?
        init(from decoder: any Decoder) throws { value = try? decoder.singleValueContainer().decode(String.self) }
    }
    private struct Git: Decodable { var branch: String? }
    private struct Source: Decodable {
        var subagent: Subagent?
        init(from decoder: any Decoder) throws {
            // Top-level sessions use a string such as "cli"; subagents use an object.
            let container = try decoder.singleValueContainer()
            subagent = (try? container.decode(Object.self))?.subagent
        }
        private struct Object: Decodable { var subagent: Subagent? }
    }
    private struct Subagent: Decodable { var thread_spawn: Spawn? }
    private struct Spawn: Decodable { var parent_thread_id: String? }
    private struct Block: Decodable { var type: String; var text: String?; var name: Loose? }
    private struct Content: Decodable {
        let isPrompt: Bool
        let title: String
        /// The tools an assistant message calls.
        let calls: [ToolCall]
        init(from decoder: any Decoder) throws {
            let container = try decoder.singleValueContainer()
            if let string = try? container.decode(String.self) {
                isPrompt = true
                title = String((string.split(separator: "\n").first ?? "").prefix(80))
                calls = []
            } else if let blocks = try? container.decode([Block].self) {
                isPrompt = !blocks.contains { $0.type == "tool_result" }
                title = String((blocks.compactMap(\.text).first?.split(separator: "\n").first ?? "").prefix(80))
                // A server tool, such as web search, is never Spindle's.
                calls = blocks.compactMap { block in
                    block.type == "tool_use" ? AgentLogParser.classify(name: block.name?.value, namespace: nil)
                        : block.type == "server_tool_use" ? .other : nil
                }
            } else { isPrompt = false; title = ""; calls = [] }
        }
    }
}
