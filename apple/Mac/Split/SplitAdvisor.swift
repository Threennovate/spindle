import Foundation
import Observation
import SpindleKit

/// Offers to split entries whose time went to several projects at once, from where agent attention went while
/// they ran. Repositories are linked to projects as the person chose, as learned from how they tracked time while
/// working in them, or by name. The logs stay on this Mac; the links chosen and the entries kept as they are go to
/// the person's account (`ActivitySync`), so every Mac and the web agree.
@Observable
final class SplitAdvisor {
    private let defaults: UserDefaults
    private let settings: DesktopSettings
    private let watcher: ActivityWatcher?

    /// Activity for the current sources and time zone, with recently used days kept within `dayLimit`.
    private var keptDays: [DayKey: ActivityDay] = [:] { didSet { inputs += 1 } }
    var days: [DayKey: ActivityDay] {
        refreshContext(time: cachedTime ?? UserTime(timeZone: .current))
        return keptDays
    }
    struct Context: Equatable {
        let enabled: Bool
        let sources: Set<AgentSource>
        let zone: String
        let fixtureRoot: URL?
        let revision: Int
        let ignoring: IgnoredRepositories
    }
    @ObservationIgnored private var cachedContext: Context?
    @ObservationIgnored private var cachedTime: UserTime?
    @ObservationIgnored private var used: [DayKey: Int] = [:]
    @ObservationIgnored private var clock = 0
    /// Enough for several calendar weeks and the boundary days of many entries.
    static let dayLimit = 128

    func context(time: UserTime) -> Context {
        Context(enabled: isEnabled, sources: sources, zone: time.timeZone.identifier,
                fixtureRoot: ActivitySnapshot.root, revision: settings.activityRevision, ignoring: ignoring)
    }

    struct Request: Equatable {
        let days: [DayKey]
        let context: Context
        let revision: Int
    }

    /// A stable view task ID; source sets are compared as values, never through their display order.
    func request(_ days: [DayKey], time: UserTime) -> Request {
        Request(days: Array(Set(days)).sorted(), context: context(time: time), revision: revision)
    }

    private func refreshContext(time: UserTime) {
        let context = context(time: time)
        guard cachedContext != context else { return }
        cachedContext = context
        cachedTime = time
        keptDays = [:]
        loadedAt = [:]
        loadedRevision = [:]
        used = [:]
        learned = [:]
        offer = nil
        driftOffer = nil
    }

    private func touch(_ days: [DayKey]) {
        for day in days { clock += 1; used[day] = clock }
    }

    /// Repositories linked to projects by the person, by repository identity.
    private(set) var chosen: [String: String] {
        didSet { inputs += 1 }
    }
    /// Branch rules on the person's links, by repository identity, in their order.
    private(set) var rules: [String: [BranchRule]] {
        didSet { if rules != oldValue { inputs += 1 } }
    }
    /// Repositories the person said go to no project, by identity: neither a learned link nor a name takes their work.
    private(set) var noProject: Set<String> {
        didSet { if noProject != oldValue { inputs += 1 } }
    }
    /// The names of linked repositories, by identity, for listing the links.
    private(set) var names: [String: String]
    /// Links chosen here that the account doesn't have yet, by repository identity.
    private(set) var unsentLinks: [String: UnsentLink]
    /// Entries kept as they are here that the account doesn't know of yet, with their workspace.
    private(set) var unsentDismissals: [String: String]
    /// The person's account, whose ID keys the hashes of ignored repositories.
    var account: String? {
        didSet { if account != oldValue { accountChanged(from: oldValue) } }
    }
    /// The repositories the person ignores, as hashes of their identities (`IgnoredRepositories`), kept here so the
    /// logs read leave them out offline too.
    private(set) var ignoredHashes: Set<String>
    /// The names of ignored repositories as this Mac knew them, by hash. The account has the hashes alone.
    private(set) var ignoredNames: [String: String]
    /// Repositories ignored (true) or no longer ignored (false) here that the account doesn't know of yet, by hash.
    private(set) var unsentIgnores: [String: Bool]
    /// Links learned from the entries and activity loaded so far.
    private(set) var learned: [String: String] = [:] {
        didSet { if learned != oldValue { inputs += 1 } }
    }
    /// Entries the person said not to suggest a split for, by ID.
    private(set) var dismissed: Set<String>
    /// A split offered for the entry the timer just stopped, to take with one click.
    private(set) var offer: SplitOffer?
    /// For a split entry the timer just stopped, ending its split where the work changed, when nobody answered that
    /// while it ran.
    private(set) var driftOffer: DriftOffer?
    /// Running entries already offered a switch to another project; once each is enough.
    @ObservationIgnored private var offeredSwitches: Set<String> = []
    /// When each recent entry's split was set, as this Mac first saw it, and whether its change was offered.
    @ObservationIgnored private var statements: [String: Statement]
    /// Counts up when a change is answered, so the chip and the menu bar card go away.
    private var answers = 0
    /// Untracked time the person dismissed in the calendar, by day and start minute ("2026-10-15|552"). Kept on this
    /// Mac only.
    private(set) var dismissedUntracked: Set<String>
    /// What the day's review was told "Not Now" for, by item ID. It leaves the review; chips and the calendar keep
    /// it. Kept on this Mac only.
    private(set) var putOff: Set<String>
    /// When the app started: a running entry first seen after this was seen from its start.
    @ObservationIgnored private let launched: Date
    /// Counts up when activity or links change, which every suggestion depends on. Reading a suggestion observes it,
    /// so views follow when one changes although the suggestions themselves are kept unobserved.
    private var inputs = 0
    /// Suggestions worked out already, by entry, with what each was worked out from: the calendar, the entry list,
    /// the timer bar and the menu bar all ask for the same ones, and the calendar asks again on every drag step.
    @ObservationIgnored private var worked: [String: (key: WorkedKey, suggestion: SplitSuggestion?)] = [:]
    /// What all of `worked` was worked out from, and the matcher for it; a change starts them over.
    @ObservationIgnored private var workedFrom: (inputs: Int, projects: [Project], time: UserTime, matcher: RepositoryLinks.Matcher)?
    @ObservationIgnored private var loadedAt: [DayKey: Date] = [:]
    /// The watcher's revision when each day was read; today is read again once it moves.
    @ObservationIgnored private var loadedRevision: [DayKey: Int] = [:]

