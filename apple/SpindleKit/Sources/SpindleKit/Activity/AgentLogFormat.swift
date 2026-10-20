import Foundation

/// Where a source keeps its session logs and which of its files they are. The loader, the watcher, settings and the
/// calendar read sources only through this, so adding one is a case here and its lines in `AgentLogParser`. Windows'
/// `AgentLogFormat.cs` has the same fields, and both run the discovery cases in `tests/vectors/agent-logs.json`.
public struct AgentLogFormat: Sendable {
    /// Which logs make one session, which the server replaces whole when it is uploaded.
    public enum Grouping: Sendable {
        /// Each log is a session.
        case log
        /// `project/session.jsonl` and everything in `project/session/` (Claude Code's subagents) are one session.
        case projectSession
    }

    public let source: AgentSource
    /// How the source is named in uploads and records: `claude`.
    public var id: String { source.rawValue }
    public let label: String
    /// Short enough for a calendar lane's heading.
    public let shortLabel: String
    /// The tool's own folder in the home folder, which is watched for changes: `.claude`.
    public let folder: String
    /// Its folder in a snapshot's or test's synthetic logs, which are kept without dots: `claude`.
    public let fixtureFolder: String
    /// Where its session logs are, below `folder`: `projects`.
    public let logs: String
    /// A log's name is `name` exactly, or starts with `prefix` and ends in `.jsonl`.
    public let prefix: String
    public let name: String?
    /// How many folders below `logs` a log is.
    public let levels: ClosedRange<Int>
    public let grouping: Grouping
    /// A file in `folder` whose rows name sessions (Codex's `session_index.jsonl`), read by
    /// `AgentLogParser.indexTitle`; watched and checked for changes with the logs.
    public let titleIndex: String?

    /// The tool's folder, or its folder in synthetic logs.
    public func root(home: URL, fixtureRoot: URL? = nil) -> URL {
        fixtureRoot?.appending(path: fixtureFolder) ?? home.appending(path: folder)
    }

    public func logs(home: URL, fixtureRoot: URL? = nil) -> URL { root(home: home, fixtureRoot: fixtureRoot).appending(path: logs) }

    /// As settings show it: `~/.claude/projects`.
    public var displayPath: String { "~/" + folder + "/" + logs }

    /// Whether a file, by its path below `logs` (`2026/09/28/rollout-x.jsonl`), is a session log.
    public func isLog(_ relative: Substring) -> Bool {
        let level = relative.utf8.reduce(0) { $1 == UInt8(ascii: "/") ? $0 + 1 : $0 }
        guard levels.contains(level) else { return false }
        let file = relative.lastIndex(of: "/").map { relative[relative.index(after: $0)...] } ?? relative
        if let name { return file == name }
        return file.hasPrefix(prefix) && file.hasSuffix(".jsonl")
    }

    /// The session a log belongs to, by its path below `logs`; logs with the same one are uploaded together.
    public func session(_ relative: Substring) -> String {
        guard grouping == .projectSession else { return String(relative) }
        let parts = relative.split(separator: "/", maxSplits: 2)
        guard parts.count >= 2 else { return String(relative) }
        return parts[0] + "/" + (parts[1].hasSuffix(".jsonl") ? parts[1].dropLast(".jsonl".count) : parts[1])
    }

    /// Every source, in the order lanes and settings show them.
    public static let all = AgentSource.allCases.map(\.format)
}

extension AgentSource {
    public var format: AgentLogFormat {
        switch self {
        case .claude:
            AgentLogFormat(source: self, label: "Claude Code", shortLabel: "Claude", folder: ".claude", fixtureFolder: "claude",
                           logs: "projects", prefix: "", name: nil, levels: 0...Int.max, grouping: .projectSession, titleIndex: nil)
        case .codex:
            // Date folders of `rollout-<time>-<id>.jsonl`.
            AgentLogFormat(source: self, label: "Codex", shortLabel: "Codex", folder: ".codex", fixtureFolder: "codex",
                           logs: "sessions", prefix: "rollout-", name: nil, levels: 0...Int.max, grouping: .log,
                           titleIndex: "session_index.jsonl")
        case .copilot:
            // `<session>/events.jsonl`, beside the session's checkpoints and files, which aren't read.
            AgentLogFormat(source: self, label: "GitHub Copilot CLI", shortLabel: "Copilot", folder: ".copilot", fixtureFolder: "copilot",
                           logs: "session-state", prefix: "", name: "events.jsonl", levels: 1...1, grouping: .log, titleIndex: nil)
        }
    }

    public var label: String { format.label }
    public var shortLabel: String { format.shortLabel }

    /// Every source by name, for copy: "Claude Code, Codex and GitHub Copilot CLI".
    public static var names: String {
        let labels = allCases.map(\.label)
        return labels.dropLast().joined(separator: ", ") + " and " + labels.last!
    }
}
