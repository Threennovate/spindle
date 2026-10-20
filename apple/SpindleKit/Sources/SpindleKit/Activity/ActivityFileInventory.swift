import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// Fresh stat data: cached URL resource values can otherwise hide an append or replacement.
struct ActivityFileStamp: Hashable, Sendable, Codable {
    let device: UInt64
    let inode: UInt64
    let size: Int
    let seconds: Int64
    let nanoseconds: Int64
    let changeSeconds: Int64
    let changeNanoseconds: Int64
    let mode: UInt32
    let isDirectory: Bool
    let isRegularFile: Bool

    var modified: Date { Date(timeIntervalSince1970: Double(seconds) + Double(nanoseconds) / 1_000_000_000) }

    static func read(_ path: String) -> Self? {
        var value = stat()
        guard path.withCString({ lstat($0, &value) }) == 0 else { return nil }
        #if canImport(Darwin)
        let seconds = Int64(value.st_mtimespec.tv_sec), nanos = Int64(value.st_mtimespec.tv_nsec)
        let changed = Int64(value.st_ctimespec.tv_sec), changeNanos = Int64(value.st_ctimespec.tv_nsec)
        #else
        let seconds = Int64(value.st_mtim.tv_sec), nanos = Int64(value.st_mtim.tv_nsec)
        let changed = Int64(value.st_ctim.tv_sec), changeNanos = Int64(value.st_ctim.tv_nsec)
        #endif
        return Self(device: UInt64(truncatingIfNeeded: value.st_dev), inode: UInt64(value.st_ino), size: Int(value.st_size),
                    seconds: seconds, nanoseconds: nanos, changeSeconds: changed, changeNanoseconds: changeNanos,
                    mode: UInt32(value.st_mode), isDirectory: value.st_mode & S_IFMT == S_IFDIR, isRegularFile: value.st_mode & S_IFMT == S_IFREG)
    }
}

/// Directory listings are refreshed on directory changes; file stamps are always refreshed.
/// A directory's date detects new children, never whether a file inside it was appended.
struct ActivityFileInventory {
    struct File: Equatable { let url: URL; let stamp: ActivityFileStamp }
    private struct Directory { let stamp: ActivityFileStamp; let children: [String] }
    private var directories = ActivityCache<String, Directory>(byteLimit: 2 * 1024 * 1024, countLimit: 4096)
    var cachedBytes: Int { directories.bytes }
    mutating func clear() { directories.removeAll() }

    /// The `.jsonl` files under `root`, in folders at most `levels` below it (a file directly in it is at 0).
    mutating func files(under root: URL, levels: Int = .max) -> [File] {
        var folders = [(path: root.path, level: 0)], result: [File] = [], visited: Set<String> = []
        while let (path, level) = folders.popLast() {
            guard let stamp = ActivityFileStamp.read(path), stamp.isDirectory else { continue }
            visited.insert(path)
            let children: [String]
            if let cached = directories.value(for: path), cached.stamp == stamp { children = cached.children }
            else {
                children = ((try? FileManager.default.contentsOfDirectory(atPath: path)) ?? [])
                    .filter { !$0.hasPrefix(".") }.map { path + "/" + $0 }
                directories.insert(Directory(stamp: stamp, children: children), for: path,
                                   cost: 256 + path.utf8.count * 2 + children.reduce(0) { $0 + 64 + $1.utf8.count * 2 })
            }
            for child in children {
                guard let stamp = ActivityFileStamp.read(child) else { continue }
                if stamp.isDirectory { if level < levels { folders.append((child, level + 1)) } }
                else if stamp.isRegularFile, child.hasSuffix(".jsonl") {
                    result.append(File(url: URL(fileURLWithPath: child), stamp: stamp))
                }
            }
        }
        directories.remove { ($0 == root.path || $0.hasPrefix(root.path + "/")) && !visited.contains($0) }
        return result.sorted { $0.url.path < $1.url.path }
    }
}
