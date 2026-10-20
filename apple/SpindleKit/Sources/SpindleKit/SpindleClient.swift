@_exported import SpindleAPI
import Foundation
import OpenAPIRuntime
import OpenAPIURLSession

public typealias Me = Components.Schemas.MeResponse
public typealias WorkspaceSummary = Components.Schemas.WorkspaceSummary
public typealias Preferences = Components.Schemas.PreferencesResponse
public typealias WorkspaceRole = Components.Schemas.WorkspaceRole
public typealias OrganizationRole = Components.Schemas.OrganizationRole
public typealias OrganizationSummary = Components.Schemas.OrganizationSummary
public typealias ServerInfo = Components.Schemas.ServerResponse

/// The API as the apps use it: every call signed in, and every failure an ``APIError`` or a ``ConnectionError``.
public final class SpindleClient: Sendable {
    public let session: AuthSession
    /// This device's live sync connection, which every call names; see ``LiveSync``.
    public let syncConnection = SyncConnection()
    let api: Client
    /// For the few downloads the generated client can't take as they are, e.g. the data export.
    let urlSession: URLSession

    public init(session: AuthSession, transport: any ClientTransport = URLSessionTransport(), urlSession: URLSession = .shared) {
        self.session = session
        self.urlSession = urlSession
        self.api = Client(
            serverURL: session.server,
            configuration: .spindle,
            transport: transport,
            // The problem middleware sees the final answer, after the auth middleware has renewed the token if needed.
            middlewares: [ProblemMiddleware(), AuthMiddleware(session: session), SyncConnectionMiddleware(connection: syncConnection)])
    }

    /// A client whose calls go through a given URL session, e.g. one that answers from a stub in tests.
    public convenience init(session: AuthSession, urlSession: URLSession) {
        self.init(session: session, transport: URLSessionTransport(configuration: .init(session: urlSession)), urlSession: urlSession)
    }

    /// Runs a call and turns whatever went wrong into the app's own errors.
    func call<T: Sendable>(_ operation: () async throws -> T) async throws -> T {
        do {
            return try await operation()
        } catch {
            throw error.asSpindleError
        }
    }

    // MARK: - Account

    public func me() async throws -> Me {
        try await call { try await api.getMe().ok.body.json }
    }

    /// Makes another workspace the one the apps show, on every device.
    public func switchWorkspace(to workspaceId: String) async throws {
        try await call { _ = try await api.setDefaultWorkspace(body: .json(.init(workspaceId: workspaceId))).noContent }
    }

    /// Creates a workspace in the organization of the current one, which the person must own or administer. They
    /// become its admin, and it becomes their current workspace.
    public func createWorkspace(name: String) async throws -> WorkspaceSummary {
        try await call { try await api.createWorkspace(body: .json(.init(name: name))).created.body.json }
    }
}
