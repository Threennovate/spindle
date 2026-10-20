import AuthenticationServices
import Foundation
import SpindleKit
#if os(macOS)
import AppKit
#else
import UIKit
#endif

/// Opens the server's Google or Apple sign-in in the system's browser sheet, and waits for it to come back to the app
/// at `spindle://auth/callback`.
@MainActor
final class BrowserSignIn: NSObject, ASWebAuthenticationPresentationContextProviding {
    private var session: ASWebAuthenticationSession?
    private var anchor: ASPresentationAnchor?

    /// The address the browser came back with, or nil when the person closed the sheet.
    func open(_ url: URL) async throws -> URL? {
        guard session == nil, let window = ASPresentationAnchor.frontmost else { throw ExternalSignInError.failed }
        anchor = window
        defer {
            session = nil
            anchor = nil
        }
        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<URL?, Error>) in
            // Not tied to the main actor: the system may call it from another thread.
            let completion: @Sendable (URL?, Error?) -> Void = { url, error in
                if let url {
                    continuation.resume(returning: url)
                } else if (error as? ASWebAuthenticationSessionError)?.code == .canceledLogin {
                    continuation.resume(returning: nil)
                } else {
                    continuation.resume(throwing: ExternalSignInError.failed)
                }
            }
            let session: ASWebAuthenticationSession
            if #available(macOS 14.4, iOS 17.4, *) {
                session = ASWebAuthenticationSession(url: url, callback: .customScheme(ExternalSignIn.callbackScheme), completionHandler: completion)
            } else {
                session = ASWebAuthenticationSession(url: url, callbackURLScheme: ExternalSignIn.callbackScheme, completionHandler: completion)
            }
            session.presentationContextProvider = self
            // Shares Safari's cookies, so someone already signed in to Google or Apple there doesn't sign in again.
            session.prefersEphemeralWebBrowserSession = false
            self.session = session
            if !session.start() {
                continuation.resume(throwing: ExternalSignInError.failed)
            }
        }
    }

    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        anchor!
    }
}

extension ASPresentationAnchor {
    /// The window in front, for the system's sign-in sheets to attach to.
    @MainActor static var frontmost: ASPresentationAnchor? {
        #if os(macOS)
        NSApplication.shared.keyWindow
        #else
        UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .filter { $0.activationState == .foregroundActive }
            .flatMap(\.windows).first(where: \.isKeyWindow)
        #endif
    }
}
