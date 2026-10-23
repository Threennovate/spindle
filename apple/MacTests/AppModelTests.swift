import Foundation
import Testing
import SpindleKit
@testable import Spindle

/// A pretend server for one test: answers each request by its path, e.g. `/api/me`. Each server has its own host,
/// so tests can run side by side.
nonisolated final class StubServer: Sendable {
    typealias Handler = @Sendable (_ path: String) throws -> (status: Int, json: String)

    let url: URL
    let session: URLSession
    let handler: Handler

    init(_ handler: @escaping Handler) {
        self.handler = handler
        let host = "t\(UUID().uuidString.prefix(8).lowercased()).example.com"
        url = URL(string: "https://\(host)")!
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        session = URLSession(configuration: configuration)
        StubURLProtocol.register(handler, for: host)
    }

    /// The paths called so far, in order.
    var paths: [String] {
        StubURLProtocol.paths(for: url.host()!)
    }
}

/// Hands each request to the stub server for its host.
nonisolated final class StubURLProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var handlers: [String: StubServer.Handler] = [:]
    nonisolated(unsafe) private static var calls: [String: [String]] = [:]

    static func register(_ handler: @escaping StubServer.Handler, for host: String) {
        lock.withLock { handlers[host] = handler }
    }

    static func paths(for host: String) -> [String] {
        lock.withLock { calls[host] ?? [] }
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func stopLoading() {}

    override func startLoading() {
        let url = request.url!
        let handler = Self.lock.withLock {
            Self.calls[url.host()!, default: []].append(url.path())
            return Self.handlers[url.host()!]
        }
        do {
            guard let handler else { throw URLError(.cannotFindHost) }
            let (status, json) = try handler(url.path())
            let response = HTTPURLResponse(
                url: url, statusCode: status, httpVersion: "HTTP/1.1",
                headerFields: ["Content-Type": status >= 400 ? "application/problem+json" : "application/json"])!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(json.utf8))
            client?.urlProtocolDidFinishLoading(self)
        } catch {
            client?.urlProtocol(self, didFailWithError: error)
        }
    }
}

/// A value that the stub server's handler and the test share.
nonisolated final class Locked<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Value

    init(_ value: Value) {
        self.value = value
    }

    func withLock<T>(_ body: (inout Value) -> T) -> T {
        lock.withLock { body(&value) }
    }
}

nonisolated func tokensJSON(access: String = "access", expiresIn seconds: TimeInterval = 900, refresh: String = "refresh") -> String {
    let format = Date.ISO8601FormatStyle(includingFractionalSeconds: true)
    let expires = Date().addingTimeInterval(seconds).formatted(format)
    let later = Date().addingTimeInterval(86_400 * 90).formatted(format)
    return #"{"accessToken":"\#(access)","accessTokenExpiresAt":"\#(expires)","refreshToken":"\#(refresh)","refreshTokenExpiresAt":"\#(later)"}"#
}

