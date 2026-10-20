import SwiftUI
import SpindleKit
import UniformTypeIdentifiers

/// Spindle's settings: this Mac's, the Pomodoro, and the account's.
struct SettingsView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        TabView {
            GeneralSettings()
                .tabItem { Label("General", systemImage: "gearshape") }
            PomodoroSettings()
                .tabItem { Label("Pomodoro", systemImage: "timer") }
            AgentSettings()
                .tabItem { Label("Agents", systemImage: "sparkles") }
            AccountSettings()
                .tabItem { Label("Account", systemImage: "person.crop.circle") }
        }
        .frame(width: 560)
    }
}

// MARK: - General

struct GeneralSettings: View {
    @Environment(Desktop.self) private var desktop
    @Environment(AppModel.self) private var model
    @Environment(Updates.self) private var updates
    @State private var notificationsDenied = false
    @State private var branchRules: BranchRulesTarget?
    /// The linked repository whose project is being chosen, by key.
    @State private var choosingProject: String?
    @State private var changingSync = false
    @State private var syncError: String?

    var body: some View {
        @Bindable var settings = desktop.settings
        Form {
            Section {
                Toggle("Open Spindle when you log in", isOn: Binding(get: { settings.opensAtLogin }, set: { settings.opensAtLogin = $0 }))
                if settings.loginItemNeedsApproval {
                    Text("Allow Spindle in System Settings › General › Login Items.").font(.caption).foregroundStyle(.secondary)
                }
                if let error = settings.loginItemError {
                    Text(error).font(.caption).foregroundStyle(.red)
                }
                Toggle("Show the timer in the menu bar", isOn: $settings.showMenuBarTimer)
            }

            Section("Keyboard shortcuts") {
                ForEach(ShortcutAction.allCases) { action in
                    LabeledContent {
                        ShortcutRecorder(combo: Binding(
                            get: { settings.shortcuts[action] },
                            set: { combo in
                                settings.shortcuts[action] = combo
                                desktop.applyShortcuts()
                            }))
                    } label: {
                        Text(action.label)
                        Text(desktop.refusedShortcuts.contains(action) ? "Another app has this shortcut. Choose another." : action.detail)
                            .foregroundStyle(desktop.refusedShortcuts.contains(action) ? .red : .secondary)
                    }
                }
            }

            Section {
                Toggle("Ask about time away from a running timer", isOn: $settings.detectIdle)
                Picker("When away for", selection: $settings.idleMinutes) {
                    ForEach([3, 5, 10, 15, 30], id: \.self) { Text("\($0) minutes").tag($0) }
                }
                .disabled(!settings.detectIdle)
            } header: {
                Text("Time away")
            } footer: {
                Text("When you come back, keep the time, drop it, or drop it and go on with the same entry.")
            }

            Section {
                Toggle("Remind me to track my time", isOn: Binding(get: { settings.remindToTrack }, set: { on in
                    settings.remindToTrack = on
                    if on {
                        Task { notificationsDenied = !(await desktop.allowNotifications()) }
                    }
                }))
                ReminderDays(days: $settings.reminders.days)
                    .disabled(!settings.remindToTrack)
                // One row, with the two times together on the right as a range, like the rows' other values.
                LabeledContent("From") {
                    HStack(spacing: 8) {
                        MinutePicker(label: "From", minutes: $settings.reminders.from)
                        Text("to").foregroundStyle(.secondary)
                        MinutePicker(label: "To", minutes: $settings.reminders.to)
                    }
                    .labelsHidden()
                    .fixedSize()
                }
                .disabled(!settings.remindToTrack)
                Picker("Every", selection: $settings.reminders.everyMinutes) {
                    ForEach([15, 30, 60, 120], id: \.self) { Text($0 < 60 ? "\($0) minutes" : $0 == 60 ? "hour" : "\($0 / 60) hours").tag($0) }
                }
                .disabled(!settings.remindToTrack)
                if notificationsDenied {
                    Text("Notifications are off for Spindle. Turn them on in System Settings › Notifications.")
                        .font(.caption).foregroundStyle(.red)
                }
            } header: {
                Text("Reminders")
            } footer: {
                Text("A notification when nothing is running during your working hours and you're at the Mac.")
            }

            Section {
                Toggle(isOn: Binding(get: { settings.useAgentLogs }, set: { on in
                    settings.useAgentLogs = on
                    if on && settings.notifySwitches {
                        Task { notificationsDenied = !(await desktop.allowNotifications()) }
                    }
                })) {
                    Text("Suggest splits from agent activity")
                    Text("When you worked on more than one project during an entry, offer to split its time.")
                }
                ForEach(AgentSource.allCases, id: \.self) { source in
                    activitySource(source, enabled: Binding(get: { settings.reads(source) }, set: { settings.setReads(source, $0) }))
                        .disabled(!settings.useAgentLogs)
                }
                Toggle(isOn: Binding(get: { settings.notifySwitches }, set: { on in
                    settings.notifySwitches = on
                    if on {
                        Task { notificationsDenied = !(await desktop.allowNotifications()) }
                    }
                })) {
                    Text("Notify me when the work moves to another project")
                    Text("While the timer runs on one project and your agents have been busy in another for a while.")
                }
                .disabled(!settings.useAgentLogs)
                Toggle(isOn: $settings.notifyDayReview) {
                    Text("Remind me to review the day")
                    Text("Once, after your working hours, when the day has splits or untracked agent time waiting.")
                }
                .disabled(!settings.useAgentLogs || !settings.notifySwitches)
                if notificationsDenied && settings.notifySwitches && settings.useAgentLogs {
                    Text("Notifications are off for Spindle. Turn them on in System Settings › Notifications.")
                        .font(.caption).foregroundStyle(.red)
                }
                if model.me != nil {
                    activitySync
                }
                LabeledContent {
                    Button("Show What the Logs Found…") {
                        desktop.onboarding = true
                        desktop.showMainWindow()
                    }
                    .disabled(!settings.useAgentLogs || model.tracker == nil)
                } label: {
                    Text("Last week in the logs")
                    Text("What the last 7 days of logs found in your tracked time, and the repositories to link to projects.")
                }
            } header: {
                Text("Parallel work")
            } footer: {
                Text("Reads \(AgentSource.names) logs on this Mac. Only times, folders, branches and session titles are kept, never prompts or answers. The logs stay on this Mac unless you sync them; the projects you choose for repositories are kept with your account. Nothing is split until you say so.")
            }

            if settings.useAgentLogs, let tracker = model.tracker {
                linkedRepositories(tracker)
            }

            Section {
                Toggle("Try beta features", isOn: $settings.betaFeatures)
                if settings.betaFeatures {
                    Toggle(isOn: $settings.showAgentActivity) {
                        Text("Agent activity in the calendar")
                        Text("Your focus and agent runs from \(AgentSource.names), beside your entries.")
                    }
                    .disabled(!settings.useAgentLogs)
                }
            } header: {
                Text("Beta")
            } footer: {
                Text("Features that are still being built. They may change or go away.")
            }

            Section("Updates") {
                if updates.isAvailable {
                    Toggle("Check for updates automatically", isOn: Bindable(updates).checksAutomatically)
                }
                LabeledContent {
                    if updates.isAvailable {
                        Button("Check Now") { updates.checkForUpdates() }
                            .disabled(!updates.canCheck)
                    }
                } label: {
                    Text("Version \(Bundle.main.version)")
                    if !updates.isAvailable {
                        Text("This build doesn't update itself.")
                    }
                }
            }
        }
        .formStyle(.grouped)
        .frame(height: 700)
        // "Unlinked atlas · Undo", after unlinking a repository here.
        .overlay(alignment: .bottom) {
            if let notice = model.notices.current {
                NoticeView(notice: notice, perform: { model.notices.perform() }) { model.notices.dismiss() }
                    .padding(.bottom, 16)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(.snappy, value: model.notices.current)
        .sheet(item: $branchRules) { target in
            if let tracker = model.tracker {
                BranchRulesEditor(repository: target.repository, tracker: tracker, splits: desktop.splits)
                    .environment(model.notices)
            }
        }
        .task {
            let notifies = settings.remindToTrack || (settings.useAgentLogs && settings.notifySwitches)
            notificationsDenied = notifies ? await desktop.notificationsDenied() : false
        }
    }

    /// Uploading agent activity to the account, so the web suggests splits too. An account setting, on every Mac.
    /// The repositories linked to projects of this workspace, each with its branch rules, and those the person said
    /// go to no project.
    @ViewBuilder private func linkedRepositories(_ tracker: Tracker) -> some View {
        let splits = desktop.splits
        let linked = (splits.chosen.filter { tracker.catalog.project($0.value) != nil }
            .map { (repository: splits.repository($0.key), projectId: Optional($0.value)) }
            + splits.noProject.map { (repository: splits.repository($0), projectId: nil) })
            .sorted { $0.repository.name.localizedStandardCompare($1.repository.name) == .orderedAscending }
        if !linked.isEmpty {
            Section {
                ForEach(linked, id: \.repository.key) { link in
                    let rules = splits.rules[link.repository.identity ?? ""] ?? []
                    LabeledContent {
                        HStack(spacing: 8) {
                            repositoryProject(link.repository, projectId: link.projectId, tracker: tracker)
                            if link.projectId != nil {
                                BranchRulesButton(repository: link.repository, short: true, editing: $branchRules)
                            }
                        }
                    } label: {
                        Text(link.repository.name)
                        if !rules.isEmpty {
                            Text(rules.map { "\($0.pattern) → \(tracker.catalog.project($0.projectId)?.name ?? "a project")" }.joined(separator: " · "))
                        }
                    }
                    .contextMenu {
                        if link.projectId != nil {
                            Button("Unlink", role: .destructive) { desktop.unlink(link.repository) }
                        }
                    }
                }
            } header: {
                Text("Linked repositories")
            } footer: {
                Text("Where each repository's work goes; No Project unlinks it. Branches can go to another project, e.g. a client's branches in a monorepo.")
            }
        }
    }

    /// The project a repository's work goes to, chosen in the usual picker, where "No project" unlinks it.
    private func repositoryProject(_ repository: ActivityRepository, projectId: String?, tracker: Tracker) -> some View {
        let project = tracker.catalog.project(projectId)
        return Button {
            choosingProject = repository.key
        } label: {
            HStack(spacing: 6) {
                if let project {
                    ProjectDot(color: project.color)
                    Text(project.name).lineLimit(1)
                } else {
                    Text("No Project").foregroundStyle(.secondary)
                }
                Image(systemName: "chevron.up.chevron.down").font(.caption2).foregroundStyle(.tertiary)
            }
        }
        .buttonStyle(.bordered)
        .help("Where \(repository.name)'s agent work goes. Choose No project to unlink it.")
        .accessibilityLabel("\(repository.name): \(project?.name ?? "no project")")
        .popover(isPresented: Binding(get: { choosingProject == repository.key }, set: { if !$0 { choosingProject = nil } }), arrowEdge: .bottom) {
            ProjectPicker(
                tracker: tracker, projectId: projectId, taskId: nil,
                onChange: { picked, _ in
                    if let picked {
                        if picked != projectId { desktop.splits.link(repository, to: picked) }
                    } else if !desktop.splits.isNoProject(repository) {
                        desktop.unlink(repository)
                    }
                },
                taken: { _, task in task != nil },
                placeholder: "Which project is \(repository.name) for?")
            .environment(model.notices)
        }
    }

    @ViewBuilder private var activitySync: some View {
        let sync = desktop.activitySync
        Toggle(isOn: Binding(get: { sync.isSyncing }, set: { on in change(sync: on, titles: false) })) {
            Text("Sync agent activity to your account")
            Text("So the web suggests splits too. Sends when you sent prompts, when agents were busy, and each repository's name, git origin and branches; never folder paths, prompts or answers. On all your Macs. Turning it off deletes what was sent.")
        }
        // Turning sync on needs the logs read here; turning it off, which deletes what was sent, never does.
        .disabled((!settings.useAgentLogs && !sync.isSyncing) || changingSync)
        Toggle(isOn: Binding(get: { sync.includesTitles }, set: { on in change(sync: true, titles: on) })) {
            Text("Include session titles")
            Text("A session's title can be the first line of a prompt. Splits work without them.")
        }
        .disabled(!sync.isSyncing || (!settings.useAgentLogs && !sync.includesTitles) || changingSync)
        if !settings.useAgentLogs && sync.isSyncing {
            Text("Reading agent logs is off on this device, so it sends nothing new. Turn sync off to delete what was sent.")
                .font(.caption).foregroundStyle(.secondary)
        }
        if let problem = syncError ?? (sync.isSyncing ? sync.problem : nil) {
            Text(problem).font(.caption).foregroundStyle(.red)
        }
    }

    private var settings: DesktopSettings { desktop.settings }

    private func change(sync: Bool, titles: Bool) {
        changingSync = true
        syncError = nil
        Task {
            do {
                try await desktop.activitySync.change(sync: sync, titles: titles)
            } catch {
                syncError = (error as? LocalizedError)?.errorDescription ?? "Couldn't change agent activity sync."
            }
            changingSync = false
        }
    }

    private func activitySource(_ source: AgentSource, enabled: Binding<Bool>) -> some View {
        let path = source.format.displayPath
        let found = FileManager.default.fileExists(atPath: (path as NSString).expandingTildeInPath)
        return Toggle(isOn: enabled) {
            Text(source.label)
            Text(path + (found ? "" : " · Not found")).font(.caption).foregroundStyle(.secondary)
        }
        .toggleStyle(.checkbox)
    }
}

/// Marks a feature that is still being built.
struct BetaBadge: View {
    var body: some View {
        Text("Beta")
            .font(.caption2.weight(.semibold))
            .foregroundStyle(.secondary)
            .padding(.horizontal, 5)
            .padding(.vertical, 1)
            .background(Color.primary.opacity(0.07), in: Capsule())
            .accessibilityLabel("Beta")
    }
}

/// The days of the week to remind on, as toggles.
private struct ReminderDays: View {
    @Binding var days: Set<Int>
    private let names = ["S", "M", "T", "W", "T", "F", "S"]
    private let fullNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"]

    var body: some View {
        LabeledContent("On") {
            HStack(spacing: 4) {
                ForEach([1, 2, 3, 4, 5, 6, 0], id: \.self) { day in
                    Toggle(names[day], isOn: Binding(
                        get: { days.contains(day) },
                        set: { on in if on { days.insert(day) } else { days.remove(day) } }))
                        .toggleStyle(.button)
                        .accessibilityLabel(fullNames[day])
                }
            }
        }
    }
}

/// A time of day, as minutes after midnight.
private struct MinutePicker: View {
    let label: String
    @Binding var minutes: Int

    var body: some View {
        Picker(label, selection: $minutes) {
            ForEach(Array(stride(from: 0, through: 24 * 60, by: 30)), id: \.self) { minute in
                Text(String(format: "%02d:%02d", minute / 60 % 24, minute % 60) + (minute == 24 * 60 ? " (midnight)" : "")).tag(minute)
            }
        }
    }
}

/// Records a key combination for a shortcut: click it, then press the keys.
private struct ShortcutRecorder: View {
    @Binding var combo: KeyCombo?
    @State private var recording = false
    @State private var monitor: Any?
    @State private var hint: String?

    var body: some View {
        HStack(spacing: 6) {
            Button {
                recording ? stop() : start()
            } label: {
                Text(recording ? (hint ?? "Press the keys…") : combo?.display ?? "Record Shortcut")
                    .frame(minWidth: 110)
                    .monospaced(combo != nil && !recording)
            }
            if combo != nil && !recording {
                Button {
                    combo = nil
                } label: {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
                .help("Remove the shortcut")
            }
        }
        .onDisappear(perform: stop)
    }

    private func start() {
        recording = true
        hint = nil
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            if event.keyCode == 53 { // Escape
                stop()
                return nil
            }
            let pressed = KeyCombo(event: event)
            guard pressed.isUsable else {
                hint = "Add ⌘, ⌃ or ⌥"
                return nil
            }
            combo = pressed
            stop()
            return nil
        }
    }

    private func stop() {
        recording = false
        if let monitor {
            NSEvent.removeMonitor(monitor)
        }
        monitor = nil
    }
}

// MARK: - Pomodoro

/// A number of minutes, shown beside its stepper on the right like the other rows' values.
private struct MinutesStepper: View {
    let label: String
    @Binding var minutes: Int
    let range: ClosedRange<Int>
    var step = 1

    var body: some View {
        LabeledContent(label) {
            HStack(spacing: 8) {
                Text(minutes == 1 ? "1 minute" : "\(minutes) minutes")
                    .monospacedDigit()
                Stepper(label, value: $minutes, in: range, step: step)
                    .labelsHidden()
            }
        }
    }
}

struct PomodoroSettings: View {
    @Environment(AppModel.self) private var model
    @Environment(Desktop.self) private var desktop
    @State private var values: PreferenceChanges?
    @State private var saving = false
    @State private var error: String?

    var body: some View {
        Form {
            if let values = Binding($values) {
                Section {
                    Toggle("Use the Pomodoro", isOn: values.pomodoroEnabled)
                    MinutesStepper(label: "Focus", minutes: values.pomodoroMinutes, range: 5...180, step: 5)
                        .disabled(!values.wrappedValue.pomodoroEnabled)
                    MinutesStepper(label: "Break", minutes: values.pomodoroBreakMinutes, range: 1...60)
                        .disabled(!values.wrappedValue.pomodoroEnabled)
                    Toggle("Stop the timer when a focus interval ends", isOn: values.pomodoroAutoStop)
                        .disabled(!values.wrappedValue.pomodoroEnabled)
                    // Saved to the account, so with a button at the end of the section, as in the account settings.
                    HStack {
                        if let error {
                            Text(error).foregroundStyle(.red).font(.callout)
                        }
                        Spacer()
                        Button(saving ? "Saving…" : "Save") { save() }
                            .buttonStyle(.brand)
                            .disabled(saving || values.wrappedValue == model.me.map { PreferenceChanges($0.preferences) })
                    }
                } footer: {
                    Text("Work in focused intervals with short breaks. You get a reminder when each interval ends. These settings are your account's, the same on every device.")
                }
            } else {
                Text("Sign in to set up the Pomodoro.").foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(height: 360)
        .onAppear { values = model.me.map { PreferenceChanges($0.preferences) } }
    }

    private func save() {
        guard let values else { return }
        let client = model.client
        saving = true
        Task {
            defer { saving = false }
            do {
                _ = try await client.updatePreferences(values)
                try? await model.refreshAccount()
                error = nil
                if values.pomodoroEnabled {
                    _ = await desktop.allowNotifications()
                }
                model.notices.show("Pomodoro saved")
            } catch {
                self.error = error.localizedDescription
            }
        }
    }
}