    /// The most dismissed entries remembered; older ones are forgotten first.
    private static let dismissedLimit = 500
    /// Today keeps changing while agents work. Without change notices it is read again after this long.
    private static let todayRefresh: TimeInterval = 120

    init(settings: DesktopSettings, watcher: ActivityWatcher? = nil, defaults: UserDefaults = .standard, launched: Date = Date()) {
        self.settings = settings
        self.watcher = watcher
        self.defaults = defaults
        self.launched = launched
        statements = defaults.data(forKey: Self.statementsKey).flatMap { try? JSONDecoder().decode([String: Statement].self, from: $0) } ?? [:]
        chosen = defaults.dictionary(forKey: Self.linksKey) as? [String: String] ?? [:]
        rules = defaults.data(forKey: Self.rulesKey).flatMap { try? JSONDecoder().decode([String: [BranchRule]].self, from: $0) } ?? [:]
        noProject = Set(defaults.stringArray(forKey: Self.noProjectKey) ?? [])
        names = defaults.dictionary(forKey: Self.namesKey) as? [String: String] ?? [:]
        unsentLinks = defaults.data(forKey: Self.unsentLinksKey).flatMap { try? JSONDecoder().decode([String: UnsentLink].self, from: $0) } ?? [:]
        unsentDismissals = defaults.dictionary(forKey: Self.unsentDismissalsKey) as? [String: String] ?? [:]
        account = defaults.string(forKey: Self.ignoredAccountKey)
        ignoredHashes = Set(defaults.stringArray(forKey: Self.ignoredKey) ?? [])
        ignoredNames = defaults.dictionary(forKey: Self.ignoredNamesKey) as? [String: String] ?? [:]
        unsentIgnores = defaults.dictionary(forKey: Self.unsentIgnoresKey) as? [String: Bool] ?? [:]
        dismissed = Set(defaults.stringArray(forKey: "dismissedSplitSuggestions") ?? [])
        dismissedUntracked = Set(defaults.stringArray(forKey: Self.dismissedUntrackedKey) ?? [])
        putOff = Set(defaults.stringArray(forKey: Self.putOffKey) ?? [])
        migratePathLinks()
    }

    private static let linksKey = "repositoryProjectLinks"
    private static let rulesKey = "repositoryBranchRules"
    private static let noProjectKey = "repositoriesWithoutProject"
    private static let namesKey = "repositoryLinkNames"
    private static let unsentLinksKey = "unsentRepositoryLinks"
    private static let unsentDismissalsKey = "unsentSplitDismissals"
    private static let statementsKey = "splitStatements"
    private static let dismissedUntrackedKey = "dismissedUntrackedTime"
    private static let putOffKey = "reviewPutOff"
    private static let ignoredKey = "ignoredRepositories"
    private static let ignoredAccountKey = "ignoredRepositoriesAccount"
    private static let ignoredNamesKey = "ignoredRepositoryNames"
    private static let unsentIgnoresKey = "unsentIgnoredRepositories"

    /// Links from before they synced were kept by the repository's path. They are kept by its identity now, and go to
    /// the account like a new choice.
    private func migratePathLinks() {
        guard let byPath = defaults.dictionary(forKey: "repositoryLinks") as? [String: String] else { return }
        for (path, projectId) in byPath {
            let repository = ActivityRepositoryResolver.resolve(
                cwd: path, home: NSHomeDirectory(), exists: { FileManager.default.fileExists(atPath: $0) },
                read: { try? String(contentsOfFile: $0, encoding: .utf8) })
            guard let identity = repository.identity, chosen[identity] == nil else { continue }
            chosen[identity] = projectId
            unsentLinks[identity] = UnsentLink(name: repository.name, projectId: projectId)
        }
        defaults.removeObject(forKey: "repositoryLinks")
        saveLinks()
    }

    private func saveLinks() {
        defaults.set(chosen, forKey: Self.linksKey)
        defaults.set(try? JSONEncoder().encode(rules), forKey: Self.rulesKey)
        defaults.set(noProject.sorted(), forKey: Self.noProjectKey)
        defaults.set(names, forKey: Self.namesKey)
        defaults.set(try? JSONEncoder().encode(unsentLinks), forKey: Self.unsentLinksKey)
    }

    /// Suggestions come from agent logs, when the person lets the app read at least one tool's. They don't depend on
    /// showing agent activity in the calendar.
    var isEnabled: Bool {
        ActivitySnapshot.root != nil || (settings.useAgentLogs && !settings.activitySources.isEmpty)
    }

    private var sources: Set<AgentSource> {
        ActivitySnapshot.root != nil ? Set(AgentSource.allCases) : settings.activitySources
    }

    // MARK: - Loading

    /// The watcher's revision, to read activity again when agents wrote their logs; 0 without a watcher.
    var revision: Int { watcher?.revision ?? 0 }

    /// Reads the activity of these days, unless it was read already; today is read again once agents wrote to
    /// their logs, or every few minutes when changes aren't being reported.
    func load(_ wanted: [DayKey], tracker: Tracker) async {
        refreshContext(time: tracker.time)
        guard isEnabled else { return }
        let context = context(time: tracker.time), revision = self.revision
        touch(wanted.filter { keptDays[$0] != nil })
        let today = tracker.time.day(of: Date())
        let missing = Array(Set(wanted)).sorted().filter { day in
            guard let at = loadedAt[day] else { return true }
            guard day >= today else { return false }
            if let watcher, watcher.isWatching { return loadedRevision[day] != watcher.revision }
            return Date().timeIntervalSince(at) > Self.todayRefresh
        }
        guard !missing.isEmpty else { return }
        guard let loaded = try? await AgentActivityLoader.shared.load(
            days: missing, time: tracker.time, sources: sources, ignoring: context.ignoring, fixtureRoot: ActivitySnapshot.root) else { return }
        guard !Task.isCancelled, context == self.context(time: tracker.time) else { return }
        absorb(loaded, entries: tracker.entries, time: tracker.time, revision: revision)
    }

