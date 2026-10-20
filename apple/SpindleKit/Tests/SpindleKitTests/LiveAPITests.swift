import Foundation
import Testing
@testable import SpindleKit

/// Against a real API, when SPINDLE_API_URL names one, e.g. http://localhost:5258 from scripts/dev.sh.
@Suite(.enabled(if: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] != nil))
struct LiveAPITests {
    let server = URL(string: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] ?? "http://localhost:5258")!
    let keychain = Keychain(service: "com.spindletrack.tests.\(UUID().uuidString)")

    @Test func registersSignsInAndOut() async throws {
        let email = "mac-\(UUID().uuidString.lowercased())@example.com"
        let session = AuthSession(server: server, tokens: keychain)
        let client = SpindleClient(session: session)

        try await session.register(fullName: "Ana Anić", email: email, password: "correct horse battery", timeZone: "Europe/Zagreb")
        let me = try await client.me()
        try await client.switchWorkspace(to: me.defaultWorkspaceId!)
        await session.signOut()

        #expect(me.email == email)
        #expect(me.fullName == "Ana Anić")
        #expect(me.workspaces.count == 1)
        #expect(!session.hasStoredSession)

        let again = AuthSession(server: server, tokens: keychain)
        try await again.signIn(email: email, password: "correct horse battery")
        #expect(try await SpindleClient(session: again).me().email == email)
        await again.signOut()
    }

    @Test func aWrongPasswordIsTurnedDown() async throws {
        let session = AuthSession(server: server, tokens: keychain)
        await #expect(throws: APIError.self) {
            try await session.signIn(email: "nobody-\(UUID().uuidString)@example.com", password: "wrong password")
        }
    }
}

/// Tracking time against a real API: what the generated client sends must be what the server reads.
@MainActor
@Suite(.enabled(if: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] != nil))
struct LiveTrackingTests {
    let server = URL(string: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] ?? "http://localhost:5258")!

    @Test func tracksTimeFromStartToStopAndChangesItAfterwards() async throws {
        let session = AuthSession(server: server, tokens: MemoryTokenStore())
        let client = SpindleClient(session: session)
        try await session.register(
            fullName: "Ana Anić", email: "mac-\(UUID().uuidString.lowercased())@example.com", password: "correct horse battery",
            timeZone: "Europe/Zagreb")
        defer { Task { await session.signOut() } }
        let me = try await client.me()
        let workspace = me.workspaces[0]
        let outbox = await Outbox(api: client, store: try LocalStore())
        let tracker = Tracker(api: client, outbox: outbox, workspace: workspace, time: UserTime(me))
        outbox.onSent = { [weak tracker] change, saved in tracker?.didSend(change, saved) }
        await tracker.load()
        #expect(tracker.state == .loaded)

        let project = try await tracker.createProject(named: "Website")
        let tag = try await tracker.createTag(named: "design")
        try await tracker.start(EntryDetails(description: "Design review", projectId: project.id, tagIds: [tag.id]))
        let started = try #require(tracker.running)
        #expect(started.projectId == project.id)

        // Started a minute earlier than it was.
        try await tracker.updateRunning { $0.start = started.start.addingTimeInterval(-60) }
        let stop = Date()
        guard case .stopped = await tracker.stopRunning(at: stop) else {
            Issue.record("The timer didn't stop")
            return
        }
        #expect(try await client.currentEntry() == nil)

        // The catalog comes back as the server has it, archived projects and clients included.
        _ = try await client.projects(in: workspace.id, includeArchived: true)
        _ = try await client.clients(in: workspace.id, includeArchived: true)
        _ = try await client.tags(in: workspace.id)
        await tracker.refresh()
        #expect(tracker.catalog.project(project.id)?.name == "Website")
        #expect(tracker.catalog.tag(tag.id)?.name == "design")
        let entry = try #require(tracker.entries.first { $0.id == started.id })
        #expect(abs(entry.stop!.timeIntervalSince(stop)) < 0.01)
        #expect(abs(entry.start.timeIntervalSince(started.start.addingTimeInterval(-60))) < 0.01)
        #expect(entry.tagIds == [tag.id])

        // No project any more: sent as a choice with nothing in it.
        try await tracker.update([entry.id], project: (projectId: nil, taskId: nil), removeTagIds: [tag.id])
        let cleared = try #require(tracker.entries.first { $0.id == entry.id })
        #expect(cleared.projectId == nil)
        #expect(cleared.tagIds.isEmpty)

        // Sent again, e.g. after a lost answer: the server answers as the first time.
        let request = EntryRequest(details: EntryDetails(description: "Once"), start: stop.addingTimeInterval(-600), stop: stop)
        let id = newID()
        let first = try await client.createEntry(id: id, in: workspace.id, request)
        let again = try await client.createEntry(id: id, in: workspace.id, request)
        #expect(first == again)

        try await tracker.duplicate(cleared)
        try await tracker.delete(cleared)
        await tracker.refresh()
        #expect(tracker.entries.filter { $0.description == "Design review" }.count == 1)
    }
}

