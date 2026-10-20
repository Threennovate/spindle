import Foundation
import SpindleAPI

public typealias DeviceSession = Components.Schemas.SessionResponse

extension DeviceSession: Identifiable {}

/// How a person likes times shown, and their Pomodoro, the same on every device.
public struct PreferenceChanges: Hashable, Sendable {
    public var weekStart: Int
    public var dateFormat: String
    public var timeFormat: String
    public var durationFormat: String
    public var pomodoroEnabled: Bool
    public var pomodoroMinutes: Int
    public var pomodoroBreakMinutes: Int
    public var pomodoroAutoStop: Bool

    public init(_ preferences: Preferences) {
        weekStart = Int(preferences.weekStart)
        dateFormat = preferences.dateFormat
        timeFormat = preferences.timeFormat
        durationFormat = preferences.durationFormat
        pomodoroEnabled = preferences.pomodoroEnabled
        pomodoroMinutes = Int(preferences.pomodoroMinutes)
        pomodoroBreakMinutes = Int(preferences.pomodoroBreakMinutes)
        pomodoroAutoStop = preferences.pomodoroAutoStop
    }
}

extension SpindleClient {
    public func passkeys() async throws -> [Components.Schemas.PasskeyResponse] {
        try await call { try await api.listPasskeys().ok.body.json }
    }

    /// An account made with Google or Apple has no password to give; the server then wants this device to have signed
    /// in within the last few minutes instead.
    public func passkeyCreationOptions(password: String?) async throws -> PasskeyRegistrationOptions {
        let response = try await call { try await api.passkeyCreationOptions(body: .json(.init(password: password))).ok.body.json }
        return try PasskeyRegistrationOptions(json: response.optionsJson)
    }

    public func savePasskey(name: String, credentialJSON: String) async throws {
        try await call { _ = try await api.savePasskey(body: .json(.init(credentialJson: credentialJSON, name: name))).ok }
    }

    public func removePasskey(_ id: String) async throws {
        try await call { _ = try await api.removePasskey(path: .init(credentialId: id)).noContent }
    }

    public func updateProfile(fullName: String, timeZone: String) async throws -> Me {
        try await call {
            try await api.updateProfile(body: .json(.init(fullName: fullName.trimmingCharacters(in: .whitespacesAndNewlines), timeZone: timeZone))).ok.body.json
        }
    }

    public func updatePreferences(_ changes: PreferenceChanges) async throws -> Me {
        let body = Components.Schemas.PreferencesRequest(
            weekStart: Int32(changes.weekStart), dateFormat: changes.dateFormat, timeFormat: changes.timeFormat,
            durationFormat: changes.durationFormat, pomodoroEnabled: changes.pomodoroEnabled,
            pomodoroMinutes: Int32(changes.pomodoroMinutes), pomodoroBreakMinutes: Int32(changes.pomodoroBreakMinutes),
            pomodoroAutoStop: changes.pomodoroAutoStop)
        return try await call { try await api.updatePreferences(body: .json(body)).ok.body.json }
    }

    /// Changes the password; the other devices are signed out, this one stays signed in.
    public func changePassword(current: String, new: String) async throws {
        try await call { _ = try await api.changePassword(body: .json(.init(currentPassword: current, newPassword: new))).noContent }
    }

    /// Where the person is signed in, this device included.
    public func sessions() async throws -> [DeviceSession] {
        try await call { try await api.listSessions().ok.body.json }
    }

    public func revokeSession(_ id: String) async throws {
        try await call { _ = try await api.revokeSession(path: .init(sessionId: id)).noContent }
    }

    public func revokeOtherSessions() async throws {
        try await call { _ = try await api.revokeOtherSessions().noContent }
    }

    /// Tells the server where APNs reaches this iPhone's Live Activity, for this session. Returns whether the server
    /// sends pushes; one without APNs settings keeps nothing.
    public func registerPushDevice(sandbox: Bool, liveActivityToken: String?, liveActivityEntryId: String?) async throws -> Bool {
        let body = Components.Schemas.PushDeviceRequest(
            sandbox: sandbox, liveActivityToken: liveActivityToken, liveActivityEntryId: liveActivityEntryId)
        return try await call { try await api.registerPushDevice(body: .json(body)).ok.body.json }
    }

    /// Deletes the account for good; the password confirms it, or for an account without one, a sign-in on this device
    /// within the last few minutes.
    public func deleteAccount(password: String?) async throws {
        try await call { _ = try await api.deleteAccount(body: .json(.init(password: password))).noContent }
    }

    /// The file name a `Content-Disposition` header gives, as a bare name: the apps save the file under it, so a
    /// name like `../../Library/x` from a server must not reach outside the folder it is saved in.
    static func attachmentFileName(_ contentDisposition: String?) -> String? {
        guard let name = contentDisposition?.firstMatch(of: /filename="?([^";]+)"?/).map({ String($0.1) }) else { return nil }
        let bare = name.split(whereSeparator: { $0 == "/" || $0 == "\\" }).last.map(String.init)?.trimmingCharacters(in: .whitespaces) ?? ""
        return bare.isEmpty || bare.allSatisfy({ $0 == "." }) ? nil : bare
    }

    /// A copy of the profile and every entry, in every workspace, as the JSON file the server makes, byte for byte.
    public func exportData() async throws -> (data: Data, fileName: String) {
        var request = URLRequest(url: session.server.appending(path: "api/me/export"))
        func send() async throws -> (Data, HTTPURLResponse) {
            request.setValue("Bearer \(try await session.validAccessToken())", forHTTPHeaderField: "Authorization")
            let (data, response) = try await urlSession.data(for: request)
            return (data, response as! HTTPURLResponse)
        }
        do {
            var (data, response) = try await send()
            if response.statusCode == 401 {
                // The token was turned down; renew it once, as every other call does.
                _ = try await session.renewAfterRejection(of: request.value(forHTTPHeaderField: "Authorization")?.replacingOccurrences(of: "Bearer ", with: "") ?? "")
                (data, response) = try await send()
            }
            guard response.statusCode == 200 else {
                throw APIError(status: response.statusCode)
            }
            let today = Date().formatted(.iso8601.year().month().day())
            return (data, Self.attachmentFileName(response.value(forHTTPHeaderField: "Content-Disposition")) ?? "spindle-data-\(today).json")
        } catch {
            throw error.asSpindleError
        }
    }
}
