import SwiftUI
import SpindleKit

/// The account and the workspace: who you are, how times are shown, the Pomodoro, security, your data, and signing
/// out. Preferences are the account's, the same on every device, and save as they change.
struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openURL) private var openURL
    @State private var naming = false
    @State private var workspaceName = ""

    var body: some View {
        NavigationStack {
            List {
                if let me = model.me {
                    Section {
                        NavigationLink {
                            ProfileView(me: me, client: model.client)
                        } label: {
                            HStack(spacing: 14) {
                                Avatar(name: me.fullName, size: 48)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(me.fullName).font(.headline)
                                    Text(me.email).font(.subheadline).foregroundStyle(.secondary)
                                }
                            }
                            .padding(.vertical, 4)
                        }
                    }
                    Section {
                        Picker("Workspace", selection: Binding(get: { model.workspace?.id ?? "" }, set: { switchWorkspace(to: $0) })) {
                            ForEach(me.workspaces, id: \.id) { workspace in
                                Text(workspace.name).tag(workspace.id)
                            }
                        }
                        if model.canCreateWorkspaces {
                            Button("New Workspace…") {
                                workspaceName = ""
                                naming = true
                            }
                        }
                    } footer: {
                        Text("Projects, clients and the time you track belong to a workspace.")
                    }
                    if let organization = model.planOrganization {
                        Section {
                            NavigationLink {
                                PlanView(organization: organization)
                            } label: {
                                LabeledContent("Plan") {
                                    if let summary = organization.planSummary {
                                        Text(summary)
                                    }
                                }
                            }
                        }
                    }
                    PreferencesSections(me: me, client: model.client)
                    Section("Security") {
                        NavigationLink("Passkeys") {
                            Form { PasskeysSection() }.navigationTitle("Passkeys")
                        }
                        if me.signsInWithPassword {
                            NavigationLink("Change Password") { PasswordView(client: model.client) }
                        } else {
                            SetPasswordButton(me: me)
                        }
                        NavigationLink("Where You're Signed In") { SessionsView(client: model.client) }
                    }
                    Section {
                        ExportDataButton(client: model.client)
                        NavigationLink("Delete Account") { DeleteAccountView(me: me, client: model.client) }
                            .foregroundStyle(.red)
                    } header: {
                        Text("Your data")
                    } footer: {
                        Text("A copy of your profile and every time entry you tracked, in every workspace, as a JSON file.")
                    }
                }
                Section {
                    Button("Open Spindle on the Web") { openURL(model.webURL("")) }
                    Button("Sign Out", role: .destructive) { model.requestSignOut() }
                } footer: {
                    Text("Spindle \(Bundle.main.version)")
                        .frame(maxWidth: .infinity)
                        .padding(.top, 12)
                }
            }
            .navigationTitle("Settings")
            // Whether this is the hosted service, which has plans, when the app opened already signed in.
            .task { if model.serverInfo == nil { await model.loadServerInfo() } }
            .alert("New workspace", isPresented: $naming) {
                TextField("Workspace name", text: $workspaceName)
                Button("Cancel", role: .cancel) {}
                Button("Create") { createWorkspace() }
            } message: {
                Text("A workspace of its own, in the same organization, with its own projects, clients and tags.")
            }
        }
    }

    private func switchWorkspace(to id: String) {
        Task {
            do {
                try await model.switchWorkspace(to: id)
            } catch {
                model.notices.show(error, fallback: "Couldn't switch workspaces.")
            }
        }
    }

    private func createWorkspace() {
        let name = workspaceName.trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty else { return }
        Task {
            do {
                try await model.createWorkspace(named: name)
                model.notices.show("Workspace “\(name)” created")
            } catch {
                model.notices.show(error, fallback: "Couldn't create the workspace.")
            }
        }
    }
}

/// How dates, times and durations are shown, and the Pomodoro: the account's preferences, saved a moment after
/// they change.
private struct PreferencesSections: View {
    @Environment(AppModel.self) private var model
    let me: Me
    let client: SpindleClient
    @State private var values: PreferenceChanges

    init(me: Me, client: SpindleClient) {
        self.me = me
        self.client = client
        _values = State(initialValue: PreferenceChanges(me.preferences))
    }

    private static let weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"]
    private static let dateFormats = ["d MMM yyyy", "dd.MM.yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd"]

    var body: some View {
        Section {
            Picker("First day of the week", selection: $values.weekStart) {
                ForEach(0..<7) { Text(Self.weekdays[$0]).tag($0) }
            }
            Picker("Date format", selection: $values.dateFormat) {
                ForEach(Self.dateFormats, id: \.self) { format in
                    Text(DayKey(year: 2026, month: 9, day: 28).formatted(format)).tag(format)
                }
            }
            Picker("Time format", selection: $values.timeFormat) {
                Text("24-hour (13:30)").tag("24h")
                Text("12-hour (1:30 PM)").tag("12h")
            }
            Picker("Duration format", selection: $values.durationFormat) {
                Text("1:30:00").tag("hms")
                Text("1:30").tag("hm")
                Text("1.50 h").tag("decimal")
            }
        } header: {
            Text("Preferences")
        } footer: {
            Text("How dates, times and durations are shown to you, on every device.")
        }
        .task(id: values) { await save() }
        Section {
            Toggle("Use the Pomodoro", isOn: $values.pomodoroEnabled)
            if values.pomodoroEnabled {
                Stepper("Focus: \(values.pomodoroMinutes) minutes", value: $values.pomodoroMinutes, in: 5...180, step: 5)
                Stepper("Break: \(values.pomodoroBreakMinutes) minutes", value: $values.pomodoroBreakMinutes, in: 1...60)
                Toggle("Stop the timer when a focus interval ends", isOn: $values.pomodoroAutoStop)
            }
        } header: {
            Text("Pomodoro")
        } footer: {
            Text("Work in focused intervals with short breaks; Spindle tells you when each ends.")
        }
    }

    private func save() async {
        guard values != PreferenceChanges(me.preferences) else { return }
        try? await Task.sleep(for: .milliseconds(600))
        guard !Task.isCancelled else { return }
        do {
            _ = try await client.updatePreferences(values)
            try? await model.refreshAccount()
        } catch {
            model.notices.show(error, fallback: "Couldn't save your preferences.")
        }
    }
}

/// Exports everything tracked, and offers the file to save or share.
private struct ExportDataButton: View {
    @Environment(AppModel.self) private var model
    let client: SpindleClient
    @State private var exporting = false
    @State private var exported: ExportedFile?

    var body: some View {
        Button {
            export()
        } label: {
            HStack {
                Text("Export My Data")
                Spacer()
                if exporting { ProgressView() }
            }
        }
        .disabled(exporting)
        .sheet(item: $exported) { file in
            ShareSheet(url: file.url)
        }
    }

    private func export() {
        exporting = true
        Task {
            defer { exporting = false }
            do {
                let file = try await client.exportData()
                let folder = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString, directoryHint: .isDirectory)
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                let url = folder.appending(path: file.fileName)
                try file.data.write(to: url, options: .atomic)
                exported = ExportedFile(url: url)
            } catch {
                model.notices.show(error, fallback: "Couldn't export your data.")
            }
        }
    }
}