/// Live sync against a real API: two devices of one person, each hearing about the other's changes but not its own.
@MainActor
@Suite(.enabled(if: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] != nil))
struct LiveSyncTests {
    let server = URL(string: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] ?? "http://localhost:5258")!

    @Test func hearsAboutChangesFromOtherDevicesButNotItsOwn() async throws {
        let email = "mac-\(UUID().uuidString.lowercased())@example.com"
        let mac = AuthSession(server: server, tokens: MemoryTokenStore(), deviceName: "Mac")
        try await mac.register(fullName: "Ana Anić", email: email, password: "correct horse battery", timeZone: "Europe/Zagreb")
        let phone = AuthSession(server: server, tokens: MemoryTokenStore(), deviceName: "Phone")
        try await phone.signIn(email: email, password: "correct horse battery")
        defer {
            Task {
                await mac.signOut()
                await phone.signOut()
            }
        }
        let macClient = SpindleClient(session: mac)
        let phoneClient = SpindleClient(session: phone)
        let workspace = try await macClient.me().defaultWorkspaceId!

        let sync = LiveSync(client: macClient)
        var heard: [SyncChange] = []
        sync.onChange = { heard.append($0) }
        sync.watch(workspace)
        sync.start()
        defer { sync.stop() }
        for _ in 0..<100 where sync.state != .connected {
            try await Task.sleep(for: .milliseconds(50))
        }
        #expect(sync.state == .connected)
        #expect(macClient.syncConnection.id != nil)
        // Watching is a call to the server; give it a moment.
        try await Task.sleep(for: .milliseconds(300))

        let now = Date()
        let request = EntryRequest(details: EntryDetails(description: "From the Mac"), start: now.addingTimeInterval(-600), stop: now)
        _ = try await macClient.createEntry(id: newID(), in: workspace, request)
        try await Task.sleep(for: .seconds(1))
        #expect(heard.isEmpty)

        _ = try await phoneClient.createEntry(id: newID(), in: workspace, EntryRequest(details: EntryDetails(description: "From the phone"), start: now.addingTimeInterval(-300), stop: now))
        for _ in 0..<100 where heard.isEmpty {
            try await Task.sleep(for: .milliseconds(50))
        }
        #expect(heard.contains { $0.topic == "entries" && $0.workspaceId == workspace })
    }
}

