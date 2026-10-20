import Foundation
import Testing
import SpindleKit
@testable import Spindle

/// The proposals agents made, as the Mac reads and answers them on the server.
struct ProposalInboxTests {
    let defaults: UserDefaults

    init() {
        let suite = "com.spindletrack.tests.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
    }

    nonisolated static func proposal(_ id: String, entryId: String? = nil, blocking: [String] = [], warnings: [String] = []) -> String {
        let entry = entryId.map { "\"\($0)\"" } ?? "null"
        let quoted = { (texts: [String]) in "[" + texts.map { "\"\($0)\"" }.joined(separator: ",") + "]" }
        return #"""
        {"id":"\#(id)","workspaceId":"w2","entryId":\#(entry),"original":null,
         "fields":{"start":"2026-10-12T08:00:00Z","stop":"2026-10-12T09:00:00Z","description":"Fix the login redirect","work":null},
         "reason":"Commits on fix/login","client":"Claude Code","proposedAt":"2026-10-12T10:00:00Z","expiresAt":"2026-10-19T10:00:00Z",
         "saves":null,"blocking":\#(quoted(blocking)),"warnings":\#(quoted(warnings))}
        """#
    }

    nonisolated static let entry = #"{"id":"e9","workspaceId":"w2","description":"Fix the login redirect","projectId":null,"taskId":null,"tagIds":[],"isBillable":false,"start":"2026-10-12T08:00:00Z","stop":"2026-10-12T09:00:00Z","shares":[],"version":"1"}"#

    /// A signed-in account whose server has `pending` waiting in Consulting (w2).
    private func signedIn(_ pending: Locked<[String]>) async throws -> (ProposalInbox, AppModel, StubServer) {
        let server = StubServer { path in
            switch path {
            case "/api/auth/login": return (200, tokensJSON())
            case "/api/me": return (200, meJSON())
            case "/api/me/time-entries/current": return (204, "")
            case "/api/workspaces/w2/agent-proposals":
                return (200, #"{"pending":[\#(pending.withLock { $0.joined(separator: ",") })],"outcomes":[]}"#)
            case let path where path.hasSuffix("/apply"): return (200, #"{"proposalId":"p1","entry":\#(Self.entry)}"#)
            case let path where path.hasSuffix("/resolve") || path.hasSuffix("/reopen"): return (204, "")
            case let path where path.hasSuffix("/settings"):
                return (200, #"{"name":"Consulting","currency":"EUR","defaultHourlyRate":null,"onlyAdminsCreateProjects":false,"onlyAdminsCreateTags":false,"lockedBefore":null,"requireDescription":false,"requireProject":false,"requireTask":false,"requireTag":false}"#)
            case let path where path.hasSuffix("/time-entries"): return (200, "[]")
            case let path where path.hasSuffix("/projects") || path.hasSuffix("/tags") || path.hasSuffix("/clients"): return (200, "[]")
            default: return (404, "{}")
            }
        }
        let model = AppModel(tokens: MemoryTokenStore(), urlSession: server.session, defaults: defaults, server: server.url,
                             openStore: { _, _ in try LocalStore() })
        await model.start()
        try await model.signIn(email: "ana@example.com", password: "correct horse")
        for _ in 0..<300 where model.tracker?.state != .loaded {
            try await Task.sleep(for: .milliseconds(10))
        }
        let inbox = ProposalInbox(model: model)
        await inbox.refresh()
        return (inbox, model, server)
    }

    @Test func theServersProposalsWaitWithWhatStandsInTheirWay() async throws {
        let pending = Locked([Self.proposal("p1", warnings: ["It overlaps another entry."]), Self.proposal("p2", entryId: "e1", blocking: ["The day is locked."])])
        let (inbox, model, _) = try await signedIn(pending)
        let tracker = try #require(model.tracker)

        #expect(inbox.pending(in: tracker).map(\.id) == ["p1", "p2"])
        #expect(inbox.change(for: "e1")?.id == "p2")
        #expect(inbox.issues(inbox.all[0], tracker: tracker) == [AgentProposals.Issue(message: "It overlaps another entry.", blocking: false)])
        #expect(inbox.issues(inbox.all[1], tracker: tracker) == [AgentProposals.Issue(message: "The day is locked.", blocking: true)])
    }

    @Test func addingAllSavesOnlyWhatNothingBlocksAndReadsThemAgain() async throws {
        let pending = Locked([Self.proposal("p1"), Self.proposal("p2", entryId: "e1", blocking: ["The day is locked."])])
        let (inbox, model, server) = try await signedIn(pending)
        let tracker = try #require(model.tracker)
        pending.withLock { $0 = [Self.proposal("p2", entryId: "e1", blocking: ["The day is locked."])] }

        let result = await inbox.acceptAll(in: tracker)

        #expect(result.added == 1 && result.failed == 0)
        #expect(server.paths.filter { $0.hasSuffix("/apply") } == ["/api/workspaces/w2/agent-proposals/p1/apply"])
        #expect(inbox.done.map(\.entryId) == ["e9"])
        #expect(inbox.pending(in: tracker).map(\.id) == ["p2"])
    }

    @Test func savingInTheEditorAndDiscardingAreAnsweredOnTheServer() async throws {
        let pending = Locked([Self.proposal("p1"), Self.proposal("p2")])
        let (inbox, _, server) = try await signedIn(pending)

        inbox.saved(inbox.all[0], as: "e7")
        inbox.discard(inbox.all[0])
        #expect(inbox.all.isEmpty)
        for _ in 0..<100 where server.paths.filter({ $0.hasSuffix("/resolve") }).count < 2 {
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(Set(server.paths.filter { $0.hasSuffix("/resolve") })
                == ["/api/workspaces/w2/agent-proposals/p1/resolve", "/api/workspaces/w2/agent-proposals/p2/resolve"])
    }

    @Test func newProposalsAreToldButThoseWaitingAtTheStartAreNot() async throws {
        let pending = Locked([Self.proposal("p1")])
        let (inbox, _, _) = try await signedIn(pending)
        let arrivals = Locked<[(Int, String?)]>([])
        inbox.onArrival = { count, client in arrivals.withLock { $0.append((count, client)) } }

        await inbox.refresh()
        #expect(arrivals.withLock { $0.isEmpty })
        pending.withLock { $0 = [Self.proposal("p1"), Self.proposal("p3")] }
        await inbox.refresh()
        #expect(arrivals.withLock { $0.map(\.0) } == [1])
        #expect(arrivals.withLock { $0.first?.1 } == "Claude Code")
    }
}
