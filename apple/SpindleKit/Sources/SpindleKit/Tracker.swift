import Foundation
import Observation

/// The signed-in person's time in one workspace: the running timer, their entries a week at a time, and what the
/// entries point at.
///
/// What it shows is the server's data with the changes still in the outbox applied on top. A change shows the
/// moment it is made, stays while it is on its way (or waits for the network), and if the server turns it down it
/// simply drops out and the error is thrown or reported. The last data loaded is kept on the device, so the timer
/// works without a connection.
@MainActor
@Observable
public final class Tracker {
    public enum LoadState: Equatable {
        case loading
        case loaded
        case failed(String)
    }

    /// Why stopping the timer did not stop it.
    public enum StopOutcome {
        case stopped
        /// The workspace requires details the entry doesn't have; the app should ask for them.
        case needsDetails(TimeEntry)
        case failed(Error)
    }

    /// Whether a change reached the server, or waits for it.
    public enum SaveOutcome: Equatable {
        case saved
        case waiting
    }

    public let workspace: WorkspaceSummary
    public let time: UserTime
    public let catalog = Catalog()
    public let outbox: Outbox
    public private(set) var settings: WorkspaceSettings?
    /// How many weeks are loaded, counting back from this one.
    public private(set) var weeksLoaded = 0
    public private(set) var state = LoadState.loading
    public private(set) var isLoadingMore = false

    /// The entries and the running timer as the server last had them: the weeks listed and the days on the
    /// calendar, each fetch replacing what it covers.
    private var serverEntries: [TimeEntry] = []
    private var serverRunning: TimeEntry?
    /// The days the calendar shows, fetched again with everything else.
    public private(set) var calendarDays: [DayKey] = []

    private let api: SpindleClient
    private let store: LocalStore?

    public init(api: SpindleClient, outbox: Outbox, store: LocalStore? = nil, workspace: WorkspaceSummary, time: UserTime) {
        self.api = api
        self.outbox = outbox
        self.store = store
        self.workspace = workspace
        self.time = time
    }

    public var isAdmin: Bool { workspace.role == .admin }

    /// The workspace's rules, once its settings are in.
    public var rules: EntryRules? {
        settings.map { EntryRules(settings: $0, isAdmin: isAdmin) }
    }

    /// Whether this person can no longer change an entry that starts at this time.
    public func isLocked(_ start: Date) -> Bool {
        rules?.isLocked(time.day(of: start)) ?? false
    }

    /// When the loaded entries start.
    public var loadedSince: Date? {
        weeksLoaded > 0 ? time.week(weeksBack: weeksLoaded - 1, from: AppClock.now).start : nil
    }

    /// The person's running entry, in this workspace or another; a person has at most one.
    public var running: TimeEntry? {
        shown.running
    }

    /// This workspace's entries in the weeks loaded, the running one included.
    public var entries: [TimeEntry] {
        let since = loadedSince
        let end = time.week(weeksBack: 0, from: AppClock.now).end
        return shown.entries.filter { entry in entry.isRunning || (since.map { entry.start >= $0 } ?? true) && entry.start < end }
    }

    /// This workspace's entries that touch these days, e.g. for the calendar, the running one included. The days can
    /// come in any order, e.g. newest first as the entry list shows them.
    public func entries(overlapping days: [DayKey]) -> [TimeEntry] {
        guard let first = days.min(), let last = days.max() else { return [] }
        let from = time.start(of: first)
        let to = time.start(of: time.adding(1, to: last))
        var entries = shown.entries
        if let running, running.workspaceId == workspace.id, !entries.contains(where: { $0.id == running.id }) {
            entries.append(running)
        }
        return entries.filter { $0.start < to && ($0.stop ?? .distantFuture) > from }
    }

    /// This workspace's entries that start within [from, to), the running one included, as the server has them now
    /// with the changes still on their way. When the server can't be reached, what is known, and `current` is false.
    public func entries(from: Date, to: Date) async -> (entries: [TimeEntry], current: Bool) {
        var current = true
        do {
            merge(try await api.timeEntries(in: workspace.id, from: from, to: to), from: from, to: to)
        } catch {
            current = false
        }
        var entries = shown.entries.filter { $0.start >= from && $0.start < to }
        if let running, running.workspaceId == workspace.id, running.start >= from, running.start < to,
           !entries.contains(where: { $0.id == running.id }) {
            entries.append(running)
        }
        return (entries.sorted { $0.start < $1.start }, current)
    }

