import AppKit
import Observation
import SpindleKit

/// One row of the quick entry panel: something to do, with what Enter, ⌘↩ and Tab do for it.
struct QuickRow: Identifiable {
    enum Tone { case normal, warning, blocked }

    var id: String
    var title: String
    var detail: String?
    var tone = Tone.normal
    var symbol: String?
    /// A project's colour, shown instead of a symbol.
    var color: String?
    /// A split's parts, shown as dots instead of a symbol.
    var parts: [SplitPart]?
    var meta: String?
    /// The detail goes on a line of its own, e.g. for what a typed line will save.
    var stacked = false
    var checked = false
    var keywords: [String] = []
    var boost = 0.0
    /// ↩
    var primary: QuickAction
    /// ⌘↩
    var secondary: QuickAction?
    /// Further actions, listed with Tab.
    var more: [QuickAction] = []

    var actions: [QuickAction] { [primary] + (secondary.map { [$0] } ?? []) + more }
}

struct QuickAction {
    var label: String
    /// The panel stays open after it, e.g. to tick several tags, or because it opened a page.
    var keepOpen = false
    var run: () async throws -> Void
}

struct QuickSection: Identifiable {
    var id: String
    var title: String?
    var rows: [QuickRow]
}

/// What was just done from the panel, to undo from it with ⌘Z or from the notification's Undo button.
struct QuickDone {
    let summary: String
    let at = Date()
    let undo: () async throws -> Void
}

/// The quick entry panel's state: the line typed and its chips, the pages opened, the selected row, and what was just
/// done. Everything goes through the tracker and its outbox, so it works offline as the rest of the app does.
@Observable
final class QuickEntry {
    enum Page {
        /// Picks a project or task; with a client too when `clients`.
        case project(title: String, pick: (String?, String?) -> Void)
        /// Ticks tags on the running entry.
        case tags
        /// Changes when the running entry started.
        case startTime
        case split(SplitDraft)
        /// Everything that can be done with one row.
        case actions(QuickRow)

        var title: String {
            switch self {
            case .project(let title, _): title
            case .tags: "Tags"
            case .startTime: "Start time"
            case .split(let draft): draft.entry.description.isEmpty ? "Split" : "Split “\(draft.entry.description)”"
            case .actions(let row): row.title
            }
        }

        var placeholder: String {
            switch self {
            case .project: "Search projects, tasks or clients"
            case .tags: "Search or create tags"
            case .startTime: "A time such as 9:40, or -15 for 15 minutes ago"
            case .split: "A share for the selected part, e.g. 60"
            case .actions: "Search actions"
            }
        }
    }

    struct Frame {
        var page: Page
        var query = ""
    }

    let model: AppModel
    let desktop: Desktop

    /// The line on the first page.
    var text = "" {
        didSet { textChanged(from: oldValue) }
    }
    var caret = 0
    /// Where to put the caret once the field shows new text.
    var caretRequest: Int?
    var chips: [EntrySentence.Chip] = []
    /// A chip turned back into text, e.g. "@Booking engine", is still completed as one, spaces and all.
    private var reverted: (start: Int, end: Int)?
    private(set) var frames: [Frame] = []
    /// The selected row; until the person moves it, the first row is, whatever arrives or reorders.
    private var chosen: String?
    private var followTop = true
    /// Something went wrong; shown in the footer until the next change.
    var error: String?
    private(set) var done: QuickDone?
    var usage: PaletteUsage
    /// Closes the panel and goes back to the app that was in front.
    @ObservationIgnored var close: () -> Void = {}

    /// How long the last action can be undone from the panel.
    static let undoFor: TimeInterval = 5 * 60

    init(model: AppModel, desktop: Desktop) {
        self.model = model
        self.desktop = desktop
        usage = model.workspace.map { PaletteUsage.load(workspaceID: $0.id) } ?? PaletteUsage()
    }

    var tracker: Tracker? { model.tracker }
    var page: Page? { frames.last?.page }

    /// Every opening starts fresh, on the first page.
    func reset() {
        frames = []
        chips = []
        reverted = nil
        text = ""
        caret = 0
        chosen = nil
        followTop = true
        error = nil
        if let workspace = model.workspace {
            usage = PaletteUsage.load(workspaceID: workspace.id)
        }
    }

    // MARK: - The field

    /// What the field shows: the line, or the page's own query.
    var query: String {
        get { frames.last?.query ?? text }
        set {
            if frames.isEmpty {
                text = newValue
            } else {
                frames[frames.count - 1].query = newValue
                splitTyped(newValue)
            }
            followTop = true
            error = nil
        }
    }

