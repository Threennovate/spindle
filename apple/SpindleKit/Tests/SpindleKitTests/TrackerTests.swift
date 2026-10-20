import Foundation
import HTTPTypes
import Observation
import OpenAPIRuntime
import Testing
@testable import SpindleKit

/// A pretend server that sees each request's body, and can hold an answer back until the test lets it go.
final class FakeServer: ClientTransport, @unchecked Sendable {
    struct Request {
        let operation: String
        let path: String
        let body: [String: Any]
        var ifMatch: String? = nil
    }

    typealias Handler = @Sendable (Request) async throws -> (HTTPResponse.Status, String)

    private let lock = NSLock()
    private var seen: [String] = []
    private let handler: Handler

    init(handler: @escaping Handler) {
        self.handler = handler
    }

    /// The operations called so far, in order, without signing in.
    var operations: [String] {
        lock.withLock { seen.filter { $0 != "refreshTokens" } }
    }

    func send(_ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String) async throws -> (HTTPResponse, HTTPBody?) {
        lock.withLock { seen.append(operationID) }
        var json: [String: Any] = [:]
        if let body, let data = try? await Data(collecting: body, upTo: 1024 * 1024) {
            json = (try? JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
        }
        if operationID == "refreshTokens" {
            return reply(.ok, tokens(access: "a", expiresIn: 900))
        }
        let (status, text) = try await handler(
            Request(operation: operationID, path: request.path ?? "", body: json, ifMatch: request.headerFields[.ifMatch]))
        return reply(status, text)
    }

    private func reply(_ status: HTTPResponse.Status, _ text: String) -> (HTTPResponse, HTTPBody?) {
        var response = HTTPResponse(status: status)
        response.headerFields[.contentType] = status.code >= 400 ? "application/problem+json" : "application/json"
        return (response, HTTPBody(text))
    }
}

/// An answer the test lets go of when it is ready.
actor Gate {
    private var waiting: [CheckedContinuation<Void, Never>] = []
    private var isOpen = false

    func wait() async {
        if isOpen { return }
        await withCheckedContinuation { waiting.append($0) }
    }

    func open() {
        isOpen = true
        waiting.forEach { $0.resume() }
        waiting = []
    }
}

func entryJSON(
    id: String, description: String = "", projectId: String? = nil, tagIds: [String] = [], billable: Bool = false,
    start: Date, stop: Date?, shares: [[String: Any]] = [], version: String = "1"
) -> String {
    let format = Date.ISO8601FormatStyle(includingFractionalSeconds: true)
    let object: [String: Any] = [
        "id": id, "workspaceId": "w1", "description": description, "projectId": projectId ?? NSNull(), "taskId": NSNull(),
        "tagIds": tagIds, "isBillable": billable, "start": start.formatted(format), "stop": stop.map { $0.formatted(format) } ?? NSNull(),
        "shares": shares, "version": version,
    ]
    return String(data: try! JSONSerialization.data(withJSONObject: object), encoding: .utf8)!
}

/// Echoes an entry the app sent back as the server would save it.
func savedJSON(_ request: FakeServer.Request, id: String? = nil, version: String = "2") -> String {
    let body = request.body
    let format = Date.ISO8601FormatStyle(includingFractionalSeconds: true)
    let start = (body["start"] as? String).flatMap { try? format.parse($0) } ?? Date()
    let stop = (body["stop"] as? String).flatMap { try? format.parse($0) }
    return entryJSON(
        id: id ?? body["id"] as? String ?? "?", description: body["description"] as? String ?? "",
        projectId: body["projectId"] as? String, tagIds: body["tagIds"] as? [String] ?? [],
        // Left out, it follows the project, as on the server; only p1 is billable here.
        billable: body["isBillable"] as? Bool ?? (body["projectId"] as? String == "p1"), start: start, stop: stop,
        shares: body["shares"] as? [[String: Any]] ?? [], version: version)
}

func settingsJSON(requireProject: Bool = false) -> String {
    #"{"name":"Studio","currency":"EUR","defaultHourlyRate":null,"onlyAdminsCreateProjects":false,"onlyAdminsCreateTags":false,"lockedBefore":null,"requireDescription":false,"requireProject":\#(requireProject),"requireTask":false,"requireTag":false}"#
}

let projectsJSON = ##"[{"id":"p1","name":"Website","clientId":null,"color":"#0B83D9","isPrivate":false,"isBillable":true,"separateBilledTime":false,"estimateSeconds":null,"archivedAt":null,"trackedSeconds":0,"tasks":[],"hourlyRate":null,"canManage":true,"billingMode":"asTracked","dailyHours":null}]"##

@MainActor
struct TrackerTests {
    let server = URL(string: "https://track.example.com")!
    let hourAgo = Date().addingTimeInterval(-3600).rounded()

