import CryptoKit
import Foundation

/// A session as the Mac uploads it when the person turned on activity sync: when they sent prompts, when the agent
/// was busy, and in which repository, named without its path. Prompts and answers are never kept, let alone sent.
public struct AgentSessionUpload: Sendable, Equatable, Codable {
    public struct Repository: Sendable, Equatable, Codable {
        public let identity: String
        public let name: String
    }

    public struct Run: Sendable, Equatable, Codable {
        public let start: Int64
        public let end: Int64
    }

    public let source: AgentSource
    public let sessionID: String
    public let parentSessionID: String?
    /// Nil for a folder outside git: its prompts still end the attention before them, so they go, without a name.
    public let repository: Repository?
    public let title: String?
    public let branches: [String]
    /// When the person sent each prompt, in Unix milliseconds.
    public let touches: [Int64]
    /// When the agent was busy: events less than `ActivityTimeline.runGap` apart, in Unix milliseconds.
    public let runs: [Run]
    public let parserVersion: Int

    /// The version of the log parser; a newer one reads logs differently, so everything is uploaded again.
    public static var parserVersion: Int { ActivityLedger.version }

    /// A session in a repository the person ignores (`ignored`) says only when its prompts were sent, which still end
    /// the attention before them, as a folder outside git's do; a subagent's says nothing, so it isn't sent.
    public init(session: AgentSession, repository: ActivityRepository, titles: Bool, ignored: Bool = false) {
        source = session.source
        sessionID = session.id
        parentSessionID = ignored ? nil : session.parentID
        // Within the server's limits, which count UTF-16 units, so one odd session can't hold up the rest.
        self.repository = ignored ? nil : repository.identity.map { Repository(identity: $0, name: Self.limited(repository.name, to: 200)) }
        let title = session.title.trimmingCharacters(in: .whitespacesAndNewlines)
        self.title = titles && !ignored && !title.isEmpty ? Self.limited(title, to: 200) : nil
        branches = ignored ? [] : session.branches.filter { !$0.isEmpty && $0.utf16.count <= 255 }.sorted().prefix(50).map { $0 }
        let events = session.events.sorted { $0.date < $1.date }
        touches = ignored && session.parentID != nil ? [] : events.filter(\.isTouch).map { Self.milliseconds($0.date) }
        var runs: [Run] = []
        if let first = events.first, !ignored {
            var start = first.date, end = first.date
            for event in events.dropFirst() {
                if event.date.timeIntervalSince(end) > ActivityTimeline.runGap {
                    runs.append(Run(start: Self.milliseconds(start), end: Self.milliseconds(end)))
                    start = event.date
                }
                end = event.date
            }
            runs.append(Run(start: Self.milliseconds(start), end: Self.milliseconds(end)))
        }
        self.runs = runs
        parserVersion = Self.parserVersion
    }

    /// What was sent, to tell whether the session changed since.
    var fingerprint: String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = .sortedKeys
        let data = (try? encoder.encode(self)) ?? Data()
        return SHA256.hash(data: data).prefix(12).map { String(format: "%02x", $0) }.joined()
    }

    static func limited(_ text: String, to units: Int) -> String {
        var text = text
        while text.utf16.count > units { text.removeLast() }
        return text
    }

    static func milliseconds(_ date: Date) -> Int64 {
        Int64((date.timeIntervalSince1970 * 1000).rounded())
    }
}

/// The server answered 400 to an upload: its sessions are invalid, however often they are sent.
public struct ActivityUploadRejected: Error {
    public init() {}
}

