import AuthenticationServices
import Foundation
import SpindleKit
#if os(macOS)
import AppKit
#else
import UIKit
#endif

/// Retains Apple's authorization controller and its continuation until the system sheet completes.
@MainActor
final class PasskeyAuthorization: NSObject, ASAuthorizationControllerDelegate, ASAuthorizationControllerPresentationContextProviding {
    private var continuation: CheckedContinuation<String, Error>?
    private var controller: ASAuthorizationController?
    private var anchor: ASPresentationAnchor?
    private var requestID: UUID?

    func assertion(_ options: PasskeyAssertionOptions) async throws -> String {
        let provider = ASAuthorizationPlatformPublicKeyCredentialProvider(relyingPartyIdentifier: options.relyingParty)
        let request = provider.createCredentialAssertionRequest(challenge: options.challenge)
        request.userVerificationPreference = .required
        return try await perform(request)
    }

    func registration(_ options: PasskeyRegistrationOptions) async throws -> String {
        let provider = ASAuthorizationPlatformPublicKeyCredentialProvider(relyingPartyIdentifier: options.relyingParty)
        let request = provider.createCredentialRegistrationRequest(challenge: options.challenge, name: options.userName, userID: options.userID)
        request.userVerificationPreference = .required
        request.attestationPreference = .none
        return try await perform(request)
    }

    private func perform(_ request: ASAuthorizationRequest) async throws -> String {
        guard continuation == nil else { throw PasskeyError.busy }
        guard let window = ASPresentationAnchor.frontmost else { throw PasskeyError.unavailable }
        anchor = window
        let requestID = UUID()
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                self.continuation = continuation
                self.requestID = requestID
                let controller = ASAuthorizationController(authorizationRequests: [request])
                self.controller = controller
                controller.delegate = self
                controller.presentationContextProvider = self
                controller.performRequests()
            }
        } onCancel: {
            Task { @MainActor in
                guard self.requestID == requestID else { return }
                self.controller?.cancel()
                self.finish(.failure(PasskeyError.cancelled))
            }
        }
    }

    func presentationAnchor(for controller: ASAuthorizationController) -> ASPresentationAnchor {
        anchor!
    }

    func authorizationController(controller: ASAuthorizationController, didCompleteWithAuthorization authorization: ASAuthorization) {
        guard self.controller === controller else { return }
        do {
            if let credential = authorization.credential as? ASAuthorizationPlatformPublicKeyCredentialAssertion {
                finish(.success(try PasskeyCredential.assertion(id: credential.credentialID, clientData: credential.rawClientDataJSON,
                    authenticatorData: credential.rawAuthenticatorData, signature: credential.signature, userID: credential.userID)))
            } else if let credential = authorization.credential as? ASAuthorizationPlatformPublicKeyCredentialRegistration,
                      let attestation = credential.rawAttestationObject {
                finish(.success(try PasskeyCredential.registration(id: credential.credentialID,
                    clientData: credential.rawClientDataJSON, attestation: attestation)))
            } else {
                finish(.failure(PasskeyError.unavailable))
            }
        } catch { finish(.failure(error)) }
    }

    func authorizationController(controller: ASAuthorizationController, didCompleteWithError error: Error) {
        guard self.controller === controller else { return }
        let cancelled = (error as? ASAuthorizationError)?.code == .canceled
        finish(.failure(cancelled ? PasskeyError.cancelled : PasskeyError.unavailable))
    }

    private func finish(_ result: Result<String, Error>) {
        let completion = continuation
        continuation = nil
        requestID = nil
        controller = nil
        anchor = nil
        completion?.resume(with: result)
    }
}