    func tracker(_ fake: FakeServer, store: LocalStore? = nil) async throws -> Tracker {
        let tokens = MemoryTokenStore()
        tokens.write("refresh", account: server.absoluteString)
        let session = AuthSession(server: server, tokens: tokens, transport: fake)
        let api = SpindleClient(session: session, transport: fake)
        let store = try store ?? LocalStore()
        let outbox = await Outbox(api: api, store: store)
        let tracker = Tracker(
            api: api,
            outbox: outbox,
            store: store,
            workspace: WorkspaceSummary(id: "w1", organizationId: "o1", name: "Studio", role: .member),
            time: UserTime(timeZone: .gmt))
        outbox.onSent = { [weak tracker] change, saved in tracker?.didSend(change, saved) }
        return tracker
    }

    /// Answers the calls every load makes, and hands the rest to `other`.
    func fakeServer(
        running: String? = nil,
        entries: String = "[]",
        requireProject: Bool = false,
        other: @escaping FakeServer.Handler = { _ in (.notFound, "{}") }
    ) -> FakeServer {
        FakeServer { request in
            switch request.operation {
            case "getWorkspaceSettings": (.ok, settingsJSON(requireProject: requireProject))
            case "listProjects": (.ok, projectsJSON)
            case "listClients", "listTags": (.ok, "[]")
            case "getCurrentTimeEntry": running.map { (.ok, $0) } ?? (.noContent, "")
            case "listTimeEntries": (.ok, entries)
            default: try await other(request)
            }
        }
    }

    @Test func loadsTheTimerTheWeekAndWhatEntriesPointAt() async throws {
        let running = entryJSON(id: "e1", description: "Design review", start: hourAgo, stop: nil)
        let tracker = try await tracker(fakeServer(running: running, entries: "[\(running)]"))

        await tracker.load()

        #expect(tracker.state == .loaded)
        #expect(tracker.running?.id == "e1")
        #expect(tracker.entries.map(\.id) == ["e1"])
        #expect(tracker.catalog.project("p1")?.name == "Website")
        #expect(tracker.settings?.name == "Studio")
        #expect(tracker.weeksLoaded == 1)
    }

    @Test func lookupsByIDAreObserved() async throws {
        let tracker = try await tracker(fakeServer())
        let changed = Locked(false)
        withObservationTracking {
            _ = tracker.catalog.project("p1")
        } onChange: {
            changed.withLock { $0 = true }
        }

        await tracker.load()

        #expect(changed.withLock { $0 })
    }

    @Test func startingShowsTheTimerAtOnceAndStopsTheOneRunning() async throws {
        let running = entryJSON(id: "e1", start: hourAgo, stop: nil)
        let tracker = try await tracker(fakeServer(running: running, entries: "[\(running)]") { request in
            request.operation == "createTimeEntry" ? (.created, savedJSON(request)) : (.notFound, "{}")
        })
        await tracker.load()
        let now = Date().rounded()

        try await tracker.start(EntryDetails(description: " Write specs ", projectId: "p1"), at: now)

        #expect(tracker.running?.description == "Write specs")
        #expect(tracker.running?.start == now)
        // Billable, like its project, unless chosen otherwise.
        #expect(tracker.running?.isBillable == true)
        #expect(tracker.entries.first { $0.id == "e1" }?.stop == now)
        #expect(tracker.entries.count == 2)
    }