/// Reports against a real API: every kind loads, exports and saves.
@Suite(.enabled(if: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] != nil))
struct LiveReportTests {
    let server = URL(string: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] ?? "http://localhost:5258")!

    @Test func loadsExportsAndSavesReports() async throws {
        let session = AuthSession(server: server, tokens: MemoryTokenStore())
        let client = SpindleClient(session: session)
        try await session.register(
            fullName: "Ana Anić", email: "mac-\(UUID().uuidString.lowercased())@example.com", password: "correct horse battery",
            timeZone: "Europe/Zagreb")
        let me = try await client.me()
        let workspace = me.workspaces[0].id
        let project = try await client.createProject(named: "Website", color: "#0B83D9", in: workspace)
        let now = Date()
        _ = try await client.createEntry(
            id: newID(), in: workspace,
            EntryRequest(details: EntryDetails(description: "Review", projectId: project.id), start: now.addingTimeInterval(-5400), stop: now.addingTimeInterval(-1800)))

        let time = UserTime(me)
        let today = time.day(of: now)
        var settings = ReportSettings()
        let range = settings.range(today: today, weekStart: time.weekStart)

        let summary = try await client.summaryReport(in: workspace, settings, range: range)
        #expect(summary.totals.seconds == 3600)
        #expect(summary.groups.first?.name == "Website")
        #expect(summary.groups.first?.subGroups.first?.name == "Review")

        settings.tab = .detailed
        let detailed = try await client.detailedReport(in: workspace, settings, range: range)
        #expect(detailed.entries.map(\.description) == ["Review"])

        settings.tab = .weekly
        let weekly = try await client.weeklyReport(in: workspace, settings, range: settings.range(today: today, weekStart: time.weekStart))
        #expect(weekly.days.count == 7)
        #expect(weekly.rows.first?.seconds == 3600)

        for format in ExportFormat.allCases {
            let file = try await client.exportReport(in: workspace, settings, range: range, format: format)
            #expect(file.data.count > 100)
            #expect(file.fileName.hasSuffix(".\(format.rawValue)"))
        }

        settings.projects = [project.id]
        let saved = try await client.saveReport(settings, named: "Website this week", in: workspace)
        #expect(try await client.savedReports(in: workspace).map(\.name) == ["Website this week"])
        #expect(ReportSettings(json: saved.parameters).projects == [project.id])
        _ = try await client.saveReport(settings, named: "Renamed", id: saved.id, in: workspace)
        try await client.deleteSavedReport(saved.id, in: workspace)
        #expect(try await client.savedReports(in: workspace).isEmpty)
        await session.signOut()
    }
}

/// Projects, tasks, clients and tags against a real API, from creation to deletion.
@Suite(.enabled(if: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] != nil))
struct LiveCatalogTests {
    let server = URL(string: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] ?? "http://localhost:5258")!

    @Test func managesProjectsTasksClientsAndTags() async throws {
        let session = AuthSession(server: server, tokens: MemoryTokenStore())
        let client = SpindleClient(session: session)
        try await session.register(
            fullName: "Ana Anić", email: "mac-\(UUID().uuidString.lowercased())@example.com", password: "correct horse battery",
            timeZone: "Europe/Zagreb")
        let workspace = try await client.me().workspaces[0].id

        let acme = try await client.saveClient(named: " Acme ", in: workspace)
        #expect(acme.name == "Acme")
        var fields = ProjectFields(name: "Website", clientId: acme.id, color: "#0B83D9", isBillable: true, estimateSeconds: 36_000, hourlyRate: 80)
        let project = try await client.saveProject(fields, in: workspace)
        #expect(project.clientId == acme.id)
        #expect(project.hourlyRate == 80)

        // A rate left out is no rate.
        fields.hourlyRate = nil
        fields.name = "Website relaunch"
        let saved = try await client.saveProject(fields, id: project.id, in: workspace)
        #expect(saved.name == "Website relaunch")
        #expect(saved.hourlyRate == nil)
        #expect(saved.estimateSeconds == 36_000)

        let design = try await client.saveTask(named: "Design", project: project.id, in: workspace)
        let done = try await client.saveTask(named: "Design", isDone: true, id: design.id, project: project.id, in: workspace)
        #expect(done.isDone)
        let other = try await client.saveTask(named: "Build", project: project.id, in: workspace)
        try await client.deleteTask(other.id, project: project.id, in: workspace)
        #expect(try await client.project(project.id, in: workspace).tasks.map(\.name) == ["Design"])

        #expect(try await client.setProjectArchived(project.id, true, in: workspace).isArchived)
        #expect(try await client.projects(in: workspace).isEmpty)
        #expect(try await client.setProjectArchived(project.id, false, in: workspace).isArchived == false)

        let now = Date()
        _ = try await client.createEntry(
            id: newID(), in: workspace,
            EntryRequest(details: EntryDetails(description: "Review", projectId: project.id), start: now.addingTimeInterval(-600), stop: now))
        try await client.deleteProject(project.id, in: workspace)
        // The time stays, without a project.
        let entries = try await client.timeEntries(in: workspace, from: now.addingTimeInterval(-3600), to: now.addingTimeInterval(60))
        #expect(entries.map(\.projectId) == [nil])

        let tag = try await client.createTag(named: "design", in: workspace)
        #expect(try await client.renameTag(tag.id, to: "Design", in: workspace).name == "Design")
        try await client.deleteTag(tag.id, in: workspace)
        #expect(try await client.tags(in: workspace).isEmpty)

        #expect(try await client.setClientArchived(acme.id, true, in: workspace).archivedAt != nil)
        _ = try await client.setClientArchived(acme.id, false, in: workspace)
        try await client.deleteClient(acme.id, in: workspace)
        #expect(try await client.clients(in: workspace, includeArchived: true).isEmpty)
        await session.signOut()
    }
}