    /// An entry of this workspace known on this device, by its ID.
    public func entry(id: String) -> TimeEntry? {
        if let running, running.id == id, running.workspaceId == workspace.id { return running }
        return shown.entries.first { $0.id == id }
    }

    /// Fetches the entries for the calendar's days, and keeps them current with everything else.
    public func loadCalendar(_ days: [DayKey]) async throws {
        calendarDays = days
        try await fetchEntries(overlapping: days)
    }

    /// Entries are listed by start, so the range starts a day early, for one that began the evening before.
    private func fetchEntries(overlapping days: [DayKey]) async throws {
        guard let first = days.first, let last = days.last else { return }
        let from = time.start(of: time.adding(-1, to: first))
        let to = time.start(of: time.adding(1, to: last))
        merge(try await api.timeEntries(in: workspace.id, from: from, to: to), from: from, to: to)
    }

    /// What the server has for [from, to) replaces what was known for it.
    private func merge(_ fetched: [TimeEntry], from: Date, to: Date) {
        let ids = Set(fetched.map(\.id))
        serverEntries = serverEntries.filter { ($0.start < from || $0.start >= to) && !ids.contains($0.id) } + fetched
    }

    private var shown: (entries: [TimeEntry], running: TimeEntry?) {
        Performance.measure("Tracker entries") {
            EntryChange.apply(outbox.changes, to: serverEntries, running: serverRunning, workspaceId: workspace.id) { [catalog] projectId in
                catalog.project(projectId)?.isBillable ?? false
            }
        }
    }

    // MARK: - Loading

    /// Shows what was loaded last time, e.g. when the app opens without a connection.
    public func restore() async {
        guard let store, let saved = await store.read(Snapshot.self, key: Snapshot.key(workspace.id)) else { return }
        let week = time.week(weeksBack: 0, from: AppClock.now)
        // Entries from a week that is over are left out; a new week starts empty.
        serverEntries = saved.weekStart == week.start ? saved.entries : []
        serverRunning = saved.running
        settings = saved.settings
        catalog.replace(projects: saved.projects, clients: saved.clients, tags: saved.tags)
        weeksLoaded = 1
        state = .loaded
    }

    /// Loads the workspace's settings, projects and tags, the running timer and this week's entries.
    public func load() async {
        if state != .loaded {
            state = .loading
        }
        async let settings = try? api.settings(of: workspace.id)
        async let catalogLoaded: Bool = (try? await catalog.load(from: api, workspaceId: workspace.id)) != nil
        do {
            let week = time.week(weeksBack: 0, from: AppClock.now)
            async let running = api.currentEntry()
            let entries = try await api.timeEntries(in: workspace.id, from: week.start, to: week.end)
            serverRunning = try await running
            merge(entries, from: week.start, to: week.end)
            weeksLoaded = 1
            state = .loaded
        } catch {
            // What was restored stays on screen; the next refresh tries again.
            if state != .loaded {
                state = .failed(error.localizedDescription)
            }
        }
        if let settings = await settings {
            self.settings = settings
        }
        _ = await catalogLoaded
        await save()
    }

    /// Adds the week before the oldest one shown.
    public func loadWeekBefore() async throws {
        guard !isLoadingMore else { return }
        isLoadingMore = true
        defer { isLoadingMore = false }
        let week = time.week(weeksBack: weeksLoaded, from: AppClock.now)
        merge(try await api.timeEntries(in: workspace.id, from: week.start, to: week.end), from: week.start, to: week.end)
        weeksLoaded += 1
    }

    /// Fetches everything shown again, e.g. when the app comes back to the front or another device changed something.
    public func refresh() async {
        guard weeksLoaded > 0, state == .loaded else {
            await load()
            return
        }
        do {
            let from = time.week(weeksBack: weeksLoaded - 1, from: AppClock.now).start
            let to = time.week(weeksBack: 0, from: AppClock.now).end
            async let running = api.currentEntry()
            let entries = try await api.timeEntries(in: workspace.id, from: from, to: to)
            serverRunning = try await running
            merge(entries, from: from, to: to)
            if !calendarDays.isEmpty {
                try await fetchEntries(overlapping: calendarDays)
            }
        } catch {
            // What is shown stays; the next refresh tries again.
            return
        }
        if let settings = try? await api.settings(of: workspace.id) {
            self.settings = settings
        }
        try? await catalog.load(from: api, workspaceId: workspace.id)
        await save()
    }

    /// Checks the running timer, which other devices start and stop, and fetches the entries again when it changed.
    public func refreshRunning() async {
        let current: TimeEntry?
        do {
            current = try await api.currentEntry()
        } catch {
            return
        }
        if current != serverRunning {
            await refresh()
        }
    }

