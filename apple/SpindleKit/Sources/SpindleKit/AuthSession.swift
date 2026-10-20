import Foundation
import HTTPTypes
import OpenAPIRuntime
import OpenAPIURLSession
import SpindleAPI

/// A signed-in device: a short-lived access token in memory, renewed with the refresh token kept in the Keychain
/// (or another ``TokenStore``).
///
/// Renewals happen one at a time: calls that need a token while one is being renewed wait for it, since every
/// refresh token can be used once and a second use would end the session.
public actor AuthSession {
    public nonisolated let server: URL
    private let tokenStore: any TokenStore
    private let deviceName: String
    private let raw: Client
    private var accessToken: String?
    private var accessTokenExpiresAt = Date.distantPast
    private var renewal: Task<String, Error>?
    private let ended: AsyncStream<Void>.Continuation

    /// Signals that the server ended the session, e.g. it was signed out on another device, and the app should show
    /// sign-in again. Signing out here is not announced.
    public nonisolated let sessionEnded: AsyncStream<Void>

    public init(server: URL, tokens: any TokenStore = Keychain(), deviceName: String = DeviceName.current, transport: any ClientTransport = URLSessionTransport()) {
        self.server = server
        self.tokenStore = tokens
        self.deviceName = deviceName
        self.raw = Client(serverURL: server, configuration: .spindle, transport: transport, middlewares: [ProblemMiddleware()])
        (sessionEnded, ended) = AsyncStream.makeStream(of: Void.self, bufferingPolicy: .bufferingNewest(1))
    }

    /// A session whose calls go through a given URL session, e.g. one that answers from a stub in tests.
    public init(server: URL, tokens: any TokenStore = Keychain(), deviceName: String = DeviceName.current, urlSession: URLSession) {
        self.init(server: server, tokens: tokens, deviceName: deviceName, transport: URLSessionTransport(configuration: .init(session: urlSession)))
    }

    /// Whether this device holds a session for the server, from an earlier sign-in.
    public nonisolated var hasStoredSession: Bool {
        tokenStore.read(account: server.absoluteString) != nil
    }

    public func signIn(email: String, password: String) async throws {
        do {
            let tokens = try await raw.login(body: .json(.init(email: email, password: password, deviceName: deviceName))).ok.body.json
            store(tokens)
        } catch {
            throw error.asSpindleError
        }
    }

    /// An opaque, single-use challenge token replaces the browser cookie for native sign-in.
    public func passkeyOptions() async throws -> Components.Schemas.NativePasskeyOptionsResponse {
        do { return try await raw.nativePasskeyOptions().ok.body.json }
        catch { throw error.asSpindleError }
    }

    public func signInWithPasskey(credentialJSON: String, challengeToken: String) async throws {
        do {
            let tokens = try await raw.nativePasskeyLogin(body: .json(.init(challengeToken: challengeToken,
                credentialJson: credentialJSON, deviceName: deviceName))).ok.body.json
            store(tokens)
        } catch { throw error.asSpindleError }
    }

    /// Finishes signing in with Google or Apple: trades the code the browser came back with for tokens, which are kept
    /// as after a password sign-in. Says whether the server made a new account.
    @discardableResult
    public func signInExternally(code: String, codeVerifier: String) async throws -> Bool {
        do {
            let tokens = try await raw.exchangeExternalSignIn(body: .json(.init(code: code, codeVerifier: codeVerifier,
                deviceName: deviceName))).ok.body.json
            store(accessToken: tokens.accessToken, expiresAt: tokens.accessTokenExpiresAt, refreshToken: tokens.refreshToken)
            return tokens.newAccount
        } catch {
            throw error.asSpindleError
        }
    }

    /// What the server offers, read before anyone signs in: whether anyone can create an account, and the terms to
    /// agree to when they do.
    public func serverInfo() async throws -> ServerInfo {
        do {
            return try await raw.getServer().ok.body.json
        } catch {
            throw error.asSpindleError
        }
    }

    public func register(fullName: String, email: String, password: String, acceptTerms: Bool = false, timeZone: String = TimeZone.current.identifier) async throws {
        do {
            let body = Components.Schemas.RegisterRequest(
                email: email, password: password, fullName: fullName, timeZone: timeZone, deviceName: deviceName, acceptTerms: acceptTerms)
            let tokens = try await raw.register(body: .json(body)).ok.body.json
            store(tokens)
        } catch {
            throw error.asSpindleError
        }
    }

    public func requestPasswordReset(email: String) async throws {
        do {
            _ = try await raw.forgotPassword(body: .json(.init(email: email))).accepted
        } catch {
            throw error.asSpindleError
        }
    }

    /// Signs this device out. The server forgets the session; if it cannot be reached, the device forgets it anyway.
    public func signOut() async {
        if let refreshToken = tokenStore.read(account: server.absoluteString) {
            _ = try? await raw.logout(body: .json(.init(refreshToken: refreshToken)))
        }
        // The app asked for this, so there is nothing to announce.
        forget(announce: false)
    }

    /// A valid access token, renewed first if it is about to expire.
    public func validAccessToken() async throws -> String {
        if let accessToken, accessTokenExpiresAt.timeIntervalSinceNow > 60 {
            return accessToken
        }
        return try await renew()
    }

    /// The server turned the access token down; get a new one, unless another call already did.
    func renewAfterRejection(of rejected: String) async throws -> String {
        if let accessToken, accessToken != rejected, accessTokenExpiresAt.timeIntervalSinceNow > 60 {
            return accessToken
        }
        return try await renew()
    }

    private func renew() async throws -> String {
        if let renewal {
            return try await renewal.value
        }
        let task = Task { () throws -> String in
            guard let refreshToken = tokenStore.read(account: server.absoluteString) else {
                throw APIError.sessionEnded
            }
            do {
                let tokens = try await raw.refreshTokens(body: .json(.init(refreshToken: refreshToken))).ok.body.json
                store(tokens)
                return tokens.accessToken
            } catch {
                // Turned down: the session was ended, e.g. signed out elsewhere or the password changed.
                if let rejected = error.asSpindleError as? APIError, rejected.status == 401 {
                    forget()
                    throw APIError.sessionEnded
                }
                throw error.asSpindleError
            }
        }
        renewal = task
        defer { renewal = nil }
        return try await task.value
    }

    private func store(_ tokens: Components.Schemas.AuthTokensResponse) {
        store(accessToken: tokens.accessToken, expiresAt: tokens.accessTokenExpiresAt, refreshToken: tokens.refreshToken)
    }

    private func store(accessToken: String, expiresAt: Date, refreshToken: String) {
        self.accessToken = accessToken
        accessTokenExpiresAt = expiresAt
        tokenStore.write(refreshToken, account: server.absoluteString)
    }

    private func forget(announce: Bool = true) {
        accessToken = nil
        accessTokenExpiresAt = .distantPast
        tokenStore.delete(account: server.absoluteString)
        if announce {
            ended.yield()
        }
    }
}

/// Signs every API call with the session's access token, and renews the token once if the server rejects it.
struct AuthMiddleware: ClientMiddleware {
    let session: AuthSession

    func intercept(
        _ request: HTTPRequest,
        body: HTTPBody?,
        baseURL: URL,
        operationID: String,
        next: @Sendable (HTTPRequest, HTTPBody?, URL) async throws -> (HTTPResponse, HTTPBody?)
    ) async throws -> (HTTPResponse, HTTPBody?) {
        let token = try await session.validAccessToken()
        var signed = request
        signed.headerFields[.authorization] = "Bearer \(token)"
        let (response, responseBody) = try await next(signed, body, baseURL)
        guard response.status == .unauthorized else {
            return (response, responseBody)
        }

        let renewed = try await session.renewAfterRejection(of: token)
        signed.headerFields[.authorization] = "Bearer \(renewed)"
        return try await next(signed, body, baseURL)
    }
}