    /// Keeps activity read elsewhere, e.g. by the calendar, so it is not read twice.
    func absorb(_ loaded: [ActivityDay], tracker: Tracker) {
        absorb(loaded, entries: tracker.entries, time: tracker.time)
    }

    /// Keeps activity, and learns links again from `entries`.
    func absorb(_ loaded: [ActivityDay], entries: @autoclosure () -> [TimeEntry], time: UserTime, revision: Int? = nil) {
        refreshContext(time: time)
        guard isEnabled, !loaded.isEmpty else { return }
        for day in loaded {
            keptDays[day.day] = day
            loadedAt[day.day] = Date()
            loadedRevision[day.day] = revision ?? self.revision
        }
        touch(loaded.map(\.day))
        while keptDays.count > Self.dayLimit {
            guard let oldest = keptDays.keys.min(by: { (used[$0] ?? 0) < (used[$1] ?? 0) }) else { break }
            keptDays.removeValue(forKey: oldest)
            loadedAt.removeValue(forKey: oldest)
            loadedRevision.removeValue(forKey: oldest)
            used.removeValue(forKey: oldest)
        }
        used = used.filter { keptDays[$0.key] != nil }
        learned = Performance.measure("Learn repository links") {
            RepositoryLinks.learn(from: entries(), activity: Array(keptDays.values))
        }
    }

    // MARK: - Suggestions

    var links: RepositoryLinks {
        refreshContext(time: cachedTime ?? UserTime(timeZone: .current))
        return RepositoryLinks(chosen: chosen, learned: learned, rules: rules, noProject: noProject)
    }

    /// The split agent activity suggests for an entry on one project, unless the person said no. An entry the person
    /// split already is left as they split it; its editor still shows how the agents' time went.
    ///
    /// Worked out once for each change of what it depends on: the entry's times and project, the activity, links
    /// and projects, and for a running entry the minute, so it moves along at most once a minute.
    func suggestion(for entry: TimeEntry, tracker: Tracker, now: Date = Date()) -> SplitSuggestion? {
        suggestion(for: entry, time: tracker.time, projects: tracker.catalog.projects, now: now)
    }

    func suggestion(for entry: TimeEntry, time: UserTime, projects: [Project], now: Date = Date()) -> SplitSuggestion? {
        refreshContext(time: time)
        guard isEnabled, !entry.isSplit, !dismissed.contains(entry.id) else { return nil }
        let matcher = matcher(time: time, projects: projects)
        let key = WorkedKey(start: entry.start, stop: entry.stop, minute: entry.stop == nil ? Int(now.timeIntervalSinceReferenceDate / 60) : 0,
                            projectId: entry.projectId)
        if let known = worked[entry.id], known.key == key {
            return known.suggestion
        }
        var suggestion = suggestion(from: entry.start, to: entry.stop ?? now, on: entry.projectId, time: time, matcher: matcher)
        if suggestion?.matches(entry) == true {
            suggestion = nil
        }
        worked[entry.id] = (key, suggestion)
        return suggestion
    }

    /// What an entry's suggestion was worked out from.
    private struct WorkedKey: Equatable {
        let start: Date
        let stop: Date?
        /// The minute a running entry's suggestion is for; 0 for a stopped entry.
        let minute: Int
        let projectId: String?
    }

    /// The split agent activity suggests for a stretch of time tracked on `projectId`, which keeps the time the
    /// logs don't account for.
    func suggestion(from start: Date, to end: Date, on projectId: String?, tracker: Tracker) -> SplitSuggestion? {
        refreshContext(time: tracker.time)
        guard isEnabled else { return nil }
        return suggestion(from: start, to: end, on: projectId, time: tracker.time,
                          matcher: matcher(time: tracker.time, projects: tracker.catalog.projects))
    }

    private func suggestion(from start: Date, to end: Date, on projectId: String?, time: UserTime,
                            matcher: RepositoryLinks.Matcher) -> SplitSuggestion? {
        let wanted = [time.day(of: start), time.day(of: end)]
        touch(wanted.filter { keptDays[$0] != nil })
        let covered = Set(wanted).sorted().compactMap { keptDays[$0] }
        guard !covered.isEmpty else { return nil }
        return Performance.measure("Split suggestion") {
            SplitSuggestion.suggest(from: start, to: end, activity: covered, fallback: projectId, placing: matcher.place)
        }
    }

    /// The links and projects as suggestions use them. When activity, links, projects or the person's time zone
    /// changed, the suggestions worked out before are forgotten.
    private func matcher(time: UserTime, projects: [Project]) -> RepositoryLinks.Matcher {
        // Comparing the catalog's same array again is immediate, as it is the same storage.
        if let from = workedFrom, from.inputs == inputs, from.projects == projects, from.time == time {
            return from.matcher
        }
        let matcher = links.matcher(among: projects)
        workedFrom = (inputs, projects, time, matcher)
        worked = [:]
        drifts = [:]
        return matcher
    }

    /// The entry as an editor opens it to take a suggestion: as separate entries when the work moved from one
    /// project to the next, or shared by percent when it went back and forth.
    static func editing(_ entry: TimeEntry, as suggestion: SplitSuggestion?, catalog: Catalog) -> EntryEditing {
        if let suggestion, let stretches = SplitApplier.stretches(suggestion, catalog: catalog) {
            return EntryEditing(entry: entry, stretches: stretches)
        }
        return EntryEditing(entry: entry, split: suggestion.flatMap { SplitApplier.parts($0, catalog: catalog) })
    }

    /// A new entry for `range` opened as the editor takes a suggestion, like an existing one.
    static func editing(newRange range: DateInterval, details: EntryDetails, as suggestion: SplitSuggestion?, catalog: Catalog,
                        onSaved: (() -> Void)? = nil) -> EntryEditing {
        var editing = EntryEditing(newRange: range)
        editing.details = details
        editing.onSaved = onSaved
        if let suggestion, let stretches = SplitApplier.stretches(suggestion, catalog: catalog) {
            editing.stretches = stretches
        } else {
            editing.split = suggestion.flatMap { SplitApplier.parts($0, catalog: catalog) }
        }
        return editing
    }