    @Test func aStartTheServerTurnsDownIsUndone() async throws {
        let running = entryJSON(id: "e1", start: hourAgo, stop: nil)
        let tracker = try await tracker(fakeServer(running: running, entries: "[\(running)]") { _ in
            (.conflict, #"{"title":"Another timer was started at the same moment.","status":409}"#)
        })
        await tracker.load()

        await #expect(throws: APIError(status: 409, title: "Another timer was started at the same moment.")) {
            try await tracker.start(EntryDetails(description: "Write specs"))
        }

        #expect(tracker.running?.id == "e1")
        #expect(tracker.running?.stop == nil)
        #expect(tracker.entries.map(\.id) == ["e1"])
    }

    @Test func stoppingAsksForTheDetailsTheWorkspaceRequires() async throws {
        let running = entryJSON(id: "e1", start: hourAgo, stop: nil)
        let fake = fakeServer(running: running, entries: "[\(running)]", requireProject: true)
        let tracker = try await tracker(fake)
        await tracker.load()

        let outcome = await tracker.stopRunning()

        guard case .needsDetails(let entry) = outcome else {
            Issue.record("Expected to be asked for details, got \(outcome)")
            return
        }
        #expect(entry.id == "e1")
        #expect(tracker.running?.id == "e1")
        #expect(!fake.operations.contains("stopTimeEntry"))
    }

