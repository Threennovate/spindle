import Foundation
import Testing
import SpindleKit
@testable import Spindle

/// The quick entry panel's model: what a typed line shows before anything is saved, chips, and the split page.
struct QuickEntryTests {
    let defaults: UserDefaults

    init() {
        let suite = "com.spindletrack.tests.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
    }

    /// A signed-in account with Atlas and Beacon, a tag, and a timer running on Atlas for half an hour.
    private func signedIn(requireProject: Bool = false) async throws -> QuickEntry {
        func ago(_ minutes: Double) -> String { Date().addingTimeInterval(-minutes * 60).formatted(.iso8601) }
        let running = #"{"id":"e1","workspaceId":"w2","description":"Agents","projectId":"p1","taskId":null,"tagIds":[],"isBillable":false,"start":"\#(ago(30))","stop":null,"shares":[],"version":"1"}"#
        let finished = #"{"id":"e0","workspaceId":"w2","description":"Planning","projectId":"p2","taskId":null,"tagIds":[],"isBillable":false,"start":"\#(ago(50))","stop":"\#(ago(40))","shares":[],"version":"1"}"#
        let settings = #"{"name":"Consulting","currency":"EUR","defaultHourlyRate":null,"onlyAdminsCreateProjects":false,"onlyAdminsCreateTags":false,"lockedBefore":null,"requireDescription":false,"requireProject":\#(requireProject),"requireTask":false,"requireTag":false}"#
        let projects = [("p1", "Atlas", "#6366f1"), ("p2", "Beacon", "#10b981")].map { id, name, color in
            #"{"id":"\#(id)","name":"\#(name)","clientId":null,"color":"\#(color)","isPrivate":false,"isBillable":false,"separateBilledTime":false,"estimateSeconds":null,"archivedAt":null,"trackedSeconds":0,"tasks":[],"hourlyRate":null,"canManage":true,"billingMode":"asTracked","dailyHours":null}"#
        }.joined(separator: ",")
        let server = StubServer { path in
            switch path {
            case "/api/auth/login": return (200, tokensJSON())
            case "/api/me": return (200, meJSON())
            case "/api/me/time-entries/current": return (200, running)
            case let path where path.hasSuffix("/settings"): return (200, settings)
            case let path where path.hasSuffix("/time-entries"): return (200, "[\(running),\(finished)]")
            case let path where path.hasSuffix("/projects"): return (200, "[\(projects)]")
            case let path where path.hasSuffix("/tags"): return (200, #"[{"id":"t1","name":"review"}]"#)
            case let path where path.hasSuffix("/clients"): return (200, "[]")
            default: return (404, "{}")
            }
        }
        let model = AppModel(tokens: MemoryTokenStore(), urlSession: server.session, defaults: defaults, server: server.url,
                             openStore: { _, _ in try LocalStore() })
        await model.start()
        try await model.signIn(email: "ana@example.com", password: "correct horse")
        for _ in 0..<300 where model.tracker?.state != .loaded || model.tracker?.settings == nil || model.tracker?.catalog.projects.isEmpty == true {
            try await Task.sleep(for: .milliseconds(10))
        }
        let quick = QuickEntry(model: model, desktop: Desktop(model: model, settings: DesktopSettings(defaults: defaults, clearActivityCache: {}), active: false))
        quick.reset()
        return quick
    }

    private func type(_ text: String, into quick: QuickEntry) {
        quick.query = text
        quick.caretMoved(to: text.utf16.count)
    }

    @Test func aMentionBecomesAChipAndTheLineShowsWhatEnterSaves() async throws {
        let quick = try await signedIn()
        type("Review PR @atl", into: quick)
        #expect(quick.mention?.query == "atl")
        #expect(quick.selected?.id == "mention:project:p1")
        #expect(quick.selected?.primary.label == "Pick project")

        quick.tab()
        try await Task.sleep(for: .milliseconds(50))
        #expect(quick.chips == [.project(projectID: "p1", taskID: nil)])
        #expect(quick.text == "Review PR ")

        type("Review PR since 0:00", into: quick)
        let preview = try #require(quick.selected)
        #expect(preview.id == "sentence.entry")
        #expect(preview.title == "Start “Review PR”")
        #expect(preview.detail?.contains("Atlas") == true)
        #expect(preview.detail?.contains("since 00:00") == true)
        #expect(preview.primary.label == "Start timer")
        #expect(quick.rows.contains { $0.id == "sentence.as-written" })
    }

    @Test func aHashTypedWithoutPickingATagStaysText() async throws {
        let quick = try await signedIn()
        type("Fix #482", into: quick)
        // No tag fits, so the line comes first, as typed.
        #expect(quick.selected?.id == "sentence.entry")
        #expect(quick.selected?.title == "Start “Fix #482”")
    }

    @Test func backspaceAtTheStartTurnsTheLastChipBackIntoText() async throws {
        let quick = try await signedIn()
        quick.addChip(.tag("t1"))
        type("Review", into: quick)
        quick.caretMoved(to: 0)

        #expect(quick.deleteAtStart())
        #expect(quick.chips.isEmpty)
        #expect(quick.text == "#review Review")
        #expect(quick.mention?.query == "review")
    }

    @Test func aRequiredProjectIsAskedForBeforeSaving() async throws {
        let quick = try await signedIn(requireProject: true)
        type("Standup yesterday 9:00-9:15", into: quick)
        let preview = try #require(quick.selected)
        #expect(preview.title == "Add “Standup”")
        #expect(preview.detail?.contains("Needs a project") == true)
        #expect(preview.primary.label == "Choose a project")
        #expect(preview.tone == .blocked)
    }

    @Test func plusFifteenMovesTheRunningTimersStartEarlier() async throws {
        let quick = try await signedIn()
        type("+15", into: quick)
        let row = try #require(quick.rows.first)
        #expect(row.title == "Started 15 min earlier")
        #expect(row.detail?.contains("0:30:") == true && row.detail?.contains("→ 0:45:") == true)
    }

    @Test func theSplitPageResizesWithTheArrowsAndTakesATypedShare() async throws {
        let quick = try await signedIn()
        let running = try #require(quick.tracker?.running)
        let draft = SplitDraft(entry: running, parts: [
            SplitPart(projectId: "p1", isBillable: false, percent: 50),
            SplitPart(projectId: "p2", isBillable: false, percent: 50),
        ])
        quick.push(.split(draft))
        #expect(quick.selected?.id == "part:0")

        draft.grow(quick.splitIndex, by: SplitDraft.step, typed: quick.query)
        draft.grow(quick.splitIndex, by: SplitDraft.step, typed: quick.query)
        #expect(draft.parts.map(\.percent) == [60, 40])

        quick.query = "70"
        #expect(quick.rows.first?.meta?.hasSuffix("70%") == true)
        // The share stays with the part it was typed for when ↓ chooses the next one.
        quick.move(1)
        #expect(draft.parts.map(\.percent) == [70, 30])
        #expect(quick.query.isEmpty)
        #expect(quick.splitIndex == 1)
        draft.grow(quick.splitIndex, by: -1, typed: quick.query)
        #expect(draft.parts.map(\.percent) == [71, 29])
        #expect(quick.selected?.primary.label == "Save split")
    }

    @Test func escapeStepsBackAPageBeforeItCloses() async throws {
        let quick = try await signedIn()
        var closed = false
        quick.close = { closed = true }
        quick.push(.tags)
        quick.cancel()
        #expect(quick.page == nil)
        #expect(!closed)
        quick.cancel()
        #expect(closed)
    }
}
