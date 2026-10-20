import Foundation
import HTTPTypes
import Observation
import OpenAPIRuntime

/// What the server says changed; see SyncChange in the API. It says what, not how: the app loads it again.
public struct SyncChange: Decodable, Sendable, Equatable {
    /// `entries`, `catalog`, `team`, `settings`, `me` or `savedReports`.
    public let topic: String
    public let workspaceId: String?
    /// Whose entries or account changed, where that matters.
    public let userId: String?

    public init(topic: String, workspaceId: String?, userId: String?) {
        self.topic = topic
        self.workspaceId = workspaceId
        self.userId = userId
    }
}

/// Keeps the app current while other devices and teammates make changes, over the API's SignalR hub
/// (`/api/hubs/sync`) with the JSON hub protocol on a WebSocket.
///
/// Without a connection the app works as before; the connection is tried again and again, waiting longer each
/// time, and after a break the app is told to load everything again, since news may have been missed.
@MainActor
@Observable
public final class LiveSync {
    public enum State: Equatable {
        case disconnected, connecting, connected
    }

    public private(set) var state = State.disconnected

    /// Something changed elsewhere.
    @ObservationIgnored public var onChange: ((SyncChange) -> Void)?
    /// Connected again after a break.
    @ObservationIgnored public var onReconnect: (() -> Void)?

    private let client: SpindleClient
    private let urlSession: URLSession
    @ObservationIgnored private var runner: Task<Void, Never>?
    @ObservationIgnored private var backoff: Task<Void, Never>?
    @ObservationIgnored private var socket: URLSessionWebSocketTask?
    @ObservationIgnored private var wanted: String?
    @ObservationIgnored private var watched: String?
    @ObservationIgnored private var invocations: [String: CheckedContinuation<Bool, Never>] = [:]
    @ObservationIgnored private var nextInvocation = 0
    @ObservationIgnored private var lastHeard = Date()

    public init(client: SpindleClient, urlSession: URLSession = URLSession(configuration: .default)) {
        self.client = client
        self.urlSession = urlSession
    }

    /// Connects, and keeps connecting again after every break, until stopped.
    public func start() {
        guard runner == nil else { return }
        runner = Task { [weak self] in
            var attempt = 0
            while !Task.isCancelled {
                guard let self else { return }
                do {
                    try await self.connect(reconnecting: attempt > 0)
                    attempt = 1
                } catch {
                    attempt += 1
                }
                self.hangUp()
                guard !Task.isCancelled else { return }
                // A while before trying again, longer after each failure: the server may be restarting.
                let delay = [1.0, 2, 5, 10, 30][min(max(attempt - 1, 0), 4)] * Double.random(in: 0.8...1.2)
                await self.pause(for: delay)
            }
        }
    }

    public func stop() {
        runner?.cancel()
        runner = nil
        backoff?.cancel()
        hangUp()
    }

    /// Tries again at once instead of waiting, e.g. when the network comes back.
    public func reconnectNow() {
        backoff?.cancel()
        if state == .connected {
            // The connection may have died with the network; if it is fine, the server answers the ping.
            send(#"{"type":6}"#)
        }
    }

    /// Follows a workspace, usually the one on screen, instead of the one followed before.
    public func watch(_ workspaceId: String) {
        wanted = workspaceId
        guard state == .connected, watched != workspaceId else { return }
        Task { await self.follow() }
    }

    // MARK: - The connection

    private func connect(reconnecting: Bool) async throws {
        state = .connecting
        let token = try await client.session.validAccessToken()
        let negotiation = try await negotiate(token: token)

        var components = URLComponents(url: client.session.server.appending(path: "api/hubs/sync"), resolvingAgainstBaseURL: false)!
        components.scheme = components.scheme == "https" ? "wss" : "ws"
        components.queryItems = [
            URLQueryItem(name: "id", value: negotiation.connectionToken),
            URLQueryItem(name: "access_token", value: token),
        ]
        let socket = urlSession.webSocketTask(with: components.url!)
        self.socket = socket
        socket.resume()

        // The handshake: the protocol this client speaks, answered with an empty record or an error.
        try await socket.send(.string(#"{"protocol":"json","version":1}"# + Self.separator))
        let answer = try await receiveRecords(from: socket)
        if let error = answer.first?["error"] as? String {
            throw LiveSyncError(message: "The server turned the connection down: \(error)")
        }

        state = .connected
        lastHeard = Date()
        client.syncConnection.id = negotiation.connectionId
        watched = nil
        // Not awaited here: the answer to Watch arrives through the loop below.
        Task { await self.follow() }
        if reconnecting {
            onReconnect?()
        }

        let keepAlive = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(15))
                guard let self, !Task.isCancelled else { return }
                // The server pings every 15 seconds; hearing nothing for 30 means the connection is gone.
                if Date().timeIntervalSince(self.lastHeard) > 30 {
                    socket.cancel(with: .goingAway, reason: nil)
                    return
                }
                self.send(#"{"type":6}"#)
            }
        }
        defer { keepAlive.cancel() }

        while true {
            for record in try await receiveRecords(from: socket) {
                lastHeard = Date()
                try handle(record)
            }
        }
    }

    private struct Negotiation: Decodable {
        let connectionId: String
        let connectionToken: String
    }

    private func negotiate(token: String) async throws -> Negotiation {
        var components = URLComponents(url: client.session.server.appending(path: "api/hubs/sync/negotiate"), resolvingAgainstBaseURL: false)!
        components.queryItems = [URLQueryItem(name: "negotiateVersion", value: "1")]
        var request = URLRequest(url: components.url!)
        request.httpMethod = "POST"
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        let (data, response) = try await urlSession.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw APIError(status: (response as? HTTPURLResponse)?.statusCode ?? 0, title: "The server wouldn't open live sync.")
        }
        return try JSONDecoder().decode(Negotiation.self, from: data)
    }

