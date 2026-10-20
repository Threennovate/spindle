import Foundation
import HTTPTypes
import Testing
@testable import SpindleKit

/// Changes carry the version of the entry they were made on, and one the server refuses because another device
/// changed the entry since stays as a conflict for the person to settle.
@MainActor
struct OutboxConflictTests {
    let server = URL(string: "https://track.example.com")!
    let hourAgo = Date().addingTimeInterval(-3600).rounded()

    func tracker(_ fake: FakeServer, store: LocalStore? = nil) async throws -> Tracker {
        let tokens = MemoryTokenStore()
        tokens.write("refresh", account: server.absoluteString)
        let api = SpindleClient(session: AuthSession(server: server, tokens: tokens, transport: fake), transport: fake)
        let outbox = await Outbox(api: api, store: try store ?? LocalStore())
        let tracker = Tracker(
            api: api, outbox: outbox, store: nil,
            workspace: WorkspaceSummary(id: "w1", organizationId: "o1", name: "Studio", role: .member),
            time: UserTime(timeZone: .gmt))
        outbox.onSent = { [weak tracker] change, saved in tracker?.didSend(change, saved) }
        outbox.onConflict = { [weak tracker] _, current in tracker?.didLearn(current) }
        return tracker
    }

    /// The server with entry e1 as another device left it: "Theirs", version 2.
    func changedElsewhere(saved: Locked<[String?]>, running: Bool = false) -> FakeServer {
        let start = hourAgo
        let stop: Date? = running ? nil : start.addingTimeInterval(1800)
        let original = entryJSON(id: "e1", description: "Original", start: start, stop: stop, version: "1")
        let theirs = entryJSON(id: "e1", description: "Theirs", start: start, stop: stop, version: "2")
        let refused = #"{"type":"urn:spindle:problem:entry-changed","title":"This entry changed since you saw it.","status":412,"current":\#(theirs)}"#
        return FakeServer { request in
            switch request.operation {
            case "getWorkspaceSettings": return (.ok, settingsJSON())
            case "listProjects": return (.ok, projectsJSON)
            case "listClients", "listTags": return (.ok, "[]")
            case "getCurrentTimeEntry": return running ? (.ok, original) : (.noContent, "")
            case "listTimeEntries": return (.ok, "[\(original)]")
            case "updateTimeEntry", "stopTimeEntry", "deleteTimeEntry":
                saved.withLock { $0.append(request.ifMatch) }
                guard request.ifMatch == "2" else { return (.preconditionFailed, refused) }
                if request.operation == "deleteTimeEntry" { return (.noContent, "") }
                if request.operation == "stopTimeEntry" {
                    return (.ok, entryJSON(id: "e1", description: "Theirs", start: start, stop: Date(), version: "3"))
                }
                return (.ok, savedJSON(request, id: "e1", version: "3"))
            default: return (.notFound, "{}")
            }
        }
    }

    func edit(_ tracker: Tracker, to description: String) async throws -> Tracker.SaveOutcome {
        let entry = try #require(tracker.entry(id: "e1"))
        var request = EntryRequest(entry)
        request.details.description = description
        return try await tracker.save(entry, as: request)
    }

    @Test func aChangeToAnEntryChangedElsewhereStaysUntilThePersonChooses() async throws {
        let sent = Locked<[String?]>([])
        let tracker = try await tracker(changedElsewhere(saved: sent))
        await tracker.load()

        let outcome = try await edit(tracker, to: "Mine")

        #expect(outcome == .waiting)
        #expect(sent.withLock { $0 } == ["1"])
        #expect(tracker.outbox.conflicts.count == 1)
        #expect(tracker.outbox.conflicts.first?.conflict?.description == "Theirs")
        // Still shown as the person left it, until they choose.
        #expect(tracker.entry(id: "e1")?.description == "Mine")

        // Another try changes nothing: it waits for the person.
        await tracker.outbox.flush()
        #expect(sent.withLock { $0.count } == 1)
    }