/// The account against a real API: profile, preferences, devices, password, data export and deletion.
@Suite(.enabled(if: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] != nil))
struct LiveAccountTests {
    let server = URL(string: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] ?? "http://localhost:5258")!

    @Test func managesTheAccountFromProfileToDeletion() async throws {
        let email = "mac-\(UUID().uuidString.lowercased())@example.com"
        let mac = AuthSession(server: server, tokens: MemoryTokenStore(), deviceName: "Mac")
        try await mac.register(fullName: "Ana Anić", email: email, password: "correct horse battery", timeZone: "Europe/Zagreb")
        let phone = AuthSession(server: server, tokens: MemoryTokenStore(), deviceName: "Phone")
        try await phone.signIn(email: email, password: "correct horse battery")
        let client = SpindleClient(session: mac)

        let renamed = try await client.updateProfile(fullName: " Ana Kovač ", timeZone: "Asia/Tokyo")
        #expect(renamed.fullName == "Ana Kovač")
        #expect(renamed.timeZone == "Asia/Tokyo")

        var preferences = PreferenceChanges(renamed.preferences)
        preferences.timeFormat = "12h"
        preferences.pomodoroEnabled = true
        preferences.pomodoroMinutes = 50
        let updated = try await client.updatePreferences(preferences)
        #expect(updated.preferences.timeFormat == "12h")
        #expect(updated.preferences.pomodoroMinutes == 50)

        let sessions = try await client.sessions()
        #expect(Set(sessions.compactMap(\.deviceName)) == ["Mac", "Phone"])
        #expect(sessions.first { $0.isCurrent }?.deviceName == "Mac")

        let export = try await client.exportData()
        let json = try JSONSerialization.jsonObject(with: export.data) as? [String: Any]
        #expect(json != nil)
        #expect(export.fileName.hasPrefix("spindle-data-"))

        // A new password signs the phone out the next time it renews its token; the Mac stays in.
        try await client.changePassword(current: "correct horse battery", new: "battery staple horse")
        let phoneToken = try await phone.validAccessToken()
        await #expect(throws: APIError.sessionEnded) { try await phone.renewAfterRejection(of: phoneToken) }
        #expect(try await client.me().fullName == "Ana Kovač")
        #expect(try await client.sessions().count == 1)

        await #expect(throws: APIError.self) { try await client.deleteAccount(password: "wrong") }
        try await client.deleteAccount(password: "battery staple horse")
        let again = AuthSession(server: server, tokens: MemoryTokenStore())
        await #expect(throws: APIError.self) { try await again.signIn(email: email, password: "battery staple horse") }
    }
}