    /// Follows the workspace wanted, leaving the one followed before.
    private func follow() async {
        guard let wanted, watched != wanted else { return }
        if let watched {
            send(Self.record(["type": 1, "target": "Unwatch", "arguments": [watched]]))
            self.watched = nil
        }
        if await invoke("Watch", [wanted]) {
            watched = wanted
        }
    }

    private func handle(_ record: [String: Any]) throws {
        switch record["type"] as? Int {
        case 1:
            // A call from the server.
            guard record["target"] as? String == "Changed", let argument = (record["arguments"] as? [Any])?.first,
                  let data = try? JSONSerialization.data(withJSONObject: argument),
                  let change = try? JSONDecoder().decode(SyncChange.self, from: data)
            else { return }
            onChange?(change)
        case 3:
            // The answer to a call of ours.
            if let id = record["invocationId"] as? String, let waiter = invocations.removeValue(forKey: id) {
                waiter.resume(returning: record["error"] == nil && (record["result"] as? Bool ?? true))
            }
        case 7:
            throw LiveSyncError(message: "The server closed the connection: \(record["error"] as? String ?? "no reason given").")
        default:
            break
        }
    }

    /// Calls a hub method and waits for its answer, false if it failed or the connection went.
    private func invoke(_ target: String, _ arguments: [String]) async -> Bool {
        nextInvocation += 1
        let id = String(nextInvocation)
        return await withCheckedContinuation { continuation in
            invocations[id] = continuation
            send(Self.record(["type": 1, "invocationId": id, "target": target, "arguments": arguments]))
        }
    }

    private func send(_ text: String) {
        socket?.send(.string(text.hasSuffix(Self.separator) ? text : text + Self.separator)) { _ in }
    }

    /// Reads the next message and splits it into its records.
    private func receiveRecords(from socket: URLSessionWebSocketTask) async throws -> [[String: Any]] {
        let text: String
        switch try await socket.receive() {
        case .string(let string): text = string
        case .data(let data): text = String(decoding: data, as: UTF8.self)
        @unknown default: return []
        }
        return text.split(separator: Character(Self.separator), omittingEmptySubsequences: true).compactMap { part in
            (try? JSONSerialization.jsonObject(with: Data(part.utf8))) as? [String: Any]
        }
    }

    private func hangUp() {
        socket?.cancel(with: .goingAway, reason: nil)
        socket = nil
        client.syncConnection.id = nil
        watched = nil
        for waiter in invocations.values {
            waiter.resume(returning: false)
        }
        invocations = [:]
        if state != .disconnected {
            state = .disconnected
        }
    }

    private func pause(for seconds: Double) async {
        let wait = Task<Void, Never> { try? await Task.sleep(for: .seconds(seconds)) }
        backoff = wait
        await wait.value
    }

    /// Every record of the protocol ends with this character.
    private static let separator = "\u{1e}"

    private static func record(_ object: [String: Any]) -> String {
        let data = (try? JSONSerialization.data(withJSONObject: object)) ?? Data("{}".utf8)
        return String(decoding: data, as: UTF8.self) + separator
    }
}

struct LiveSyncError: Error {
    let message: String
}

/// This device's live sync connection. API calls name it, so the server leaves this device out when it tells the
/// apps about the change: it has it already.
public final class SyncConnection: @unchecked Sendable {
    private let lock = NSLock()
    private var value: String?

    public init() {}

    public var id: String? {
        get { lock.withLock { value } }
        set { lock.withLock { value = newValue } }
    }
}

/// Adds the header that names this device's sync connection.
struct SyncConnectionMiddleware: ClientMiddleware {
    let connection: SyncConnection

    func intercept(
        _ request: HTTPRequest,
        body: HTTPBody?,
        baseURL: URL,
        operationID: String,
        next: @Sendable (HTTPRequest, HTTPBody?, URL) async throws -> (HTTPResponse, HTTPBody?)
    ) async throws -> (HTTPResponse, HTTPBody?) {
        var request = request
        if let id = connection.id {
            request.headerFields[HTTPField.Name("X-Sync-Connection")!] = id
        }
        return try await next(request, body, baseURL)
    }
}
