import SwiftUI
import SpindleKit

/// Signing in, creating an account and asking for a new password.
struct AuthView: View {
    enum Mode: Equatable {
        case signIn
        case register
        case forgotPassword
        /// The reset link was sent to this address.
        case resetSent(String)
        /// Choosing between Spindle Cloud and the person's own server.
        case server
    }

    @Environment(AppModel.self) private var model
    @State private var mode = Self.initialMode
    // Shared by every mode, so switching between them keeps what was typed.
    @State private var email = ""

    var body: some View {
        ScrollView {
            VStack(spacing: 28) {
                Logo(height: 36)
                VStack(alignment: .leading, spacing: 20) {
                    header
                    content
                }
                .padding(28)
                .frame(width: 400)
                .background(Color(nsColor: .windowBackgroundColor), in: RoundedRectangle(cornerRadius: 14))
                .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(.separator))
                .shadow(color: .black.opacity(0.06), radius: 12, y: 4)
                footer
            }
            .padding(.vertical, 48)
            .frame(maxWidth: .infinity)
        }
        .frame(minWidth: 480, minHeight: 560)
        .background(Color(nsColor: .underPageBackgroundColor))
        .animation(.default, value: mode)
        .task { await model.loadServerInfo() }
    }

    private static var initialMode: Mode {
        #if DEBUG
        if let mode = SnapshotRun.current?.authMode {
            return mode
        }
        #endif
        return .signIn
    }

    @ViewBuilder private var header: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(.title.weight(.semibold))
            Text(description)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    @ViewBuilder private var content: some View {
        switch mode {
        case .signIn:
            SignInForm(email: $email) {
                mode = .forgotPassword
            }
        case .register:
            RegisterForm(email: $email)
        case .forgotPassword:
            ForgotPasswordForm(email: $email) { sentTo in
                mode = .resetSent(sentTo)
            }
        case .resetSent:
            Button("Back to Sign In") {
                mode = .signIn
            }
            .buttonStyle(.brand)
            .controlSize(.large)
            .frame(maxWidth: .infinity)
            .keyboardShortcut(.defaultAction)
        case .server:
            ServerForm { mode = .signIn }
        }
    }

    @ViewBuilder private var footer: some View {
        VStack(spacing: 10) {
            links
            if mode == .signIn || mode == .register {
                serverLine
            }
        }
    }

    /// Which server signing in goes to, and a way to choose another.
    private var serverLine: some View {
        HStack(spacing: 4) {
            Text(model.isCloud ? "Spindle Cloud" : model.server.host(percentEncoded: false) ?? model.server.absoluteString)
                .foregroundStyle(.secondary)
            Text("·").foregroundStyle(.tertiary)
            Button("Change server") { mode = .server }
                .buttonStyle(.brandLink)
        }
        .font(.callout)
    }

    @ViewBuilder private var links: some View {
        switch mode {
        case .signIn:
            // A server that takes people only by invitation doesn't offer to create an account; the invitation's
            // link does that on the web.
            if model.serverInfo?.registrationOpen != false {
                HStack(spacing: 4) {
                    Text("New to Spindle?").foregroundStyle(.secondary)
                    Button("Create an account") { mode = .register }
                        .buttonStyle(.brandLink)
                }
            }
        case .register:
            HStack(spacing: 4) {
                Text("Already have an account?").foregroundStyle(.secondary)
                Button("Sign in") { mode = .signIn }
                    .buttonStyle(.brandLink)
            }
        case .forgotPassword:
            Button("Back to sign in") { mode = .signIn }
                .buttonStyle(.brandLink)
        case .resetSent:
            EmptyView()
        case .server:
            Button("Back to sign in") { mode = .signIn }
                .buttonStyle(.brandLink)
        }
    }

    private var title: String {
        switch mode {
        case .signIn: "Sign in"
        case .register: "Create your account"
        case .forgotPassword: "Forgot your password?"
        case .resetSent: "Check your inbox"
        case .server: "Choose a server"
        }
    }

    private var description: String {
        switch mode {
        case .signIn: "Welcome back. Sign in to continue tracking your time."
        case .register: "Start tracking your time in less than a minute."
        case .forgotPassword: "Enter the email you sign in with. We'll send you a link to choose a new password."
        case .resetSent(let address): "If an account uses \(address), we sent it a link to choose a new password."
        case .server: "Spindle Cloud, or a Spindle server that you or your team run."
        }
    }
}
