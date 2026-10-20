import Foundation

/// Where a device keeps its refresh token between launches, one per server.
public protocol TokenStore: Sendable {
    func read(account: String) -> String?
    func write(_ value: String, account: String)
    func delete(account: String)
}

extension Keychain: TokenStore {}

/// Keeps tokens for as long as the app runs, for automated runs that must not touch the Keychain.
public final class MemoryTokenStore: TokenStore, @unchecked Sendable {
    private let lock = NSLock()
    private var values: [String: String] = [:]

    public init() {}

    public func read(account: String) -> String? {
        lock.withLock { values[account] }
    }

    public func write(_ value: String, account: String) {
        lock.withLock { values[account] = value }
    }

    public func delete(account: String) {
        _ = lock.withLock { values.removeValue(forKey: account) }
    }
}