    @Test func stoppingTakesTheServersWordOnRequiredDetails() async throws {
        let running = entryJSON(id: "e1", start: hourAgo, stop: nil)
        let tracker = try await tracker(fakeServer(running: running, entries: "[\(running)]") { _ in
            (.badRequest, #"{"title":"One or more validation errors occurred.","status":400,"errors":{"projectId":["A project is required in this workspace."]}}"#)
        })
        await tracker.load()

        let outcome = await tracker.stopRunning()

        guard case .needsDetails = outcome else {
            Issue.record("Expected to be asked for details, got \(outcome)")
            return
        }
        #expect(tracker.running?.id == "e1")
        #expect(tracker.entries.first?.stop == nil)
    }

    @Test func stoppingSendsTheTimeItStopped() async throws {
        let running = entryJSON(id: "e1", start: hourAgo, stop: nil)
        let sentStop = Locked<String?>(nil)
        let tracker = try await tracker(fakeServer(running: running, entries: "[\(running)]") { request in
            sentStop.withLock { $0 = request.body["stop"] as? String }
            return (.ok, entryJSON(id: "e1", start: hourAgo, stop: Date()))
        })
        await tracker.load()
        let stop = Date().rounded()

        let outcome = await tracker.stopRunning(at: stop)

        guard case .stopped = outcome else {
            Issue.record("Expected the timer to stop, got \(outcome)")
            return
        }
        #expect(tracker.running == nil)
        #expect(sentStop.withLock { $0 } == stop.formatted(Date.ISO8601FormatStyle(includingFractionalSeconds: true)))
    }

    @Test func aRefreshWhileAChangeIsOnItsWayKeepsTheChange() async throws {
        let gate = Gate()
        let tracker = try await tracker(fakeServer { request in
            await gate.wait()
            return (.created, savedJSON(request))
        })
        await tracker.load()

        let starting = Task { try await tracker.start(EntryDetails(description: "Write specs")) }
        while tracker.running == nil {
            await Task.yield()
        }
        // The server doesn't have the new timer yet.
        await tracker.refresh()
        #expect(tracker.running?.description == "Write specs")
        #expect(tracker.entries.count == 1)

        await gate.open()
        try await starting.value
        #expect(tracker.running?.description == "Write specs")
    }

    @Test func aDeleteTheServerTurnsDownIsUndone() async throws {
        let finished = entryJSON(id: "e1", description: "Design review", start: hourAgo, stop: Date().addingTimeInterval(-60))
        let tracker = try await tracker(fakeServer(entries: "[\(finished)]") { _ in
            (.badRequest, #"{"title":"One or more validation errors occurred.","status":400,"errors":{"start":["Time before 1 September 2026 is locked. Ask an admin to change it."]}}"#)
        })
        await tracker.load()
        let entry = tracker.entries[0]

        await #expect(throws: APIError.self) {
            try await tracker.delete(entry)
        }
        #expect(tracker.entries.map(\.id) == ["e1"])
    }

    @Test func continuingStartsAgainWithTheSameDetails() async throws {
        let finished = entryJSON(id: "e1", description: "Design review", projectId: "p1", tagIds: ["t1"], billable: false, start: hourAgo, stop: Date())
        let tracker = try await tracker(fakeServer(entries: "[\(finished)]") { request in (.created, savedJSON(request)) })
        await tracker.load()

        try await tracker.continueEntry(tracker.entries[0])

        #expect(tracker.running?.description == "Design review")
        #expect(tracker.running?.projectId == "p1")
        #expect(tracker.running?.tagIds == ["t1"])
        // Not billable, as the entry was, though its project is.
        #expect(tracker.running?.isBillable == false)
        #expect(tracker.running?.id != "e1")
    }

    @Test func duplicatingAddsACopyAtTheSameTime() async throws {
        let stop = Date().rounded()
        let finished = entryJSON(id: "e1", description: "Design review", start: hourAgo, stop: stop)
        let tracker = try await tracker(fakeServer(entries: "[\(finished)]") { request in (.created, savedJSON(request)) })
        await tracker.load()

        try await tracker.duplicate(tracker.entries[0])

        #expect(tracker.entries.count == 2)
        #expect(Set(tracker.entries.map(\.start)) == [hourAgo])
        #expect(Set(tracker.entries.map(\.stop)) == [stop])
    }

    @Test func theCalendarShowsAMovedEntryWhereItWasMovedToBeforeAndAfterTheServerHasIt() async throws {
        let day = Date().addingTimeInterval(-10 * 86_400).rounded()
        let stored = entryJSON(id: "e1", description: "Planning", start: day, stop: day.addingTimeInterval(3600))
        let gate = Gate()
        let tracker = try await tracker(fakeServer(entries: "[\(stored)]") { request in
            await gate.wait()
            return (.ok, savedJSON(request, id: "e1"))
        })
        await tracker.load()
        let time = tracker.time
        let days = CalendarLayout.week(containing: time.day(of: day), in: time)
        try await tracker.loadCalendar(days)
        let entry = try #require(tracker.entries(overlapping: days).first)

        var moved = EntryRequest(entry)
        moved.start = day.addingTimeInterval(7200)
        moved.stop = day.addingTimeInterval(9000)
        let moving = Task { try await tracker.update(entry, to: moved) }
        while tracker.outbox.pending.isEmpty {
            await Task.yield()
        }
        #expect(tracker.entries(overlapping: days).first?.start == day.addingTimeInterval(7200))

        await gate.open()
        try await moving.value
        #expect(tracker.outbox.pending.isEmpty)
        #expect(tracker.entries(overlapping: days).first?.start == day.addingTimeInterval(7200))
        #expect(tracker.entries(overlapping: days).first?.stop == day.addingTimeInterval(9000))
    }

    @Test func entriesOverlappingDaysNewestFirstAreTheSameAsOldestFirst() async throws {
        let day = Date().addingTimeInterval(-10 * 86_400).rounded()
        let stored = entryJSON(id: "e1", description: "Planning", start: day, stop: day.addingTimeInterval(3600))
        let tracker = try await tracker(fakeServer(entries: "[\(stored)]"))
        await tracker.load()
        let time = tracker.time
        let days = CalendarLayout.week(containing: time.day(of: day), in: time)
        try await tracker.loadCalendar(days)
        #expect(tracker.entries(overlapping: days).map(\.id) == ["e1"])
        #expect(tracker.entries(overlapping: days.reversed()).map(\.id) == ["e1"])
    }

    @Test func loadsTheWeekBefore() async throws {
        // Inside the previous week whatever day it is; eight days ago is two weeks back on a Monday.
        let lastWeek = UserTime(timeZone: .gmt).week(weeksBack: 1, from: Date()).start.addingTimeInterval(3600)
        let older = entryJSON(id: "old", start: lastWeek, stop: lastWeek.addingTimeInterval(600))
        let calls = Locked(0)
        let tracker = try await tracker(FakeServer { request in
            switch request.operation {
            case "listTimeEntries":
                let call = calls.withLock { value in
                    value += 1
                    return value
                }
                return (.ok, call == 1 ? "[]" : "[\(older)]")
            case "getCurrentTimeEntry": return (.noContent, "")
            case "getWorkspaceSettings": return (.ok, settingsJSON())
            default: return (.ok, "[]")
            }
        })
        await tracker.load()

        try await tracker.loadWeekBefore()

        #expect(tracker.weeksLoaded == 2)
        #expect(tracker.entries.map(\.id) == ["old"])
    }
}

/// Tracking without a connection: changes wait on the device and go when the server can be reached.
@MainActor
struct OfflineTests {
    let server = URL(string: "https://track.example.com")!
    let hourAgo = Date().addingTimeInterval(-3600).rounded()

