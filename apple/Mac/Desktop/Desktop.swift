import AppKit
import CoreGraphics
import Observation
import SpindleKit

/// What the app does on its own on the Mac: global shortcuts, asking about time away from a running timer,
/// reminders to track, and the Pomodoro.
@Observable
final class Desktop {
    let settings: DesktopSettings
    /// Offers to split entries whose time went to several projects, from agent activity.
    let splits: SplitAdvisor
    /// Notices agents writing their logs, so activity is read again soon after.
    let activityChanges = ActivityWatcher()
    /// Sends repository links and kept suggestions to the account, and the activity itself when sync is on.
    let activitySync: ActivitySync
    /// Connects Claude Code and Codex to the server's MCP.
    let connector: AgentConnector
    /// Entries and changes agents proposed, waiting for the person on the server.
    let proposals: ProposalInbox
    /// When the current Pomodoro break ends, if on one.
    private(set) var breakEnds: Date?
    /// Shortcuts macOS wouldn't take, e.g. because another app has them.
    private(set) var refusedShortcuts: [ShortcutAction] = []
    /// The days whose review is open in the window, if any.
    var reviewing: ReviewScope? {
        didSet { if reviewing == nil { reviewed = [:] } }
    }
    /// What was done with the review's items while it is open, by item: they keep their place, with Undo.
    private(set) var reviewed: [String: ReviewOutcome] = [:]
    /// Shows what agent logs found in the last week, right after they were turned on.
    var onboarding = false

    private let model: AppModel
    @ObservationIgnored private lazy var notifier = Notifier()
    @ObservationIgnored private lazy var hotKeys = HotKeys()
    /// The panel to type what you work on over any app, as Spotlight.
    @ObservationIgnored private(set) lazy var quickEntry = QuickEntryController(model: model, desktop: self)
    @ObservationIgnored private var ticking: Task<Void, Never>?
    /// Opens the main window again after it was closed; the window hands it over when it first appears.
    @ObservationIgnored var openMainWindow: (() -> Void)?
    @ObservationIgnored private var awaySince: Date?
    @ObservationIgnored private var asleepSince: Date?
    @ObservationIgnored private var asking = false
    @ObservationIgnored private var lastReminder: Date?
    /// The agents' untracked work the last reminder named, for its "Start from 9:12" button; the next stretch of it
    /// may be named without waiting for the next reminder.
    @ObservationIgnored private var remindedOf: (projectId: String, since: Date)?
    @ObservationIgnored private var lastStop: Date?
    @ObservationIgnored private var lastRunning: TimeEntry?
    /// The entry the Pomodoro stopped, to continue after the break.
    @ObservationIgnored private var pausedEntry: TimeEntry?
    /// The agents' revision when the running entry was last checked for a move to another project.
    @ObservationIgnored private var checkedRevision = -1
    @ObservationIgnored private var checkedContext: SplitAdvisor.Context?
    @ObservationIgnored private var lastActivityCheck = Date.distantPast
    @ObservationIgnored private var checkingActivity = false
    /// The switch or change offered in the last notification, for its buttons.
    @ObservationIgnored private var changeOffer: ChangeOffer?
    /// What one click last saved, and when, to undo from its notice or notification: one change, or all that
    /// "Take All" saved, each with the review item it took, if any.
    @ObservationIgnored private var lastTake: (changes: [(change: SavedChange, item: String?)], at: Date)?
    /// When the day's review was last looked for at the end of working hours, and the day it was offered for.
    @ObservationIgnored private var dayReviewChecked = Date.distantPast
    @ObservationIgnored private var dayReviewOffered: DayKey?

