import SwiftUI
import SpindleKit

/// Signing in, creating an account, asking for a new password and choosing the server.
struct AuthView: View {
    enum Mode: Equatable {
        case signIn
        case register
        case forgotPassword
        /// The reset link was sent to this address.
        case resetSent(String)
        /// Choosing the person's own server instead of Spindle Cloud.
        case server
    }

    @Environment(AppModel.self) private var model
    @State private var mode = Self.initialMode
    // Shared by every mode, so switching between them keeps what was typed.
    @State private var email = ""

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 28) {
                Logo(height: 32)
                    .frame(maxWidth: .infinity)
                    .padding(.top, 36)
                VStack(alignment: .leading, spacing: 8) {
                    Text(title)
                        .font(.largeTitle.bold())
                    Text(description)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                content
                footer
                    .frame(maxWidth: .infinity)
            }
            .padding(.horizontal, 24)
            .padding(.bottom, 32)
            .frame(maxWidth: 520)
            .frame(maxWidth: .infinity)
        }
        .scrollDismissesKeyboard(.interactively)
        .background(Color(.systemGroupedBackground))
        .animation(.default, value: mode)
        .task { await model.loadServerInfo() }
    }

    private static var initialMode: Mode {
        #if DEBUG
        if let mode = LaunchOptions.current.authMode {
            return mode
        }
        #endif
        return .signIn
    }

    @ViewBuilder private var content: some View {
        switch mode {
        case .signIn:
            SignInForm(email: $email) { mode = .forgotPassword }
        case .register:
            RegisterForm(email: $email)
        case .forgotPassword:
            ForgotPasswordForm(email: $email) { mode = .resetSent($0) }
        case .resetSent:
            Button("Back to Sign In") { mode = .signIn }
                .buttonStyle(.prominent)
        case .server:
            ServerForm { mode = .signIn }
        }
    }

    @ViewBuilder private var footer: some View {
        VStack(spacing: 24) {
            links
            if mode == .signIn || mode == .register {
                serverLine
            }
        }
    }

    /// Which server signing in goes to, and a quiet way to choose another. Spindle Cloud stays the default.
    private var serverLine: some View {
        HStack(spacing: 4) {
            Text(model.isCloud ? "Spindle Cloud" : Self.name(of: model.server))
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.middle)
            Text("·").foregroundStyle(.tertiary)
            Button("Change server") { mode = .server }
                .buttonStyle(.brandLink)
        }
        .font(.footnote)
    }

    /// A server's address as people type it, without the scheme.
    static func name(of server: URL) -> String {
        let address = server.absoluteString
        for scheme in ["https://", "http://"] where address.hasPrefix(scheme) {
            return String(address.dropFirst(scheme.count))
        }
        return address
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
        case .server: "Your own server"
        }
    }

    private var description: String {
        switch mode {
        case .signIn: "Welcome back. Sign in to continue tracking your time."
        case .register: "Start tracking your time in less than a minute."
        case .forgotPassword: "Enter the email you sign in with. We'll send you a link to choose a new password."
        case .resetSent(let address): "If an account uses \(address), we sent it a link to choose a new password."
        case .server: "Use a Spindle server that you or your team run. You sign in to each server separately."
        }
    }
}

private struct EmailField: View {
    @Binding var email: String

    var body: some View {
        // Verbatim, or the address in the placeholder would read as a link.
        TextField(text: $email, prompt: Text(verbatim: "you@example.com")) { Text("Email") }
            .textContentType(.username)
            .keyboardType(.emailAddress)
            .textInputAutocapitalization(.never)
            .autocorrectionDisabled()
    }
}

private struct SignInForm: View {
    @Environment(AppModel.self) private var model
    @Binding var email: String
    let forgotPassword: () -> Void

    @State private var password = ""
    @State private var errors = FormErrors()
    @State private var pending = false
    @FocusState private var focus: Field?

