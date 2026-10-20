import Foundation
import HTTPTypes
import OpenAPIRuntime
import Testing
@testable import SpindleKit

/// A pretend server: answers each request with whatever the test's handler returns, and counts the calls.
final class FakeTransport: ClientTransport, @unchecked Sendable {
    typealias Handler = @Sendable (HTTPRequest, String) async throws -> (HTTPResponse.Status, String)
    private let lock = NSLock()
    private var counts: [String: Int] = [:]
    private var authorizations: [String] = []
    private var sentBodies: [String: [String]] = [:]
    let handler: Handler

    init(handler: @escaping Handler) {
        self.handler = handler
    }

    func calls(_ operationID: String) -> Int {
        lock.withLock { counts[operationID, default: 0] }
    }

    var seenAuthorizations: [String] {
        lock.withLock { authorizations }
    }

    /// The JSON bodies sent with an operation, in order.
    func bodies(_ operationID: String) -> [String] {
        lock.withLock { sentBodies[operationID, default: []] }
    }

    func send(_ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String) async throws -> (HTTPResponse, HTTPBody?) {
        lock.withLock {
            counts[operationID, default: 0] += 1
            if let authorization = request.headerFields[.authorization] {
                authorizations.append(authorization)
            }
        }
        if let body {
            let text = try await String(collecting: body, upTo: 1 << 20)
            lock.withLock { sentBodies[operationID, default: []].append(text) }
        }
        let (status, json) = try await handler(request, operationID)
        var response = HTTPResponse(status: status)
        response.headerFields[.contentType] = status.code >= 400 ? "application/problem+json" : "application/json"
        return (response, HTTPBody(json))
    }
}

func tokens(access: String, expiresIn seconds: TimeInterval, refresh: String = "refresh-\(UUID().uuidString)") -> String {
    let expires = Date().addingTimeInterval(seconds).formatted(Date.ISO8601FormatStyle(includingFractionalSeconds: true))
    let later = Date().addingTimeInterval(86_400 * 90).formatted(Date.ISO8601FormatStyle(includingFractionalSeconds: true))
    return #"{"accessToken":"\#(access)","accessTokenExpiresAt":"\#(expires)","refreshToken":"\#(refresh)","refreshTokenExpiresAt":"\#(later)"}"#
}

let meJSON = #"{"id":"u1","email":"ana@example.com","emailConfirmed":true,"fullName":"Ana","timeZone":"Europe/Zagreb","preferences":{"weekStart":1,"dateFormat":"d MMM yyyy","timeFormat":"24h","durationFormat":"hms","pomodoroEnabled":false,"pomodoroMinutes":25,"pomodoroBreakMinutes":5,"pomodoroAutoStop":false},"defaultWorkspaceId":"w1","organizations":[],"workspaces":[]}"#

@Suite(.serialized) struct AuthSessionTests {
    let keychain = Keychain(service: "com.spindletrack.tests.\(UUID().uuidString)")
    let server = URL(string: "https://track.example.com")!

    @Test func signingInKeepsTheRefreshTokenInTheKeychain() async throws {
        let transport = FakeTransport { _, operation in
            operation == "login" ? (.ok, tokens(access: "a1", expiresIn: 900, refresh: "r1")) : (.notFound, "{}")
        }
        let session = AuthSession(server: server, tokens: keychain, transport: transport)

        try await session.signIn(email: "ana@example.com", password: "correct horse battery")

        #expect(keychain.read(account: server.absoluteString) == "r1")
        #expect(session.hasStoredSession)
        #expect(try await session.validAccessToken() == "a1")
        keychain.delete(account: server.absoluteString)
    }