    /// Keeps what was loaded for next time.
    private func save() async {
        guard let store, state == .loaded else { return }
        let week = time.week(weeksBack: 0, from: AppClock.now)
        let snapshot = Snapshot(
            weekStart: week.start,
            entries: serverEntries.filter { $0.start >= week.start },
            running: serverRunning,
            settings: settings,
            projects: catalog.projects,
            clients: catalog.clients,
            tags: catalog.tags)
        await store.write(snapshot, key: Snapshot.key(workspace.id))
    }

    /// What is kept on the device for a workspace.
    struct Snapshot: Codable {
        var weekStart: Date
        var entries: [TimeEntry]
        var running: TimeEntry?
        var settings: WorkspaceSettings?
        var projects: [Project]
        var clients: [WorkspaceClient]
        var tags: [Tag]

        static func key(_ workspaceId: String) -> String { "workspace:\(workspaceId)" }
    }

    // MARK: - Changes the server confirmed

    /// The server has a change from the outbox: its answer becomes what the server has.
    public func didSend(_ change: EntryChange, _ saved: TimeEntry?) {
        switch change {
        case .create:
            guard let saved else { return }
            if saved.isRunning {
                // The server stopped the timer that was running where this one starts.
                if var previous = serverRunning, previous.id != saved.id {
                    previous.stop = max(previous.start, min(saved.start, AppClock.now))
                    putServer(previous)
                }
                serverRunning = saved
            }
            putServer(saved)
        case .update, .stop:
            guard let saved else { return }
            didLearn(saved)
        case .delete(let id, _, _):
            serverEntries.removeAll { $0.id == id }
            if serverRunning?.id == id {
                serverRunning = nil
            }
        }
    }

    /// The server has an entry as it is now, e.g. as another device changed it, which a change from this device
    /// found when it was refused.
    public func didLearn(_ entry: TimeEntry) {
        putServer(entry)
        if entry.isRunning {
            serverRunning = entry
        } else if serverRunning?.id == entry.id {
            serverRunning = nil
        }
    }

    private func putServer(_ entry: TimeEntry) {
        if let index = serverEntries.firstIndex(where: { $0.id == entry.id }) {
            serverEntries[index] = entry
        } else if entry.workspaceId == workspace.id {
            serverEntries.append(entry)
        }
    }

    // MARK: - The timer

    /// Starts a timer. One already running stops where this one starts, as the server does. Returns the new entry's
    /// ID, e.g. to undo the start.
    @discardableResult
    public func start(_ details: EntryDetails, at start: Date = AppClock.now) async throws -> String {
        let id = newID()
        try await outbox.submit(.create(EntryRequest(details: details, start: start, stop: nil), id: id, workspaceId: workspace.id))
        return id
    }

    /// Stops the running timer. A finished entry must have the details the workspace requires, so when they are
    /// missing the timer keeps running and the app asks for them.
    public func stopRunning(at stop: Date = AppClock.now) async -> StopOutcome {
        guard let entry = running else { return .stopped }
        if let rules, !rules.missingDetails(entry.details, project: catalog.project(entry.projectId)).isEmpty {
            return .needsDetails(entry)
        }
        do {
            try await outbox.submit(.stop(at: max(stop, entry.start), id: entry.id, workspaceId: entry.workspaceId, version: entry.version))
            return .stopped
        } catch let error as APIError where error.isAboutDetails {
            // The settings may have changed since they were loaded; the server's word counts.
            return .needsDetails(entry)
        } catch {
            return .failed(error)
        }
    }

    /// Changes the running entry, e.g. its description, project or start.
    public func updateRunning(_ edit: (inout EntryRequest) -> Void) async throws {
        guard let entry = running else { return }
        var request = EntryRequest(entry)
        edit(&request)
        try await update(entry, to: request)
    }

    // MARK: - Entries

    /// Saves an entry as edited: changes one, or adds one when `entry` is nil. Online it waits for the server, so
    /// the person sees at once whether it was saved; offline the change waits and this says so.
    @discardableResult
    /// `id` is a new entry's, e.g. for an agent's proposal to name the entry it became.
    public func save(_ entry: TimeEntry?, as request: EntryRequest, id: String = newID()) async throws -> SaveOutcome {
        let change: EntryChange = if let entry {
            .update(request, id: entry.id, workspaceId: entry.workspaceId, version: entry.version)
        } else {
            .create(request, id: id, workspaceId: workspace.id)
        }
        return try await outbox.submit(change) == nil ? .waiting : .saved
    }