    private enum Field { case email, password }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            if let form = errors.form {
                FormBanner(text: form)
            } else if let notice = model.signInNotice {
                FormBanner(text: notice, kind: .notice)
            }
            ExternalSignInButtons(pending: $pending, errors: $errors)
            LabeledField(label: "Email", error: errors["email"]) {
                EmailField(email: $email)
                    .focused($focus, equals: .email)
                    .submitLabel(.next)
                    .onSubmit { focus = .password }
            }
            LabeledField(label: "Password", error: errors["password"]) {
                SecureField("Password", text: $password)
                    .textContentType(.password)
                    .focused($focus, equals: .password)
                    .submitLabel(.go)
                    .onSubmit(submit)
            }
            Button("Forgot your password?", action: forgotPassword)
                .buttonStyle(.brandLink)
                .font(.subheadline)
                .frame(maxWidth: .infinity, alignment: .trailing)
            Button(pending ? "Signing in…" : "Sign in", action: submit)
                .buttonStyle(.prominent)
                .disabled(pending)
            if model.supportsPasskeys {
                Button(action: submitPasskey) {
                    Label("Sign in with a passkey", systemImage: "person.badge.key.fill")
                }
                .buttonStyle(.secondary)
                .disabled(pending)
            }
        }
        #if DEBUG
        .task { if LaunchOptions.current.submitsForms { submit() } }
        #endif
    }

    private func submit() {
        guard !pending else { return }
        let email = email.trimmingCharacters(in: .whitespacesAndNewlines)
        var validation = FormErrors()
        validation["email"] = Validation.email(email)
        validation["password"] = password.isEmpty ? "Enter your password." : nil
        errors = validation
        guard validation.isEmpty else {
            focus = validation["email"] != nil ? .email : .password
            return
        }
        focus = nil
        pending = true
        Task {
            defer { pending = false }
            do {
                try await model.signIn(email: email, password: password)
            } catch {
                errors = FormErrors(error, fields: ["email", "password"])
            }
        }
    }

    private func submitPasskey() {
        guard !pending else { return }
        focus = nil
        errors = FormErrors()
        pending = true
        Task {
            defer { pending = false }
            do { try await model.signInWithPasskey() }
            catch { errors = FormErrors(error, fields: []) }
        }
    }
}

private struct RegisterForm: View {
    @Environment(AppModel.self) private var model
    @Binding var email: String

    @State private var fullName = ""
    @State private var password = ""
    @State private var acceptTerms = false
    @State private var errors = FormErrors()
    @State private var pending = false
    @FocusState private var focus: Field?

    private enum Field { case fullName, email, password }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            if let form = errors.form {
                FormBanner(text: form)
            }
            ExternalSignInButtons(pending: $pending, errors: $errors)
            LabeledField(label: "Full name", error: errors["fullName"]) {
                TextField("Your name", text: $fullName)
                    .textContentType(.name)
                    .focused($focus, equals: .fullName)
                    .submitLabel(.next)
                    .onSubmit { focus = .email }
            }
            LabeledField(label: "Email", error: errors["email"]) {
                EmailField(email: $email)
                    .focused($focus, equals: .email)
                    .submitLabel(.next)
                    .onSubmit { focus = .password }
            }
            LabeledField(label: "Password", error: errors["password"], hint: "At least \(Validation.passwordMinLength) characters.") {
                SecureField("Password", text: $password)
                    .textContentType(.newPassword)
                    .focused($focus, equals: .password)
                    .submitLabel(.go)
                    .onSubmit(submit)
            }
            if let termsUrl = model.serverInfo?.termsUrl {
                TermsAgreement(termsUrl: termsUrl, privacyUrl: model.serverInfo?.privacyUrl, accepted: $acceptTerms, error: errors["acceptTerms"])
            }
            Button(pending ? "Creating account…" : "Create account", action: submit)
                .buttonStyle(.prominent)
                .disabled(pending)
        }
    }

    private func submit() {
        let fullName = fullName.trimmingCharacters(in: .whitespacesAndNewlines)
        let email = email.trimmingCharacters(in: .whitespacesAndNewlines)
        var validation = FormErrors()
        validation["fullName"] = fullName.isEmpty ? "Enter your name." : nil
        validation["email"] = Validation.email(email)
        validation["password"] = password.count < Validation.passwordMinLength
            ? "Use at least \(Validation.passwordMinLength) characters." : nil
        validation["acceptTerms"] = model.serverInfo?.termsUrl != nil && !acceptTerms
            ? "Agree to the terms of service and privacy policy to create an account." : nil
        errors = validation
        guard validation.isEmpty else {
            focus = validation["fullName"] != nil ? .fullName : validation["email"] != nil ? .email : .password
            return
        }
        focus = nil
        pending = true
        Task {
            defer { pending = false }
            do {
                try await model.register(fullName: fullName, email: email, password: password, acceptTerms: acceptTerms)
            } catch {
                errors = FormErrors(error, fields: ["fullName", "email", "password", "acceptTerms"])
            }
        }
    }
}