    var placeholder: String {
        if let page { return page.placeholder }
        if !chips.isEmpty { return "Describe it, add a time…" }
        return tracker?.running != nil ? "Search, or type what you’re doing next…" : "What are you working on? @project #tag since 9:40"
    }

    private func textChanged(from old: String) {
        guard let range = reverted else { return }
        // The reverted chip's text grows and shrinks with what is typed in it.
        let end = range.end + text.utf16.count - old.utf16.count
        reverted = caret > range.start && caret <= end ? (range.start, end) : nil
    }

    func caretMoved(to position: Int) {
        caret = position
        if let range = reverted, position <= range.start || position > range.end {
            reverted = nil
        }
    }

    /// The `@project` or `#tag` being typed at the caret, on the first page.
    var mention: EntrySentence.Mention? {
        guard frames.isEmpty else { return nil }
        if let range = reverted, caret > range.start, caret <= range.end {
            let units = Array(text.utf16)
            if range.end <= units.count, let sign = String(decoding: units[range.start...range.start], as: UTF16.self).first, sign == "@" || sign == "#" {
                return EntrySentence.Mention(sign: sign, query: String(decoding: units[(range.start + 1)..<range.end], as: UTF16.self), start: range.start, end: range.end)
            }
        }
        return EntrySentence.mention(in: text, caret: caret)
    }

    var options: EntrySentence.Options {
        EntrySentence.Options(hour12: tracker?.time.hour12 ?? false, dateFormat: model.me?.preferences.dateFormat ?? "dd.MM.yyyy")
    }

    /// Adds a chip, taking out the mention it was picked for. A project or client replaces the one picked before.
    func addChip(_ chip: EntrySentence.Chip) {
        var next = (text: text, caret: text.utf16.count)
        if let mention { next = EntrySentence.removing(mention, from: text) }
        switch chip {
        case .project, .client:
            chips.removeAll { if case .project = $0 { true } else if case .client = $0 { true } else { false } }
            chips.append(chip)
        default:
            if !chips.contains(chip) { chips.append(chip) }
        }
        reverted = nil
        text = next.text
        caret = next.caret
        caretRequest = next.caret
        followTop = true
    }

    func removeChip(at index: Int) {
        guard chips.indices.contains(index) else { return }
        chips.remove(at: index)
        followTop = true
    }

    /// The last chip turns back into text, to change what was picked.
    func revertLastChip() {
        guard let chip = chips.popLast(), let tracker else { return }
        let token: String = switch chip {
        case let .project(projectID, taskID): "@" + (tracker.catalog.task(taskID)?.name ?? tracker.catalog.project(projectID)?.name ?? "")
        case let .client(id): "@" + (tracker.catalog.client(id)?.name ?? "")
        case let .tag(id): "#" + (tracker.catalog.tag(id)?.name ?? "")
        case .billable: "$"
        }
        let length = token.utf16.count
        text = text.isEmpty ? token : "\(token) \(text)"
        caret = length
        caretRequest = length
        reverted = token.hasPrefix("@") || token.hasPrefix("#") ? (0, length) : nil
        followTop = true
    }

    /// Adds text at the end of the line and puts the caret there, e.g. `#` to pick a tag.
    func append(_ addition: String) {
        let trimmed = text.replacing(/\s+$/, with: "")
        text = trimmed.isEmpty ? addition : "\(trimmed) \(addition)"
        caret = text.utf16.count
        caretRequest = caret
        followTop = true
    }

    // MARK: - Pages

    func push(_ page: Page) {
        frames.append(Frame(page: page))
        chosen = nil
        followTop = true
    }

    func back() {
        guard !frames.isEmpty else { return }
        frames.removeLast()
        chosen = nil
        followTop = true
        caretRequest = query.utf16.count
    }

    // MARK: - Selection

    var sections: [QuickSection] {
        guard let tracker else { return [] }
        return QuickEntryRows(quick: self, tracker: tracker, now: Date()).sections
    }

    var rows: [QuickRow] { sections.flatMap(\.rows) }

    var selected: QuickRow? {
        let rows = rows
        if !followTop, let chosen, let row = rows.first(where: { $0.id == chosen }) { return row }
        return rows.first
    }

    /// The part being changed on the split page: the selected row's, without working out the rows, which need it.
    var splitIndex: Int {
        followTop ? 0 : SplitDraft.index(of: chosen) ?? 0
    }