    /// A server that can be switched off; while off, every call fails as without a network.
    func switchableServer(online: Locked<Bool>, running: String? = nil) -> FakeServer {
        FakeServer { request in
            guard online.withLock({ $0 }) else { throw URLError(.notConnectedToInternet) }
            switch request.operation {
            case "getWorkspaceSettings": return (.ok, settingsJSON())
            case "listProjects": return (.ok, projectsJSON)
            case "listClients", "listTags": return (.ok, "[]")
            case "getCurrentTimeEntry": return running.map { (.ok, $0) } ?? (.noContent, "")
            case "listTimeEntries": return (.ok, running.map { "[\($0)]" } ?? "[]")
            case "createTimeEntry": return (.created, savedJSON(request))
            case "stopTimeEntry": return (.ok, entryJSON(id: "e1", start: hourAgo, stop: Date()))
            default: return (.notFound, "{}")
            }
        }
    }

    func tracker(_ fake: FakeServer, store: LocalStore) async -> Tracker {
        let tokens = MemoryTokenStore()
        tokens.write("refresh", account: server.absoluteString)
        let api = SpindleClient(session: AuthSession(server: server, tokens: tokens, transport: fake), transport: fake)
        let outbox = await Outbox(api: api, store: store)
        let tracker = Tracker(
            api: api, outbox: outbox, store: store,
            workspace: WorkspaceSummary(id: "w1", organizationId: "o1", name: "Studio", role: .member),
            time: UserTime(timeZone: .gmt))
        outbox.onSent = { [weak tracker] change, saved in tracker?.didSend(change, saved) }
        return tracker
    }

    @Test func aTimerStartsWithoutAConnectionAndReachesTheServerLater() async throws {
        let online = Locked(true)
        let tracker = await tracker(switchableServer(online: online), store: try LocalStore())
        await tracker.load()
        online.withLock { $0 = false }

        try await tracker.start(EntryDetails(description: "On the train", projectId: "p1"))

        #expect(tracker.running?.description == "On the train")
        #expect(tracker.running?.isBillable == true)
        #expect(tracker.outbox.isOffline)
        #expect(tracker.outbox.pending.count == 1)

        online.withLock { $0 = true }
        await tracker.outbox.flush()

        #expect(tracker.outbox.pending.isEmpty)
        #expect(!tracker.outbox.isOffline)
        #expect(tracker.running?.description == "On the train")
        await tracker.refresh()
        #expect(tracker.entries.count == 0 || tracker.running != nil)
    }

    @Test func changesMadeOfflineJoinBeforeTheyAreSent() async throws {
        let online = Locked(true)
        let tracker = await tracker(switchableServer(online: online), store: try LocalStore())
        await tracker.load()
        online.withLock { $0 = false }

        try await tracker.start(EntryDetails(description: "On the train"))
        guard case .stopped = await tracker.stopRunning() else {
            Issue.record("The timer didn't stop")
            return
        }

        // One entry, created already stopped.
        #expect(tracker.outbox.pending.count == 1)
        guard case .create(let request, _, _)? = tracker.outbox.pending.first?.change else {
            Issue.record("Expected a single create")
            return
        }
        #expect(request.stop != nil)
        #expect(tracker.running == nil)
        #expect(tracker.entries.count == 1)

        // Deleted before it was ever sent: nothing left to send.
        try await tracker.delete(tracker.entries[0])
        #expect(tracker.outbox.pending.isEmpty)
        #expect(tracker.entries.isEmpty)
    }