    // MARK: - Offers

    /// Offers a split for an entry the timer just stopped, when its time went to several linked projects. Waits a
    /// moment for the stop to reach the entries, and reads that time's activity first.
    @discardableResult
    func offerAfterStop(_ entryId: String, tracker: Tracker) async -> SplitOffer? {
        guard isEnabled else { return nil }
        var stopped: TimeEntry?
        for _ in 0..<20 {
            stopped = tracker.entries.first { $0.id == entryId && $0.stop != nil }
            if stopped != nil { break }
            try? await Task.sleep(for: .milliseconds(250))
        }
        guard let entry = stopped else { return nil }
        await load(Self.days(of: [entry], time: tracker.time), tracker: tracker)
        // A split entry gets only the change nobody answered while it ran.
        if entry.isSplit {
            driftOffer = drift(for: entry, tracker: tracker)
            return nil
        }
        // Repositories without a project are left to the entry's chip, where they can be linked, and so is a
        // suggestion the logs back only weakly: it waits quietly instead of interrupting.
        guard let suggestion = suggestion(for: entry, tracker: tracker), suggestion.strength == .strong,
              suggestion.splitParts(billable: { _ in false }) != nil else { return nil }
        let offer = SplitOffer(entry: entry, suggestion: suggestion)
        self.offer = offer
        return offer
    }

    /// Takes the offer away, e.g. once taken, or when the person said no to it.
    func withdrawOffer(_ entryId: String? = nil) {
        if entryId == nil || offer?.entry.id == entryId { offer = nil }
        if entryId == nil || driftOffer?.entry.id == entryId { driftOffer = nil }
    }

    /// What to tell the person about the running entry while it runs: the work moved from its project to another
    /// one, which has had the agents' attention for at least `after`, or its split no longer describes the work.
    /// Each is offered once: once per entry for a switch, once per split for a change.
    func changeOffer(for running: TimeEntry, tracker: Tracker, after: TimeInterval = 15 * 60, now: Date = Date()) -> ChangeOffer? {
        changeOffer(for: running, time: tracker.time, projects: tracker.catalog.projects, after: after, now: now)
    }

    func changeOffer(for running: TimeEntry, time: UserTime, projects: [Project], after: TimeInterval = 15 * 60, now: Date = Date()) -> ChangeOffer? {
        if running.isSplit {
            guard let drift = drift(for: running, time: time, projects: projects, now: now), statements[running.id]?.notified != true else { return nil }
            return .drift(drift)
        }
        guard !offeredSwitches.contains(running.id), let suggestion = suggestion(for: running, time: time, projects: projects, now: now),
              let stretches = suggestion.projectStretches, stretches.count == 2,
              stretches[0].projectId == running.projectId, stretches[1].projectId != running.projectId,
              now.timeIntervalSince(stretches[1].range.start) >= after else { return nil }
        return .switchProject(SplitOffer(entry: running, suggestion: suggestion))
    }

    func markOffered(_ offer: ChangeOffer) {
        switch offer {
        case .switchProject(let offer):
            offeredSwitches.insert(offer.entry.id)
        case .drift(let offer):
            guard var statement = statements[offer.entry.id], statement.parts == offer.statement else { return }
            statement.notified = true
            statements[offer.entry.id] = statement
            saveStatements()
        }
    }

    // MARK: - When the work changes after a split

    /// When an entry's current split was set, as this Mac saw it, and what became of the change offered for it.
    struct Statement: Codable, Equatable {
        /// The parts as they were then; another split starts over.
        let parts: String
        let at: Date
        /// The change was offered in a notification; ignoring that does nothing.
        var notified = false
        /// Taken, looked at, or "Not Now": nothing more is offered for this split.
        var answered = false
    }

    /// The most entries whose statements are remembered, the newest kept.
    private static let statementLimit = 20

    /// The parts as a statement remembers them.
    static func partsKey(_ entry: TimeEntry) -> String {
        partsKey(entry.parts)
    }

    static func partsKey(_ parts: [SplitPart]) -> String {
        parts.map { "\($0.projectId ?? "")/\($0.taskId ?? "")/\($0.percent)" }.joined(separator: ",")
    }

    /// When the running entry's current split was set. A split there when the timer started counts from the start;
    /// one this Mac first sees later counts from then, which only makes an offer later, never wrong. A stopped entry
    /// has the statement it had while it ran, if this Mac saw it run.
    func statement(for entry: TimeEntry, now: Date = Date()) -> Statement? {
        let parts = Self.partsKey(entry)
        if let known = statements[entry.id], known.parts == parts { return known }
        guard entry.stop == nil else { return nil }
        let seen = statements[entry.id] != nil || entry.start < launched ? now : entry.start
        let statement = Statement(parts: parts, at: max(entry.start, min(seen, now)))
        state(entry.id, statement)
        return statement
    }

    /// Remembers when an entry's split was set, e.g. for the entry a change just started, from where it begins.
    func state(_ entryId: String, _ statement: Statement) {
        statements[entryId] = statement
        if statements.count > Self.statementLimit {
            for (id, _) in statements.sorted(by: { $0.value.at > $1.value.at }).dropFirst(Self.statementLimit) { statements[id] = nil }
        }
        saveStatements()
    }

    private func saveStatements() {
        defaults.set(try? JSONEncoder().encode(statements), forKey: Self.statementsKey)
    }

    /// Nothing more is offered for this split: it was taken, looked at, or put off with "Not Now".
    func answer(_ offer: DriftOffer) {
        withdrawOffer(offer.entry.id)
        guard var statement = statements[offer.entry.id], statement.parts == offer.statement else { return }
        statement.answered = true
        statements[offer.entry.id] = statement
        saveStatements()
        answers += 1
    }

    /// How the work changed since a split entry's split was set, while nobody answered that. Worked out once a
    /// minute for a running entry, like suggestions.
    func drift(for entry: TimeEntry, tracker: Tracker, now: Date = Date()) -> DriftOffer? {
        drift(for: entry, time: tracker.time, projects: tracker.catalog.projects, now: now)
    }