/// Uploads agent activity that changed since the last upload, for the person's own account, while they let it.
/// Remembers what it sent per session in a small file, so only changed sessions go; after sync was turned on again,
/// titles were allowed, or the parser changed, everything goes again.
public actor ActivityUploader {
    /// What was sent, and under which settings.
    struct State: Codable, Equatable {
        var syncedSince: Date?
        var titles = false
        var parserVersion = AgentSessionUpload.parserVersion
        /// The sources read; one turned on since is read from the start of the history again. Nil in state written
        /// before there were more than Claude Code and Codex.
        var sources: Set<AgentSource>?
        /// When the last full look at the logs began; the next one reads only logs changed since.
        var scannedAt: Date?
        /// The hashes of the repositories ignored then. Nil in state written before there was an ignore list.
        var ignored: Set<String>?
        var sent: [String: String] = [:]
    }

    /// How far back activity goes when sync is turned on.
    public static let history: TimeInterval = 90 * 24 * 60 * 60
    /// The most sessions in one request.
    public static let batchSize = 100

    private let file: URL?
    private var state: State

    public init(file: URL?) {
        self.file = file
        state = file.flatMap { try? Data(contentsOf: $0) }.flatMap { try? JSONDecoder().decode(State.self, from: $0) } ?? State()
    }

    /// The usual place, beside the activity ledger.
    public static var defaultFile: URL? {
        ActivityLedger.defaultDirectory?.deletingLastPathComponent().appending(path: "AgentActivityUploads.json")
    }

    /// Uploads the sessions that changed, those in repositories `ignoring` names as their prompts' times alone.
    /// `sessions` reads the logs changed since a date; `send` uploads a batch and
    /// throws `ActivityUploadRejected` when the server refuses it as invalid. Then the batch's sessions go one at a
    /// time: a session still refused counts as handled until it changes, so one bad session never holds up the ones
    /// after it. Returns how many sessions went and how many were refused.
    @discardableResult
    public func upload(
        syncedSince: Date,
        titles: Bool,
        sources: Set<AgentSource> = Set(AgentSource.allCases),
        ignoring: IgnoredRepositories = .none,
        now: Date = Date(),
        sessions: @Sendable (Date) async throws -> [(session: AgentSession, repository: ActivityRepository)],
        send: @Sendable ([AgentSessionUpload]) async throws -> Void
    ) async throws -> (sent: Int, rejected: Int) {
        if state.syncedSince != syncedSince || state.titles != titles || state.parserVersion != AgentSessionUpload.parserVersion {
            state = State(syncedSince: syncedSince, titles: titles)
        }
        // A source turned on (or added by an update) has sessions older than the last look; the ones already sent are
        // skipped below.
        if !sources.isSubset(of: state.sources ?? [.claude, .codex]) { state.scannedAt = nil }
        state.sources = sources
        // A repository ignored or no longer ignored since changes sessions older than the last look.
        if (state.ignored ?? []) != ignoring.hashes { state.scannedAt = nil }
        state.ignored = ignoring.hashes
        // A minute's overlap, for logs written while the last look was under way.
        let since = state.scannedAt.map { $0.addingTimeInterval(-60) } ?? now.addingTimeInterval(-Self.history)
        var ignored: [String: Bool] = [:]
        let read = try await sessions(since).map { item in
            let key = item.repository.key
            let isIgnored = ignored[key] ?? ignoring.contains(item.repository)
            ignored[key] = isIgnored
            return AgentSessionUpload(session: item.session, repository: item.repository, titles: titles, ignored: isIgnored)
        }
        // A session with nothing to say isn't sent. One that was (before its repository was ignored, after which the
        // server deleted it) has to be sent again once it says something.
        for session in read where session.touches.isEmpty && session.runs.isEmpty { state.sent[session.sessionID] = nil }
        let changed = read
            .filter { !$0.touches.isEmpty || !$0.runs.isEmpty }
            .filter { state.sent[$0.sessionID] != $0.fingerprint }
            .sorted { ($0.runs.first?.start ?? $0.touches.first ?? 0) < ($1.runs.first?.start ?? $1.touches.first ?? 0) }
        var sent = 0, rejected = 0
        for start in stride(from: 0, to: changed.count, by: Self.batchSize) {
            let batch = Array(changed[start..<min(start + Self.batchSize, changed.count)])
            do {
                try await send(batch)
                for session in batch { state.sent[session.sessionID] = session.fingerprint }
                sent += batch.count
                save()
                continue
            } catch is ActivityUploadRejected {}
            for session in batch {
                do {
                    // A batch of one was just refused; asking again would say the same.
                    if batch.count == 1 { throw ActivityUploadRejected() }
                    try await send([session])
                    sent += 1
                } catch is ActivityUploadRejected {
                    rejected += 1
                }
                state.sent[session.sessionID] = session.fingerprint
                save()
            }
        }
        state.scannedAt = now
        save()
        return (sent, rejected)
    }

    /// Forgets what was sent, e.g. once sync is turned off and the server deleted it.
    public func reset() {
        state = State()
        if let file { try? FileManager.default.removeItem(at: file) }
    }

    private func save() {
        guard let file, let data = try? JSONEncoder().encode(state) else { return }
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: file, options: .atomic)
    }
}
