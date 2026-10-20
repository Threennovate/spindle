import Foundation
import Security

/// Keeps the refresh token in the Keychain, one per server, so a signed-in device stays signed in and no other app
/// can read the token.
public struct Keychain: Sendable {
    public let service: String

    public init(service: String = "com.spindletrack.refresh-token") {
        self.service = service
    }

    public func read(account: String) -> String? {
        var query = baseQuery(account: account)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else {
            return nil
        }
        return String(data: data, encoding: .utf8)
    }

    public func write(_ value: String, account: String) {
        let data = Data(value.utf8)
        let update = [kSecValueData as String: data] as CFDictionary
        if SecItemUpdate(baseQuery(account: account) as CFDictionary, update) == errSecItemNotFound {
            var item = baseQuery(account: account)
            item[kSecValueData as String] = data
            // Readable once the Mac is unlocked, and never copied to other devices.
            item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            SecItemAdd(item as CFDictionary, nil)
        }
    }

    public func delete(account: String) {
        SecItemDelete(baseQuery(account: account) as CFDictionary)
    }

    private func baseQuery(account: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }
}
