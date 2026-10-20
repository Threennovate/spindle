import SwiftUI
import SpindleKit
import UniformTypeIdentifiers

/// The account, as on the web app: profile, how times are shown, password, devices, a copy of the data, and
/// deleting it all.
struct AccountSettings: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if let me = model.me {
            let client = model.client
            Form {
                ProfileSection(me: me, client: client)
                PreferencesSection(me: me, client: client)
                if me.signsInWithPassword {
                    PasswordSection(client: client)
                } else {
                    SetPasswordSection(me: me)
                }
                PasskeysSection()
                DevicesSection(client: client)
                DataSection(me: me, client: client)
            }
            .formStyle(.grouped)
            .frame(height: 680)
        } else {
            Text("Sign in to see your account.")
                .foregroundStyle(.secondary)
                .frame(height: 200)
        }
    }
}

private struct ProfileSection: View {
    @Environment(AppModel.self) private var model
    let me: Me
    let client: SpindleClient
    @State private var fullName = ""
    @State private var timeZone = ""
    @State private var saving = false
    @State private var error: String?

    var body: some View {
        Section {
            LabeledContent("Email", value: me.email)
            TextField("Full name", text: $fullName)
            Picker("Time zone", selection: $timeZone) {
                ForEach(TimeZone.knownTimeZoneIdentifiers.sorted(), id: \.self) { Text($0.replacingOccurrences(of: "_", with: " ")).tag($0) }
            }
            if timeZone != TimeZone.current.identifier {
                Button("Use this Mac's time zone (\(TimeZone.current.identifier.replacingOccurrences(of: "_", with: " ")))") {
                    timeZone = TimeZone.current.identifier
                }
                .buttonStyle(.brandLink)
            }
            SaveRow(error: error, saving: saving, changed: fullName != me.fullName || timeZone != me.timeZone) {
                guard !fullName.trimmingCharacters(in: .whitespaces).isEmpty else {
                    error = "Enter your name."
                    return
                }
                saving = true
                defer { saving = false }
                do {
                    _ = try await client.updateProfile(fullName: fullName, timeZone: timeZone)
                    try? await model.refreshAccount()
                    error = nil
                    model.notices.show("Profile saved")
                } catch {
                    self.error = error.localizedDescription
                }
            }
        } header: {
            Text("Profile")
        } footer: {
            Text("Your name is how teammates see you. Days and times are counted in your time zone.")
        }
        .onAppear {
            fullName = me.fullName
            timeZone = me.timeZone
        }
    }
}

private struct PreferencesSection: View {
    @Environment(AppModel.self) private var model
    let me: Me
    let client: SpindleClient
    @State private var values: PreferenceChanges?
    @State private var saving = false
    @State private var error: String?

    private static let weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"]
    private static let dateFormats = ["d MMM yyyy", "dd.MM.yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd"]

    var body: some View {
        Section {
            if let values = Binding($values) {
                Picker("First day of the week", selection: values.weekStart) {
                    ForEach(0..<7) { Text(Self.weekdays[$0]).tag($0) }
                }
                Picker("Date format", selection: values.dateFormat) {
                    ForEach(Self.dateFormats, id: \.self) { format in
                        Text(DayKey(year: 2026, month: 9, day: 28).formatted(format)).tag(format)
                    }
                }
                Picker("Time format", selection: values.timeFormat) {
                    Text("24-hour (13:30)").tag("24h")
                    Text("12-hour (1:30 PM)").tag("12h")
                }
                Picker("Duration format", selection: values.durationFormat) {
                    Text("1:30:00").tag("hms")
                    Text("1:30").tag("hm")
                    Text("1.50 h").tag("decimal")
                }
                SaveRow(error: error, saving: saving, changed: values.wrappedValue != PreferenceChanges(me.preferences)) {
                    saving = true
                    defer { saving = false }
                    do {
                        _ = try await client.updatePreferences(values.wrappedValue)
                        try? await model.refreshAccount()
                        error = nil
                        model.notices.show("Preferences saved")
                    } catch {
                        self.error = error.localizedDescription
                    }
                }
            }
        } header: {
            Text("Preferences")
        } footer: {
            Text("How dates, times and durations are shown to you, on every device.")
        }
        .onAppear { values = PreferenceChanges(me.preferences) }
    }
}

private struct PasswordSection: View {
    @Environment(AppModel.self) private var model
    let client: SpindleClient
    @State private var current = ""
    @State private var new = ""
    @State private var repeated = ""
    @State private var saving = false
    @State private var error: String?

    var body: some View {
        Section {
            SecureField("Current password", text: $current)
                .textContentType(.password)
            SecureField("New password", text: $new)
                .textContentType(.newPassword)
            SecureField("Repeat the new password", text: $repeated)
                .textContentType(.newPassword)
            SaveRow(error: error, saving: saving, changed: !current.isEmpty || !new.isEmpty, label: "Change Password") {
                if new.count < Validation.passwordMinLength {
                    error = "Use at least \(Validation.passwordMinLength) characters."
                    return
                }
                if new != repeated {
                    error = "The new passwords don't match."
                    return
                }
                saving = true
                defer { saving = false }
                do {
                    try await client.changePassword(current: current, new: new)
                    current = ""
                    new = ""
                    repeated = ""
                    error = nil
                    model.notices.show("Password changed. Your other devices are signed out.")
                } catch {
                    self.error = FormErrors(error, fields: []).form ?? error.localizedDescription
                }
            }
        } header: {
            Text("Password")
        } footer: {
            Text("At least 8 characters; a few words make a strong one. Changing it signs you out on every other device.")
        }
    }
}