    func drift(for entry: TimeEntry, time: UserTime, projects: [Project], now: Date = Date()) -> DriftOffer? {
        refreshContext(time: time)
        _ = answers
        guard isEnabled, entry.isSplit, !dismissed.contains(entry.id), let statement = statement(for: entry, now: now),
              !statement.answered else { return nil }
        let matcher = matcher(time: time, projects: projects)
        let key = DriftKey(start: entry.start, stop: entry.stop, minute: entry.stop == nil ? Int(now.timeIntervalSinceReferenceDate / 60) : 0,
                           parts: statement.parts, statedAt: statement.at)
        let drift: SplitDrift.Drift?
        if let known = drifts[entry.id], known.key == key {
            drift = known.drift
        } else {
            let end = entry.stop ?? now
            let wanted = [time.day(of: entry.start), time.day(of: end)]
            touch(wanted.filter { keptDays[$0] != nil })
            let covered = Set(wanted).sorted().compactMap { keptDays[$0] }
            drift = covered.isEmpty ? nil : SplitDrift.detect(
                parts: entry.parts.compactMap(\.projectId), from: entry.start, statedAt: statement.at, now: end,
                activity: covered, placing: matcher.place)
            drifts[entry.id] = (key, drift)
        }
        return drift.map { DriftOffer(entry: entry, drift: $0, statement: statement.parts) }
    }

    /// What a split entry's change was worked out from.
    private struct DriftKey: Equatable {
        let start: Date
        let stop: Date?
        let minute: Int
        let parts: String
        let statedAt: Date
    }

    @ObservationIgnored private var drifts: [String: (key: DriftKey, drift: SplitDrift.Drift?)] = [:]

    /// The days to read for these entries' suggestions.
    static func days(of entries: [TimeEntry], time: UserTime, now: Date = Date()) -> [DayKey] {
        Array(Set(entries.flatMap { [time.day(of: $0.start), time.day(of: $0.stop ?? now)] }))
    }

    // MARK: - Time nobody tracked

    /// How long after a timer starts the timer bar offers to move its start back.
    static let earlierStartFor: TimeInterval = 10 * 60

    /// For the first minutes of a running entry: the start agents' untracked work right before it suggests. Never
    /// before the previous entry's stop, the day's start, or into a locked day.
    func earlierStart(for running: TimeEntry, tracker: Tracker, now: Date = Date()) -> EarlierStart? {
        earlierStart(for: running, entries: tracker.entries, time: tracker.time, projects: tracker.catalog.projects,
                     isLocked: tracker.isLocked, now: now)
    }

    func earlierStart(for running: TimeEntry, entries: [TimeEntry], time: UserTime, projects: [Project],
                      isLocked: (Date) -> Bool = { _ in false }, now: Date = Date()) -> EarlierStart? {
        refreshContext(time: time)
        guard isEnabled, running.stop == nil, now.timeIntervalSince(running.start) < Self.earlierStartFor,
              let day = keptDays[time.day(of: running.start)] else { return nil }
        let others = entries.filter { $0.id != running.id }
        let previousStop = others.compactMap(\.stop).filter { $0 <= running.start }.max()
        let floor = max(time.start(of: day.day), previousStop ?? .distantPast)
        let matcher = matcher(time: time, projects: projects)
        guard let stretch = UntrackedActivity.earlierStart(
                before: running.start, attention: day.attention, covered: Self.covered(others, now: now), notBefore: floor,
                placing: { matcher.place($0)?.projectId }),
              !isLocked(stretch.range.start) else { return nil }
        return EarlierStart(entry: running, start: stretch.range.start, projectId: stretch.projects.first)
    }

    /// With no timer running: agents at work in a linked project, for long enough that the reminder to track names
    /// it. Repositories without a project don't count.
    func ongoingUntracked(tracker: Tracker, now: Date = Date()) -> UntrackedActivity.Stretch? {
        ongoingUntracked(entries: tracker.entries, time: tracker.time, projects: tracker.catalog.projects, now: now)
    }

    func ongoingUntracked(entries: [TimeEntry], time: UserTime, projects: [Project], now: Date = Date()) -> UntrackedActivity.Stretch? {
        refreshContext(time: time)
        guard isEnabled, !entries.contains(where: \.isRunning), let day = keptDays[time.day(of: now)] else { return nil }
        let lastStop = entries.compactMap(\.stop).filter { $0 <= now }.max()
        let matcher = matcher(time: time, projects: projects)
        return UntrackedActivity.ongoing(attention: day.attention, covered: Self.covered(entries, now: now),
                                         notBefore: max(time.start(of: day.day), lastStop ?? .distantPast), now: now) {
            matcher.place($0)?.projectId
        }
    }

    /// The branches agents worked on in a repository, in the activity read so far, the most attention first.
    func branches(of repository: ActivityRepository) -> [String] {
        guard let identity = repository.identity else { return [] }
        var seconds: [String: TimeInterval] = [:]
        for segment in keptDays.values.flatMap(\.attention) where segment.repository.identity == identity {
            for branch in segment.branches { seconds[branch, default: 0] += segment.range.duration }
        }
        return seconds.sorted { $0.value != $1.value ? $0.value > $1.value : $0.key < $1.key }.map(\.key)
    }

    /// A linked repository as the activity names it, or as its link was named.
    func repository(_ identity: String) -> ActivityRepository {
        if let found = keptDays.values.lazy.flatMap(\.attention).first(where: { $0.repository.identity == identity }) {
            return found.repository
        }
        return ActivityRepository(linked: identity, name: names[identity] ?? identity.split(separator: "/").last.map(String.init) ?? identity)
    }

