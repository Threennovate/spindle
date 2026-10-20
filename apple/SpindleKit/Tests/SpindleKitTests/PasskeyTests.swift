import Foundation
import Testing
@testable import SpindleKit

struct PasskeyTests {
    @Test func optionsDecodeBinaryChallengesAndUserHandles() throws {
        let challenge = Data((0..<32).map(UInt8.init))
        let userID = Data("user-id".utf8)
        let assertion = try PasskeyAssertionOptions(json: #"{"challenge":"\#(challenge.base64URL)","rpId":"track.example.com"}"#)
        #expect(assertion.challenge == challenge)
        #expect(assertion.relyingParty == "track.example.com")
        let registration = try PasskeyRegistrationOptions(json: #"{"challenge":"\#(challenge.base64URL)","rp":{"id":"track.example.com"},"user":{"id":"\#(userID.base64URL)","name":"ana@example.com"}}"#)
        #expect(registration.userID == userID)
        #expect(registration.userName == "ana@example.com")
    }

    @Test func corruptOptionsAreRejected() {
        #expect(throws: (any Error).self) { try PasskeyAssertionOptions(json: #"{"challenge":"!invalid!","rpId":"track.example.com"}"#) }
        #expect(throws: (any Error).self) { try PasskeyRegistrationOptions(json: "{}") }
    }

    @Test func assertionsPreserveEverySignedByteAsBase64URL() throws {
        let id = Data([0xfb, 0xff, 0x01])
        let client = Data(#"{"type":"webauthn.get"}"#.utf8)
        let auth = Data([1, 2, 3])
        let signature = Data([0xff, 0xfb, 0x00])
        let user = Data("user-id".utf8)
        let json = try PasskeyCredential.assertion(id: id, clientData: client, authenticatorData: auth, signature: signature, userID: user)
        let body = try #require(JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any])
        #expect(body["type"] as? String == "public-key")
        #expect(body["id"] as? String == "-_8B")
        #expect(body["rawId"] as? String == "-_8B")
        let response = try #require(body["response"] as? [String: String])
        #expect(Data(base64URL: try #require(response["clientDataJSON"])) == client)
        #expect(Data(base64URL: try #require(response["signature"])) == signature)
        #expect(Data(base64URL: try #require(response["userHandle"])) == user)
    }

    @Test func nativeSignInStoresTokensInTheKeychainWithoutACookie() async throws {
        let store = MemoryTokenStore()
        let server = URL(string: "https://track.example.com")!
        let transport = FakeTransport { _, operation in
            switch operation {
            case "nativePasskeyOptions": return (.ok, #"{"optionsJson":"{}","challengeToken":"single-use-token"}"#)
            case "nativePasskeyLogin": return (.ok, tokens(access: "passkey-access", expiresIn: 900, refresh: "passkey-refresh"))
            default: return (.notFound, "{}")
            }
        }
        let session = AuthSession(server: server, tokens: store, transport: transport)
        let options = try await session.passkeyOptions()
        #expect(options.challengeToken == "single-use-token")
        try await session.signInWithPasskey(credentialJSON: "{}", challengeToken: options.challengeToken)
        #expect(store.read(account: server.absoluteString) == "passkey-refresh")
        #expect(try await session.validAccessToken() == "passkey-access")
    }

    @Test func failedNativeSignInDoesNotStoreASession() async {
        let store = MemoryTokenStore()
        let transport = FakeTransport { _, _ in (.unauthorized, #"{"title":"Invalid passkey","status":401}"#) }
        let session = AuthSession(server: URL(string: "https://track.example.com")!, tokens: store, transport: transport)
        await #expect(throws: APIError(status: 401, title: "Invalid passkey")) {
            try await session.signInWithPasskey(credentialJSON: "{}", challengeToken: "used-token")
        }
        #expect(!session.hasStoredSession)
    }
}