    init(model: AppModel, settings: DesktopSettings = DesktopSettings(), active: Bool = true) {
        self.model = model
        self.settings = settings
        splits = SplitAdvisor(settings: settings, watcher: activityChanges)
        activitySync = ActivitySync(model: model, splits: splits, settings: settings)
        proposals = ProposalInbox(model: model)
        connector = AgentConnector(model: model)
        guard active else { return }
        proposals.onArrival = { [weak self] count, client in self?.proposalsArrived(count, from: client) }
        watchActivity()
        notifier.onAction = { [weak self] action, kind in self?.handle(action, kind) }
        applyShortcuts()
        ticking = Task { [weak self] in
            while !Task.isCancelled {
                self?.tick()
                try? await Task.sleep(for: .seconds(5))
            }
        }
        let center = NSWorkspace.shared.notificationCenter
        center.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.asleepSince = Date() }
        }
        center.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.didWake() }
        }
    }

    // MARK: - Shortcuts

    func applyShortcuts() {
        let actions: [(ShortcutAction, () -> Void)] = [
            (.quickEntry, { [weak self] in self?.quickEntry.toggle() }),
            (.toggleTimer, { [weak self] in Task { await self?.model.toggleTimer() } }),
            (.newEntry, { [weak self] in self?.showNewEntry() }),
        ]
        let shortcuts = actions.compactMap { action, run in settings.shortcuts[action].map { ($0, run, action) } }
        let refused = hotKeys.register(shortcuts.map { ($0.0, $0.1) })
        refusedShortcuts = shortcuts.filter { refused.contains($0.0) }.map(\.2)
    }

    private func showNewEntry() {
        showMainWindow()
        model.focusDescription()
    }

    // MARK: - Every few seconds

    /// Watches the logs agent activity reads, or none while it is off; settings can change it at any time.
    private func watchActivity() {
        _ = splits.days
        activityChanges.watch(splits.isEnabled && ActivitySnapshot.root == nil ? settings.activitySources : [])
    }

    private func tick() {
        watchActivity()
        let now = Date()
        runningChanged()
        let running = model.tracker?.running
        let idle = Self.idleSeconds()
        watchIdle(running, idle: idle, now: now)
        remind(running, idle: idle, now: now)
        pomodoro(running, now: now)
        // Notes when the running entry's split is set, also when it is split only later.
        if let running, splits.isEnabled { _ = splits.statement(for: running, now: now) }
        watchParallelWork(running, now: now)
        offerDayReview(now: now)
        activitySync.tick(revision: splits.revision)
        proposals.sync()
    }

    /// Notices the timer starting, stopping or moving to another entry. Called every few seconds, and by the window
    /// as soon as it sees the change.
    func runningChanged() {
        let now = Date()
        let running = model.tracker?.running
        if running?.id != lastRunning?.id {
            if lastRunning != nil && running == nil {
                lastStop = now
            }
            if let stopped = lastRunning {
                Task { await offerSplit(afterStopping: stopped.id) }
                activitySync.uploadSoon()
            }
            if running != nil {
                // A timer started: nothing to remind of, and the break is over.
                notifier.withdraw(.reminder)
                if breakEnds != nil {
                    breakEnds = nil
                    pausedEntry = nil
                }
            }
            lastRunning = running
        }
    }

    /// Seconds since the last keyboard or mouse input.
    private static func idleSeconds() -> TimeInterval {
        CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: CGEventType(rawValue: ~0)!)
    }

    // MARK: - Time away

    private func watchIdle(_ running: TimeEntry?, idle: TimeInterval, now: Date) {
        guard settings.detectIdle, let running else {
            awaySince = nil
            return
        }
        if awaySince == nil, idle >= Double(settings.idleMinutes) * 60 {
            awaySince = max(running.start, now.addingTimeInterval(-idle))
        } else if let since = awaySince, idle < 5 {
            awaySince = nil
            askAboutTimeAway(since: since, entry: running)
        }
    }

    private func didWake() {
        guard settings.detectIdle, let since = asleepSince, let running = model.tracker?.running else { return }
        asleepSince = nil
        if Date().timeIntervalSince(since) >= Double(settings.idleMinutes) * 60 {
            awaySince = nil
            askAboutTimeAway(since: max(since, running.start), entry: running)
        }
    }

    /// The timer ran while nobody was at the Mac: keep that time, or drop it.
    private func askAboutTimeAway(since: Date, entry: TimeEntry) {
        guard !asking, let tracker = model.tracker else { return }
        asking = true
        defer { asking = false }
        let away = Date().timeIntervalSince(since)
        let alert = NSAlert()
        alert.messageText = "You were away for \(Durations.hoursMinutes(away))"
        alert.informativeText = "Since \(tracker.time.clock(since)), while “\(entry.description.isEmpty ? "No description" : entry.description)” was running. What should happen to that time?"
        alert.addButton(withTitle: "Keep It")
        alert.addButton(withTitle: "Discard It")
        alert.addButton(withTitle: "Discard It and Continue")
        NSApp.activate()
        let answer = alert.runModal()
        guard model.tracker?.running?.id == entry.id else { return }
        switch answer {
        case .alertSecondButtonReturn:
            Task { await model.stop(at: since) }
        case .alertThirdButtonReturn:
            Task {
                if await model.stop(at: since) {
                    var details = entry.details
                    details.isBillable = entry.isBillable
                    await model.start(details)
                }
            }
        default:
            break
        }
    }

    // MARK: - Reminders

    private func remind(_ running: TimeEntry?, idle: TimeInterval, now: Date) {
        guard settings.remindToTrack, let tracker = model.tracker, breakEnds == nil else { return }
        // Agents at work in a linked project with no timer make the reminder concrete, as soon as they have been at
        // it for a while, once for each stretch of their work.
        let ongoing = running == nil ? splits.ongoingUntracked(tracker: tracker, now: now) : nil
        let work = ongoing.flatMap { stretch in stretch.projects.first.map { (projectId: $0, since: stretch.range.start) } }
        let new = work != nil && work?.since != remindedOf?.since
        let last = [lastReminder, lastStop].compactMap { $0 }.max()
        guard settings.reminders.shouldRemind(now: now, in: tracker.time, isRunning: running != nil, lastRemindedOrStopped: new ? nil : last,
                                              idleSeconds: idle) else { return }
        lastReminder = now
        remindedOf = work
        if let work {
            let name = tracker.catalog.project(work.projectId)?.name ?? "a project"
            let clock = tracker.time.clock(work.since)
            notifier.post(.reminder, title: "Agents have been working in \(name) since \(clock)", body: "Start \(name) from \(clock)?",
                          actions: [(.startFrom, "Start from \(clock)"), (.keep, "Not Now")])
        } else {
            notifier.post(.reminder, title: "Are you tracking your time?", body: "Nothing is running. Start a timer to keep your day complete.",
                          actions: [(.startTimer, "Start Timer")])
        }
    }

    /// Starts the timer on the project agents have been working in, from when they began, as the reminder offered.
    private func startRemindedWork() {
        guard let tracker = model.tracker, tracker.running == nil, let work = remindedOf else { return }
        remindedOf = nil
        guard model.readOnlyOrganization == nil, !tracker.isLocked(work.since) else {
            Task { await model.start() }
            return
        }
        let name = tracker.catalog.project(work.projectId)?.name ?? "the project"
        Task {
            do {
                let id = try await tracker.start(EntryDetails(projectId: work.projectId), at: work.since)
                saved(SavedChange(before: nil, added: [id]), "Started \(name) from \(tracker.time.clock(work.since))")
            } catch {
                model.notices.show(error, fallback: "Couldn't start the timer.")
            }
        }
    }

    // MARK: - Pomodoro

    private func pomodoro(_ running: TimeEntry?, now: Date) {
        guard let preferences = model.me?.preferences, preferences.pomodoroEnabled else {
            breakEnds = nil
            return
        }
        if let ends = breakEnds, now >= ends {
            breakEnds = nil
            notifier.post(.breakOver, title: "Break's over", body: pausedEntry.map { "Ready to continue “\($0.description.isEmpty ? "No description" : $0.description)”?" } ?? "Ready for the next focus interval?")
        }
        guard let running else { return }
        let focus = Int(preferences.pomodoroMinutes)
        let intervals = Pomodoro.intervalsDone(elapsed: now.timeIntervalSince(running.start), focusMinutes: focus)
        guard intervals > 0 else { return }
        // Once per interval, even across launches.
        let key = "\(running.id):\(intervals)"
        guard UserDefaults.standard.string(forKey: "pomodoroLast") != key else { return }
        UserDefaults.standard.set(key, forKey: "pomodoroLast")

        let breakMinutes = Int(preferences.pomodoroBreakMinutes)
        NSSound(named: "Glass")?.play()
        if preferences.pomodoroAutoStop {
            pausedEntry = running
            breakEnds = now.addingTimeInterval(Double(breakMinutes) * 60)
            let stop = running.start.addingTimeInterval(Double(intervals * focus) * 60)
            Task { await model.stop(at: min(stop, now)) }
            notifier.post(.focusDone, title: "Time for a break", body: "\(focus) minutes of focus done. The timer stopped; take \(breakMinutes) minutes off.")
        } else {
            notifier.post(.focusDone, title: "Time for a break", body: "\(focus) minutes of focus done. Take \(breakMinutes) minutes off.")
        }
    }

    // MARK: - Notification buttons

    private func handle(_ action: Notifier.Action?, _ kind: Notifier.Kind) {
        switch action {
        case .startTimer:
            Task { await model.start() }
        case .startFrom:
            startRemindedWork()
        case .stopTimer:
            Task { await model.stop() }
        case .continueEntry:
            if let entry = pausedEntry {
                var details = entry.details
                details.isBillable = entry.isBillable
                Task { await model.start(details) }
            } else {
                Task { await model.start() }
            }
            pausedEntry = nil
        case .split:
            if let offer = splits.offer { take(offer) }
        case .undo:
            if kind == .splitSaved { undoTake() } else { quickEntry.quick.undo() }
        case .review where kind == .dayReview:
            if let day = dayReviewOffered ?? model.tracker.map({ $0.time.day(of: Date()) }) { openReview(.day(day)) }
        case .review:
            reviewProposals()
        case .switchProject:
            switch changeOffer {
            case .switchProject(let offer): take(offer)
            case .drift(let offer): take(offer)
            case nil: break
            }
            changeOffer = nil
        case .keep where kind == .reminder:
            // "Not Now" on agents' untracked work: the next reminder comes as usual.
            break
        case .keep:
            // "Still on Atlas" is only an answer for now: the switch was offered, and the split can still come when
            // the timer stops. "Keep as Is" on a stopped entry's split means never. "Not Now" on a change to a split
            // puts it off until the split changes.
            if kind == .splitOffer, let offer = splits.offer { keep(offer) }
            if kind == .switchOffer, case .drift(let offer) = changeOffer { splits.answer(offer) }
            changeOffer = nil
        case nil:
            // The notification itself: bring the app up, ready to type, or with the split to look at.
            if kind == .proposals {
                reviewProposals()
            } else if kind == .dayReview, let day = dayReviewOffered {
                openReview(.day(day))
            } else if kind == .reminder || kind == .breakOver {
                showNewEntry()
            } else if kind == .switchOffer, let offer = changeOffer {
                switch offer {
                case .switchProject(let offer): review(offer)
                case .drift(let offer): review(offer)
                }
                changeOffer = nil
            } else if kind == .splitOffer, let offer = splits.offer {
                review(offer)
            } else {
                NSApp.activate()
            }
        }
    }

    // MARK: - Parallel work

    /// While a timer runs, reads today's agent activity again when agents wrote their logs (or every few minutes
    /// without change notices), so a move to another project is noticed even with the window closed.
    private func watchParallelWork(_ running: TimeEntry?, now: Date) {
        // Without a timer, today's activity is for the reminder to track.
        guard splits.isEnabled, running != nil || settings.remindToTrack, let tracker = model.tracker, !checkingActivity else { return }
        let context = splits.context(time: tracker.time)
        let changed = checkedContext != context
        let due = changed || (activityChanges.isWatching
            ? activityChanges.revision != checkedRevision
            : now.timeIntervalSince(lastActivityCheck) >= 150)
        guard due else { return }
        checkingActivity = true
        checkedContext = context
        checkedRevision = activityChanges.revision
        lastActivityCheck = now
        Task { [weak self] in
            await self?.splits.load([tracker.time.day(of: Date())], tracker: tracker)
            self?.checkingActivity = false
            if tracker.running != nil { self?.offerChange(tracker) }
        }
    }

    /// Notifies once when the running entry's work changed a while ago: "Still on Atlas?" when it moved to another
    /// project, or, for a split, when one of its projects went quiet or another joined.
    private func offerChange(_ tracker: Tracker) {
        guard settings.notifySwitches, let running = tracker.running,
              let offer = splits.changeOffer(for: running, tracker: tracker) else { return }
        switch offer {
        case .switchProject(let switchOffer):
            guard let move = switchOffer.move else { return }
            let from = tracker.catalog.project(running.projectId)?.name ?? "this project"
            let to = tracker.catalog.project(move.projectId)?.name ?? "another project"
            notifier.post(.switchOffer, title: "Still on \(from)?",
                          body: "Your agents have been working in \(to) since \(tracker.time.clock(move.at)). Switch the timer to \(to) from then?",
                          actions: [(.switchProject, "Switch to \(to)"), (.keep, "Still on \(from)")])
        case .drift(let drift):
            notifier.post(.switchOffer, title: drift.headline(catalog: tracker.catalog, time: tracker.time),
                          body: drift.question(catalog: tracker.catalog, time: tracker.time),
                          actions: [(.switchProject, drift.title(catalog: tracker.catalog, time: tracker.time)), (.keep, "Not Now")])
        }
        splits.markOffered(offer)
        changeOffer = offer
    }

    /// Offers a split for the entry the timer just stopped: in the window, or in a notification when the app is in
    /// the background.
    private func offerSplit(afterStopping entryId: String) async {
        guard let tracker = model.tracker else { return }
        let offer = await splits.offerAfterStop(entryId, tracker: tracker)
        if let drift = splits.driftOffer, drift.entry.id == entryId, !NSApp.isActive && settings.notifySwitches {
            changeOffer = .drift(drift)
            notifier.post(.switchOffer, title: drift.headline(catalog: tracker.catalog, time: tracker.time),
                          body: drift.question(catalog: tracker.catalog, time: tracker.time),
                          actions: [(.switchProject, drift.title(catalog: tracker.catalog, time: tracker.time)), (.keep, "Not Now")])
        }
        guard let offer else { return }
        if !NSApp.isActive && settings.notifySwitches {
            let name = offer.entry.description.isEmpty ? "the time" : "“\(offer.entry.description)”"
            let summary = SplitApplier.summary(offer.suggestion, entry: offer.entry, catalog: tracker.catalog, time: tracker.time)
            notifier.post(.splitOffer, title: "Split \(name)?", body: "Your agents worked on more than one project: \(summary).")
        }
    }

    /// Takes an offered split with one click, on the entry as it is now. When the workspace needs details the split
    /// can't fill in, the editor opens with it instead.
    func take(_ offer: SplitOffer) {
        guard let tracker = model.tracker,
              let entry = tracker.running?.id == offer.entry.id ? tracker.running : tracker.entries.first(where: { $0.id == offer.entry.id }),
              !entry.isSplit, entry.projectId == offer.entry.projectId else {
            splits.withdrawOffer(offer.entry.id)
            return
        }
        splits.withdrawOffer(entry.id)
        Task {
            do {
                switch try await SplitApplier.apply(offer.suggestion, to: entry, tracker: tracker) {
                case .saved(let change):
                    let text = if entry.isRunning, let move = offer.move {
                        "Switched to \(tracker.catalog.project(move.projectId)?.name ?? "the other project")"
                    } else {
                        "Split saved"
                    }
                    saved(change, text)
                case .needsDetails:
                    review(offer)
                }
            } catch {
                model.notices.show(error, fallback: "Couldn't split the entry.")
            }
        }
    }

    /// Ends a split entry where its work changed and continues in a new entry, with one click. When the workspace
    /// needs details the change can't fill in, the editor opens instead.
    func take(_ offer: DriftOffer) {
        splits.answer(offer)
        guard let tracker = model.tracker,
              let entry = tracker.running?.id == offer.entry.id ? tracker.running : tracker.entries.first(where: { $0.id == offer.entry.id }),
              SplitAdvisor.partsKey(entry) == offer.statement else { return }
        let parts = SplitDrift.continuing(entry.parts, after: offer.drift) { tracker.catalog.project($0)?.isBillable ?? false }
        Task {
            do {
                switch try await SplitApplier.endAndContinue(entry, at: offer.drift.at, on: parts, tracker: tracker) {
                case .saved(let change):
                    // The new entry's split was set where it begins, and nothing about it was offered yet.
                    if let id = change.added.first, entry.isRunning {
                        splits.state(id, SplitAdvisor.Statement(parts: SplitAdvisor.partsKey(parts), at: offer.drift.at))
                    }
                    let text = switch offer.drift {
                    case .quiet(_, let at): "Split ended at \(tracker.time.clock(at))"
                    case .joined(let projectId, let at): "Added \(tracker.catalog.project(projectId)?.name ?? "the project") from \(tracker.time.clock(at))"
                    }
                    saved(change, text)
                case .needsDetails:
                    review(offer)
                }
            } catch {
                model.notices.show(error, fallback: "Couldn't change the entry.")
            }
        }
    }

    /// Moves the running entry's start back to where the agents' untracked work began. A project the person chose
    /// stays; a timer without one gets the agents' project too.
    func take(_ offer: EarlierStart) {
        guard let tracker = model.tracker, let running = tracker.running, running.id == offer.entry.id, running.start == offer.entry.start,
              !tracker.isLocked(offer.start) else { return }
        let project = running.projectId == nil ? tracker.catalog.project(offer.fillsProjectId) : nil
        Task {
            do {
                try await tracker.updateRunning {
                    $0.start = offer.start
                    if let project {
                        $0.details.projectId = project.id
                        $0.details.taskId = nil
                        $0.details.isBillable = project.isBillable
                        $0.details.shares = []
                    }
                }
                let started = project.map { "Started \($0.name) at" } ?? "Started at"
                saved(SavedChange(before: running, added: []), "\(started) \(tracker.time.clock(offer.start))")
            } catch {
                model.notices.show(error, fallback: "Couldn't change the start.")
            }
        }
    }

    // MARK: - Agent activity in the calendar

    /// Agent activity in the calendar is a beta feature: it can be shown only with beta features on.
    var canShowActivity: Bool { ActivitySnapshot.root != nil || settings.betaFeatures }

    /// Whether the calendar shows agent activity. Hiding it doesn't stop the logs being read for suggestions.
    var showsActivity: Bool { canShowActivity && settings.showAgentActivity && settings.useAgentLogs }

    /// Shows or hides agent activity in the calendar. Showing it is asking for the logs to be read.
    func toggleActivity() {
        setShowsActivity(!showsActivity)
    }

    func setShowsActivity(_ on: Bool) {
        settings.showAgentActivity = on
        if on { settings.useAgentLogs = true }
    }

    /// Says a repository's work goes to no project, with Undo putting back what it had.
    func unlink(_ repository: ActivityRepository) {
        let before = splits.choice(of: repository)
        splits.unlink(repository)
        model.notices.show("Unlinked \(repository.name)", action: Notice.Action(title: "Undo") { [weak self] in
            self?.splits.choose(before, for: repository)
        })
    }

    /// Opens a split entry whose work changed in the editor, to change its split or times there.
    func review(_ offer: DriftOffer) {
        splits.answer(offer)
        guard let tracker = model.tracker,
              let entry = tracker.running?.id == offer.entry.id ? tracker.running : tracker.entries.first(where: { $0.id == offer.entry.id }) else { return }
        showMainWindow()
        model.editing = EntryEditing(entry: entry)
    }

    /// Says what one click just saved, with Undo: a notice in the window, and a notification when the window isn't
    /// in front, e.g. after a take from the menu bar or a notification.
    func saved(_ change: SavedChange, _ text: String) {
        saved([change], text)
    }

    func saved(_ changes: [SavedChange], _ text: String) {
        saved(changes.map { ($0, nil) }, text)
    }

    private func saved(_ changes: [(change: SavedChange, item: String?)], _ text: String) {
        lastTake = (changes, Date())
        notifier.withdraw(.splitSaved)
        model.notices.show(text, action: Notice.Action(title: "Undo") { [weak self] in self?.undoTake() })
        if !NSApp.isActive {
            notifier.post(.splitSaved, title: text, body: "Undo puts the entry back as it was.", sound: false)
        }
    }

    /// How long a take can be undone from its notification.
    static let undoTakeFor: TimeInterval = 5 * 60

    /// Puts back the entry the last one-click take changed, and removes the entries it added.
    func undoTake() {
        notifier.withdraw(.splitSaved)
        guard let tracker = model.tracker, let take = lastTake, Date().timeIntervalSince(take.at) < Self.undoTakeFor else { return }
        lastTake = nil
        Task {
            do {
                // The newest first, so each is put back over what came after it.
                for (change, item) in take.changes.reversed() {
                    try await change.undo(tracker: tracker)
                    if let item { reviewed[item] = nil }
                }
                model.notices.show("Undone")
            } catch {
                model.notices.show(error, fallback: "Couldn't undo.")
            }
        }
    }

    /// Opens the entry with the offered split taken, to change it before saving.
    func review(_ offer: SplitOffer) {
        guard let tracker = model.tracker else { return }
        let entry = tracker.running?.id == offer.entry.id ? tracker.running : tracker.entries.first { $0.id == offer.entry.id }
        splits.withdrawOffer(offer.entry.id)
        guard let entry else { return }
        showMainWindow()
        model.editing = SplitAdvisor.editing(entry, as: offer.suggestion, catalog: tracker.catalog)
    }

    /// Leaves the entry as it is and stops suggesting a split for it.
    func keep(_ offer: SplitOffer) {
        splits.dismiss(offer.entry.id, workspaceId: offer.entry.workspaceId)
    }

    // MARK: - The day's review

    /// Opens the review of these days in the window.
    func openReview(_ scope: ReviewScope) {
        notifier.withdraw(.dayReview)
        if model.section != .timer && model.section != .calendar { model.section = .timer }
        showMainWindow()
        reviewing = scope
    }

    /// What the review offers on these days, with the time agents proposed entries for spoken for, as in the
    /// calendar.
    func review(on days: [DayKey], tracker: Tracker) -> [ReviewItem] {
        let proposed = proposals.pending(in: tracker).compactMap { proposal -> DateInterval? in
            let request = proposal.request(current: proposal.entryId.flatMap(tracker.entry(id:)))
            return request.stop.map { DateInterval(start: request.start, end: max(request.start, $0)) }
        }
        return splits.review(on: days, tracker: tracker, covered: proposed)
    }

    /// Reads what the review of these days needs: their entries as the server has them, and their agent activity.
    func loadReview(_ days: [DayKey], tracker: Tracker) async {
        guard let first = days.min(), let last = days.max() else { return }
        // A day before too, for an entry that runs over midnight into the first.
        _ = await tracker.entries(from: tracker.time.start(of: tracker.time.adding(-1, to: first)),
                                  to: tracker.time.start(of: tracker.time.adding(1, to: last)))
        await splits.load(days, tracker: tracker)
    }

    /// Takes one item of the review with one click, with Undo. When the workspace needs details one click can't
    /// fill in, the editor opens with it instead.
    func take(_ item: ReviewItem) async {
        guard let tracker = model.tracker else { return }
        guard splits.blocker(item, tracker: tracker) == nil else {
            change(item)
            return
        }
        do {
            switch try await apply(item, tracker: tracker) {
            case .saved(let change)?:
                let text = Self.taken(item, catalog: tracker.catalog, time: tracker.time)
                reviewed[item.id] = ReviewOutcome(item: item, kind: .taken(text, change))
                saved([(change, item.id)], text)
            case .needsDetails?: change(item)
            case nil: break
            }
        } catch {
            model.notices.show(error, fallback: "Couldn't save it.")
        }
    }

    /// Takes every item one click can take, as they are offered, with one Undo for all of them. Those that need
    /// details, or a project for a repository, stay in the review, and the notice says how many. Returns how many
    /// were taken.
    @discardableResult
    func takeAll(_ items: [ReviewItem]) async -> Int {
        guard let tracker = model.tracker else { return 0 }
        var changes: [(change: SavedChange, item: String?)] = []
        var failed = 0
        for item in items where splits.blocker(item, tracker: tracker) == nil {
            do {
                if case .saved(let change)? = try await apply(item, tracker: tracker) {
                    reviewed[item.id] = ReviewOutcome(item: item, kind: .taken(Self.taken(item, catalog: tracker.catalog, time: tracker.time), change))
                    changes.append((change, item.id))
                }
            } catch {
                failed += 1
            }
        }
        let left = items.count - changes.count - failed
        let text = [
            changes.count == 1 ? "Saved 1 suggestion" : "Saved \(changes.count) suggestions",
            failed == 0 ? nil : "\(failed) couldn't be saved",
            left == 0 ? nil : "\(left) \(left == 1 ? "needs" : "need") a project or details",
        ].compactMap { $0 }.joined(separator: " · ")
        if !changes.isEmpty {
            saved(changes, text)
        } else if failed > 0 {
            model.notices.show(text, kind: .error)
        }
        return changes.count
    }

    /// Leaves an item out of the review, "Not Now"; it keeps its place until the review closes, with Undo.
    func notNow(_ item: ReviewItem) {
        splits.putOff(item)
        reviewed[item.id] = ReviewOutcome(item: item, kind: .notNow)
    }

    /// Takes back what was done with one item of the open review: a take is undone, and an item put off comes back.
    func undo(reviewed id: String) {
        guard let outcome = reviewed[id], let tracker = model.tracker else { return }
        switch outcome.kind {
        case .changed:
            return
        case .notNow:
            splits.bringBack(outcome.item)
            reviewed[id] = nil
        case .taken(_, let change):
            Task {
                do {
                    try await change.undo(tracker: tracker)
                    reviewed[id] = nil
                    // The footer's Undo keeps the rest of what it covered.
                    guard var take = lastTake, take.changes.contains(where: { $0.item == id }) else { return }
                    take.changes.removeAll { $0.item == id }
                    if take.changes.isEmpty {
                        lastTake = nil
                        notifier.withdraw(.splitSaved)
                        model.notices.show("Undone")
                    } else {
                        lastTake = take
                        let count = take.changes.count
                        model.notices.show(count == 1 ? "Saved 1 suggestion" : "Saved \(count) suggestions",
                                           action: Notice.Action(title: "Undo") { [weak self] in self?.undoTake() })
                    }
                } catch {
                    model.notices.show(error, fallback: "Couldn't undo.")
                }
            }
        }
    }

    /// Opens an item of the review in the editor with what it offers filled in, to change before saving.
    func change(_ item: ReviewItem) {
        guard let tracker = model.tracker else { return }
        var editing: EntryEditing
        switch item {
        case .split(let offer):
            guard let entry = tracker.entry(id: offer.entry.id) else { return }
            editing = SplitAdvisor.editing(entry, as: offer.suggestion, catalog: tracker.catalog)
        case .drift(let offer):
            guard let entry = tracker.entry(id: offer.entry.id) else { return }
            editing = EntryEditing(entry: entry)
        case .untracked(let block):
            editing = splits.editing(block, tracker: tracker)
        }
        // Saved there, the item keeps its place in the review, as saved in the editor.
        let onSaved = editing.onSaved
        editing.onSaved = { [weak self] in
            onSaved?()
            if self?.reviewing != nil { self?.reviewed[item.id] = ReviewOutcome(item: item, kind: .changed) }
        }
        model.editing = editing
    }

    /// Saves what a review item offers, on the entry as it is now. Nil when the entry changed meanwhile, so the item
    /// no longer applies.
    private func apply(_ item: ReviewItem, tracker: Tracker) async throws -> SplitApplier.Outcome? {
        switch item {
        case .split(let offer):
            guard let entry = tracker.entry(id: offer.entry.id), !entry.isSplit, entry.projectId == offer.entry.projectId,
                  entry.start == offer.entry.start, entry.stop == offer.entry.stop else { return nil }
            return try await SplitApplier.apply(offer.suggestion, to: entry, tracker: tracker)
        case .drift(let offer):
            guard let entry = tracker.entry(id: offer.entry.id), SplitAdvisor.partsKey(entry) == offer.statement else { return nil }
            let parts = SplitDrift.continuing(entry.parts, after: offer.drift) { tracker.catalog.project($0)?.isBillable ?? false }
            let outcome = try await SplitApplier.endAndContinue(entry, at: offer.drift.at, on: parts, tracker: tracker)
            if case .saved = outcome { splits.answer(offer) }
            return outcome
        case .untracked(let block):
            guard !tracker.isLocked(block.range.start), let requests = splits.requests(for: block, tracker: tracker) else { return .needsDetails }
            guard SplitApplier.missing(requests, tracker: tracker) == nil else { return .needsDetails }
            return .saved(try await SplitApplier.save(requests, replacing: nil, tracker: tracker))
        }
    }

    /// What one click on an item did, for its notice: "Split saved", "Split ended at 11:05", "Added Atlas 14:00–15:30".
    static func taken(_ item: ReviewItem, catalog: Catalog, time: UserTime) -> String {
        switch item {
        case .split: return "Split saved"
        case .drift(let offer):
            return switch offer.drift {
            case .quiet(_, let at): "Split ended at \(time.clock(at))"
            case .joined(let projectId, let at): "Added \(catalog.project(projectId)?.name ?? "the project") from \(time.clock(at))"
            }
        case .untracked(let block):
            let name = block.stretch.projects.first.flatMap { catalog.project($0)?.name } ?? "an entry"
            return "Added \(name) \(time.clock(block.range.start))–\(time.clock(block.range.end))"
        }
    }

    /// Once, in the hours after work on a working day, says the day has suggestions waiting in its review, if
    /// notifications for splits are on. Looked for every few minutes then, as the last timer may stop late.
    private func offerDayReview(now: Date) {
        guard settings.notifyDayReview, settings.notifySwitches, splits.isEnabled, let tracker = model.tracker,
              let day = settings.reminders.endedWorkingDay(at: now, in: tracker.time), dayReviewOffered != day,
              now.timeIntervalSince(dayReviewChecked) >= 10 * 60,
              UserDefaults.standard.string(forKey: "dayReviewOffered") != day.description else { return }
        dayReviewChecked = now
        Task { [weak self] in
            guard let self else { return }
            await splits.load([day], tracker: tracker)
            let count = review(on: [day], tracker: tracker).count
            guard count > 0, dayReviewOffered != day else { return }
            dayReviewOffered = day
            UserDefaults.standard.set(day.description, forKey: "dayReviewOffered")
            notifier.post(.dayReview, title: "Today: \(count) to review",
                          body: ReviewItem.breakdown(review(on: [day], tracker: tracker)) + ".",
                          actions: [(.review, "Review")])
        }
    }

    /// Tells of proposals an agent just made: a notice in the window, and a notification while the person is in
    /// another app, e.g. the terminal they asked from.
    private func proposalsArrived(_ count: Int, from client: String?) {
        let summary = "\(client ?? "An agent") proposed \(count == 1 ? "a change" : "\(count) changes") to your time."
        model.notices.show(summary)
        if !NSApp.isActive {
            notifier.post(.proposals, title: summary, body: "Nothing is saved until you add it. Review them in Spindle.")
        }
    }

    /// Opens the window with the proposals to review.
    func reviewProposals() {
        notifier.withdraw(.proposals)
        if model.section != .timer && model.section != .calendar { model.section = .timer }
        showMainWindow()
        proposals.reviewing = true
    }

    /// Says what the quick entry panel just did, with Undo, after it went away.
    func notifyQuickEntry(_ summary: String) {
        notifier.post(.quickEntry, title: summary, body: "From quick entry. Undo here, or with ⌘Z in the panel.", sound: false)
    }

    func showMainWindow() {
        NSApp.activate()
        if let window = NSApp.windows.first(where: { $0.identifier?.rawValue.hasPrefix("main") == true && $0.isVisible }) {
            window.makeKeyAndOrderFront(nil)
        } else {
            openMainWindow?()
        }
    }

    /// Asks for notifications when a setting needs them; false if the person turned them off.
    func allowNotifications() async -> Bool {
        await notifier.requestPermission()
    }

    func notificationsDenied() async -> Bool {
        await notifier.isDenied()
    }
}