    func select(_ id: String) {
        chosen = id
        followTop = false
    }

    func move(_ by: Int) {
        // A share typed on the split page stays with the part it was typed for.
        if case .split(let draft) = page, !query.isEmpty {
            draft.parts = draft.shown(typed: query, at: splitIndex)
            frames[frames.count - 1].query = ""
        }
        let rows = rows
        guard !rows.isEmpty else { return }
        let index = selected.flatMap { row in rows.firstIndex { $0.id == row.id } } ?? 0
        select(rows[(index + by + rows.count) % rows.count].id)
    }

    // MARK: - Running actions

    func run(_ row: QuickRow, _ action: QuickAction) {
        // Only the first page teaches the panel what is used; the pages inside are steps of one thing.
        if frames.isEmpty, let workspace = model.workspace {
            usage.record(row.id)
            usage.save(workspaceID: workspace.id)
        }
        let pages = frames.count
        Task {
            do {
                try await action.run()
                if !action.keepOpen && frames.count <= pages { close() }
            } catch {
                self.error = error.localizedDescription.isEmpty ? "That didn’t work." : error.localizedDescription
            }
        }
    }

    func submit() {
        guard let row = selected else { return }
        run(row, row.primary)
    }

    func submitAlternate() {
        guard let row = selected, let secondary = row.secondary else { return }
        run(row, secondary)
    }

    /// Tab: completes the mention being typed, or lists everything that can be done with the selected row.
    func tab() {
        guard let row = selected else { return }
        if mention != nil, row.id.hasPrefix("mention:") {
            run(row, row.primary)
        } else if row.secondary != nil || !row.more.isEmpty {
            push(.actions(row))
        }
    }

    /// Esc steps back a page before it closes the panel.
    func cancel() {
        if frames.isEmpty { close() } else { back() }
    }

    /// Backspace with the caret at the start: back a page when its query is empty, or a chip back into text.
    func deleteAtStart() -> Bool {
        if !frames.isEmpty {
            guard query.isEmpty else { return false }
            back()
            return true
        }
        guard !chips.isEmpty else { return false }
        revertLastChip()
        return true
    }

    // MARK: - Undo

    /// Remembers what was just done, says so in a notification with Undo, and offers it in the panel for a while.
    func did(_ summary: String, undo: @escaping () async throws -> Void) {
        done = QuickDone(summary: summary, undo: undo)
        desktop.notifyQuickEntry(summary)
    }

    var undoable: QuickDone? {
        done.flatMap { Date().timeIntervalSince($0.at) < Self.undoFor ? $0 : nil }
    }

    func undo() {
        guard let done = undoable else { return }
        self.done = nil
        Task {
            do {
                try await done.undo()
                model.notices.show("Undone: \(done.summary)")
            } catch {
                self.error = error.localizedDescription
                model.notices.show(error, fallback: "Couldn't undo.")
            }
        }
    }

    // MARK: - Saving, with a way back

    /// Starts a timer; the one running before stops where it starts, and Undo puts it back as it was.
    func start(_ details: EntryDetails, at start: Date = Date(), summary: String) async throws {
        guard let tracker else { return }
        let previous = tracker.running
        let id = try await tracker.start(details, at: start)
        did(summary) {
            try await tracker.delete(id: id)
            if let previous { try await self.resume(previous) }
        }
    }

    /// Adds a finished entry; Undo deletes it.
    func add(_ request: EntryRequest, summary: String) async throws {
        guard let tracker else { return }
        let id = try await tracker.add(request)
        did(summary) { try await tracker.delete(id: id) }
    }

    /// Changes an entry; Undo saves it as it was.
    func change(_ entry: TimeEntry, to request: EntryRequest, summary: String) async throws {
        guard let tracker else { return }
        let before = EntryRequest(entry)
        try await tracker.update(entry, to: request)
        did(summary) { try await tracker.update(entry, to: before) }
    }

    /// Stops the running timer; Undo runs it again from where it started.
    func stop(summary: String) async throws {
        guard let tracker, let running = tracker.running else { return }
        switch await tracker.stopRunning() {
        case .stopped:
            did(summary) { try await self.resume(running) }
        case .needsDetails(let entry):
            // The details the workspace requires are asked for in the window.
            desktop.showMainWindow()
            model.editing = EntryEditing(entry: entry, stopping: true)
        case .failed(let error):
            throw error
        }
    }