private struct ForgotPasswordForm: View {
    @Environment(AppModel.self) private var model
    @Binding var email: String
    let sent: (String) -> Void

    @State private var errors = FormErrors()
    @State private var pending = false
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            if let form = errors.form {
                FormBanner(text: form)
            }
            LabeledField(label: "Email", error: errors["email"]) {
                EmailField(email: $email)
                    .focused($focused)
                    .submitLabel(.send)
                    .onSubmit(submit)
            }
            Button(pending ? "Sending…" : "Send reset link", action: submit)
                .buttonStyle(.prominent)
                .disabled(pending)
        }
    }

    private func submit() {
        let email = email.trimmingCharacters(in: .whitespacesAndNewlines)
        var validation = FormErrors()
        validation["email"] = Validation.email(email)
        errors = validation
        guard validation.isEmpty else { return }
        focused = false
        pending = true
        Task {
            defer { pending = false }
            do {
                try await model.requestPasswordReset(email: email)
                sent(email)
            } catch {
                errors = FormErrors(error, fields: ["email"])
            }
        }
    }
}

/// Chooses the person's own server, after checking that a Spindle server answers there, or goes back to Spindle Cloud.
private struct ServerForm: View {
    @Environment(AppModel.self) private var model
    let done: () -> Void

    @State private var address = ""
    @State private var error: String?
    @State private var pending = false
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            LabeledField(label: "Server address", error: error, hint: "The address you open Spindle at in a browser.") {
                TextField(text: $address, prompt: Text(verbatim: "spindle.example.com")) { Text("Server address") }
                    .textContentType(.URL)
                    .keyboardType(.URL)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .focused($focused)
                    .submitLabel(.go)
                    .onSubmit(submit)
            }
            Text("Passkeys work only with Spindle Cloud. On your own server, sign in with your email and password.")
                .font(.footnote)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Button(pending ? "Checking…" : "Use this server", action: submit)
                .buttonStyle(.prominent)
                .disabled(pending)
            if !model.isCloud {
                Button("Use Spindle Cloud") {
                    model.useCloud()
                    done()
                }
                .buttonStyle(.secondary)
                .disabled(pending)
            }
        }
        .onAppear {
            address = model.isCloud ? "" : AuthView.name(of: model.server)
            focused = model.isCloud
        }
    }

    private func submit() {
        guard !pending else { return }
        let url: URL
        do {
            url = try ServerAddress.ownServer(address)
        } catch {
            self.error = error.localizedDescription
            return
        }
        error = nil
        focused = false
        pending = true
        Task {
            defer { pending = false }
            guard await model.isSpindleServer(url) else {
                error = "No Spindle server answered at \(AuthView.name(of: url)). Check the address, and that the server is running."
                return
            }
            model.useServer(url)
            done()
        }
    }
}
