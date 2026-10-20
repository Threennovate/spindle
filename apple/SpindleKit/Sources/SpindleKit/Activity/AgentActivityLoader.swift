import Foundation
import os

/// Owns parsed files and the ledger. Interactive reads run here; upload parsing runs one file at a time on a
/// utility task, returning here to commit it. Prompt text is read on its own worker and is never kept.
public actor AgentActivityLoader {
    public static let shared = AgentActivityLoader(ledgerDirectory: ActivityLedger.defaultDirectory)
    private typealias Stamp = ActivityFileStamp
    private var inventory = ActivityFileInventory()
    private var ledger: ActivityLedger?
    private struct Fingerprint: Equatable, Sendable { let prefix: UInt64; let boundary: UInt64 }
    private struct Cached: Sendable {
        let stamp: Stamp
        let parser: AgentLogParser
        let state: ActivityLineReader.State
        let fingerprint: Fingerprint
        let sessions: [AgentSession]
        /// Whether `sessions` show the whole file. A record from the ledger may stop before an unfinished last line.
        var isComplete = true
    }
    /// Bytes passed to the line reader, used by tests to guard incremental loading.
    private(set) var parsedBytes: UInt64 = 0
    private var cache = ActivityCache<String, Cached>(byteLimit: 32 * 1024 * 1024, countLimit: 2048)
    private struct Metadata { let stamp: Stamp; let session: AgentSession }
    private var metadata = ActivityCache<String, Metadata>(byteLimit: 1024 * 1024, countLimit: 4096)
    private struct Index { let stamp: Stamp; let titles: [String: String] }
    private var indices = ActivityCache<String, Index>(byteLimit: 1024 * 1024, countLimit: 4)
    private struct Repository {
        let value: ActivityRepository
        let dependencies: [String: Stamp]
        let missing: Set<String>
    }
    private var repositories = ActivityCache<String, Repository>(byteLimit: 2 * 1024 * 1024, countLimit: 2048)
    private struct Request: Hashable {
        let days: [DayKey]
        let zone: String
        let home: String
        /// Each source's logs folder, so fixture roots don't share days.
        let folders: [String]
        let sources: Set<AgentSource>
    }
    private struct Completed {
        let files: [ActivityFileInventory.File]
        let indexes: [Stamp?]
        let dependencies: [String: Stamp]
        let missing: Set<String>
        let days: [ActivityDay]
    }
    private var completed = ActivityCache<Request, Completed>(byteLimit: 16 * 1024 * 1024, countLimit: 8)

    private var generation = 0
    private struct UploadKey: Equatable {
        let start: Date
        let sources: Set<AgentSource>
        let home: URL
        let fixtureRoot: URL?
    }
    private typealias Sessions = [(session: AgentSession, repository: ActivityRepository)]
    private var upload: (id: UUID, key: UploadKey, task: Task<Sessions, Error>)?
    /// Tests can hold one background read without holding the loader's actor.
    private let beforeBackgroundFile: @Sendable () async throws -> Void

    public init() { beforeBackgroundFile = {} }

    /// Keeps what was parsed in `ledgerDirectory` too, when there is one.
    public init(ledgerDirectory: URL?) {
        beforeBackgroundFile = {}
        ledger = ledgerDirectory.map(ActivityLedger.init(directory:))
    }

    init(ledgerDirectory: URL?, beforeBackgroundFile: @escaping @Sendable () async throws -> Void) {
        self.beforeBackgroundFile = beforeBackgroundFile
        ledger = ledgerDirectory.map(ActivityLedger.init(directory:))
    }

    init(fileCacheBytes: Int, completedCacheBytes: Int = 16 * 1024 * 1024,
         repositoryCacheBytes: Int = 2 * 1024 * 1024) {
        beforeBackgroundFile = {}
        cache = ActivityCache(byteLimit: fileCacheBytes, countLimit: 2048)
        completed = ActivityCache(byteLimit: completedCacheBytes, countLimit: 8)
        repositories = ActivityCache(byteLimit: repositoryCacheBytes, countLimit: 2048)
    }

    struct CacheUsage: Sendable { let files: Int; let requests: Int; let repositories: Int; let bytes: Int }
    var cacheUsage: CacheUsage {
        CacheUsage(files: cache.count, requests: completed.count, repositories: repositories.count,
                   bytes: cache.bytes + metadata.bytes + indices.bytes + repositories.bytes + completed.bytes + inventory.cachedBytes)
    }

    /// Releases derived activity and directory listings, without reading or changing log files.
    public func clearCache() {
        generation += 1
        upload?.task.cancel()
        upload = nil
        cache.removeAll(); metadata.removeAll(); indices.removeAll()
        repositories.removeAll(); completed.removeAll(); inventory.clear()
        ledger?.removeAll()
    }

    /// Wait measures only queueing before the interactive work starts, separately from parsing and timelines.
    public nonisolated func load(days: [DayKey], time: UserTime, sources: Set<AgentSource>,
                                 home: URL = URL(fileURLWithPath: NSHomeDirectory()), fixtureRoot: URL? = nil) async throws -> [ActivityDay] {
        let wait = Performance.signposter.beginInterval("Activity wait", id: Performance.signposter.makeSignpostID())
        return try await loadStarted(days: days, time: time, sources: sources, home: home, fixtureRoot: fixtureRoot, wait: wait)
    }

    private func loadStarted(days: [DayKey], time: UserTime, sources: Set<AgentSource>,
                     home: URL = URL(fileURLWithPath: NSHomeDirectory()),
                     fixtureRoot: URL? = nil, wait: OSSignpostIntervalState) async throws -> [ActivityDay] {
        Performance.signposter.endInterval("Activity wait", wait)
        return try await Performance.measure("Activity load") {
            let generation = self.generation
            guard let first = days.min(), !sources.isEmpty else {
                if sources.isEmpty { clearCache() }
                return []
            }
            try Task.checkCancellation()
            // Include the idle cap before midnight, even when that file has no later writes.
            let start = time.start(of: first).addingTimeInterval(-ActivityTimeline.idleCap)
            let inputs = inputs(since: start, sources: sources, home: home, fixtureRoot: fixtureRoot)
            let request = Request(days: days, zone: time.timeZone.identifier, home: home.path,
                                  folders: AgentSource.allCases.map { inputs.logs[$0]!.path }, sources: sources)
            let signature = inputs.signature, indexStamps = inputs.indexes.map(\.stamp)
            if let existing = completed.value(for: request), existing.files == signature, existing.indexes == indexStamps,
               Self.unchanged(existing.dependencies, missing: existing.missing) {
                try Task.checkCancellation()
                return existing.days
            }
            let gathered = try await Performance.measure("Activity parse") {
                try await gather(inputs, since: start, sources: sources, home: home, generation: generation)
            }
            try check(generation)
            let byID = Dictionary(gathered.sessions.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
            let repos = gathered.repos, dependencies = gathered.dependencies, missingDependencies = gathered.missing
            let result = try days.map { day in
                try Task.checkCancellation()
                let nearby = Self.sessions(byID, near: day, time: time)
                var value = ActivityTimeline.build(sessions: nearby, day: day, time: time, repository: { repos[$0]! })
                // The timeline needs carry-in and parent metadata; popovers only need contributing sessions and
                // that day's events. Don't let eight display requests retain eight copies of the entire history.
                let ids = value.attention.reduce(into: Set(value.runs.map(\.sessionID))) { $0.formUnion($1.sessionIDs) }
                let start = time.start(of: day), end = time.start(of: time.adding(1, to: day))
                value.sessions = nearby.filter { ids.contains($0.id) }.map { session in
                    var session = session
                    session.events = session.events.filter { $0.date >= start && $0.date < end }
                    return session
                }
                return value
            }
            // Own dependency stamps even when a repository entry was evicted; otherwise a cached day could
            // miss a changed origin in an evicted repository.
            let cost = (request.home.utf8.count + request.folders.reduce(0) { $0 + $1.utf8.count } + request.zone.utf8.count) * 2
                + request.days.count * 32 + signature.reduce(512) { $0 + 128 + $1.url.path.utf8.count * 2 }
                + Self.dependencyCost(dependencies, missing: missingDependencies)
                + result.reduce(0) { $0 + $1.sessions.reduce(512) { $0 + $1.activityCacheCost }
                    + $1.runs.reduce(0) { $0 + 128 + $1.sessionID.utf8.count * 2 + $1.repository.activityCacheCost }
                    + $1.attention.reduce(0) { $0 + 128 + $1.repository.activityCacheCost
                        + $1.sessionIDs.reduce(0) { $0 + 64 + $1.utf8.count * 2 } }
                    + $1.blocks.count * 32 + $1.totals.keys.reduce(0) { $0 + 128 + $1.activityCacheCost } }
            completed.insert(Completed(files: signature, indexes: indexStamps, dependencies: dependencies, missing: missingDependencies, days: result),
                             for: request, cost: cost)
            return result
        }
    }

    /// Every session with events since `start`, whole, and the repository it belongs to: a subagent's is its root
    /// session's, as in the timeline. For uploading activity, which needs sessions rather than days.
    public func sessions(since start: Date, sources: Set<AgentSource>,
                         home: URL = URL(fileURLWithPath: NSHomeDirectory()),
                         fixtureRoot: URL? = nil) async throws -> [(session: AgentSession, repository: ActivityRepository)] {
        try Task.checkCancellation()
        let key = UploadKey(start: start, sources: sources, home: home, fixtureRoot: fixtureRoot)
        let generation = self.generation
        if let upload, upload.key == key {
            let result = try await upload.task.value
            try check(generation)
            guard !upload.task.isCancelled else { throw CancellationError() }
            return result
        }
        // A newer request replaces obsolete work; equal requests join the same read.
        upload?.task.cancel()
        let id = UUID()
        let task = Task.detached(priority: .utility) {
            try await self.sessionsStarted(key, generation: generation)
        }
        upload = (id, key, task)
        defer { if upload?.id == id { upload = nil } }
        let result = try await task.value
        try check(generation)
        guard !task.isCancelled else { throw CancellationError() }
        return result
    }

    private func sessionsStarted(_ key: UploadKey, generation: Int) async throws -> Sessions {
        try await Performance.measure("Activity sessions for upload") {
            try check(generation)
            guard !key.sources.isEmpty else { return [] }
            let gathered = try await gather(inputs(since: key.start, sources: key.sources, home: key.home, fixtureRoot: key.fixtureRoot),
                                            since: key.start, sources: key.sources, home: key.home, generation: generation, background: true, wholeSessions: true)
            try check(generation)
            return try await Self.inBackground {
                let byID = Dictionary(gathered.sessions.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
                return try gathered.sessions.compactMap { session in
                    try Task.checkCancellation()
                    guard session.events.contains(where: { $0.date >= key.start }) else { return nil }
                    var root = session
                    var seen: Set<String> = [session.id]
                    while let parent = root.parentID, seen.insert(parent).inserted, let next = byID[parent] { root = next }
                    guard let repository = gathered.repos[root.cwd] else { return nil }
                    return (session, repository)
                }
            }
        }
    }

    private func check(_ generation: Int) throws {
        try Task.checkCancellation()
        guard generation == self.generation else { throw CancellationError() }
    }

    private struct Inputs {
        /// Each source's logs folder, enabled or not.
        let logs: [AgentSource: URL]
        /// The logs of each enabled source, in path order.
        let files: [AgentSource: [ActivityFileInventory.File]]
        /// The enabled sources' title indexes, whether there or not.
        let indexes: [(source: AgentSource, url: URL, stamp: Stamp?)]
        let retained: [(path: String, source: AgentSource)]
        var signature: [ActivityFileInventory.File] { AgentSource.allCases.flatMap { files[$0] ?? [] } }
    }

    private func inputs(since start: Date, sources: Set<AgentSource>, home: URL, fixtureRoot: URL?) -> Inputs {
        let logs = Dictionary(uniqueKeysWithValues: AgentSource.allCases.map { ($0, $0.format.logs(home: home, fixtureRoot: fixtureRoot)) })
        var files: [AgentSource: [ActivityFileInventory.File]] = [:]
        for source in AgentSource.allCases where sources.contains(source) {
            let format = source.format, root = logs[source]!, prefix = root.path.count + 1
            files[source] = inventory.files(under: root, levels: format.levels.upperBound)
                .filter { format.isLog($0.url.path.dropFirst(prefix)) }
        }
        let indexes = AgentSource.allCases.compactMap { source -> (AgentSource, URL, Stamp?)? in
            guard sources.contains(source), let name = source.format.titleIndex else { return nil }
            let url = source.format.root(home: home, fixtureRoot: fixtureRoot).appending(path: name)
            return (source, url, Stamp.read(url.path))
        }
        let paths = Set(files.values.joined().map { $0.url.path })
        // Logs the agents deleted since they were read still count, from their records.
        var retained: [(path: String, source: AgentSource)] = []
        if ledger != nil {
            for source in AgentSource.allCases where sources.contains(source) {
                retained += ledger!.missing(under: logs[source]!.path, sources: [source], since: start, present: paths).map { ($0, source) }
            }
        }
        let kept = paths.union(retained.map(\.path))
        cache.remove { !kept.contains($0) }
        metadata.remove { !paths.contains($0) }
        return Inputs(logs: logs, files: files, indexes: indexes, retained: retained)
    }

    private struct Gathered: Sendable {
        let sessions: [AgentSession]
        let repos: [String: ActivityRepository]
        let dependencies: [String: Stamp]
        let missing: Set<String>
    }

    /// Every session in the logs changed since `start`, whole, and the repository of each working directory.
    private func gather(_ inputs: Inputs, since start: Date, sources: Set<AgentSource>, home: URL, generation: Int, background: Bool = false,
                        wholeSessions: Bool = false) async throws -> Gathered {
        let retained = inputs.retained
        var files: [(ActivityFileInventory.File, AgentSource)] = []
        // Logs are chosen by when they were last written: Codex keeps resumed threads in their original date folders.
        for source in AgentSource.allCases where sources.contains(source) {
            let logs = inputs.files[source] ?? []
            if wholeSessions {
                // A session's logs go together (Claude Code's subagents have their own): the server replaces a session
                // whole, so one changed log must not send a part of it.
                let prefix = inputs.logs[source]!.path.count + 1, format = source.format
                func session(_ file: ActivityFileInventory.File) -> String { format.session(file.url.path.dropFirst(prefix)) }
                let changed = Set(logs.filter { $0.stamp.modified >= start }.map(session))
                files += logs.filter { changed.contains(session($0)) }.map { ($0, source) }
            } else {
                files += logs.filter { $0.stamp.modified >= start }.map { ($0, source) }
            }
        }
        var combined: [String: AgentSession] = [:]
        for (file, source) in files {
            if background { await Task.yield() }
            try check(generation)
            let url = file.url, stamp = file.stamp
            let parsed: [AgentSession]
            let previous = cache.value(for: url.path) ?? recorded(url.path)
            if let cached = previous, cached.stamp == stamp, cached.isComplete {
                parsed = cached.sessions
                remember(cached, for: url.path)
            } else {
                do {
                    let result: (cached: Cached, bytes: UInt64)
                    if background {
                        let before = beforeBackgroundFile
                        result = try await Self.inBackground {
                            try await before()
                            return try Self.readFile(url, stamp: stamp, source: source, previous: previous)
                        }
                    } else {
                        result = try Self.readFile(url, stamp: stamp, source: source, previous: previous)
                    }
                    try check(generation)
                    parsedBytes += result.bytes
                    let loaded = result.cached
                    parsed = loaded.sessions
                    // An interactive read may have read a newer append while this file was off the actor.
                    // A file deleted since the read still gets its record, unless a newer record already exists.
                    let current = Stamp.read(url.path)
                    let latest = current == nil ? (cache.value(for: url.path) ?? recorded(url.path, gone: true)) : nil
                    if current == stamp || (current == nil && (latest?.stamp == stamp || latest?.stamp == previous?.stamp)) {
                        remember(loaded, for: url.path)
                        ledger?.write(record(of: loaded, path: url.path, source: source), force: previous == nil)
                    }
                } catch is CancellationError { throw CancellationError() }
                catch { continue }
            }
            for session in parsed {
                if var existing = combined[session.id] {
                    existing.events += session.events
                    existing.branches.formUnion(session.branches)
                    if existing.title.isEmpty { existing.title = session.title }
                    combined[session.id] = existing
                } else { combined[session.id] = session }
            }
        }
        for (path, _) in retained {
            if background { await Task.yield() }
            try check(generation)
            guard let cached = cache.value(for: path) ?? recorded(path, gone: true) else { continue }
            remember(cached, for: path)
            for session in cached.sessions {
                if var existing = combined[session.id] {
                    existing.events += session.events
                    existing.branches.formUnion(session.branches)
                    if existing.title.isEmpty { existing.title = session.title }
                    combined[session.id] = existing
                } else { combined[session.id] = session }
            }
        }
        ledger?.flush()
        // A child's parent may have started before the selected folders. Read only its first metadata line;
        // do not manufacture touches or depend on the child's (possibly different) working directory.
        var missing = Set(combined.values.compactMap(\.parentID)).subtracting(combined.keys)
        if !missing.isEmpty {
            var attempted: Set<String> = []
            while let id = missing.sorted().first {
                if background { await Task.yield() }
                try check(generation)
                missing.remove(id)
                guard attempted.insert(id).inserted,
                      let source = AgentSource.allCases.first(where: { id.hasPrefix($0.rawValue + ":") }) else { continue }
                // A parent's log is named after it, as Codex names its rollouts.
                let rawID = String(id.dropFirst(source.rawValue.count + 1))
                guard let candidate = (inputs.files[source] ?? []).first(where: { $0.url.lastPathComponent.hasSuffix(rawID + ".jsonl") }) else { continue }
                let parent: AgentSession?
                if let cached = metadata.value(for: candidate.url.path), cached.stamp == candidate.stamp { parent = cached.session }
                else {
                    do {
                        var parser = AgentLogParser(source: source)
                        parser.consume(try ActivityLineReader.firstLine(candidate.url))
                        parent = parser.finish().first(where: { $0.id == id })
                        if let parent { metadata.insert(Metadata(stamp: candidate.stamp, session: parent),
                                                       for: candidate.url.path, cost: 256 + candidate.url.path.utf8.count * 2 + parent.activityCacheCost) }
                    } catch is CancellationError { throw CancellationError() }
                    catch { parent = nil }
                }
                if let parent {
                    combined[id] = parent
                    if let next = parent.parentID, combined[next] == nil, !attempted.contains(next) { missing.insert(next) }
                }
            }
        }
        // Last row wins: Codex appends renamed thread titles to its small index.
        var titles: [String: String] = [:]
        for (source, indexURL, indexStamp) in inputs.indexes {
            guard let indexStamp else { continue }
            if let cached = indices.value(for: indexURL.path), cached.stamp == indexStamp { titles.merge(cached.titles) { _, row in row } }
            else {
                do {
                    var rows: [String: String] = [:]
                    try Self.lines(indexURL) { data in
                        if let (id, title) = AgentLogParser.indexTitle(data, source: source) { rows[id] = title }
                    }
                    titles.merge(rows) { _, row in row }
                    indices.insert(Index(stamp: indexStamp, titles: rows), for: indexURL.path,
                                   cost: indexURL.path.utf8.count * 2 + rows.reduce(256) { $0 + 128 + ($1.key.utf8.count + $1.value.utf8.count) * 2 })
                } catch is CancellationError { throw CancellationError() }
                catch { }
            }
        }
        try Task.checkCancellation()
        let accumulated = combined, knownTitles = titles
        let sessions = background
            ? try await Self.inBackground { try Self.normalized(accumulated, titles: knownTitles) }
            : try Self.normalized(accumulated, titles: knownTitles)
        var repos: [String: ActivityRepository] = [:]
        var dependencies: [String: Stamp] = [:], missingDependencies: Set<String> = []
        for cwd in Set(sessions.map(\.cwd)) {
            if background { await Task.yield() }
            try check(generation)
            let key = home.path + "|" + cwd
            let cached = repositories.value(for: key)
            let resolved: Repository
            if let cached, Self.unchanged(cached.dependencies, missing: cached.missing) { resolved = cached }
            else {
                resolved = Self.resolveRepository(cwd: cwd, home: home.path)
                repositories.insert(resolved, for: key, cost: 512 + key.utf8.count * 2 + resolved.value.activityCacheCost
                    + Self.dependencyCost(resolved.dependencies, missing: resolved.missing))
            }
            repos[cwd] = resolved.value
            dependencies.merge(resolved.dependencies) { _, latest in latest }
            missingDependencies.formUnion(resolved.missing)
        }
        return Gathered(sessions: sessions, repos: repos, dependencies: dependencies, missing: missingDependencies)
    }

    /// Large event arrays are deduplicated and sorted away from the actor during an upload too.
    private nonisolated static func normalized(_ combined: [String: AgentSession], titles: [String: String]) throws -> [AgentSession] {
        return try combined.values.map { value in
            try Task.checkCancellation()
            var value = value
            value.title = titles[value.id] ?? value.title
            // Forked logs and repeated Claude records can carry identical events.
            value.events = Array(Set(value.events.map { EventKey(date: $0.date, touch: $0.isTouch) }))
                .map { AgentEvent(date: $0.date, isTouch: $0.touch) }
                .sorted { ($0.date, $0.isTouch ? 1 : 0) < ($1.date, $1.isTouch ? 1 : 0) }
            return value
        }.sorted { $0.id < $1.id }
    }

    private nonisolated static func inBackground<T: Sendable>(
        _ work: @escaping @Sendable () async throws -> T
    ) async throws -> T {
        let task = Task.detached(priority: .utility, operation: work)
        return try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
    }

    /// The sessions, and their events, that can change a day's timeline: events from the idle cap (or run gap)
    /// before it until a run gap after it, so carried-in attention and runs over midnight come out the same, and
    /// the parents that place a child session in its repository. Weeks of history then cost each day little.
    /// Events must be sorted by date.
    static func sessions(_ byID: [String: AgentSession], near day: DayKey, time: UserTime) -> [AgentSession] {
        let from = time.start(of: day).addingTimeInterval(-max(ActivityTimeline.idleCap, ActivityTimeline.runGap))
        let to = time.start(of: time.adding(1, to: day)).addingTimeInterval(ActivityTimeline.runGap)
        func window(_ session: AgentSession) -> AgentSession {
            var result = session
            let events = session.events
            let lower = events.partitioningIndex { $0.date >= from }, upper = events.partitioningIndex { $0.date > to }
            result.events = lower < upper ? Array(events[lower..<upper]) : []
            return result
        }
        var nearby: [String: AgentSession] = [:]
        for session in byID.values {
            let windowed = window(session)
            if !windowed.events.isEmpty { nearby[session.id] = windowed }
        }
        for session in Array(nearby.values) {
            var parent = session.parentID
            while let id = parent, nearby[id] == nil, let next = byID[id] {
                nearby[id] = window(next)
                parent = next.parentID
            }
        }
        return nearby.values.sorted { $0.id < $1.id }
    }

    /// Keeps a file's parse in memory, as far as the cache allows.
    private func remember(_ cached: Cached, for path: String) {
        cache.insert(cached, for: path,
                     cost: path.utf8.count * 2 + cached.parser.activityCacheCost + cached.sessions.reduce(256) { $0 + $1.activityCacheCost })
    }

    /// A file's parse as the ledger has it. It shows the whole file when the parser stopped at its end.
    private func recorded(_ path: String, gone: Bool = false) -> Cached? {
        guard let record = ledger?.record(for: path, gone: gone) else { return nil }
        return Cached(stamp: record.stamp, parser: record.parser, state: record.state,
                      fingerprint: Fingerprint(prefix: record.prefix, boundary: record.boundary),
                      sessions: record.parser.finish(), isComplete: record.state.offset == UInt64(record.stamp.size))
    }

    private func record(of cached: Cached, path: String, source: AgentSource) -> ActivityLedger.Record {
        ActivityLedger.Record(version: ActivityLedger.version, path: path, source: source, stamp: cached.stamp, parser: cached.parser,
                              state: cached.state, prefix: cached.fingerprint.prefix, boundary: cached.fingerprint.boundary)
    }

    private static func dependencyCost(_ dependencies: [String: Stamp], missing: Set<String>) -> Int {
        dependencies.keys.reduce(0) { $0 + 256 + $1.utf8.count * 2 }
            + missing.reduce(0) { $0 + 64 + $1.utf8.count * 2 }
    }

    private static func unchanged(_ dependencies: [String: Stamp], missing: Set<String>) -> Bool {
        !dependencies.contains { Stamp.read($0.key) != $0.value } && !missing.contains { Stamp.read($0) != nil }
    }

    private static func resolveRepository(cwd: String, home: String) -> Repository {
        var dependencies: [String: Stamp] = [:], missing: Set<String> = []
        func note(_ path: String) -> Stamp? {
            if let stamp = Stamp.read(path) { dependencies[path] = stamp; return stamp }
            missing.insert(path)
            return nil
        }
        let value = ActivityRepositoryResolver.resolve(cwd: cwd, home: home,
            exists: { note($0) != nil }, read: { path in
                _ = note(path)
                return try? String(contentsOfFile: path, encoding: .utf8)
            })
        return Repository(value: value, dependencies: dependencies, missing: missing)
    }

    private struct EventKey: Hashable { let date: Date; let touch: Bool }

    /// Resume after the last newline, or at EOF while discarding an oversized record. A valid unterminated
    /// tail is displayed using a copy and reread after growth, so partial records cannot be lost or doubled.
    private nonisolated static func readFile(_ url: URL, stamp: Stamp, source: AgentSource, previous: Cached?) throws -> (cached: Cached, bytes: UInt64) {
        try autoreleasepool {
            let file = try FileHandle(forReadingFrom: url)
            defer { try? file.close() }
            var parser = AgentLogParser(source: source)
            var state = ActivityLineReader.State()
            // Resume where the previous parse stopped, if the file only grew since. A ledger record can stop before an
            // unfinished last line, so the same size can also resume.
            if let previous, previous.stamp.device == stamp.device, previous.stamp.inode == stamp.inode,
               stamp.size >= previous.stamp.size, previous.state.offset <= UInt64(stamp.size),
               try Self.fingerprint(file, through: previous.state.offset) == previous.fingerprint {
                parser = previous.parser
                parser.prepareForRead()
                state = previous.state
            }
            try file.seek(toOffset: state.offset)
            let result = try ActivityLineReader.read(file, from: state, through: UInt64(stamp.size)) { parser.consume($0) }
            var displayed = parser
            if !result.tail.isEmpty { displayed.consume(result.tail) }
            return (Cached(stamp: stamp, parser: parser, state: result.state,
                           fingerprint: try Self.fingerprint(file, through: result.state.offset), sessions: displayed.finish()), result.bytes)
        }
    }

    /// Small checksums detect in-place rewrites at the prefix or resume boundary without retaining log text.
    private static func fingerprint(_ file: FileHandle, through offset: UInt64) throws -> Fingerprint {
        func hash(at position: UInt64, count: Int) throws -> UInt64 {
            try file.seek(toOffset: position)
            let data = try file.read(upToCount: count) ?? Data()
            return data.reduce(UInt64(14695981039346656037)) { ($0 ^ UInt64($1)) &* 1099511628211 }
        }
        let count = Int(min(4096, offset))
        return try Fingerprint(prefix: hash(at: 0, count: count), boundary: hash(at: offset - UInt64(count), count: count))
    }

    private static func lines(_ url: URL, consume: (Data) -> Void) throws {
        let file = try FileHandle(forReadingFrom: url)
        defer { try? file.close() }
        let result = try ActivityLineReader.read(file, from: .init(), recordLimit: ActivityLineReader.metadataLimit, consume: consume)
        if !result.tail.isEmpty { consume(result.tail) }
    }

}

private extension Array {
    /// The first index whose element satisfies `belongs`, for an array where those elements all come last.
    func partitioningIndex(where belongs: (Element) -> Bool) -> Int {
        var low = 0, high = count
        while low < high {
            let middle = (low + high) / 2
            if belongs(self[middle]) { high = middle } else { low = middle + 1 }
        }
        return low
    }
}
