import CryptoKit
import Foundation
import SpindleAPI

public typealias SignInProvider = Components.Schemas.SignInProviderResponse

extension Me {
    /// An account made with Google or Apple has no password until the person sets one. Older servers don't say, and
    /// every account there has one.
    public var signsInWithPassword: Bool {
        hasPassword ?? true
    }
}

/// Signing in with Google or Apple goes through the server: the browser visits the provider and comes back to the
/// server, which sends the app to `spindle://auth/callback` with a one-time code. The app trades the code for the
/// same tokens as a password sign-in.
///
/// The code is tied to this sign-in with PKCE: the app keeps a random verifier and sends the server only its hash, so
/// another app that catches the redirect can't use the code.
public enum ExternalSignIn {
    public static let callbackScheme = "spindle"
    public static let redirectURI = "spindle://auth/callback"

    /// A verifier and the challenge sent in its place, for one sign-in.
    public struct PKCE: Sendable, Equatable {
        public let verifier: String
        public let challenge: String

        /// A new verifier from 32 random bytes, 43 characters in base64url.
        public init() {
            var generator = SystemRandomNumberGenerator()
            self.init(verifier: Data((0..<32).map { _ in UInt8.random(in: .min ... .max, using: &generator) }).base64URL)
        }

        public init(verifier: String) {
            self.verifier = verifier
            challenge = Data(SHA256.hash(data: Data(verifier.utf8))).base64URL
        }
    }

    /// Where the browser starts: the server's page that sends it on to the provider. The terms count as agreed to,
    /// since the buttons say that continuing agrees to them; the time zone is the new account's, if one is made.
    public static func startURL(server: URL, provider: String, challenge: String,
                                timeZone: String = TimeZone.current.identifier, invitation: String? = nil) -> URL {
        var query = [("redirectUri", redirectURI), ("codeChallenge", challenge), ("timeZone", timeZone), ("acceptTerms", "true")]
        if let invitation, !invitation.isEmpty {
            query.append(("invitation", invitation))
        }
        var components = URLComponents(url: server.appending(path: "api/auth/external/\(provider)/start"), resolvingAgainstBaseURL: false)!
        // Encoded strictly, so the redirect's `:` and `/` and a zone's `/` reach the server as they are.
        components.percentEncodedQuery = query.map { "\($0)=\(encode($1))" }.joined(separator: "&")
        return components.url!
    }

    private static func encode(_ value: String) -> String {
        value.addingPercentEncoding(withAllowedCharacters: .alphanumerics.union(CharacterSet(charactersIn: "-._~"))) ?? value
    }

    public enum Callback: Equatable, Sendable {
        case code(String)
        /// The person turned it down at the provider, or closed the page.
        case cancelled
        /// Neither a code nor a cancel, e.g. a redirect that isn't the server's.
        case failed
    }

    /// What the server sent the browser back with.
    public static func callback(_ url: URL) -> Callback {
        guard url.scheme == callbackScheme, url.host() == "auth", url.path() == "/callback",
              let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems
        else { return .failed }
        if let code = items.first(where: { $0.name == "code" })?.value, !code.isEmpty {
            return .code(code)
        }
        return items.first(where: { $0.name == "error" })?.value == "cancelled" ? .cancelled : .failed
    }
}

public enum ExternalSignInError: LocalizedError, Sendable {
    case failed

    public var errorDescription: String? {
        "Signing in didn't finish. Please try again, or sign in with your email."
    }
}