    /// The projects agents were at work in during the last `within`, the most attention first, each with the task a
    /// branch rule names: what the project picker offers first. Repositories without a project don't count.
    func agentsNow(time: UserTime, projects: [Project], now: Date = Date(), within: TimeInterval = 15 * 60) -> [RepositoryLinks.Placement] {
        refreshContext(time: time)
        guard isEnabled else { return [] }
        let window = DateInterval(start: now - within, end: now)
        let days = Set([time.day(of: window.start), time.day(of: now)]).compactMap { keptDays[$0] }
        guard !days.isEmpty else { return [] }
        let matcher = matcher(time: time, projects: projects)
        var seconds: [RepositoryLinks.Placement: TimeInterval] = [:]
        for segment in days.flatMap(\.attention) where segment.repository.isRepository {
            guard let overlap = segment.range.intersection(with: window), overlap.duration > 0,
                  let placement = matcher.place(segment) else { continue }
            seconds[placement, default: 0] += overlap.duration
        }
        return seconds.sorted { $0.value != $1.value ? $0.value > $1.value : $0.key.projectId < $1.key.projectId }.map(\.key)
    }

    /// Past agent attention no entry covers on these days, for the calendar and the day's review, without what the
    /// person dismissed and without locked days. A stretch needs a prompt of its own. `covered` adds time that is spoken for otherwise, e.g. agents' proposals.
    func untracked(on days: [DayKey], tracker: Tracker, covered: [DateInterval] = [], now: Date = Date()) -> [UntrackedBlock] {
        untracked(on: days, entries: tracker.entries(overlapping: days), time: tracker.time, projects: tracker.catalog.projects,
                  covered: covered, isDayLocked: { tracker.rules?.isLocked($0) == true }, now: now)
    }

    func untracked(on days: [DayKey], entries: [TimeEntry], time: UserTime, projects: [Project], covered: [DateInterval] = [],
                   isDayLocked: (DayKey) -> Bool = { _ in false }, now: Date = Date()) -> [UntrackedBlock] {
        refreshContext(time: time)
        guard isEnabled else { return [] }
        let matcher = matcher(time: time, projects: projects)
        let spoken = Self.covered(entries, now: now) + covered
        return days.filter { !isDayLocked($0) }.flatMap { key -> [UntrackedBlock] in
            guard let day = keptDays[key] else { return [] }
            let bounds = DateInterval(start: time.start(of: key), end: time.start(of: time.adding(1, to: key)))
            let prompts = day.attention.flatMap(\.prompts)
            return UntrackedActivity.stretches(attention: day.attention, covered: spoken, within: bounds, now: now) { matcher.place($0)?.projectId }
                // Only time the person prompted in: the minutes the logs assume after the last prompt before a stop
                // are no sign of work nobody tracked.
                .filter { stretch in prompts.contains { stretch.range.contains($0) } }
                .map { UntrackedBlock(day: key, stretch: $0, minute: Int($0.range.start.timeIntervalSince(bounds.start) / 60)) }
                .filter { !dismissedUntracked.contains($0.id) }
        }
    }

    /// A new entry for untracked time, opened in the editor with its project, or its split, filled in.
    func editing(_ block: UntrackedBlock, tracker: Tracker) -> EntryEditing {
        let projectId = block.stretch.projects.first
        let suggestion = suggestion(from: block.range.start, to: block.range.end, on: projectId, tracker: tracker)
        return Self.editing(newRange: block.range, details: EntryDetails(projectId: projectId), as: suggestion, catalog: tracker.catalog)
    }

    /// Hides untracked time in the calendar, on this Mac.
    func dismiss(_ block: UntrackedBlock) {
        dismissedUntracked.insert(block.id)
        let kept = (defaults.stringArray(forKey: Self.dismissedUntrackedKey) ?? []) + [block.id]
        defaults.set(Array(kept.suffix(Self.dismissedLimit)), forKey: Self.dismissedUntrackedKey)
    }

    /// Leaves an item out of the day's review: "Not Now". Nothing else changes.
    func putOff(_ item: ReviewItem) {
        putOff.insert(item.id)
        let kept = (defaults.stringArray(forKey: Self.putOffKey) ?? []) + [item.id]
        defaults.set(Array(kept.suffix(Self.dismissedLimit)), forKey: Self.putOffKey)
    }

    /// Brings an item put off back into the review: Undo of "Not Now".
    func bringBack(_ item: ReviewItem) {
        putOff.remove(item.id)
        defaults.set((defaults.stringArray(forKey: Self.putOffKey) ?? []).filter { $0 != item.id }, forKey: Self.putOffKey)
    }

    /// The time entries take, a running one up to now.
    private static func covered(_ entries: [TimeEntry], now: Date) -> [DateInterval] {
        entries.map { DateInterval(start: $0.start, end: max($0.start, $0.stop ?? now)) }
    }

    // MARK: - The person's choices

    /// Links a repository to the project its work goes to, from now on, on every device. Its branch rules stay.
    func link(_ repository: ActivityRepository, to projectId: String) {
        guard let identity = repository.identity else { return }
        chosen[identity] = projectId
        noProject.remove(identity)
        names[identity] = repository.name
        unsentLinks[identity] = UnsentLink(name: repository.name, projectId: projectId, rules: unsentLinks[identity]?.rules)
        saveLinks()
    }

    /// Links a repository to `projectId` with these branch rules, which replace those it had, on every device.
    func link(_ repository: ActivityRepository, to projectId: String, rules: [BranchRule]) {
        guard let identity = repository.identity else { return }
        chosen[identity] = projectId
        noProject.remove(identity)
        names[identity] = repository.name
        self.rules[identity] = rules.isEmpty ? nil : rules
        unsentLinks[identity] = UnsentLink(name: repository.name, projectId: projectId, rules: rules)
        saveLinks()
    }

    /// What the person chose for a repository: a project with its branch rules, no project, or neither, which leaves
    /// it to a learned link or its name.
    enum RepositoryChoice: Equatable {
        case project(String, rules: [BranchRule])
        case noProject
        case unchosen
    }

    func choice(of repository: ActivityRepository) -> RepositoryChoice {
        guard let identity = repository.identity else { return .unchosen }
        if noProject.contains(identity) { return .noProject }
        if let projectId = chosen[identity] { return .project(projectId, rules: rules[identity] ?? []) }
        return .unchosen
    }