    /// Puts an entry that was running back to running. A stopped entry can't be restarted, so it is replaced by a
    /// running one with the same details and start.
    private func resume(_ entry: TimeEntry) async throws {
        guard let tracker else { return }
        var details = entry.details
        details.isBillable = entry.isBillable
        try await tracker.delete(id: entry.id)
        try await tracker.start(details, at: entry.start)
    }

    /// Takes the split agent activity suggests: shares of the entry, or entries one after the other when the work
    /// moved from one project to the next. Undo restores the entry and removes the ones added.
    func apply(_ offer: SplitOffer) async throws {
        guard let tracker else { return }
        let entry = tracker.running?.id == offer.entry.id ? tracker.running! : tracker.entries.first { $0.id == offer.entry.id } ?? offer.entry
        let summary = SplitApplier.summary(offer.suggestion, entry: entry, catalog: tracker.catalog, time: tracker.time)
        desktop.splits.withdrawOffer(entry.id)
        switch try await SplitApplier.apply(offer.suggestion, to: entry, tracker: tracker) {
        case .saved(let change):
            did("Split: \(summary)") { try await change.undo(tracker: tracker) }
        case .needsDetails:
            review(offer)
        }
    }

    /// The split needs what only the editor asks for, e.g. a task: it opens there, with the split taken.
    private func review(_ offer: SplitOffer) {
        desktop.review(offer)
    }

    // MARK: - The split page

    /// What is typed on the split page is the selected part's share; `+` adds a project instead of being typed.
    private func splitTyped(_ value: String) {
        guard case .split(let draft) = page, value.contains("+") else { return }
        frames[frames.count - 1].query = value.replacing("+", with: "")
        draft.addPart(self)
    }
}

/// A split being made on the split page. A class, so it is the same split when the page comes back from picking a
/// project.
@Observable
final class SplitDraft {
    let entry: TimeEntry
    var parts: [SplitPart]

    /// The panel splits into at most this many parts; the window's editor takes more.
    static let maxParts = 3
    static let step = 5

    init(entry: TimeEntry, parts: [SplitPart]) {
        self.entry = entry
        self.parts = parts
    }

    /// The part a row stands for.
    static func index(of rowID: String?) -> Int? {
        rowID.flatMap { $0.hasPrefix("part:") ? Int($0.dropFirst(5)) : nil }
    }

    /// The parts with what is typed given to part `index`.
    func shown(typed: String, at index: Int) -> [SplitPart] {
        guard let share = Int(typed.trimmingCharacters(in: .whitespaces)), (0...99).contains(share), parts.count > 1 else { return parts }
        return Self.with(parts, SplitMath.setting(parts.map(\.percent), at: index, to: share))
    }

    static func with(_ parts: [SplitPart], _ percents: [Int]) -> [SplitPart] {
        zip(parts, percents).map { part, percent in
            var part = part
            part.percent = percent
            return part
        }
    }

    /// Makes part `index` bigger (or smaller, when `by` is negative): it grows into the next part, or the last one
    /// into the part before it.
    func grow(_ index: Int, by: Int, typed: String) {
        let base = shown(typed: typed, at: index)
        guard base.count > 1 else { return }
        let percents = base.map(\.percent)
        let before = percents.prefix(index).reduce(0, +)
        let next = index < base.count - 1
            ? SplitMath.moveBoundary(percents, after: index, to: Double(before + percents[index] + by))
            : SplitMath.moveBoundary(percents, after: index - 1, to: Double(before - by))
        parts = Self.with(base, next)
    }

    func addPart(_ quick: QuickEntry) {
        guard parts.count < Self.maxParts else { return }
        parts = shown(typed: quick.query, at: quick.splitIndex)
        quick.query = ""
        quick.push(.project(title: "Split with") { [weak self, weak quick] projectId, taskId in
            guard let self, let quick else { return }
            let percents = SplitMath.adding(to: self.parts.map(\.percent))
            let billable = quick.tracker?.catalog.project(projectId)?.isBillable ?? false
            self.parts = Self.with(self.parts + [SplitPart(projectId: projectId, taskId: taskId, isBillable: billable, percent: 0)], percents)
            quick.back()
            quick.select("part:\(self.parts.count - 1)")
        })
    }

    func remove(_ index: Int) {
        guard parts.count > 2 else { return }
        let percents = SplitMath.removing(parts.map(\.percent), at: index)
        parts = Self.with(parts.enumerated().filter { $0.offset != index }.map(\.element), percents)
    }
}
