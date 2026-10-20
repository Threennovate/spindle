import CoreServices
import Foundation
import Observation
import SpindleKit

/// Notices when agents write their logs, so activity is read again soon after, instead of every few minutes.
/// Agents write many times a second while they work, so changes are reported at most every `interval`.
@Observable
final class ActivityWatcher {
    /// Moves on with each reported change; views read activity again when it does.
    private(set) var revision = 0
    /// Whether macOS is reporting changes. Without it, activity is read again on a timer instead.
    private(set) var isWatching = false

    static let interval: TimeInterval = 15

    @ObservationIgnored private var stream: FSEventStreamRef?
    @ObservationIgnored private var watched: [String] = []
    @ObservationIgnored private var pending = false
    @ObservationIgnored private var lastReport = Date.distantPast

    isolated deinit { stop() }

    /// Watches the logs of these tools, or nothing when activity is off. Calling it again with the same tools
    /// keeps the current watch.
    func watch(_ sources: Set<AgentSource>, home: URL = URL(fileURLWithPath: NSHomeDirectory())) {
        // The folders each tool writes to, such as Claude Code's projects or Codex's sessions and their index.
        let folders = sources.sorted { $0.rawValue < $1.rawValue }.map { $0.format.root(home: home).path }.filter { FileManager.default.fileExists(atPath: $0) }
        guard folders != watched else { return }
        stop()
        watched = folders
        guard !folders.isEmpty else { return }
        var context = FSEventStreamContext(version: 0, info: Unmanaged.passUnretained(self).toOpaque(),
                                           retain: nil, release: nil, copyDescription: nil)
        let flags = FSEventStreamCreateFlags(kFSEventStreamCreateFlagFileEvents | kFSEventStreamCreateFlagUseCFTypes
                                             | kFSEventStreamCreateFlagNoDefer)
        guard let stream = FSEventStreamCreate(nil, { _, info, count, paths, flags, _ in
            guard let info else { return }
            let watcher = Unmanaged<ActivityWatcher>.fromOpaque(info).takeUnretainedValue()
            let paths = unsafeBitCast(paths, to: NSArray.self) as? [String] ?? []
            let rescan = (0..<count).contains { flags[$0] & FSEventStreamEventFlags(kFSEventStreamEventFlagMustScanSubDirs) != 0 }
            MainActor.assumeIsolated { watcher.noticed(paths, rescan: rescan) }
        }, &context, folders as CFArray, FSEventStreamEventId(kFSEventStreamEventIdSinceNow), 1.0, flags) else { return }
        FSEventStreamSetDispatchQueue(stream, .main)
        guard FSEventStreamStart(stream) else {
            FSEventStreamInvalidate(stream)
            FSEventStreamRelease(stream)
            return
        }
        self.stream = stream
        isWatching = true
    }

    func stop() {
        if let stream {
            FSEventStreamStop(stream)
            FSEventStreamInvalidate(stream)
            FSEventStreamRelease(stream)
        }
        stream = nil
        watched = []
        isWatching = false
    }

    /// Only session logs count; the tools also keep other files that change often.
    private func noticed(_ paths: [String], rescan: Bool) {
        let relevant = rescan || paths.contains { path in
            path.hasSuffix(".jsonl") && AgentLogFormat.all.contains { format in
                path.contains("/\(format.folder)/\(format.logs)/")
                    || format.titleIndex.map { path.hasSuffix("/\(format.folder)/\($0)") } == true
            }
        }
        guard relevant, !pending else { return }
        pending = true
        let wait = max(0, Self.interval - Date().timeIntervalSince(lastReport))
        DispatchQueue.main.asyncAfter(deadline: .now() + wait) { [weak self] in
            guard let self else { return }
            pending = false
            lastReport = Date()
            revision += 1
        }
    }
}