    /// Whether the person said the repository's work goes to no project.
    func isNoProject(_ repository: ActivityRepository) -> Bool {
        repository.identity.map(noProject.contains) ?? false
    }

    /// Says a repository's work goes to no project, from now on, on every device: its link and branch rules go, and
    /// neither a learned link nor a project named like it takes its work.
    func unlink(_ repository: ActivityRepository) {
        choose(.noProject, for: repository)
    }

    /// Saves what the person chose for a repository, e.g. what it was before an Unlink, on every device.
    func choose(_ choice: RepositoryChoice, for repository: ActivityRepository) {
        guard let identity = repository.identity else { return }
        switch choice {
        case .project(let projectId, let rules):
            link(repository, to: projectId, rules: rules)
            return
        case .noProject:
            noProject.insert(identity)
        case .unchosen:
            noProject.remove(identity)
        }
        chosen[identity] = nil
        rules[identity] = nil
        names[identity] = repository.name
        unsentLinks[identity] = UnsentLink(name: repository.name, projectId: nil, noProject: choice == .noProject ? true : nil)
        saveLinks()
    }

    // MARK: - Ignored repositories

    /// The repositories to leave out of everything read from the logs.
    var ignoring: IgnoredRepositories {
        account.map { IgnoredRepositories(account: $0, hashes: ignoredHashes) } ?? .none
    }

    /// Whether the person ignores this repository.
    func isIgnored(_ repository: ActivityRepository) -> Bool {
        ignoring.contains(repository)
    }

    /// Ignores a repository on every device: none of its prompts and runs show or are uploaded from now on, and its
    /// link goes, so the account keeps nothing that names it. Returns its hash, to stop ignoring it with.
    @discardableResult
    func ignore(_ repository: ActivityRepository) -> String? {
        guard let identity = repository.identity, let account else { return nil }
        let hash = IgnoredRepositories.hash(identity, account: account)
        ignoredNames[hash] = repository.name
        unsentIgnores[hash] = true
        ignoredHashes.insert(hash)
        forgetLink(identity)
        saveIgnored()
        return hash
    }

    /// Stops ignoring a repository on every device; its activity comes back from the logs, which were never changed.
    func unignore(_ hash: String) {
        guard ignoredHashes.contains(hash) || unsentIgnores[hash] == true else { return }
        unsentIgnores[hash] = false
        ignoredHashes.remove(hash)
        saveIgnored()
    }

    /// An ignored repository, by hash, with its name as this Mac knew it; nil when it was ignored elsewhere and this
    /// Mac never saw it.
    func ignoredName(_ hash: String) -> String? {
        ignoredNames[hash]
    }

    /// The account's list, with what was changed here and not sent yet. Names come from links and activity read so far,
    /// before both forget the repositories.
    func absorb(ignored hashes: [String]) {
        var next = Set(hashes)
        for (hash, ignored) in unsentIgnores {
            if ignored { next.insert(hash) } else { next.remove(hash) }
        }
        guard next != ignoredHashes, let account else { return }
        let added = next.subtracting(ignoredHashes)
        if !added.isEmpty {
            let known = Set(names.keys).union(keptDays.values.flatMap(\.attention).compactMap(\.repository.identity))
            for identity in known where added.contains(IgnoredRepositories.hash(identity, account: account)) {
                let hash = IgnoredRepositories.hash(identity, account: account)
                ignoredNames[hash] = ignoredNames[hash] ?? names[identity] ?? repository(identity).name
                forgetLink(identity)
            }
        }
        ignoredHashes = next
        saveIgnored()
    }

    /// The account has this change now, unless it was changed again meanwhile.
    func sent(ignore hash: String, _ ignored: Bool) {
        guard unsentIgnores[hash] == ignored else { return }
        unsentIgnores.removeValue(forKey: hash)
        saveIgnored()
    }

    /// The hashes are the account's own: another account's list says nothing here.
    private func accountChanged(from previous: String?) {
        guard previous != nil, account != nil else { return }
        ignoredHashes = []
        ignoredNames = [:]
        unsentIgnores = [:]
        saveIgnored()
    }

    private func forgetLink(_ identity: String) {
        chosen[identity] = nil
        rules[identity] = nil
        noProject.remove(identity)
        names[identity] = nil
        unsentLinks[identity] = nil
        saveLinks()
    }

    private func saveIgnored() {
        defaults.set(account, forKey: Self.ignoredAccountKey)
        defaults.set(ignoredHashes.sorted(), forKey: Self.ignoredKey)
        defaults.set(ignoredNames.filter { ignoredHashes.contains($0.key) || unsentIgnores[$0.key] != nil }, forKey: Self.ignoredNamesKey)
        defaults.set(unsentIgnores, forKey: Self.unsentIgnoresKey)
    }

    /// Stops suggesting a split for this entry, on every device.
    func dismiss(_ entryId: String, workspaceId: String) {
        withdrawOffer(entryId)
        remember(dismissed: [entryId])
        unsentDismissals[entryId] = workspaceId
        defaults.set(unsentDismissals, forKey: Self.unsentDismissalsKey)
    }

    private func remember(dismissed entryIds: [String]) {
        let new = entryIds.filter { !dismissed.contains($0) }
        guard !new.isEmpty else { return }
        dismissed.formUnion(new)
        let kept = (defaults.stringArray(forKey: "dismissedSplitSuggestions") ?? []) + new
        defaults.set(Array(kept.suffix(Self.dismissedLimit)), forKey: "dismissedSplitSuggestions")
    }

    // MARK: - The account's copy

    /// A link chosen here, still to send, with its branch rules when they changed (nil keeps the account's). Without
    /// a project it says the repository goes to no project, or with `noProject` nil forgets its link.
    struct UnsentLink: Codable, Equatable {
        let name: String
        let projectId: String?
        var rules: [BranchRule]? = nil
        var noProject: Bool? = nil
    }

