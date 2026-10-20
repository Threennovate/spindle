import Foundation
import Testing
import SpindleKit
@testable import Spindle

struct LiveActivityStateTests {
    @Test func showsTheRunningEntryAndNothingOtherwise() {
        let start = Date(timeIntervalSince1970: 1_790_000_000)
        let running = TimeEntry(id: "e1", workspaceId: "w", description: "Writing", projectId: nil, taskId: nil, tagIds: [], isBillable: false, start: start, stop: nil, shares: [], version: "1")

        let state = LiveActivityController.state(for: running, catalog: nil)

        #expect(state?.entryId == "e1")
        #expect(state?.description == "Writing")
        #expect(state?.start == start)
        #expect(state?.projectName == nil)
        #expect(LiveActivityController.state(for: nil, catalog: nil) == nil)
    }
}

/// The intents behind Siri, Shortcuts and the Live Activity's stop button, on a new account on a real API. They run
/// only when SPINDLE_API_URL is set (TEST_RUNNER_SPINDLE_API_URL for xcodebuild), like the kit's live tests.
@Suite(.enabled(if: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] != nil), .serialized)
struct TimerIntentLiveTests {
    @Test func startsAndStopsTheTimerThroughTheAppsModel() async throws {
        let server = try #require(URL(string: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] ?? ""))
        let model = AppModel(tokens: MemoryTokenStore(), defaults: UserDefaults(suiteName: UUID().uuidString)!, server: server, openStore: { _, _ in try LocalStore() })
        try await model.register(fullName: "Intent Test", email: "intent-\(UUID().uuidString.prefix(8).lowercased())@example.com", password: "correct horse battery", acceptTerms: false)
        let previous = AppModelHolder.current
        AppModelHolder.current = model
        defer { AppModelHolder.current = previous }

        try await TimerIntentHandler.start(description: "Started by Siri")
        #expect(model.tracker?.running?.description == "Started by Siri")

        try await TimerIntentHandler.stop()
        #expect(model.tracker?.running == nil)
        // The server agrees.
        await model.tracker?.refreshRunning()
        #expect(model.tracker?.running == nil)
        #expect(model.tracker?.entries.contains { $0.description == "Started by Siri" && $0.stop != nil } == true)
    }

    @Test func stoppingWithNothingRunningDoesNothing() async throws {
        let server = try #require(URL(string: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] ?? ""))
        let model = AppModel(tokens: MemoryTokenStore(), defaults: UserDefaults(suiteName: UUID().uuidString)!, server: server, openStore: { _, _ in try LocalStore() })
        try await model.register(fullName: "Intent Test", email: "intent-\(UUID().uuidString.prefix(8).lowercased())@example.com", password: "correct horse battery", acceptTerms: false)
        let previous = AppModelHolder.current
        AppModelHolder.current = model
        defer { AppModelHolder.current = previous }

        try await TimerIntentHandler.stop()
        #expect(model.tracker?.running == nil)
    }
}
