import Foundation

public enum PasskeyError: LocalizedError, Sendable {
    case invalidOptions
    case unavailable
    case cancelled
    case busy

    public var errorDescription: String? {
        switch self {
        case .invalidOptions: "The passkey request was invalid. Please try again."
        case .unavailable: "Passkeys are unavailable. Check that this app can access passkeys for Spindle, or sign in with your password."
        case .cancelled: "The passkey request was cancelled. You can try again."
        case .busy: "A passkey request is already in progress."
        }
    }
}

public struct PasskeyAssertionOptions: Sendable {
    public let challenge: Data
    public let relyingParty: String

    public init(json: String) throws {
        struct Options: Decodable { let challenge: String; let rpId: String }
        let options = try JSONDecoder().decode(Options.self, from: Data(json.utf8))
        guard let challenge = Data(base64URL: options.challenge), challenge.count >= 16, !options.rpId.isEmpty else { throw PasskeyError.invalidOptions }
        self.challenge = challenge
        relyingParty = options.rpId
    }
}

public struct PasskeyRegistrationOptions: Sendable {
    public let challenge: Data
    public let relyingParty: String
    public let userID: Data
    public let userName: String

    public init(json: String) throws {
        struct Options: Decodable {
            struct RP: Decodable { let id: String }
            struct User: Decodable { let id: String; let name: String }
            let challenge: String; let rp: RP; let user: User
        }
        let options = try JSONDecoder().decode(Options.self, from: Data(json.utf8))
        guard let challenge = Data(base64URL: options.challenge), challenge.count >= 16,
              let userID = Data(base64URL: options.user.id), !userID.isEmpty, !options.rp.id.isEmpty else { throw PasskeyError.invalidOptions }
        self.challenge = challenge
        relyingParty = options.rp.id
        self.userID = userID
        userName = options.user.name
    }
}

/// Serializes Apple's raw credential bytes as the same WebAuthn JSON that a browser sends.
public enum PasskeyCredential {
    public static func assertion(id: Data, clientData: Data, authenticatorData: Data, signature: Data, userID: Data) throws -> String {
        try json(id: id, response: [
            "clientDataJSON": clientData.base64URL, "authenticatorData": authenticatorData.base64URL,
            "signature": signature.base64URL, "userHandle": userID.base64URL,
        ])
    }

    public static func registration(id: Data, clientData: Data, attestation: Data) throws -> String {
        try json(id: id, response: ["clientDataJSON": clientData.base64URL,
                                   "attestationObject": attestation.base64URL, "transports": ["internal"]])
    }

    private static func json(id: Data, response: [String: Any]) throws -> String {
        let data = try JSONSerialization.data(withJSONObject: [
            "id": id.base64URL, "rawId": id.base64URL, "type": "public-key",
            "authenticatorAttachment": "platform", "response": response, "clientExtensionResults": [:],
        ])
        return String(decoding: data, as: UTF8.self)
    }
}

extension Data {
    init?(base64URL: String) {
        let base64 = base64URL.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        self.init(base64Encoded: base64 + String(repeating: "=", count: (4 - base64.count % 4) % 4))
    }

    var base64URL: String {
        base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }
}