    public func update(_ entry: TimeEntry, to request: EntryRequest) async throws {
        try await outbox.submit(.update(request, id: entry.id, workspaceId: entry.workspaceId, version: entry.version))
    }

    /// Starts a timer with an earlier entry's details.
    public func continueEntry(_ entry: TimeEntry) async throws {
        try await start(entry.details)
    }

    /// Adds a copy of a finished entry, at the same time.
    public func duplicate(_ entry: TimeEntry) async throws {
        guard let stop = entry.stop else { return }
        try await outbox.submit(.create(EntryRequest(details: entry.details, start: entry.start, stop: stop), id: newID(), workspaceId: entry.workspaceId))
    }

    public func delete(_ entry: TimeEntry) async throws {
        try await outbox.submit(.delete(id: entry.id, workspaceId: entry.workspaceId, version: entry.version))
    }

    /// Adds a finished entry and returns its ID, e.g. to undo adding it.
    @discardableResult
    public func add(_ request: EntryRequest) async throws -> String {
        let id = newID()
        try await outbox.submit(.create(request, id: id, workspaceId: workspace.id))
        return id
    }

    /// Deletes an entry of this workspace by its ID, e.g. one just added, to undo that.
    public func delete(id: String) async throws {
        try await outbox.submit(.delete(id: id, workspaceId: workspace.id, version: entry(id: id)?.version))
    }

    /// Deletes several entries at once, all or none. Needs the server.
    public func delete(_ ids: [String]) async throws {
        try await api.deleteEntries(ids, in: workspace.id)
        serverEntries.removeAll { ids.contains($0.id) }
        if let running = serverRunning, ids.contains(running.id) {
            serverRunning = nil
        }
    }

    /// Changes several entries at once, all or none. Nil leaves that part of each entry as it is. Needs the server.
    public func update(
        _ ids: [String],
        description: String? = nil,
        project: (projectId: String?, taskId: String?)? = nil,
        isBillable: Bool? = nil,
        addTagIds: [String]? = nil,
        removeTagIds: [String]? = nil
    ) async throws {
        let saved = try await api.updateEntries(
            ids, in: workspace.id, description: description, project: project, isBillable: isBillable,
            addTagIds: addTagIds, removeTagIds: removeTagIds)
        for entry in saved {
            putServer(entry)
            if serverRunning?.id == entry.id {
                serverRunning = entry
            }
        }
    }

    /// Earlier entries that match what is typed, to start again. The server searches; its answer is narrowed to
    /// what is typed now, in case the person typed on in the meantime.
    public func suggestions(matching text: String) async throws -> [TimeEntrySuggestion] {
        let term = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let found = try await api.suggestions(in: workspace.id, search: term)
        return found.filter { suggestion in
            term.isEmpty
                || suggestion.description.localizedCaseInsensitiveContains(term)
                || catalog.project(suggestion.projectId)?.name.localizedCaseInsensitiveContains(term) == true
        }
    }

    /// Descriptions written before that complete what is typed, those on `projectId` first, narrowed to what is typed
    /// now and without the text itself.
    public func descriptions(matching text: String, projectId: String?) async throws -> [String] {
        let term = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let found = try await api.descriptions(in: workspace.id, search: term, projectId: projectId)
        return found.filter { $0.localizedCaseInsensitiveContains(term) && $0.caseInsensitiveCompare(term) != .orderedSame }
    }

    // MARK: - Projects and tags

    /// Loads the projects, clients and tags again, e.g. after changing one. Entries of a deleted project or tag
    /// changed too, so they load again as well.
    public func reloadCatalog(entriesChanged: Bool = false) async {
        try? await catalog.load(from: api, workspaceId: workspace.id)
        if entriesChanged {
            await refresh()
        } else {
            await save()
        }
    }

    /// Whether this person can create projects in the workspace.
    public var canCreateProjects: Bool {
        isAdmin || settings?.onlyAdminsCreateProjects == false
    }

    /// Whether this person can create tags in the workspace.
    public var canCreateTags: Bool {
        isAdmin || settings?.onlyAdminsCreateTags == false
    }

    public func createTag(named name: String) async throws -> Tag {
        let tag = try await api.createTag(named: name.trimmingCharacters(in: .whitespacesAndNewlines), in: workspace.id)
        catalog.add(tag)
        return tag
    }

    public func createProject(named name: String) async throws -> Project {
        let project = try await api.createProject(
            named: name.trimmingCharacters(in: .whitespacesAndNewlines),
            color: ProjectColors.next(afterCount: catalog.projects.count),
            in: workspace.id)
        catalog.add(project)
        return project
    }
}