    @Test func wrongPasswordsSayWhy() async {
        let transport = FakeTransport { _, _ in (.unauthorized, #"{"title":"Invalid email or password.","status":401}"#) }
        let session = AuthSession(server: server, tokens: keychain, transport: transport)

        await #expect(throws: APIError(status: 401, title: "Invalid email or password.")) {
            try await session.signIn(email: "ana@example.com", password: "nope")
        }
        #expect(!session.hasStoredSession)
    }

    @Test func calls_share_one_renewal_of_an_expired_token() async throws {
        keychain.write("r0", account: server.absoluteString)
        let transport = FakeTransport { _, operation in
            switch operation {
            case "refreshTokens":
                try await Task.sleep(for: .milliseconds(100))
                return (.ok, tokens(access: "a2", expiresIn: 900))
            case "getMe": return (.ok, meJSON)
            default: return (.notFound, "{}")
            }
        }
        let client = SpindleClient(session: AuthSession(server: server, tokens: keychain, transport: transport), transport: transport)

        async let first = client.me()
        async let second = client.me()
        async let third = client.me()
        let names = try await [first, second, third].map(\.fullName)

        #expect(names == ["Ana", "Ana", "Ana"])
        #expect(transport.calls("refreshTokens") == 1)
        #expect(transport.seenAuthorizations.allSatisfy { $0 == "Bearer a2" })
        keychain.delete(account: server.absoluteString)
    }

    @Test func a_rejected_token_is_renewed_and_the_call_tried_again() async throws {
        keychain.write("r0", account: server.absoluteString)
        let transport = FakeTransport { request, operation in
            switch operation {
            case "refreshTokens": return (.ok, tokens(access: "fresh", expiresIn: 900))
            case "getMe":
                return request.headerFields[.authorization] == "Bearer fresh" ? (.ok, meJSON) : (.unauthorized, "{}")
            default: return (.notFound, "{}")
            }
        }
        let session = AuthSession(server: server, tokens: keychain, transport: transport)
        let client = SpindleClient(session: session, transport: transport)

        #expect(try await client.me().fullName == "Ana")
        // The first renewal got "fresh" already; the retry used it without renewing again.
        #expect(transport.calls("getMe") == 1)
        #expect(transport.calls("refreshTokens") == 1)
        keychain.delete(account: server.absoluteString)
    }

    @Test func a_session_ended_elsewhere_signs_this_device_out() async throws {
        keychain.write("revoked", account: server.absoluteString)
        let transport = FakeTransport { _, operation in
            operation == "refreshTokens" ? (.unauthorized, #"{"title":"Invalid refresh token.","status":401}"#) : (.ok, meJSON)
        }
        let session = AuthSession(server: server, tokens: keychain, transport: transport)
        let client = SpindleClient(session: session, transport: transport)
        var ended = session.sessionEnded.makeAsyncIterator()

        await #expect(throws: APIError.sessionEnded) {
            try await client.me()
        }
        // Signalled once, so this does not wait.
        #expect(!session.hasStoredSession)
        #expect(await ended.next() != nil)
        #expect(!session.hasStoredSession)
    }

    @Test func a_memory_store_keeps_the_session_for_this_run_only() async throws {
        let transport = FakeTransport { _, operation in
            operation == "login" ? (.ok, tokens(access: "a1", expiresIn: 900, refresh: "r1")) : (.noContent, "")
        }
        let memory = MemoryTokenStore()
        let session = AuthSession(server: server, tokens: memory, transport: transport)

        try await session.signIn(email: "ana@example.com", password: "correct horse battery")
        #expect(memory.read(account: server.absoluteString) == "r1")
        #expect(keychain.read(account: server.absoluteString) == nil)

        await session.signOut()
        #expect(memory.read(account: server.absoluteString) == nil)
        #expect(transport.calls("logout") == 1)
    }

    @Test func validation_errors_come_with_their_fields() async throws {
        keychain.write("r0", account: server.absoluteString)
        let transport = FakeTransport { _, operation in
            switch operation {
            case "refreshTokens": return (.ok, tokens(access: "a", expiresIn: 900))
            default: return (.badRequest, #"{"title":"One or more validation errors occurred.","status":400,"errors":{"workspaceId":["You are not a member of that workspace."]}}"#)
            }
        }
        let client = SpindleClient(session: AuthSession(server: server, tokens: keychain, transport: transport), transport: transport)

        do {
            try await client.switchWorkspace(to: "w9")
            Issue.record("Expected a validation error")
        } catch let error as APIError {
            #expect(error.status == 400)
            #expect(error.message(for: "workspaceId") == "You are not a member of that workspace.")
            #expect(error.errorDescription == "You are not a member of that workspace.")
        }
        keychain.delete(account: server.absoluteString)
    }
}

@Suite struct ReadOnlyErrorTests {
    @Test @MainActor func aReadOnlyRefusalSaysWhyAndIsNotSentAgain() {
        let error = APIError(status: 402, title: "This organization is read-only.", detail: "Its trial or plan has ended.")

        #expect(error.isReadOnly)
        #expect(error.errorDescription == APIError.readOnlyMessage)
        #expect(!Outbox.isWorthRetrying(error))
    }
}