    @Test func waitingChangesSurviveTheAppQuitting() async throws {
        let file = FileManager.default.temporaryDirectory.appending(path: "spindle-\(UUID().uuidString).sqlite")
        defer { try? FileManager.default.removeItem(at: file) }
        let online = Locked(true)

        do {
            let tracker = await tracker(switchableServer(online: online), store: try LocalStore(url: file))
            await tracker.load()
            online.withLock { $0 = false }
            try await tracker.start(EntryDetails(description: "On the train"))
            #expect(tracker.outbox.pending.count == 1)
        }

        // The next launch, still offline: what was loaded and what waits are both there.
        let again = await tracker(switchableServer(online: online), store: try LocalStore(url: file))
        await again.restore()
        await again.load()
        #expect(again.state == .loaded)
        #expect(again.catalog.project("p1")?.name == "Website")
        #expect(again.outbox.pending.count == 1)
        #expect(again.running?.description == "On the train")

        online.withLock { $0 = true }
        await again.outbox.flush()
        #expect(again.outbox.pending.isEmpty)
    }

    @Test func aChangeTheServerTurnsDownLaterIsReported() async throws {
        let online = Locked(true)
        let rejecting = Locked(false)
        let fake = FakeServer { request in
            guard online.withLock({ $0 }) else { throw URLError(.notConnectedToInternet) }
            if rejecting.withLock({ $0 }), request.operation == "createTimeEntry" {
                return (.badRequest, #"{"title":"One or more validation errors occurred.","status":400,"errors":{"start":["Time before 1 September 2026 is locked. Ask an admin to change it."]}}"#)
            }
            return request.operation == "getCurrentTimeEntry" ? (.noContent, "") : (.ok, request.operation == "getWorkspaceSettings" ? settingsJSON() : "[]")
        }
        let tracker = await tracker(fake, store: try LocalStore())
        let rejected = Locked<String?>(nil)
        tracker.outbox.onRejected = { _, error, reported in
            #expect(!reported)
            rejected.withLock { $0 = error.localizedDescription }
        }
        await tracker.load()
        online.withLock { $0 = false }
        let result = try await tracker.save(nil, as: EntryRequest(details: EntryDetails(description: "Old"), start: hourAgo, stop: hourAgo.addingTimeInterval(600)))
        #expect(result == .waiting)
        #expect(tracker.entries.count == 1)

        online.withLock { $0 = true }
        rejecting.withLock { $0 = true }
        await tracker.outbox.flush()

        #expect(tracker.entries.isEmpty)
        #expect(rejected.withLock { $0 } == "Time before 1 September 2026 is locked. Ask an admin to change it.")
    }
}

struct EntryChangeTests {
    let start = Date(timeIntervalSince1970: 1_790_665_200)

    func entry(_ id: String, stop: Date?) -> TimeEntry {
        TimeEntry(id: id, workspaceId: "w1", description: id, projectId: nil, taskId: nil, tagIds: [], isBillable: false, start: start, stop: stop, shares: [], version: "1")
    }

    func request(_ description: String, stop: Date? = nil) -> EntryRequest {
        EntryRequest(details: EntryDetails(description: description), start: start, stop: stop)
    }

    @Test func startingStopsTheTimerRunningWhereTheNewOneStarts() {
        let running = entry("a", stop: nil)
        let later = EntryRequest(details: EntryDetails(description: "b"), start: start.addingTimeInterval(600), stop: nil)

        let shown = EntryChange.apply([.create(later, id: "b", workspaceId: "w1")], to: [running], running: running, workspaceId: "w1") { _ in false }

        #expect(shown.running?.id == "b")
        #expect(shown.entries.first { $0.id == "a" }?.stop == start.addingTimeInterval(600))
    }

