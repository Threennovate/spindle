import SwiftUI
import SpindleKit

struct SignInForm: View {
    @Environment(AppModel.self) private var model
    @Binding var email: String
    let forgotPassword: () -> Void

    @State private var password = ""
    @State private var errors = FormErrors()
    @State private var pending = false
    @FocusState private var focus: Field?

    private enum Field { case email, password }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            if let form = errors.form {
                FormBanner(text: form)
            } else if let notice = model.signInNotice {
                FormBanner(text: notice, kind: .notice)
            }
            ExternalSignInButtons(pending: $pending, errors: $errors)
            FormField(label: "Email", error: errors["email"]) {
                TextField("", text: $email)
                    .textContentType(.username)
                    .focused($focus, equals: .email)
            }
            FormField(label: "Password", error: errors["password"]) {
                SecureField("", text: $password)
                    .textContentType(.password)
                    .focused($focus, equals: .password)
            }
            HStack {
                Spacer()
                Button("Forgot your password?", action: forgotPassword)
                    .buttonStyle(.brandLink)
                    .font(.callout)
            }
            .padding(.top, -8)
            Button(action: submit) {
                Text(pending ? "Signing in…" : "Sign in")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.brand)
            .controlSize(.large)
            .keyboardShortcut(.defaultAction)
            .disabled(pending)
            if model.supportsPasskeys {
                Button("Sign in with a passkey", action: submitPasskey)
                    .frame(maxWidth: .infinity)
                    .controlSize(.large)
                    .disabled(pending)
            }
        }
        .onAppear { focus = .email }
        #if DEBUG
        .task { if SnapshotRun.current?.submitsForms == true { submit() } }
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
            focus = validation["email"] != nil ? .email : validation["password"] != nil ? .password : nil
            return
        }

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
        errors = FormErrors()
        pending = true
        Task {
            defer { pending = false }
            do { try await model.signInWithPasskey() }
            catch { errors = FormErrors(error, fields: []) }
        }
    }
}

struct RegisterForm: View {
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
        VStack(alignment: .leading, spacing: 16) {
            if let form = errors.form {
                FormBanner(text: form)
            }
            ExternalSignInButtons(pending: $pending, errors: $errors)
            FormField(label: "Full name", error: errors["fullName"]) {
                TextField("", text: $fullName)
                    .textContentType(.name)
                    .focused($focus, equals: .fullName)
            }
            FormField(label: "Email", error: errors["email"]) {
                TextField("", text: $email)
                    .textContentType(.username)
                    .focused($focus, equals: .email)
            }
            FormField(label: "Password", error: errors["password"], hint: "At least \(Validation.passwordMinLength) characters.") {
                SecureField("", text: $password)
                    .textContentType(.newPassword)
                    .focused($focus, equals: .password)
            }
            if let termsUrl = model.serverInfo?.termsUrl {
                TermsAgreement(termsUrl: termsUrl, privacyUrl: model.serverInfo?.privacyUrl, accepted: $acceptTerms, error: errors["acceptTerms"])
            }
            Button(action: submit) {
                Text(pending ? "Creating account…" : "Create account")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.brand)
            .controlSize(.large)
            .keyboardShortcut(.defaultAction)
            .disabled(pending)
        }
        .onAppear { focus = .fullName }
        #if DEBUG
        .task { if SnapshotRun.current?.submitsForms == true { submit() } }
        #endif
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
            focus = validation["fullName"] != nil ? .fullName
                : validation["email"] != nil ? .email
                : validation["password"] != nil ? .password : nil
            return
        }

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

struct ForgotPasswordForm: View {
    @Environment(AppModel.self) private var model
    @Binding var email: String
    let sent: (String) -> Void

    @State private var errors = FormErrors()
    @State private var pending = false
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            if let form = errors.form {
                FormBanner(text: form)
            }
            FormField(label: "Email", error: errors["email"]) {
                TextField("", text: $email)
                    .textContentType(.username)
                    .focused($focused)
            }
            Button(action: submit) {
                Text(pending ? "Sending…" : "Send reset link")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.brand)
            .controlSize(.large)
            .keyboardShortcut(.defaultAction)
            .disabled(pending)
        }
        .onAppear { focused = true }
    }

    private func submit() {
        let email = email.trimmingCharacters(in: .whitespacesAndNewlines)
        var validation = FormErrors()
        validation["email"] = Validation.email(email)
        errors = validation
        guard validation.isEmpty else { return }

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

/// Chooses Spindle Cloud or the person's own server, and checks that a Spindle server answers there first.
struct ServerForm: View {
    @Environment(AppModel.self) private var model
    let done: () -> Void

    @State private var own = false
    @State private var address = ""
    @State private var error: String?
    @State private var pending = false
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            if let error {
                FormBanner(text: error)
            }
            Picker("Server", selection: $own) {
                Text("Spindle Cloud").tag(false)
                Text("Your own server").tag(true)
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()
            if own {
                FormField(label: "Address", hint: "The address you open Spindle at in a browser, e.g. spindle.example.com.") {
                    TextField("", text: $address)
                        .textContentType(.URL)
                        .focused($focused)
                }
                Text("Passkeys work only with Spindle Cloud. On your own server, sign in with your email and password.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Button(action: submit) {
                Text(pending ? "Checking…" : "Use this server")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.brand)
            .controlSize(.large)
            .keyboardShortcut(.defaultAction)
            .disabled(pending)
        }
        .onAppear {
            own = !model.isCloud
            address = model.isCloud ? "" : model.server.absoluteString
        }
        .onChange(of: own) { _, own in
            error = nil
            focused = own
        }
    }

    private func submit() {
        guard own else {
            model.useCloud()
            done()
            return
        }
        let url: URL
        do {
            url = try ServerAddress.ownServer(address)
        } catch {
            self.error = error.localizedDescription
            return
        }
        pending = true
        Task {
            defer { pending = false }
            guard await model.isSpindleServer(url) else {
                error = "No Spindle server answered at \(url.host(percentEncoded: false) ?? url.absoluteString). Check the address, and that the server is running."
                return
            }
            model.useServer(url)
            done()
        }
    }
}
