import SwiftUI
import SpindleKit

/// "Continue with Google" and "Continue with Apple", above the email form for signing in and creating an account
/// alike, with the terms under them on a server that has terms: continuing is agreeing to them, as it says. Nothing
/// on a server that offers no providers.
struct ExternalSignInButtons: View {
    @Environment(AppModel.self) private var model
    /// The form's own: the whole form waits while the browser sheet is open.
    @Binding var pending: Bool
    @Binding var errors: FormErrors
    var invitation: String?

    @State private var opening: String?

    var body: some View {
        if !model.signInProviders.isEmpty {
            VStack(spacing: 12) {
                ForEach(model.signInProviders, id: \.name) { provider in
                    button(provider)
                }
                if let termsUrl = model.serverInfo?.termsUrl {
                    Text(terms(termsUrl, privacyUrl: model.serverInfo?.privacyUrl))
                        .font(Self.smallFont)
                        .foregroundStyle(.secondary)
                        .tint(Color.brandText)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }
                HStack(spacing: 10) {
                    line
                    Text("or with email")
                        .font(Self.smallFont)
                        .foregroundStyle(.secondary)
                    line
                }
                .accessibilityElement(children: .combine)
            }
            .frame(maxWidth: .infinity)
        }
    }

    private func button(_ provider: SignInProvider) -> some View {
        Button {
            continueWith(provider.name)
        } label: {
            HStack(spacing: 8) {
                ProviderMark(name: provider.name)
                Text(opening == provider.name ? "Opening \(provider.displayName)…" : "Continue with \(provider.displayName)")
            }
            .frame(maxWidth: .infinity)
        }
        #if os(macOS)
        .controlSize(.large)
        #else
        .buttonStyle(.secondary)
        #endif
        .disabled(pending)
    }

    #if os(macOS)
    private static let smallFont = Font.callout
    #else
    private static let smallFont = Font.footnote
    #endif

    private var line: some View {
        Rectangle()
            .fill(.separator)
            .frame(height: 1)
            .frame(maxWidth: .infinity)
    }

    private func terms(_ termsUrl: String, privacyUrl: String?) -> AttributedString {
        var markdown = "By continuing, you agree to the [terms of service](\(termsUrl))"
        if let privacyUrl {
            markdown += " and the [privacy policy](\(privacyUrl))"
        }
        return (try? AttributedString(markdown: markdown + ".")) ?? AttributedString("By continuing, you agree to the terms of service.")
    }

    private func continueWith(_ provider: String) {
        guard !pending else { return }
        errors = FormErrors()
        pending = true
        opening = provider
        Task {
            defer {
                pending = false
                opening = nil
            }
            do { try await model.signInExternally(with: provider, invitation: invitation) }
            catch { errors = FormErrors(error, fields: []) }
        }
    }
}

/// The providers' own marks, as their sign-in guidelines ask: Google's four colours, Apple's in the text colour.
private struct ProviderMark: View {
    let name: String

    var body: some View {
        switch name {
        case "google":
            Image("GoogleMark")
                .resizable()
                .frame(width: 18, height: 18)
                .accessibilityHidden(true)
        case "apple":
            Image(systemName: "apple.logo")
                .accessibilityHidden(true)
        default:
            EmptyView()
        }
    }
}
