import CryptoKit
import Foundation

/// The repositories a person told Spindle to ignore: none of their prompts and runs show anywhere, nor are they
/// uploaded, though their prompts still end the attention before them, as a folder outside git's do. The list syncs
/// between the person's devices as hashes alone (`hash(_:account:)`), so the server never learns which they are.
public struct IgnoredRepositories: Sendable, Hashable {
    /// The person's account ID, which keys the hashes.
    public let account: String
    /// The hashes of the ignored repositories' identities.
    public let hashes: Set<String>

    public init(account: String, hashes: Set<String>) {
        self.account = account.lowercased()
        self.hashes = hashes
    }

    /// Nothing ignored.
    public static let none = IgnoredRepositories(account: "", hashes: [])

    public var isEmpty: Bool { hashes.isEmpty }

    public func contains(_ repository: ActivityRepository) -> Bool {
        repository.identity.map(contains(identity:)) ?? false
    }

    public func contains(identity: String) -> Bool {
        !hashes.isEmpty && hashes.contains(Self.hash(identity, account: account))
    }

    /// This repository's hash for this person.
    public func hash(of identity: String) -> String {
        Self.hash(identity, account: account)
    }

    /// HMAC-SHA256 with the account ID (lower case, with hyphens) as the key, over the repository's identity, in
    /// lower-case hex: the same on the Mac, Windows and the server (tests/vectors/ignored-repositories.json), and
    /// different for another person.
    public static func hash(_ identity: String, account: String) -> String {
        let key = SymmetricKey(data: Data(account.lowercased().utf8))
        return HMAC<SHA256>.authenticationCode(for: Data(identity.utf8), using: key).map { String(format: "%02x", $0) }.joined()
    }
}
