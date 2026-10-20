import SwiftUI
import SpindleKit

/// Your name, as teammates see it, and the time zone days are counted in.
struct ProfileView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let me: Me
    let client: SpindleClient

    @State private var fullName = ""
    @State private var timeZone = ""
    @State private var saving = false
    @State private var error: String?

    var body: some View {
        Form {
            Section {
                LabeledContent("Email", value: me.email)
                TextField("Full name", text: $fullName)
                    .textContentType(.name)
            } footer: {
                Text("Your name is how teammates see you.")
            }
            Section {
                NavigationLink {
                    TimeZonePicker(selection: $timeZone)
                } label: {
                    LabeledContent("Time zone", value: timeZone.replacingOccurrences(of: "_", with: " "))
                }
                if timeZone != TimeZone.current.identifier {
                    Button("Use this iPhone's time zone (\(TimeZone.current.identifier.replacingOccurrences(of: "_", with: " ")))") {
                        timeZone = TimeZone.current.identifier
                    }
                }
            } footer: {
                if let error {
                    Text(error).foregroundStyle(.red)
                } else {
                    Text("Days and times are counted in your time zone, on every device.")
                }
            }
        }
        .navigationTitle("Profile")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .confirmationAction) {
                Button(saving ? "Saving…" : "Save", action: save)
                    .disabled(saving || (fullName == me.fullName && timeZone == me.timeZone))
            }
        }
        .onAppear {
            if fullName.isEmpty {
                fullName = me.fullName
                timeZone = me.timeZone
            }
        }
    }

    private func save() {
        guard !fullName.trimmingCharacters(in: .whitespaces).isEmpty else {
            error = "Enter your name."
            return
        }
        saving = true
        Task {
            defer { saving = false }
            do {
                _ = try await client.updateProfile(fullName: fullName.trimmingCharacters(in: .whitespaces), timeZone: timeZone)
                try? await model.refreshAccount()
                error = nil
                model.notices.show("Profile saved")
                dismiss()
            } catch {
                self.error = error.localizedDescription
            }
        }
    }
}

/// Every time zone, searchable by place.
private struct TimeZonePicker: View {
    @Environment(\.dismiss) private var dismiss
    @Binding var selection: String
    @State private var search = ""

    private static let zones = TimeZone.knownTimeZoneIdentifiers.sorted()

    var body: some View {
        let term = search.trimmingCharacters(in: .whitespaces)
        List(Self.zones.filter { term.isEmpty || $0.replacingOccurrences(of: "_", with: " ").localizedCaseInsensitiveContains(term) }, id: \.self) { zone in
            Button {
                selection = zone
                dismiss()
            } label: {
                HStack {
                    Text(zone.replacingOccurrences(of: "_", with: " "))
                    Spacer()
                    if zone == selection {
                        Image(systemName: "checkmark").fontWeight(.semibold).foregroundStyle(Color.brandText)
                    }
                }
                .contentShape(Rectangle())
            }
            .tint(.primary)
        }
        .searchable(text: $search, placement: .navigationBarDrawer(displayMode: .always), prompt: "Search, e.g. Zagreb")
        .navigationTitle("Time Zone")
        .navigationBarTitleDisplayMode(.inline)
    }
}

/// A new password; changing it signs you out everywhere else.
struct PasswordView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let client: SpindleClient

    @State private var current = ""
    @State private var new = ""
    @State private var repeated = ""
    @State private var saving = false
    @State private var error: String?

    var body: some View {
        Form {
            Section {
                SecureField("Current password", text: $current)
                    .textContentType(.password)
            }
            Section {
                SecureField("New password", text: $new)
                    .textContentType(.newPassword)
                SecureField("Repeat the new password", text: $repeated)
                    .textContentType(.newPassword)
            } footer: {
                if let error {
                    Text(error).foregroundStyle(.red)
                } else {
                    Text("At least \(Validation.passwordMinLength) characters; a few words make a strong one. Changing it signs you out on every other device.")
                }
            }
        }
        .navigationTitle("Change Password")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .confirmationAction) {
                Button(saving ? "Saving…" : "Change", action: save)
                    .disabled(saving || current.isEmpty || new.isEmpty)
            }
        }
    }

    private func save() {
        if new.count < Validation.passwordMinLength {
            error = "Use at least \(Validation.passwordMinLength) characters."
            return
        }
        if new != repeated {
            error = "The new passwords don't match."
            return
        }
        saving = true
        Task {
            defer { saving = false }
            do {
                try await client.changePassword(current: current, new: new)
                model.notices.show("Password changed. Your other devices are signed out.")
                dismiss()
            } catch {
                self.error = FormErrors(error, fields: []).form ?? error.localizedDescription
            }
        }
    }
}