nonisolated func meJSON(defaultWorkspace: String = "w2") -> String {
    #"""
    {"id":"u1","email":"ana@example.com","emailConfirmed":true,"fullName":"Ana Kovač","timeZone":"Europe/Zagreb",
     "preferences":{"weekStart":1,"dateFormat":"dd.MM.yyyy","timeFormat":"24h","durationFormat":"hms","pomodoroEnabled":false,"pomodoroMinutes":25,"pomodoroBreakMinutes":5,"pomodoroAutoStop":false},
     "defaultWorkspaceId":"\#(defaultWorkspace)",
     "organizations":[{"id":"o1","name":"Ana Kovač","role":"owner"}],
     "workspaces":[{"id":"w1","organizationId":"o1","name":"Personal","role":"admin"},{"id":"w2","organizationId":"o1","name":"Consulting","role":"member"}]}
    """#
}

struct AppModelTests {
    let defaults: UserDefaults

    init() {
        let suite = "com.spindletrack.tests.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
    }

    private func model(_ server: StubServer, tokens: MemoryTokenStore = MemoryTokenStore(), store: LocalStore? = nil) -> AppModel {
        AppModel(
            tokens: tokens, urlSession: server.session, defaults: defaults, server: server.url,
            openStore: { _, _ in try store ?? LocalStore() })
    }

    @Test func withoutASessionSignInShows() async {
        let server = StubServer { _ in (404, "{}") }
        let model = model(server)

        await model.start()

        #expect(model.phase == .signedOut)
        #expect(model.server == server.url)
        #expect(server.paths.isEmpty)
    }

    @Test func signingInLoadsTheAccount() async throws {
        let server = StubServer { path in
            switch path {
            case "/api/auth/login": (200, tokensJSON())
            case "/api/me": (200, meJSON())
            default: (404, "{}")
            }
        }
        let tokens = MemoryTokenStore()
        let model = model(server, tokens: tokens)
        await model.start()

        try await model.signIn(email: "ana@example.com", password: "correct horse")

        #expect(model.phase == .signedIn)
        #expect(model.me?.fullName == "Ana Kovač")
        #expect(model.workspace?.name == "Consulting")
        #expect(!model.isWorkspaceAdmin)
        #expect(model.canCreateWorkspaces)
        #expect(tokens.read(account: server.url.absoluteString) == "refresh")
    }

    @Test func anInvitedMemberCreatesAWorkspaceOfTheirOwn() async throws {
        let me = #"""
        {"id":"u1","email":"ana@example.com","emailConfirmed":true,"fullName":"Ana Kovač","timeZone":"Europe/Zagreb",
         "preferences":{"weekStart":1,"dateFormat":"dd.MM.yyyy","timeFormat":"24h","durationFormat":"hms","pomodoroEnabled":false,"pomodoroMinutes":25,"pomodoroBreakMinutes":5,"pomodoroAutoStop":false},
         "defaultWorkspaceId":"w1",
         "organizations":[{"id":"o1","name":"Firma","role":"member"}],
         "workspaces":[{"id":"w1","organizationId":"o1","name":"Firma","role":"member"}],
         "newWorkspace":{"allowed":true,"organizationId":null,"trialDays":90}}
        """#
        let server = StubServer { path in
            switch path {
            case "/api/auth/login": (200, tokensJSON())
            case "/api/me": (200, me)
            default: (404, "{}")
            }
        }
        let model = model(server)
        await model.start()

        try await model.signIn(email: "ana@example.com", password: "correct horse")

        #expect(model.canCreateWorkspaces)
        #expect(model.newWorkspaceDescription == "A space of your own, apart from Firma, with its own projects, clients, tags and team. It's not on Firma's plan: it starts its own 90-day free trial.")
    }

    @Test func aWrongPasswordLeavesSignInShowing() async {
        let server = StubServer { _ in (401, #"{"title":"Incorrect email or password.","status":401}"#) }
        let model = model(server)
        await model.start()

        await #expect(throws: APIError(status: 401, title: "Incorrect email or password.")) {
            try await model.signIn(email: "ana@example.com", password: "wrong")
        }
        #expect(model.phase == .signedOut)
        #expect(model.signInNotice == nil)
    }

    @Test func theSessionFromTheLastLaunchIsPickedUp() async {
        let server = StubServer { path in
            switch path {
            case "/api/auth/refresh": (200, tokensJSON())
            case "/api/me": (200, meJSON(defaultWorkspace: "w1"))
            default: (404, "{}")
            }
        }
        let tokens = MemoryTokenStore()
        tokens.write("stored", account: server.url.absoluteString)
        let model = model(server, tokens: tokens)

        await model.start()

        #expect(model.phase == .signedIn)
        #expect(model.workspace?.name == "Personal")
        #expect(model.isWorkspaceAdmin)
        #expect(server.paths == ["/api/auth/refresh", "/api/me"])
    }

    @Test func anUnreachableServerOffersToTryAgain() async {
        let server = StubServer { _ in throw URLError(.notConnectedToInternet) }
        let tokens = MemoryTokenStore()
        tokens.write("stored", account: server.url.absoluteString)
        let model = model(server, tokens: tokens)

        await model.start()

        #expect(model.phase == .unavailable("Couldn't reach Spindle. Check your connection and try again."))
        // The session is kept, to try again once the server is back.
        #expect(tokens.read(account: server.url.absoluteString) == "stored")
    }

    @Test func aSessionEndedElsewhereShowsSignInWithANotice() async throws {
        let ended = Locked(false)
        let server = StubServer { path in
            switch (path, ended.withLock { $0 }) {
            case ("/api/auth/login", _): (200, tokensJSON())
            case ("/api/me", false): (200, meJSON())
            // Signed out on another device: the access token and the refresh token are both turned down.
            case ("/api/me", true): (401, "")
            case ("/api/auth/refresh", _): (401, #"{"title":"Invalid refresh token.","status":401}"#)
            default: (404, "{}")
            }
        }
        let model = model(server)
        await model.start()
        try await model.signIn(email: "ana@example.com", password: "correct horse")
        #expect(model.phase == .signedIn)

        ended.withLock { $0 = true }
        await #expect(throws: APIError.sessionEnded) {
            try await model.refreshAccount()
        }

        // Announced by the session, whichever call ran into it.
        for _ in 0..<100 where model.phase != .signedOut {
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(model.phase == .signedOut)
        #expect(model.signInNotice == "Your session has ended. Please sign in again.")
        #expect(model.me == nil)
    }

    @Test func aSessionTurnedDownRightAfterSigningInSaysSo() async throws {
        let server = StubServer { path in
            switch path {
            // Expires at once, so the next call renews it, and the server turns the renewal down.
            case "/api/auth/login": (200, tokensJSON(expiresIn: 0))
            case "/api/auth/refresh": (401, #"{"title":"Invalid refresh token.","status":401}"#)
            default: (404, "{}")
            }
        }
        let model = model(server)
        await model.start()
        try await model.signIn(email: "ana@example.com", password: "correct horse")

        #expect(model.phase == .signedOut)
        #expect(model.signInNotice == "Your session has ended. Please sign in again.")
        #expect(model.me == nil)
    }

    @Test func signingOutEndsTheSessionQuietly() async throws {
        let server = StubServer { path in
            switch path {
            case "/api/auth/login": (200, tokensJSON())
            case "/api/me": (200, meJSON())
            case "/api/auth/logout": (204, "")
            default: (404, "{}")
            }
        }
        let tokens = MemoryTokenStore()
        let model = model(server, tokens: tokens)
        await model.start()
        try await model.signIn(email: "ana@example.com", password: "correct horse")

        await model.signOut()

        #expect(model.phase == .signedOut)
        #expect(model.signInNotice == nil)
        #expect(model.me == nil)
        #expect(tokens.read(account: server.url.absoluteString) == nil)
        #expect(server.paths.contains("/api/auth/logout"))
        #expect(model.tracker == nil)
    }

    @Test func switchingWorkspacesShowsTheOtherOne() async throws {
        let current = Locked("w2")
        let server = StubServer { path in
            switch path {
            case "/api/auth/login": return (200, tokensJSON())
            case "/api/me": return (200, meJSON(defaultWorkspace: current.withLock { $0 }))
            case "/api/me/default-workspace":
                current.withLock { $0 = "w1" }
                return (204, "")
            default: return (404, "{}")
            }
        }
        let model = model(server)
        await model.start()
        try await model.signIn(email: "ana@example.com", password: "correct horse")
        #expect(model.workspace?.name == "Consulting")

        try await model.switchWorkspace(to: "w1")

        #expect(model.workspace?.name == "Personal")
        #expect(model.isWorkspaceAdmin)
        // The account reloads after the switch; the tracker's own calls may come in between.
        let paths = server.paths
        let switched = try #require(paths.firstIndex(of: "/api/me/default-workspace"))
        #expect(paths[switched...].contains("/api/me"))
    }

    // MARK: - Tracking

    /// Answers what a signed-in model loads, with a running entry and the workspace's rules.
    private func trackingServer(requireProject: Bool, current: Locked<String>) -> StubServer {
        // Minutes ago, so both are in the week the model loads whenever the tests run.
        func ago(_ minutes: Double) -> String { Date().addingTimeInterval(-minutes * 60).formatted(.iso8601) }
        let running = #"{"id":"e1","workspaceId":"w2","description":"Design review","projectId":null,"taskId":null,"tagIds":[],"isBillable":false,"start":"\#(ago(1))","stop":null,"shares":[],"version":"1"}"#
        let finished = #"{"id":"e0","workspaceId":"w2","description":"Planning","projectId":null,"taskId":null,"tagIds":[],"isBillable":false,"start":"\#(ago(3))","stop":"\#(ago(2))","shares":[],"version":"1"}"#
        let settings = #"{"name":"Consulting","currency":"EUR","defaultHourlyRate":null,"onlyAdminsCreateProjects":false,"onlyAdminsCreateTags":false,"lockedBefore":null,"requireDescription":false,"requireProject":\#(requireProject),"requireTask":false,"requireTag":false}"#
        return StubServer { path in
            switch path {
            case "/api/auth/login": return (200, tokensJSON())
            case "/api/me": return (200, meJSON(defaultWorkspace: current.withLock { $0 }))
            case "/api/me/default-workspace":
                current.withLock { $0 = "w1" }
                return (204, "")
            case "/api/me/time-entries/current": return (200, running)
            case let path where path.hasSuffix("/settings"): return (200, settings)
            case let path where path.hasSuffix("/time-entries"): return (200, "[\(running),\(finished)]")
            case let path where path.hasSuffix("/projects") || path.hasSuffix("/clients") || path.hasSuffix("/tags"): return (200, "[]")
            default: return (404, "{}")
            }
        }
    }

    private func signedIn(_ server: StubServer) async throws -> AppModel {
        let model = model(server)
        await model.start()
        try await model.signIn(email: "ana@example.com", password: "correct horse")
        for _ in 0..<200 where model.tracker?.state != .loaded || model.tracker?.settings == nil {
            try await Task.sleep(for: .milliseconds(10))
        }
        return model
    }

    @Test func signingInStartsTrackingTheCurrentWorkspace() async throws {
        let model = try await signedIn(trackingServer(requireProject: false, current: Locked("w2")))

        #expect(model.tracker?.workspace.id == "w2")
        #expect(model.tracker?.running?.id == "e1")
        #expect(model.tracker?.entries.count == 2)
    }

    @Test func stoppingATimerThatLacksRequiredDetailsOpensItToAddThem() async throws {
        let model = try await signedIn(trackingServer(requireProject: true, current: Locked("w2")))

        await model.stop()

        #expect(model.editing?.entry?.id == "e1")
        #expect(model.editing?.stopping == true)
        #expect(model.notices.current?.text == "Add the details this workspace requires to stop the timer.")
        #expect(model.tracker?.running?.id == "e1")
    }

    @Test func anotherWorkspaceGetsItsOwnTracker() async throws {
        let model = try await signedIn(trackingServer(requireProject: false, current: Locked("w2")))
        let first = model.tracker

        try await model.switchWorkspace(to: "w1")

        #expect(model.tracker?.workspace.id == "w1")
        #expect(model.tracker !== first)
    }

    @Test func newEntryPutsTheCursorInTheTimer() async throws {
        let model = try await signedIn(trackingServer(requireProject: false, current: Locked("w2")))
        model.section = .reports

        model.focusDescription()

        #expect(model.section == .timer)
        #expect(model.wantsDescriptionFocus)
    }

    // MARK: - Offline

    @Test func opensWithoutTheServerFromWhatThisMacKept() async throws {
        let online = Locked(true)
        let base = trackingServer(requireProject: false, current: Locked("w2"))
        let server = StubServer { path in
            guard online.withLock({ $0 }) else { throw URLError(.notConnectedToInternet) }
            return try base.handler(path)
        }
        let store = try LocalStore()
        let tokens = MemoryTokenStore()
        let first = model(server, tokens: tokens, store: store)
        await first.start()
        try await first.signIn(email: "ana@example.com", password: "correct horse")
        for _ in 0..<200 where first.tracker?.state != .loaded {
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(first.tracker?.running?.id == "e1")

        // The next launch, without a network.
        online.withLock { $0 = false }
        let again = model(server, tokens: tokens, store: store)
        await again.start()
        for _ in 0..<200 where again.tracker?.state != .loaded {
            try await Task.sleep(for: .milliseconds(10))
        }

        #expect(again.phase == .signedIn)
        #expect(again.me?.fullName == "Ana Kovač")
        #expect(again.tracker?.running?.id == "e1")
        #expect(again.tracker?.entries.count == 2)
    }

    @Test func signingOutAsksFirstWhenChangesWait() async throws {
        let online = Locked(true)
        let base = trackingServer(requireProject: false, current: Locked("w2"))
        let server = StubServer { path in
            guard online.withLock({ $0 }) else { throw URLError(.notConnectedToInternet) }
            return try base.handler(path)
        }
        let model = model(server)
        await model.start()
        try await model.signIn(email: "ana@example.com", password: "correct horse")
        for _ in 0..<200 where model.tracker?.state != .loaded {
            try await Task.sleep(for: .milliseconds(10))
        }
        online.withLock { $0 = false }
        await model.stop()
        #expect(model.waitingChanges == 1)
        #expect(model.tracker?.running == nil)

        model.requestSignOut()

        #expect(model.confirmingSignOut)
        #expect(model.phase == .signedIn)
    }

    // MARK: - Choosing the server

    @Test func ownServerIsCheckedChosenAndRemembered() async {
        let own = StubServer { path in path == "/health" ? (200, "Healthy") : (404, "{}") }
        let cloud = URL(string: "https://cloud.example.com")!
        let model = AppModel(tokens: MemoryTokenStore(), urlSession: own.session, defaults: defaults, cloud: cloud, openStore: { _, _ in try LocalStore() })
        await model.start()
        #expect(model.isCloud)

        #expect(await model.isSpindleServer(own.url))
        model.useServer(own.url)

        #expect(model.server == own.url)
        #expect(!model.isCloud)
        #expect(!model.supportsPasskeys)
        #expect(model.webURL("/plan") == own.url.appending(path: "/plan"))
        // The next launch starts on the same server, and Spindle Cloud can be chosen again.
        let next = AppModel(tokens: MemoryTokenStore(), urlSession: own.session, defaults: defaults, cloud: cloud, openStore: { _, _ in try LocalStore() })
        #expect(next.server == own.url)
        await next.start()
        next.useCloud()
        #expect(AppModel(tokens: MemoryTokenStore(), defaults: defaults, cloud: cloud).server == cloud)
    }

    @Test func somethingElseAtTheAddressIsNotASpindleServer() async {
        let other = StubServer { _ in (404, "{}") }
        let model = model(other)

        #expect(!(await model.isSpindleServer(other.url)))
    }

    /// Someone who skipped the version that remembers the address still moves along, from the build's former list.
    @Test func aSignInAtAFormerCloudAddressMovesAlongToo() {
        let old = URL(string: "https://old.example.com")!
        let new = URL(string: "https://new.example.com")!
        let tokens = MemoryTokenStore()
        tokens.write("stored", account: old.absoluteString)
        defaults.set("u1", forKey: "account:\(old.absoluteString)")

        let model = AppModel(tokens: tokens, defaults: defaults, cloud: new, formerClouds: [old], openStore: { _, _ in try LocalStore() })

        #expect(model.client.session.hasStoredSession)
        #expect(tokens.read(account: old.absoluteString) == nil)
        #expect(defaults.string(forKey: "account:\(new.absoluteString)") == "u1")
    }

    @Test func whenSpindleCloudMovesTheSignInMovesAlong() {
        let old = URL(string: "https://old.example.com")!
        let new = URL(string: "https://new.example.com")!
        let tokens = MemoryTokenStore()
        tokens.write("stored", account: old.absoluteString)
        defaults.set(old.absoluteString, forKey: "cloudServer")
        defaults.set("u1", forKey: "account:\(old.absoluteString)")

        let model = AppModel(tokens: tokens, defaults: defaults, cloud: new, openStore: { _, _ in try LocalStore() })

        #expect(model.server == new)
        #expect(model.client.session.hasStoredSession)
        #expect(tokens.read(account: old.absoluteString) == nil)
        #expect(defaults.string(forKey: "account:\(new.absoluteString)") == "u1")
        #expect(defaults.string(forKey: "account:\(old.absoluteString)") == nil)
    }
}
