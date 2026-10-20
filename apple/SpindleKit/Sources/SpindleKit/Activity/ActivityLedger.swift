import Foundation

/// What was read from each agent log, kept on disk so a launch doesn't parse every log again: the parser's state
/// (sessions, their metadata and event timestamps) and where in the file it stopped. Only what the parser keeps
/// in memory is written, never prompts or model output.
///
/// A log the agent has since deleted (Claude Code removes old sessions) keeps its record, so the time it shows
/// stays explained. Records older than `retention` go.
struct ActivityLedger {
    /// Raised whenever the parser reads logs differently, so records written by an older parser are read again.
    /// A log the agent has deleted can't be, so its older record still counts. 2: prompts that only ask Spindle's
    /// MCP tools something are not touches. A new source needs no bump: its logs have no records yet, so they are read.
    static let version = 2
    static let retention: TimeInterval = 400 * 24 * 60 * 60

    struct Record: Codable {
        var version: Int
        var path: String
        var source: AgentSource
        var stamp: ActivityFileStamp
        var parser: AgentLogParser
        var state: ActivityLineReader.State
        var prefix: UInt64
        var boundary: UInt64
    }

    /// One line of the manifest: where a log's record is, and when the log last changed.
    struct Entry: Codable {
        var file: String
        var source: AgentSource
        var modified: Date
    }

    let directory: URL
    private var manifest: [String: Entry]?
    private var manifestChanged = false
    /// When each record was last written in this process; busy logs are written at most every `writeInterval`.
    private var written: [String: Date] = [:]
    static let writeInterval: TimeInterval = 60

    init(directory: URL) { self.directory = directory }

    /// The usual place, in this app's Application Support folder.
    static var defaultDirectory: URL? {
        guard let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first else { return nil }
        return support.appending(path: Bundle.main.bundleIdentifier ?? "Spindle").appending(path: "AgentActivity")
    }

    private var manifestURL: URL { directory.appending(path: "manifest.plist") }

    private mutating func entries() -> [String: Entry] {
        if let manifest { return manifest }
        let loaded = (try? Data(contentsOf: manifestURL)).flatMap { try? PropertyListDecoder().decode(Manifest.self, from: $0) }
        // An older parser's manifest still says where its records are; each record says which parser wrote it.
        var result = loaded?.entries ?? [:]
        if loaded != nil, loaded?.version != Self.version { manifestChanged = true }
        let oldest = Date().addingTimeInterval(-Self.retention)
        for (path, entry) in result where entry.modified < oldest {
            try? FileManager.default.removeItem(at: directory.appending(path: entry.file))
            result.removeValue(forKey: path)
            manifestChanged = true
        }
        manifest = result
        return result
    }

    private struct Manifest: Codable { var version: Int; var entries: [String: Entry] }

    /// The record of a log, if one was written by this parser version, or by any for a log that is gone.
    mutating func record(for path: String, gone: Bool = false) -> Record? {
        guard let entry = entries()[path],
              let data = try? Data(contentsOf: directory.appending(path: entry.file)),
              let record = try? PropertyListDecoder().decode(Record.self, from: data),
              record.version == Self.version || gone, record.path == path else { return nil }
        return record
    }

    /// Logs with a record under `root` that are no longer on disk, changed since `since`.
    mutating func missing(under root: String, sources: Set<AgentSource>, since: Date, present: Set<String>) -> [String] {
        entries().filter { path, entry in
            path.hasPrefix(root + "/") && sources.contains(entry.source) && entry.modified >= since && !present.contains(path)
        }.map(\.key).sorted()
    }

    /// Writes a log's record, unless it was written moments ago; a later launch resumes from the last one written.
    mutating func write(_ record: Record, force: Bool = false) {
        let now = Date()
        if !force, let last = written[record.path], now.timeIntervalSince(last) < Self.writeInterval { return }
        let file = Self.fileName(for: record.path)
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let encoder = PropertyListEncoder()
            encoder.outputFormat = .binary
            try encoder.encode(record).write(to: directory.appending(path: file), options: .atomic)
        } catch { return }
        written[record.path] = now
        var current = entries()
        current[record.path] = Entry(file: file, source: record.source, modified: record.stamp.modified)
        manifest = current
        manifestChanged = true
    }

    /// Saves the manifest if records were added or removed.
    mutating func flush() {
        guard manifestChanged, let manifest else { return }
        let encoder = PropertyListEncoder()
        encoder.outputFormat = .binary
        guard let data = try? encoder.encode(Manifest(version: Self.version, entries: manifest)) else { return }
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        if (try? data.write(to: manifestURL, options: .atomic)) != nil { manifestChanged = false }
    }

    /// Deletes every record.
    mutating func removeAll() {
        try? FileManager.default.removeItem(at: directory)
        manifest = [:]
        manifestChanged = false
        written = [:]
    }

    /// A stable name from the log's path (FNV-1a), so a record is found without listing the folder.
    static func fileName(for path: String) -> String {
        let hash = path.utf8.reduce(UInt64(14695981039346656037)) { ($0 ^ UInt64($1)) &* 1099511628211 }
        return String(hash, radix: 16) + ".plist"
    }
}
