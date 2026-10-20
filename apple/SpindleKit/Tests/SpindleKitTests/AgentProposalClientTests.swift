import Foundation
import Testing
@testable import SpindleKit

@Suite(.serialized) struct AgentProposalClientTests {
    let keychain = Keychain(service: "com.spindletrack.tests.\(UUID().uuidString)")
    let server = URL(string: "https://track.example.com")!

    static let list = #"""
    {"pending":[
      {"id":"p1","workspaceId":"w1","entryId":null,"original":null,
       "fields":{"start":"2026-10-12T08:00:00Z","stop":"2026-10-12T09:30:00Z","description":null,
                 "work":{"projectId":"a","taskId":null,"isBillable":true,"shares":[
                   {"projectId":"a","taskId":null,"isBillable":true,"percent":60,"description":"Fix the login redirect"},
                   {"projectId":"b","taskId":"t","isBillable":false,"percent":40,"description":"Review the API"}]}},
       "reason":"Commits on fix/login","client":"Claude Code","proposedAt":"2026-10-12T10:00:00Z","expiresAt":"2026-10-19T10:00:00Z",
       "saves":null,"blocking":[],"warnings":["It overlaps another entry."]},
      {"id":"p2","workspaceId":"w1","entryId":"e1",
       "original":{"start":"2026-10-12T11:00:00Z","stop":"2026-10-12T12:00:00Z","description":"Standup","projectId":"a","taskId":null,"isBillable":true,"shares":[]},
       "fields":{"start":null,"stop":"2026-10-12T12:30:00Z","description":null,"work":null},
       "reason":"The meeting ran late","client":"Codex","proposedAt":"2026-10-12T13:00:00Z","expiresAt":"2026-10-19T13:00:00Z",
       "saves":null,"blocking":["The day is locked."],"warnings":["The entry changed since this was proposed."]}],
     "outcomes":[]}
    """#

    static let entry = #"{"id":"e9","workspaceId":"w1","userId":"u1","description":"","projectId":"a","taskId":null,"tagIds":[],"isBillable":true,"start":"2026-10-12T08:00:00Z","stop":"2026-10-12T09:30:00Z","shares":[],"version":"1","createdAt":"2026-10-12T10:00:00Z","updatedAt":"2026-10-12T10:00:00Z"}"#

    func client(_ transport: FakeTransport) -> SpindleClient {
        keychain.write("r0", account: server.absoluteString)
        return SpindleClient(session: AuthSession(server: server, tokens: keychain, transport: transport), transport: transport)
    }

    func transport() -> FakeTransport {
        FakeTransport { _, operation in
            switch operation {
            case "refreshTokens": (.ok, tokens(access: "a", expiresIn: 900))
            case "listAgentProposals": (.ok, Self.list)
            case "applyAgentProposal": (.ok, #"{"proposalId":"p1","entry":\#(Self.entry)}"#)
            case "resolveAgentProposal", "reopenAgentProposal": (.noContent, "")
            default: (.notFound, "{}")
            }
        }
    }

    @Test func theServersProposalsBecomeTheAppsWithTheServersWords() async throws {
        let proposals = try await client(transport()).agentProposals(in: "w1")
        keychain.delete(account: server.absoluteString)

        #expect(proposals.pending.map(\.id) == ["p1", "p2"])
        let new = proposals.pending[0]
        #expect(new.isNew && new.client == "Claude Code" && new.reason == "Commits on fix/login")
        #expect(new.fields.work?.shares.map(\.percent) == [60, 40])
        #expect(new.fields.work?.shares.map(\.description) == ["Fix the login redirect", "Review the API"])
        // A split proposed with a description per part is saved with them.
        let request = new.request(current: nil)
        #expect(request.describeParts == true)
        #expect(request.details.shares.map(\.projectId) == ["a", "b"])

        let change = proposals.pending[1]
        #expect(change.entryId == "e1")
        #expect(change.original?.details.description == "Standup")
        #expect(change.fields.start == nil && change.fields.stop != nil)
        #expect(proposals.issues["p1"] == [AgentProposals.Issue(message: "It overlaps another entry.", blocking: false)])
        #expect(proposals.issues["p2"] == [AgentProposals.Issue(message: "The day is locked.", blocking: true),
                                          AgentProposals.Issue(message: "The entry changed since this was proposed.", blocking: false)])
    }

    @Test func answersGoToTheServer() async throws {
        let transport = transport()
        let client = client(transport)
        let entry = try await client.applyAgentProposal("p1", in: "w1")
        try await client.savedAgentProposal("p2", as: "e1", in: "w1")
        try await client.discardAgentProposal("p3", in: "w1")
        try await client.reopenAgentProposal("p1", in: "w1")
        keychain.delete(account: server.absoluteString)

        #expect(entry.id == "e9")
        #expect(transport.calls("applyAgentProposal") == 1)
        #expect(transport.calls("reopenAgentProposal") == 1)
        let bodies = transport.bodies("resolveAgentProposal").map { $0.filter { !$0.isWhitespace } }
        #expect(bodies.count == 2)
        #expect(bodies[0].contains(#""outcome":"saved""#) && bodies[0].contains(#""entryId":"e1""#))
        #expect(bodies[1].contains(#""outcome":"discarded""#) && !bodies[1].contains("entryId"))
    }
}