/// An account made with Google or Apple has no password to change; a link by email sets its first one, the same as
/// for a forgotten password.
struct SetPasswordButton: View {
    @Environment(AppModel.self) private var model
    let me: Me
    @State private var sending = false

    var body: some View {
        Button(sending ? "Sending…" : "Email Me a Link to Set a Password") {
            sending = true
            Task {
                defer { sending = false }
                do {
                    try await model.requestPasswordReset(email: me.email)
                    model.notices.show("We sent a link to \(me.email)")
                } catch {
                    model.notices.show(error, fallback: "Couldn't send the link.")
                }
            }
        }
        .disabled(sending)
    }
}

/// The devices and browsers signed in to the account; any but this iPhone can be signed out.
struct SessionsView: View {
    @Environment(AppModel.self) private var model
    let client: SpindleClient
    @State private var sessions: [DeviceSession] = []
    @State private var error: String?

    var body: some View {
        List {
            Section {
                ForEach(sessions) { session in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(session.deviceName ?? "Unknown device")
                        Text(session.isCurrent ? "This iPhone" : "Last active \(session.lastActiveAt.formatted(.relative(presentation: .named)))")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                    }
                    .swipeActions(edge: .trailing) {
                        if !session.isCurrent {
                            Button("Sign Out", role: .destructive) { revoke(session) }
                        }
                    }
                }
            } footer: {
                Text(error ?? "Swipe to sign out of a device you no longer use or don't recognize.")
                    .foregroundStyle(error == nil ? Color.secondary : Color.red)
            }
            if sessions.count > 1 {
                Section {
                    Button("Sign Out Everywhere Else", role: .destructive, action: revokeOthers)
                }
            }
        }
        .navigationTitle("Signed In")
        .navigationBarTitleDisplayMode(.inline)
        .task { await load() }
        .refreshable { await load() }
    }

    private func load() async {
        do {
            sessions = try await client.sessions().sorted { ($0.isCurrent ? 0 : 1, $1.lastActiveAt) < ($1.isCurrent ? 0 : 1, $0.lastActiveAt) }
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
    }

    private func revoke(_ session: DeviceSession) {
        Task {
            do {
                try await client.revokeSession(session.id)
                await load()
            } catch {
                self.error = error.localizedDescription
            }
        }
    }

    private func revokeOthers() {
        Task {
            do {
                try await client.revokeOtherSessions()
                await load()
                model.notices.show("Signed out everywhere else")
            } catch {
                self.error = error.localizedDescription
            }
        }
    }
}

/// Deletes the account, after the password. An account without one, made with Google or Apple, needs a recent sign-in
/// instead, which the server checks and asks for if it's too long ago.
struct DeleteAccountView: View {
    @Environment(AppModel.self) private var model
    let me: Me
    let client: SpindleClient
    @State private var password = ""
    @State private var error: String?
    @State private var deleting = false

    var body: some View {
        Form {
            Section {
                Text("Workspaces only you use are deleted with everything in them. In workspaces you share, your time stays for the team's reports, without your name.")
                Text("This cannot be undone. Export your data first if you want to keep it.")
                    .fontWeight(.medium)
            }
            Section {
                if me.signsInWithPassword {
                    SecureField("Your password", text: $password)
                        .textContentType(.password)
                }
            } footer: {
                if let error {
                    Text(error).foregroundStyle(.red)
                }
            }
            Section {
                Button(deleting ? "Deleting…" : "Delete My Account", role: .destructive, action: delete)
                    .disabled((me.signsInWithPassword && password.isEmpty) || deleting)
            }
        }
        .navigationTitle("Delete Account")
        .navigationBarTitleDisplayMode(.inline)
    }

    private func delete() {
        deleting = true
        Task {
            defer { deleting = false }
            do {
                try await client.deleteAccount(password: me.signsInWithPassword ? password : nil)
                await model.signOut(notice: "Your account was deleted.")
            } catch {
                self.error = FormErrors(error, fields: ["password"]).fields["password"] ?? error.localizedDescription
            }
        }
    }
}
