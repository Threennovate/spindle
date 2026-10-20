import SwiftUI
import SpindleKit

/// The same passkeys are available on the web and in both Apple apps.
struct PasskeysSection: View {
    @Environment(AppModel.self) private var model
    @State private var passkeys: [Components.Schemas.PasskeyResponse] = []
    @State private var loading = true
    @State private var pending = false
    @State private var name = ""
    @State private var password = ""
    @State private var error: String?
    @State private var removing: Components.Schemas.PasskeyResponse?

    var body: some View {
        Section {
            if loading { ProgressView() }
            if !loading && passkeys.isEmpty { Text("You haven't added any passkeys yet.").foregroundStyle(.secondary) }
            ForEach(passkeys, id: \.id) { passkey in
                HStack {
                    VStack(alignment: .leading) {
                        Text(passkey.name)
                        Text("Added \(passkey.createdAt.formatted(date: .abbreviated, time: .omitted))")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("Remove", role: .destructive) { removing = passkey }
                        .disabled(pending)
                }
            }
            if model.supportsPasskeys {
                TextField("Passkey name", text: $name)
                if hasPassword {
                    SecureField("Current password", text: $password).textContentType(.password)
                }
                Button(pending ? "Please wait…" : "Add a passkey", action: add)
                    .disabled(pending || name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || (hasPassword && password.isEmpty) || passkeys.count >= 10)
            }
            if let error { Text(error).foregroundStyle(.red).font(.callout) }
        } header: {
            Text("Passkeys")
        } footer: {
            Text(hasPassword
                ? "Sign in with Touch ID, Face ID or your device passcode. Your password remains available for account recovery."
                : "Sign in with Touch ID, Face ID or your device passcode, as well as with Google or Apple.")
        }
        .task { await load() }
        .confirmationDialog("Remove passkey?", isPresented: Binding(get: { removing != nil }, set: { if !$0 { removing = nil } }), titleVisibility: .visible) {
            if let removing {
                Button("Remove \(removing.name)", role: .destructive) { remove(removing.id) }
            }
        } message: {
            Text(hasPassword ? "You can still sign in with your password or another passkey." : "You can still sign in with Google or Apple, or with another passkey.")
        }
    }

    /// An account made with Google or Apple has no password; adding a passkey to it needs a recent sign-in instead, which
    /// the server checks and asks for if it's too long ago.
    private var hasPassword: Bool {
        model.me?.signsInWithPassword ?? true
    }

    private func load() async {
        defer { loading = false }
        do { passkeys = try await model.client.passkeys() }
        catch { self.error = error.localizedDescription }
    }

    private func add() {
        guard !pending else { return }
        let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty, name.count <= 100 else { error = "Use a name of at most 100 characters."; return }
        pending = true
        error = nil
        Task {
            defer { pending = false; password = "" }
            do {
                try await model.addPasskey(name: name, password: hasPassword ? password : nil)
                self.name = ""
                model.notices.show("Passkey added")
                await load()
            } catch { self.error = error.localizedDescription }
        }
    }

    private func remove(_ id: String) {
        guard !pending else { return }
        pending = true
        error = nil
        Task {
            defer { pending = false }
            do {
                try await model.client.removePasskey(id)
                model.notices.show("Passkey removed")
                await load()
            } catch { self.error = error.localizedDescription }
        }
    }
}