/// An account made with Google or Apple has no password to change; a link by email sets its first one, the same as
/// for a forgotten password.
private struct SetPasswordSection: View {
    @Environment(AppModel.self) private var model
    let me: Me
    @State private var sending = false
    @State private var error: String?

    var body: some View {
        Section {
            HStack {
                Text("You sign in with Google or Apple. A password lets you sign in with your email, too.")
                    .foregroundStyle(.secondary)
                Spacer()
                Button(sending ? "Sending…" : "Email Me a Link to Set a Password") { send() }
                    .disabled(sending)
            }
            if let error {
                Text(error).font(.callout).foregroundStyle(.red)
            }
        } header: {
            Text("Password")
        }
    }

    private func send() {
        sending = true
        Task {
            defer { sending = false }
            do {
                try await model.requestPasswordReset(email: me.email)
                error = nil
                model.notices.show("We sent a link to \(me.email)")
            } catch {
                self.error = error.localizedDescription
            }
        }
    }
}

private struct DevicesSection: View {
    @Environment(AppModel.self) private var model
    let client: SpindleClient
    @State private var sessions: [DeviceSession] = []
    @State private var error: String?

    var body: some View {
        Section {
            ForEach(sessions) { session in
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(session.deviceName ?? "Unknown device")
                        Text(session.isCurrent ? "This Mac" : "Last active \(session.lastActiveAt.formatted(.relative(presentation: .named)))")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    if !session.isCurrent {
                        Button("Sign Out") { revoke(session) }
                    }
                }
            }
            if sessions.count > 1 {
                Button("Sign Out Everywhere Else") { revokeOthers() }
            }
            if let error {
                Text(error).font(.callout).foregroundStyle(.red)
            }
        } header: {
            Text("Where you're signed in")
        } footer: {
            Text("Sign out of a device you no longer use or don't recognize.")
        }
        .task { await load() }
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

private struct DataSection: View {
    @Environment(AppModel.self) private var model
    let me: Me
    let client: SpindleClient
    @State private var exporting = false
    @State private var deleting = false
    @State private var password = ""
    @State private var deleteError: String?

    var body: some View {
        Section {
            HStack {
                Text("A copy of your profile and every time entry you tracked, in every workspace, as a JSON file.")
                    .foregroundStyle(.secondary)
                Spacer()
                Button(exporting ? "Exporting…" : "Export…") { export() }
                    .disabled(exporting)
            }
            HStack {
                Text("Workspaces only you use are deleted with everything in them. In workspaces you share, your time stays for the team's reports, without your name.")
                    .foregroundStyle(.secondary)
                Spacer()
                Button("Delete My Account…", role: .destructive) { deleting = true }
            }
        } header: {
            Text("Your data")
        }
        .sheet(isPresented: $deleting) {
            VStack(alignment: .leading, spacing: 14) {
                Text("Delete your account?").font(.title2.weight(.semibold))
                Text("This cannot be undone. Export your data first if you want to keep it.")
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if me.signsInWithPassword {
                    FormField(label: "Your password", error: deleteError) {
                        SecureField("", text: $password)
                    }
                } else if let deleteError {
                    // Without a password, the server asks for a recent sign-in instead, and says so here.
                    Text(deleteError)
                        .font(.callout)
                        .foregroundStyle(.red)
                        .fixedSize(horizontal: false, vertical: true)
                }
                HStack {
                    Spacer()
                    Button("Cancel", role: .cancel) {
                        deleting = false
                        password = ""
                    }
                    .keyboardShortcut(.cancelAction)
                    Button("Delete Account", role: .destructive) { deleteAccount() }
                        .keyboardShortcut(.defaultAction)
                        .disabled(me.signsInWithPassword && password.isEmpty)
                }
            }
            .padding(24)
            .frame(width: 420)
        }
    }

    private func export() {
        exporting = true
        Task {
            defer { exporting = false }
            do {
                let file = try await client.exportData()
                let panel = NSSavePanel()
                panel.nameFieldStringValue = file.fileName
                panel.allowedContentTypes = [.json]
                guard panel.runModal() == .OK, let url = panel.url else { return }
                try file.data.write(to: url, options: .atomic)
                model.notices.show("Your data was exported")
            } catch {
                model.notices.show(error, fallback: "Couldn't export your data.")
            }
        }
    }

    private func deleteAccount() {
        Task {
            do {
                try await client.deleteAccount(password: me.signsInWithPassword ? password : nil)
                deleting = false
                await model.signOut(notice: "Your account was deleted.")
            } catch {
                deleteError = FormErrors(error, fields: ["password"]).fields["password"] ?? error.localizedDescription
            }
        }
    }
}

/// A section's save button, with what went wrong beside it.
private struct SaveRow: View {
    let error: String?
    let saving: Bool
    let changed: Bool
    var label = "Save"
    let save: () async -> Void

    var body: some View {
        HStack {
            if let error {
                Text(error).font(.callout).foregroundStyle(.red)
            }
            Spacer()
            Button(saving ? "Saving…" : label) {
                Task { await save() }
            }
            .buttonStyle(.brand)
            .disabled(saving || !changed)
        }
    }
}
