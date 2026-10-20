import Foundation
import Testing
@testable import SpindleKit

struct ExternalSignInTests {
    let server = URL(string: "https://track.example.com")!

    @Test func challengeIsTheVerifiersSHA256InBase64URL() {
        // The example in RFC 7636, appendix B.
        let pkce = ExternalSignIn.PKCE(verifier: "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")
        #expect(pkce.challenge == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM")
    }

    @Test func newVerifiersAre43URLSafeCharactersAndDiffer() {
        let first = ExternalSignIn.PKCE(), second = ExternalSignIn.PKCE()
        #expect(first.verifier.count == 43)
        #expect(first.challenge.count == 43)
        #expect(first.verifier.allSatisfy { $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" })
        #expect(first.verifier != second.verifier)
    }

    @Test func startURLAsksForTheAppsRedirectWithTheChallenge() {
        let url = ExternalSignIn.startURL(server: server, provider: "google", challenge: "abc-_123", timeZone: "Europe/Zagreb")
        #expect(url.absoluteString == "https://track.example.com/api/auth/external/google/start?redirectUri=spindle%3A%2F%2Fauth%2Fcallback"
            + "&codeChallenge=abc-_123&timeZone=Europe%2FZagreb&acceptTerms=true")
    }

    @Test func startURLCarriesAnInvitation() throws {
        let url = ExternalSignIn.startURL(server: URL(string: "https://example.com/spindle")!, provider: "apple", challenge: "c",
                                          timeZone: "UTC", invitation: "token+/=")
        #expect(url.path() == "/spindle/api/auth/external/apple/start")
        let items = try #require(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems)
        #expect(items.first { $0.name == "invitation" }?.value == "token+/=")
        #expect(url.query()?.contains("invitation=token%2B%2F%3D") == true)
    }

    @Test func callbackGivesTheCodeOrSaysItWasCancelled() {
        #expect(ExternalSignIn.callback(URL(string: "spindle://auth/callback?code=one-time")!) == .code("one-time"))
        #expect(ExternalSignIn.callback(URL(string: "spindle://auth/callback?error=cancelled")!) == .cancelled)
    }

    @Test func callbackWithoutACodeFails() {
        #expect(ExternalSignIn.callback(URL(string: "spindle://auth/callback")!) == .failed)
        #expect(ExternalSignIn.callback(URL(string: "spindle://auth/callback?code=")!) == .failed)
        #expect(ExternalSignIn.callback(URL(string: "spindle://auth/callback?error=failed")!) == .failed)
        #expect(ExternalSignIn.callback(URL(string: "spindle://elsewhere?code=one-time")!) == .failed)
        #expect(ExternalSignIn.callback(URL(string: "https://track.example.com/auth/callback?code=one-time")!) == .failed)
    }

    @Test func exchangeKeepsTheTokensLikeAPasswordSignIn() async throws {
        let store = MemoryTokenStore()
        let transport = FakeTransport { _, operation in
            guard operation == "exchangeExternalSignIn" else { return (.notFound, "{}") }
            let json = tokens(access: "external-access", expiresIn: 900, refresh: "external-refresh")
            return (.ok, String(json.dropLast()) + #","newAccount":true}"#)
        }
        let session = AuthSession(server: server, tokens: store, deviceName: "Ana's Mac", transport: transport)

        let newAccount = try await session.signInExternally(code: "one-time", codeVerifier: "verifier")

        #expect(newAccount)
        #expect(store.read(account: server.absoluteString) == "external-refresh")
        #expect(try await session.validAccessToken() == "external-access")
        let body = try #require(transport.bodies("exchangeExternalSignIn").first)
        let sent = try #require(JSONSerialization.jsonObject(with: Data(body.utf8)) as? [String: String])
        #expect(sent == ["code": "one-time", "codeVerifier": "verifier", "deviceName": "Ana's Mac"])
    }

    @Test func refusedExchangeSaysWhyAndKeepsNoSession() async {
        let store = MemoryTokenStore()
        let transport = FakeTransport { _, _ in (.badRequest, #"{"title":"This sign-in link has expired.","status":400}"#) }
        let session = AuthSession(server: server, tokens: store, transport: transport)
        await #expect(throws: APIError(status: 400, title: "This sign-in link has expired.")) {
            try await session.signInExternally(code: "used", codeVerifier: "verifier")
        }
        #expect(!session.hasStoredSession)
    }
}
