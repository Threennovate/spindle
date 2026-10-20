import Foundation
import Testing
import SpindleKit
@testable import Spindle

@MainActor
struct ActivityWatcherTests {
    @Test func writingASessionLogIsNoticedAndOtherFilesAreNot() async throws {
        let home = FileManager.default.temporaryDirectory.appending(path: "watch-" + UUID().uuidString)
        let project = home.appending(path: ".claude/projects/repo")
        try FileManager.default.createDirectory(at: project, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let watcher = ActivityWatcher()
        watcher.watch([.claude], home: home)
        defer { watcher.stop() }
        #expect(watcher.isWatching)

        try "{}".write(to: home.appending(path: ".claude/settings.json"), atomically: true, encoding: .utf8)
        try await Task.sleep(for: .seconds(2.5))
        #expect(watcher.revision == 0)

        let written = Date()
        try "{}\n".write(to: project.appending(path: "session.jsonl"), atomically: false, encoding: .utf8)
        for _ in 0..<100 where watcher.revision == 0 { try await Task.sleep(for: .milliseconds(50)) }
        #expect(watcher.revision == 1)
        print("Activity watcher noticed a log write after \(Int(Date().timeIntervalSince(written) * 1000)) ms")
    }

    @Test func turningActivityOffStopsWatching() throws {
        let home = FileManager.default.temporaryDirectory.appending(path: "watch-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: home.appending(path: ".codex"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let watcher = ActivityWatcher()
        watcher.watch([.codex], home: home)
        #expect(watcher.isWatching)
        watcher.watch([], home: home)
        #expect(!watcher.isWatching)
    }
}