    /// The links the account has, chosen here or elsewhere, with their branch rules; those chosen here and not yet
    /// sent still count.
    func absorb(links: [String: String], rules accountRules: [String: [BranchRule]] = [:], names accountNames: [String: String] = [:],
                noProject accountNoProject: Set<String> = []) {
        names.merge(accountNames) { _, new in new }
        var next = links
        var nextRules = accountRules.filter { !$0.value.isEmpty }
        var nextNoProject = accountNoProject
        for (identity, link) in unsentLinks {
            if let projectId = link.projectId {
                next[identity] = projectId
                nextNoProject.remove(identity)
                if let unsent = link.rules { nextRules[identity] = unsent.isEmpty ? nil : unsent }
            } else {
                next[identity] = nil
                nextRules[identity] = nil
                if link.noProject == true { nextNoProject.insert(identity) } else { nextNoProject.remove(identity) }
            }
        }
        guard next != chosen || nextRules != rules || nextNoProject != noProject else { return }
        chosen = next
        rules = nextRules
        noProject = nextNoProject
        saveLinks()
    }

    /// The links as the account lists them, but those of repositories ignored here, which the account forgets.
    func absorb(_ links: [RepositoryLink]) {
        let ignoring = ignoring
        let links = links.filter { !ignoring.contains(identity: $0.identity) }
        absorb(links: Dictionary(links.compactMap { link in link.projectId.map { (link.identity, $0) } }, uniquingKeysWith: { first, _ in first }),
               rules: Dictionary(links.map { ($0.identity, $0.rules) }, uniquingKeysWith: { first, _ in first }),
               names: Dictionary(links.map { ($0.identity, $0.name) }, uniquingKeysWith: { first, _ in first }),
               noProject: Set(links.filter { $0.projectId == nil }.map(\.identity)))
    }

    /// The account has this link now, unless it was changed again meanwhile.
    func sent(link identity: String, projectId: String?, rules: [BranchRule]? = nil, noProject: Bool? = nil) {
        guard let unsent = unsentLinks[identity], unsent.projectId == projectId, unsent.rules == rules, unsent.noProject == noProject else { return }
        unsentLinks.removeValue(forKey: identity)
        saveLinks()
    }

    /// Entries kept as they are on another device, or on the web.
    func absorb(dismissals entryIds: [String]) {
        remember(dismissed: entryIds)
    }

    func sent(dismissal entryId: String) {
        unsentDismissals.removeValue(forKey: entryId)
        defaults.set(unsentDismissals, forKey: Self.unsentDismissalsKey)
    }
}

/// What changed in a running entry's work, offered once to take with one click.
enum ChangeOffer {
    /// An entry on one project moved to another: "Still on Atlas?".
    case switchProject(SplitOffer)
    /// A split entry's work changed after the split was set.
    case drift(DriftOffer)
}

/// A split entry's work changed after its split was set: end it there and continue in a new entry.
struct DriftOffer: Identifiable, Equatable {
    let entry: TimeEntry
    let drift: SplitDrift.Drift
    /// The parts the change was worked out for.
    let statement: String
    var id: String { entry.id }

    /// "End Split at 11:05", or "Add Comet from 11:20".
    func title(catalog: Catalog, time: UserTime) -> String {
        switch drift {
        case .quiet(_, let at): "End Split at \(time.clock(at))"
        case .joined(let projectId, let at): "Add \(catalog.project(projectId)?.name ?? "Project") from \(time.clock(at))"
        }
    }

    /// "Beacon has been quiet since 11:05", or "Agents have been in Comet since 11:20".
    func headline(catalog: Catalog, time: UserTime) -> String {
        switch drift {
        case .quiet(let projects, let at):
            "\(Self.names(projects, catalog: catalog)) \(projects.count == 1 ? "has" : "have") been quiet since \(time.clock(at))"
        case .joined(let projectId, let at):
            "Agents have been in \(catalog.project(projectId)?.name ?? "another project") since \(time.clock(at))"
        }
    }

    /// "End the split then and continue on Atlas?", or "Add Comet to the split from then?", after the headline.
    func question(catalog: Catalog, time: UserTime) -> String {
        switch drift {
        case .quiet(let projects, _):
            let rest = entry.parts.compactMap(\.projectId).filter { !projects.contains($0) }
            return "End the split then and continue on \(Self.names(rest, catalog: catalog))?"
        case .joined(let projectId, _):
            return "Add \(catalog.project(projectId)?.name ?? "the project") to the split from then?"
        }
    }

    static func names(_ projects: [String], catalog: Catalog) -> String {
        let names = projects.map { catalog.project($0)?.name ?? "a project" }
        guard names.count > 1 else { return names.first ?? "" }
        return names.dropLast().joined(separator: ", ") + " and " + names.last!
    }
}

/// "Start at 9:12?": agents were at work right before a timer started, with nothing tracked.
struct EarlierStart: Equatable {
    let entry: TimeEntry
    let start: Date
    /// The project the agents' attention went to most, if any.
    let projectId: String?

    /// The agents' project, when the timer has none: taking the offer sets it too, as it replaces nothing the person
    /// chose.
    var fillsProjectId: String? {
        guard let projectId, entry.projectId == nil, !entry.isSplit else { return nil }
        return projectId
    }

    /// The agents' project, when it isn't the timer's: the start moves, the project stays the person's.
    var otherProjectId: String? {
        guard fillsProjectId == nil, let projectId, !entry.parts.contains(where: { $0.projectId == projectId }) else { return nil }
        return projectId
    }
}

/// Agent attention nobody tracked, as the calendar shows it.
struct UntrackedBlock: Identifiable, Equatable {
    let day: DayKey
    let stretch: UntrackedActivity.Stretch
    /// The minute of the day it starts at, which dismissing it remembers.
    let minute: Int
    var range: DateInterval { stretch.range }
    var id: String { "\(day)|\(minute)" }
}

/// A suggestion offered for one entry, to take with one click.
struct SplitOffer: Identifiable {
    let entry: TimeEntry
    let suggestion: SplitSuggestion
    var id: String { entry.id }

    /// The project the work moved to and when, when the suggestion is a move rather than shares.
    var move: (projectId: String, at: Date)? {
        guard let stretches = suggestion.projectStretches, stretches.count == 2 else { return nil }
        return (stretches[1].projectId, stretches[1].range.start)
    }
}