    @Test func updatesStopsAndDeletesApplyInOrder() {
        let running = entry("a", stop: nil)
        let changes: [EntryChange] = [
            .update(request("renamed"), id: "a", workspaceId: "w1"),
            .stop(at: start.addingTimeInterval(60), id: "a", workspaceId: "w1"),
            .create(request("new", stop: start.addingTimeInterval(30)), id: "c", workspaceId: "w1"),
            .delete(id: "c", workspaceId: "w1"),
        ]

        let shown = EntryChange.apply(changes, to: [running], running: running, workspaceId: "w1") { _ in false }

        #expect(shown.running == nil)
        #expect(shown.entries.map(\.id) == ["a"])
        #expect(shown.entries[0].description == "renamed")
        #expect(shown.entries[0].stop == start.addingTimeInterval(60))
    }

    @Test func entriesOfOtherWorkspacesStayOut() {
        let shown = EntryChange.apply([.create(request("elsewhere", stop: start), id: "x", workspaceId: "w2")], to: [], running: nil, workspaceId: "w1") { _ in false }
        #expect(shown.entries.isEmpty)
    }

    @Test(arguments: [
        // (first, then, joined)
        ("create", "update", "create"),
        ("create", "stop", "create"),
        ("create", "delete", "nothing"),
        ("update", "update", "update"),
        ("update", "stop", "update"),
        ("update", "delete", "delete"),
        ("stop", "update", "update"),
        ("stop", "delete", "delete"),
        ("delete", "update", "separate"),
    ])
    func joinsChangesToTheSameEntry(first: String, then: String, joined: String) {
        func change(_ kind: String) -> EntryChange {
            switch kind {
            case "create": .create(request("a"), id: "a", workspaceId: "w1")
            case "update": .update(request("b"), id: "a", workspaceId: "w1")
            case "stop": .stop(at: start.addingTimeInterval(60), id: "a", workspaceId: "w1")
            default: .delete(id: "a", workspaceId: "w1")
            }
        }
        let result = change(first).merged(with: change(then))
        switch (joined, result) {
        case ("nothing", .some(nil)), ("separate", nil):
            break
        case (_, .some(.some(let merged))):
            let kind = switch merged {
            case .create: "create"
            case .update: "update"
            case .stop: "stop"
            case .delete: "delete"
            }
            #expect(kind == joined)
        default:
            Issue.record("\(first) then \(then) gave \(String(describing: result))")
        }
    }
}

struct LocalStoreTests {
    @Test func keepsDocumentsAndChangesInOrder() async throws {
        let store = try LocalStore()
        await store.write(["a": 1], key: "numbers")
        #expect(await store.read([String: Int].self, key: "numbers") == ["a": 1])
        await store.write(["a": 2], key: "numbers")
        #expect(await store.read([String: Int].self, key: "numbers") == ["a": 2])

        let first = try await store.append(.delete(id: "a", workspaceId: "w1"))
        let second = try await store.append(.delete(id: "b", workspaceId: "w1"))
        #expect(await store.changes().map(\.change.entryId) == ["a", "b"])

        try await store.replace(first, with: .delete(id: "c", workspaceId: "w1"))
        await store.removeChange(second)
        #expect(await store.changes().map(\.change.entryId) == ["c"])
        #expect(await store.read(String.self, key: "missing") == nil)
    }

    @Test func putsEachAccountInItsOwnFile() {
        let one = LocalStore.url(server: URL(string: "http://localhost:5258")!, userId: "u1")
        let other = LocalStore.url(server: URL(string: "https://track.example.com")!, userId: "u1")
        #expect(one.lastPathComponent == "localhost-5258-u1.sqlite")
        #expect(one != other)
    }
}

/// A value that a pretend server and a test share.
final class Locked<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Value

    init(_ value: Value) {
        self.value = value
    }

    func withLock<T>(_ body: (inout Value) -> T) -> T {
        lock.withLock { body(&value) }
    }
}

extension Date {
    /// To the second, so it reads back from JSON exactly.
    func rounded() -> Date {
        Date(timeIntervalSince1970: timeIntervalSince1970.rounded(.down))
    }
}