    @Test func keepingTheirsDropsTheChange() async throws {
        let sent = Locked<[String?]>([])
        let tracker = try await tracker(changedElsewhere(saved: sent))
        await tracker.load()
        _ = try await edit(tracker, to: "Mine")

        await tracker.outbox.keepTheirs(try #require(tracker.outbox.conflicts.first).seq)

        #expect(tracker.outbox.pending.isEmpty)
        #expect(tracker.entry(id: "e1")?.description == "Theirs")
        #expect(sent.withLock { $0.count } == 1)
    }

    @Test func keepingMineSavesItOverTheirs() async throws {
        let sent = Locked<[String?]>([])
        let tracker = try await tracker(changedElsewhere(saved: sent))
        await tracker.load()
        _ = try await edit(tracker, to: "Mine")

        await tracker.outbox.keepMine(try #require(tracker.outbox.conflicts.first).seq)

        #expect(tracker.outbox.pending.isEmpty)
        #expect(sent.withLock { $0 } == ["1", "2"])
        #expect(tracker.entry(id: "e1")?.description == "Mine")
        #expect(tracker.entry(id: "e1")?.version == "3")
    }

    @Test func aDeleteOfAnEntryChangedElsewhereKeepsItUntilThePersonChooses() async throws {
        let sent = Locked<[String?]>([])
        let tracker = try await tracker(changedElsewhere(saved: sent))
        await tracker.load()

        try await tracker.delete(try #require(tracker.entry(id: "e1")))

        #expect(tracker.outbox.conflicts.count == 1)
        #expect(tracker.entry(id: "e1") == nil)
        await tracker.outbox.keepTheirs(try #require(tracker.outbox.conflicts.first).seq)
        #expect(tracker.entry(id: "e1")?.description == "Theirs")
    }

    @Test func stoppingATimerRenamedElsewhereStopsItAnyway() async throws {
        let sent = Locked<[String?]>([])
        let tracker = try await tracker(changedElsewhere(saved: sent, running: true))
        await tracker.load()

        guard case .stopped = await tracker.stopRunning() else {
            Issue.record("The timer didn't stop")
            return
        }

        #expect(tracker.outbox.pending.isEmpty)
        #expect(sent.withLock { $0 } == ["1", "2"])
        #expect(tracker.running == nil)
    }

    @Test func aChangeMadeWhileAnotherIsOnItsWayIsSentOnTheVersionThatOneGets() async throws {
        let gate = Gate()
        let sent = Locked<[String?]>([])
        let start = hourAgo
        let fake = FakeServer { request in
            switch request.operation {
            case "getWorkspaceSettings": return (.ok, settingsJSON())
            case "listProjects": return (.ok, projectsJSON)
            case "listClients", "listTags": return (.ok, "[]")
            case "getCurrentTimeEntry": return (.noContent, "")
            case "listTimeEntries": return (.ok, "[\(entryJSON(id: "e1", start: start, stop: start.addingTimeInterval(600), version: "1"))]")
            case "updateTimeEntry":
                let count = sent.withLock { $0.append(request.ifMatch); return $0.count }
                if count == 1 { await gate.wait() }
                return (.ok, savedJSON(request, id: "e1", version: "\(count + 1)"))
            default: return (.notFound, "{}")
            }
        }
        let tracker = try await tracker(fake)
        await tracker.load()

        let first = Task { _ = try await edit(tracker, to: "First") }
        while sent.withLock({ $0.isEmpty }) { await Task.yield() }
        let second = Task { _ = try await edit(tracker, to: "Second") }
        while tracker.outbox.pending.count < 2 { await Task.yield() }
        await gate.open()
        try await first.value
        try await second.value

        #expect(sent.withLock { $0 } == ["1", "2"])
        #expect(tracker.entry(id: "e1")?.description == "Second")
    }

    @Test func changesQueuedBeforeVersionsStillLoad() throws {
        let request = EntryRequest(details: EntryDetails(description: "Offline"), start: hourAgo, stop: nil)
        let encoder = JSONEncoder()
        for change in [
            EntryChange.update(request, id: "e1", workspaceId: "w1", version: "7"),
            .stop(at: hourAgo, id: "e1", workspaceId: "w1", version: "7"),
            .delete(id: "e1", workspaceId: "w1", version: "7"),
        ] {
            // As an app that predates versions stored it: the same, without the version.
            var object = try #require(JSONSerialization.jsonObject(with: encoder.encode(change)) as? [String: [String: Any]])
            let key = try #require(object.keys.first)
            object[key]?["version"] = nil
            let old = try JSONSerialization.data(withJSONObject: object)

            let decoded = try JSONDecoder().decode(EntryChange.self, from: old)

            #expect(decoded == change.on(version: nil))
        }
    }
}