/// Agent activity against a real API: links, kept suggestions and uploads, as the Mac sends them.
@MainActor
@Suite(.enabled(if: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] != nil))
struct LiveAgentActivityTests {
    let server = URL(string: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] ?? "http://localhost:5258")!

    @Test func linksKeptEntriesAndActivityReachTheAccount() async throws {
        let session = AuthSession(server: server, tokens: MemoryTokenStore())
        let client = SpindleClient(session: session)
        try await session.register(
            fullName: "Ana Anić", email: "mac-\(UUID().uuidString.lowercased())@example.com", password: "correct horse battery",
            timeZone: "Europe/Zagreb")
        defer { Task { await session.signOut() } }
        let workspace = try await client.me().workspaces[0]
        let outbox = await Outbox(api: client, store: try LocalStore())
        let tracker = Tracker(api: client, outbox: outbox, workspace: workspace, time: UserTime(try await client.me()))
        await tracker.load()
        let project = try await tracker.createProject(named: "Atlas")
        let start = Date().addingTimeInterval(-7200)
        let entry = try await client.createEntry(
            id: UUID().uuidString.lowercased(), in: workspace.id,
            EntryRequest(details: EntryDetails(description: "Agents", projectId: project.id), start: start, stop: start.addingTimeInterval(3600)))
        let atlas = ActivityRepository(key: "/code/atlas", name: "atlas", origin: "git@github.com:acme/atlas.git", isRepository: true)
        let scratch = ActivityRepository(key: "/tmp/scratch", name: "scratch", origin: nil, isRepository: false)
        let sessions = [
            AgentSession(id: "claude:a", source: .claude, parentID: nil, cwd: atlas.key, title: "Checkout", branches: ["main"],
                         events: [AgentEvent(date: start, isTouch: true), AgentEvent(date: start.addingTimeInterval(60), isTouch: false)]),
            AgentSession(id: "codex:b", source: .codex, parentID: nil, cwd: scratch.key, title: "", branches: [],
                         events: [AgentEvent(date: start.addingTimeInterval(600), isTouch: true)]),
        ]

        try await client.linkRepository(identity: atlas.identity!, name: atlas.name, to: project.id, in: workspace.id)
        try await client.dismissSplitSuggestion(entryId: entry.id, in: workspace.id)
        await #expect(throws: APIError.self) {
            try await client.uploadAgentSessions([AgentSessionUpload(session: sessions[0], repository: atlas, titles: false)], device: UUID().uuidString)
        }
        let settings = try await client.updateAgentActivitySettings(sync: true, titles: true)
        let uploader = ActivityUploader(file: nil)
        let sent = try await uploader.upload(
            syncedSince: try #require(settings.syncedSince), titles: true,
            sessions: { _ in [(sessions[0], atlas), (sessions[1], scratch)] },
            send: { try await client.uploadAgentSessions($0, device: UUID().uuidString) })

        #expect(try await client.repositoryLinks().map { [$0.identity, $0.projectId] } == [["origin:github.com/acme/atlas", project.id]])
        #expect(try await client.dismissedSplitSuggestions() == [entry.id])
        #expect(sent.sent == 2 && sent.rejected == 0)
        #expect(try await client.me().agentActivity?.titles == true)
    }
}

/// Agent proposals against a real API: listed with what stands in their way, applied, discarded and reopened.
@Suite(.enabled(if: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] != nil))
struct LiveAgentProposalTests {
    let server = URL(string: ProcessInfo.processInfo.environment["SPINDLE_API_URL"] ?? "http://localhost:5258")!

    @Test func proposalsAreListedAppliedDiscardedAndReopened() async throws {
        let session = AuthSession(server: server, tokens: MemoryTokenStore(), deviceName: "Mac")
        try await session.register(fullName: "Ana Anić", email: "proposals-\(UUID().uuidString.lowercased())@example.com",
                                   password: "correct horse battery", timeZone: "Europe/Zagreb")
        defer { Task { await session.signOut() } }
        let client = SpindleClient(session: session)
        let workspace = try await client.me().defaultWorkspaceId!
        let day = Date().addingTimeInterval(-86_400).formatted(.iso8601.year().month().day())
        // As an agent proposes them over MCP: two entries that overlap each other.
        let proposed = try await client.call {
            try await client.api.proposeEntries(path: .init(workspaceId: workspace), body: .json(.init(entries: [
                .init(start: "\(day)T09:00", stop: "\(day)T10:00", description: "Fix the login redirect", reason: "Commits on fix/login"),
                .init(start: "\(day)T09:30", stop: "\(day)T11:00", description: "Review the API"),
            ], client: "Claude Code"))).ok.body.json
        }
        #expect(proposed.proposals.count == 2)

        let list = try await client.agentProposals(in: workspace)
        #expect(list.pending.map(\.fields.description) == ["Fix the login redirect", "Review the API"])
        #expect(list.pending.allSatisfy { $0.client == "Claude Code" && $0.isNew })
        #expect(list.issues.values.allSatisfy { issues in issues.contains { !$0.blocking } })

        let entry = try await client.applyAgentProposal(list.pending[0].id, in: workspace)
        #expect(entry.description == "Fix the login redirect")
        try await client.discardAgentProposal(list.pending[1].id, in: workspace)
        #expect(try await client.agentProposals(in: workspace).pending.isEmpty)

        try await client.reopenAgentProposal(list.pending[1].id, in: workspace)
        #expect(try await client.agentProposals(in: workspace).pending.map(\.id) == [list.pending[1].id])
        try await client.savedAgentProposal(list.pending[1].id, as: entry.id, in: workspace)
        #expect(try await client.agentProposals(in: workspace).pending.isEmpty)
    }
}
