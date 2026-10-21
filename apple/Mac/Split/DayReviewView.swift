import SwiftUI
import SpindleKit

/// The day's review: every suggestion the app has for a day (or a week) in its place on the day, to take, change or
/// put off one by one with the keyboard, or all at once. Each row says what the time is now and what it becomes, and
/// what was done keeps its place, with Undo, until the review closes. Nothing is saved without a click.
struct DayReviewView: View {
    @Environment(AppModel.self) private var model
    @Environment(Desktop.self) private var desktop
    let tracker: Tracker
    let scope: ReviewScope

    @State private var selected: String?
    /// Where the selection was, to keep the place when the item there is taken.
    @State private var selectedIndex = 0
    @State private var busy: Set<String> = []
    @State private var takingAll = false
    @State private var loaded = false
    @FocusState private var focused: Bool

    private var time: UserTime { tracker.time }
    private var catalog: Catalog { tracker.catalog }

    var body: some View {
        let items = desktop.review(on: scope.days, tracker: tracker)
        // What was answered stays in its place, done; only the rest waits.
        let waiting = items.filter { desktop.reviewed[$0.id] == nil }
        let handled = desktop.reviewed.values.filter { scope.days.contains(time.day(of: $0.item.range.start)) }
        VStack(spacing: 0) {
            header(waiting, handled: handled)
                .padding(.horizontal, 20)
                .padding(.vertical, 16)
            Divider()
            content(waiting, handled: handled)
            Divider()
            footer(waiting)
                .padding(.horizontal, 20)
                .padding(.vertical, 12)
        }
        .frame(width: 800, height: 640)
        .task(id: scope) {
            loaded = false
            await desktop.loadReview(scope.days, tracker: tracker)
            loaded = true
        }
        .onChange(of: waiting.map(\.id)) { _, ids in follow(ids) }
        .onAppear {
            focused = true
            if selected == nil { selected = waiting.first?.id }
        }
    }

    // MARK: - Header and footer

    private func header(_ waiting: [ReviewItem], handled: [ReviewOutcome]) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .center, spacing: 12) {
                Image(systemName: ActivityStyle.symbol)
                    .font(.title2)
                    .foregroundStyle(Color.brandText)
                    .frame(width: 36, height: 36)
                    .background(Color.brand.opacity(0.18), in: RoundedRectangle(cornerRadius: 9))
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(.title2.weight(.semibold))
                    if !waiting.isEmpty {
                        Text(ReviewItem.breakdown(waiting))
                            .foregroundStyle(.secondary)
                    } else if !handled.isEmpty {
                        Text("All done")
                            .foregroundStyle(.secondary)
                    }
                }
                Spacer()
                HStack(spacing: 2) {
                    Button { move(by: -1) } label: { Image(systemName: "chevron.left") }
                        .help(scope.isWeek ? "Previous week" : "Previous day")
                        .keyboardShortcut(.leftArrow, modifiers: [.command])
                    Button { move(by: 1) } label: { Image(systemName: "chevron.right") }
                        .help(scope.isWeek ? "Next week" : "Next day")
                        .keyboardShortcut(.rightArrow, modifiers: [.command])
                        .disabled(scope.days.contains(time.day(of: Date())))
                }
                .buttonStyle(.bordered)
                Picker("View", selection: Binding(get: { scope.isWeek }, set: { week in switchTo(week: week) })) {
                    Text("Day").tag(false)
                    Text("Week").tag(true)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                let takeable = waiting.filter { !busy.contains($0.id) && desktop.splits.blocker($0, tracker: tracker) == nil }
                Button(takingAll ? "Saving…" : "Take All (\(takeable.count))") { takeAll(waiting) }
                    .buttonStyle(.brand)
                    .disabled(takeable.isEmpty || takingAll)
                    .help(takeable.count < waiting.count
                          ? "Save every suggestion here as offered; those that need a project or details stay, with one Undo for all"
                          : "Save every suggestion here as offered, with one Undo for all")
            }
            let total = waiting.count + handled.count
            if total > 0 {
                HStack(spacing: 10) {
                    ProgressView(value: Double(handled.count), total: Double(total))
                        .progressViewStyle(.linear)
                        .tint(Color.brand)
                        .controlSize(.small)
                    Text("\(handled.count) of \(total) done")
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                        .fixedSize()
                }
                .accessibilityElement(children: .ignore)
                .accessibilityLabel("Reviewed")
                .accessibilityValue("\(handled.count) of \(total) done")
                .animation(.snappy, value: handled.count)
            }
        }
    }

    private var title: String {
        guard scope.isWeek, let first = scope.days.first else {
            return scope.days.first.map { "\(time.dayHeading($0, now: Date())) · \(time.longDay($0))" } ?? ""
        }
        let today = time.day(of: Date())
        if scope.days.last == today, first == time.adding(-6, to: today) { return "The last 7 days" }
        return CalendarLayout.label(scope.days, in: time)
    }

    private func footer(_ waiting: [ReviewItem]) -> some View {
        HStack(spacing: 14) {
            if let notice = model.notices.current {
                NoticeView(notice: notice, perform: { model.notices.perform() }) { model.notices.dismiss() }
                    .controlSize(.small)
            } else if !waiting.isEmpty {
                KeyHint(keys: "↑↓", text: "Move")
                KeyHint(keys: "↩", text: "Take")
                KeyHint(keys: "E", text: "Change")
                KeyHint(keys: "⌫", text: "Not Now")
            }
            Spacer()
            Button("Close") { desktop.reviewing = nil }
                .keyboardShortcut(.cancelAction)
                .controlSize(.large)
        }
        .animation(.snappy, value: model.notices.current)
    }

    // MARK: - Content

    @ViewBuilder private func content(_ waiting: [ReviewItem], handled: [ReviewOutcome]) -> some View {
        if waiting.isEmpty && handled.isEmpty {
            ContentUnavailableView {
                Label(loaded ? "Nothing to review" : "Reading agent logs…", systemImage: loaded ? "checkmark.circle" : ActivityStyle.symbol)
            } description: {
                Text(loaded
                     ? (scope.isWeek ? "No splits or untracked agent time on these days." : "No splits or untracked agent time on this day.")
                     : "Reading agent logs for these days.")
            }
            .frame(maxHeight: .infinity)
        } else {
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 22) {
                        if waiting.isEmpty {
                            AllDone()
                        }
                        ForEach(days(waiting, handled: handled), id: \.self) { day in
                            daySection(day, waiting: waiting.filter { time.day(of: $0.range.start) == day },
                                       handled: handled.filter { time.day(of: $0.item.range.start) == day })
                        }
                    }
                    .padding(20)
                }
                .onChange(of: selected) { _, id in
                    guard let id else { return }
                    withAnimation(.snappy) { proxy.scrollTo(id, anchor: .center) }
                }
            }
            .focusable()
            .focused($focused)
            .focusEffectDisabled()
            .onKeyPress(keys: [.upArrow, .downArrow, .return, .delete, "e"]) { press in
                key(press.key, items: waiting) ? .handled : .ignored
            }
        }
    }

    private func days(_ waiting: [ReviewItem], handled: [ReviewOutcome]) -> [DayKey] {
        Array(Set((waiting.map(\.range) + handled.map(\.item.range)).map { time.day(of: $0.start) })).sorted()
    }

    private func daySection(_ day: DayKey, waiting: [ReviewItem], handled: [ReviewOutcome]) -> some View {
        // A single day shows its other entries too, so each suggestion sits in its place on the day. The entries an
        // item is about, or that a take made, belong to its row.
        let own = Set(waiting.compactMap(\.entry?.id) + handled.flatMap { outcome -> [String] in
            var ids = outcome.item.entry.map { [$0.id] } ?? []
            if case .taken(_, let change) = outcome.kind { ids += change.added }
            return ids
        })
        let others = scope.isWeek ? [] : tracker.entries(overlapping: [day]).filter { !own.contains($0.id) }
        let rows = (waiting.map { Row.item($0) } + handled.map { Row.done($0) } + others.map { Row.entry($0) })
            .sorted { $0.start < $1.start }
        return VStack(alignment: .leading, spacing: 8) {
            if scope.isWeek {
                HStack(alignment: .firstTextBaseline) {
                    Text(time.dayHeading(day, now: Date()))
                        .font(.headline)
                    if !waiting.isEmpty {
                        Text("\(waiting.count) to review")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                }
            }
            ReviewStrip(tracker: tracker, day: day, items: waiting, selected: selected) { id in
                selected = id
                focused = true
            }
            .padding(.bottom, 4)
            ForEach(rows) { row in
                switch row {
                case .item(let item):
                    ReviewRow(tracker: tracker, item: item, selected: selected == item.id, busy: busy.contains(item.id),
                              onTake: { take(item) }, onChange: { desktop.change(item) }, onNotNow: { desktop.notNow(item) })
                        .id(item.id)
                        .onTapGesture { selected = item.id; focused = true }
                case .done(let outcome):
                    DoneRow(tracker: tracker, outcome: outcome) { desktop.undo(reviewed: outcome.item.id) }
                        .id("done|\(outcome.item.id)")
                case .entry(let entry):
                    ContextRow(tracker: tracker, entry: entry)
                }
            }
        }
    }

    private enum Row: Identifiable {
        case item(ReviewItem)
        case done(ReviewOutcome)
        case entry(TimeEntry)
        var id: String {
            switch self {
            case .item(let item): item.id
            case .done(let outcome): "done|\(outcome.item.id)"
            case .entry(let entry): "entry|\(entry.id)"
            }
        }
        var start: Date {
            switch self {
            case .item(let item): item.range.start
            case .done(let outcome): outcome.item.range.start
            case .entry(let entry): entry.start
            }
        }
    }

    // MARK: - Keyboard

    /// ↑↓ move, Return takes (or opens the editor when one click can't), E opens the editor, ⌫ puts off.
    private func key(_ key: KeyEquivalent, items: [ReviewItem]) -> Bool {
        let index = items.firstIndex { $0.id == selected }
        switch key {
        case .upArrow, .downArrow:
            let next = key == .upArrow ? (index ?? items.count) - 1 : (index ?? -1) + 1
            guard items.indices.contains(next) else { return true }
            selected = items[next].id
            selectedIndex = next
        case .return:
            guard let index else { return false }
            take(items[index])
        case "e":
            guard let index else { return false }
            desktop.change(items[index])
        case .delete:
            guard let index else { return false }
            desktop.notNow(items[index])
        default:
            return false
        }
        return true
    }

    /// When the selected item is answered, the one now in its place among those waiting is selected.
    private func follow(_ ids: [String]) {
        if let selected, let index = ids.firstIndex(of: selected) {
            selectedIndex = index
            return
        }
        selected = ids.isEmpty ? nil : ids[min(selectedIndex, ids.count - 1)]
    }

    // MARK: - Actions

    private func take(_ item: ReviewItem) {
        guard !busy.contains(item.id) else { return }
        busy.insert(item.id)
        Task {
            await desktop.take(item)
            busy.remove(item.id)
        }
    }

    private func takeAll(_ items: [ReviewItem]) {
        takingAll = true
        busy.formUnion(items.map(\.id))
        Task {
            await desktop.takeAll(items)
            busy.subtract(items.map(\.id))
            takingAll = false
        }
    }

    private func move(by steps: Int) {
        guard let first = scope.days.first else { return }
        let day = time.adding(steps * (scope.isWeek ? 7 : 1), to: first)
        desktop.reviewing = scope.isWeek ? .week(of: day, time: time) : .day(day)
        selected = nil
    }

    private func switchTo(week: Bool) {
        guard week != scope.isWeek else { return }
        let today = time.day(of: Date())
        // From a week to the day the selection is on, or the last day shown up to today.
        let day = selectedDay ?? scope.days.filter { $0 <= today }.last ?? scope.days[0]
        desktop.reviewing = week ? .week(of: day, time: time) : .day(day)
    }

    private var selectedDay: DayKey? {
        desktop.review(on: scope.days, tracker: tracker).first { $0.id == selected }.map { time.day(of: $0.range.start) }
    }
}

/// "All done", over the answered items once nothing waits.
private struct AllDone: View {
    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "checkmark.circle.fill")
                .font(.title3)
                .foregroundStyle(Color.brandText)
            Text("All done.").fontWeight(.semibold)
                + Text(" Everything here is answered. Undo works until you close the review.").foregroundColor(.secondary)
        }
        .font(.callout)
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.brand.opacity(0.1), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Color.brand.opacity(0.35)))
        .accessibilityElement(children: .combine)
    }
}

/// "↩ Take": a key and what it does, in the review's footer.
private struct KeyHint: View {
    let keys: String
    let text: String

    var body: some View {
        HStack(spacing: 5) {
            Text(keys)
                .font(.caption.weight(.semibold).monospaced())
                .padding(.horizontal, 5)
                .padding(.vertical, 1)
                .background(Color.primary.opacity(0.07), in: RoundedRectangle(cornerRadius: 4))
                .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(Color.primary.opacity(0.12)))
            Text(text)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .combine)
    }
}

/// One suggestion: what the time is now and what it becomes, with its evidence, and its answers. The main button is
/// what one click does: take it, or, when one click can't, "Change…", with the reason in the row.
private struct ReviewRow: View {
    @Environment(Desktop.self) private var desktop
    let tracker: Tracker
    let item: ReviewItem
    let selected: Bool
    let busy: Bool
    let onTake: () -> Void
    let onChange: () -> Void
    let onNotNow: () -> Void

    @State private var linking: ActivityRepository?

    private var time: UserTime { tracker.time }
    private var catalog: Catalog { tracker.catalog }

    var body: some View {
        let blocker = desktop.splits.blocker(item, tracker: tracker)
        HStack(alignment: .top, spacing: 12) {
            rail
                .frame(width: 4)
                .frame(maxHeight: .infinity)
            VStack(alignment: .leading, spacing: 6) {
                heading
                ChangeLine(item: item, results: desktop.splits.results(item, tracker: tracker), catalog: catalog, time: time)
                detail(blocker)
            }
            Spacer(minLength: 8)
            VStack(alignment: .trailing, spacing: 8) {
                HStack(spacing: 10) {
                    Text("\(time.clock(item.range.start)) – \(time.clock(item.range.end))")
                        .foregroundStyle(.secondary)
                    Text(Durations.format(item.range.duration, as: .hm))
                        .fontWeight(.semibold)
                }
                .monospacedDigit()
                HStack(spacing: 6) {
                    Button("Not Now", action: onNotNow)
                        .buttonStyle(.borderless)
                        .foregroundStyle(.secondary)
                        .help("Leave it out of the review; nothing changes (⌫)")
                    if blocker == nil {
                        Button("Change…", action: onChange)
                            .help("Open it in the editor with this filled in, to change it before saving (E)")
                        Button(item.action(catalog: catalog, time: time), action: onTake)
                            .buttonStyle(.brand)
                            .disabled(busy)
                            .help("Save it as offered, with Undo (↩)")
                    } else {
                        Button("Change…", action: onChange)
                            .buttonStyle(.brand)
                            .disabled(busy)
                            .help("One click can't save this; open it in the editor (↩ or E)")
                    }
                }
                .controlSize(.small)
            }
        }
        .padding(12)
        .background(selected ? Color.brand.opacity(0.1) : Color.primary.opacity(0.03), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(selected ? Color.brand.opacity(0.85) : Color.primary.opacity(0.12),
                                                                 lineWidth: selected ? 1.5 : 1))
        .contentShape(Rectangle())
        .opacity(busy ? 0.5 : 1)
        .accessibilityElement(children: .contain)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    /// The colour of the time as it is now: the entry's project, or hatched for untracked time.
    @ViewBuilder private var rail: some View {
        if let entry = item.entry {
            RoundedRectangle(cornerRadius: 2)
                .fill(catalog.project(entry.projectId).map { Color(hex: $0.color) } ?? .secondary)
        } else {
            Hatching(color: Color.secondary.opacity(0.7), spacing: 4)
                .clipShape(RoundedRectangle(cornerRadius: 2))
        }
    }

    @ViewBuilder private var heading: some View {
        if let entry = item.entry {
            HStack(spacing: 8) {
                Text(entry.description.isEmpty ? "No description" : entry.description)
                    .fontWeight(.medium)
                    .foregroundStyle(entry.description.isEmpty ? .secondary : .primary)
                    .lineLimit(1)
                Group {
                    if entry.isSplit {
                        SplitLabel(catalog: catalog, parts: entry.parts)
                    } else if let project = catalog.project(entry.projectId) {
                        ProjectLabel(project: project, task: catalog.task(entry.taskId))
                    }
                }
                .font(.callout)
                .lineLimit(1)
            }
        } else {
            HStack(spacing: 6) {
                Text("Not tracked")
                    .fontWeight(.medium)
                Text("· agents were at work")
                    .foregroundStyle(.secondary)
            }
        }
    }

    /// Why one click can't take it, and what Change… is for.
    private func reason(_ blocker: SplitApplier.Blocker?) -> String? {
        switch blocker {
        case .details(let need) where need == SplitApplier.timesChanged: "\(need). Change… to look at it again."
        case .details(let need): "\(need). Change… to add it."
        case .unlinked where item.entry == nil: "In repositories without a project. Change… to choose one."
        // A split names its repositories, to link right here.
        case .unlinked, nil: nil
        }
    }

    @ViewBuilder private func detail(_ blocker: SplitApplier.Blocker?) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            switch item {
            case .split(let offer):
                EvidenceStrip(suggestion: offer.suggestion, catalog: catalog, time: time, showsTimes: false)
                    .frame(maxWidth: 360)
                if offer.suggestion.strength == .weak {
                    Text("The logs back this only lightly: \(Int((offer.suggestion.coverage * 100).rounded()))% of the time, few prompts.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                ForEach(offer.suggestion.unlinked, id: \.self) { repository in
                    link(repository)
                }
            case .drift(let offer):
                Text(offer.headline(catalog: catalog, time: time) + ".")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            case .untracked(let block):
                if block.stretch.unlinked > 0 && !block.stretch.projects.isEmpty {
                    Text("Agents also worked \(Durations.hoursMinutes(block.stretch.unlinked)) in repositories without a project.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            if let reason = reason(blocker) {
                Label(reason, systemImage: "pencil")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    /// "Which project is copper for?", right in the row, as the editor's banner asks.
    private func link(_ repository: ActivityRepository) -> some View {
        Button {
            linking = repository
        } label: {
            Label("Choose a project for \(repository.name)", systemImage: "link")
                .font(.caption)
        }
        .buttonStyle(.brandLink)
        .help("It is remembered for your account, on the web too.")
        .popover(isPresented: Binding(get: { linking == repository }, set: { if !$0 { linking = nil } }), arrowEdge: .bottom) {
            ProjectPicker(
                tracker: tracker, projectId: nil, taskId: nil,
                onChange: { projectId, _ in
                    if let projectId { desktop.splits.link(repository, to: projectId) } else { desktop.unlink(repository) }
                },
                choosingAnew: true,
                placeholder: "Which project is \(repository.name) for?",
                onIgnore: { desktop.ignore(repository) })
        }
    }
}

/// "● Atlas 1:30 → 2 entries: ● Atlas 09:00–09:40 · ● Beacon 09:40–10:30": the time as it is now, struck through, and
/// what one click makes of it, so a take can be judged without working it out.
private struct ChangeLine: View {
    let item: ReviewItem
    let results: [ReviewResult]
    let catalog: Catalog
    let time: UserTime

    private var weak: Bool {
        if case .split(let offer) = item { return offer.suggestion.strength == .weak }
        return false
    }

    var body: some View {
        FlowLayout(spacing: 6, lineSpacing: 4) {
            Image(systemName: ActivityStyle.symbol)
                .foregroundStyle(weak ? Color.secondary : Color.brandText)
                .accessibilityHidden(true)
            now
            Image(systemName: "arrow.right")
                .foregroundStyle(.secondary)
                .accessibilityLabel("becomes")
            if results.count > 1 {
                Text("\(results.count) entries:").foregroundStyle(.secondary)
            } else if item.entry == nil {
                Text("New Entry").foregroundStyle(.secondary)
            }
            // The separator ends the piece before it, so a wrapped line starts with an entry.
            ForEach(results.indices, id: \.self) { index in
                HStack(spacing: 6) {
                    becomes(results[index])
                    if index < results.count - 1 { Text("·").foregroundStyle(.secondary) }
                }
            }
        }
        .font(.callout)
        .monospacedDigit()
        .padding(.horizontal, 8)
        .padding(.vertical, 5)
        .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 6))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(spoken)
    }

    /// The time as it is: the entry's project or split and its time, or nothing tracked.
    @ViewBuilder private var now: some View {
        if let entry = item.entry {
            HStack(spacing: 4) {
                if entry.isSplit {
                    SplitDots(catalog: catalog, parts: entry.parts, size: 7)
                } else {
                    dot(catalog.project(entry.projectId).map { Color(hex: $0.color) } ?? .secondary)
                }
                Text(nowName(entry)).strikethrough()
                Text(Durations.format(item.range.duration, as: .hm)).strikethrough()
            }
            .foregroundStyle(.secondary)
        } else {
            Text("Nothing tracked").foregroundStyle(.secondary)
        }
    }

    private func nowName(_ entry: TimeEntry) -> String {
        entry.isSplit ? entry.parts.map { catalog.project($0.projectId)?.name ?? "No project" }.joined(separator: " · ")
            : catalog.project(entry.projectId)?.name ?? "No project"
    }

    /// One entry it leaves: its times when there are several, each project's time when it is one.
    @ViewBuilder private func becomes(_ result: ReviewResult) -> some View {
        if results.count > 1 {
            HStack(spacing: 4) {
                ForEach(result.pieces.indices, id: \.self) { index in
                    dot(color(result.pieces[index].target))
                }
                Text(result.pieces.map { name($0.target) }.joined(separator: " · ")).fontWeight(.medium)
                Text("\(time.clock(result.range.start))–\(time.clock(result.range.end))").foregroundStyle(.secondary)
            }
        } else {
            HStack(spacing: 6) {
                ForEach(result.pieces.indices, id: \.self) { index in
                    let piece = result.pieces[index]
                    if index > 0 { Text("·").foregroundStyle(.secondary) }
                    HStack(spacing: 4) {
                        dot(color(piece.target))
                        Text(name(piece.target)).fontWeight(.medium)
                        Text(Durations.format(piece.seconds, as: .hm)).foregroundStyle(.secondary)
                    }
                }
            }
        }
    }

    private func dot(_ color: Color) -> some View {
        Circle().fill(color).frame(width: 7, height: 7)
    }

    private func name(_ target: SplitSuggestion.Target?) -> String {
        target.map { EvidenceStrip.name($0, catalog: catalog) } ?? "No project"
    }

    private func color(_ target: SplitSuggestion.Target?) -> Color {
        target.map { EvidenceStrip.color($0, catalog: catalog) } ?? .secondary
    }

    /// "Atlas 1:30 becomes 2 entries: Atlas 09:00 to 09:40, Beacon 09:40 to 10:30".
    private var spoken: String {
        let from = item.entry.map { "\(nowName($0)) \(Durations.hoursMinutes(item.range.duration))" } ?? "Nothing tracked"
        let to = results.count > 1
            ? "\(results.count) entries: " + results.map {
                "\($0.pieces.map { name($0.target) }.joined(separator: " and ")) \(time.clock($0.range.start)) to \(time.clock($0.range.end))"
            }.joined(separator: ", ")
            : (item.entry == nil ? "a new entry, " : "") + (results.first?.pieces ?? []).map { "\(name($0.target)) \(Durations.hoursMinutes($0.seconds))" }.joined(separator: ", ")
        return "\(from) becomes \(to)"
    }
}

/// An item answered while the review is open, in its place: what became of it, with Undo until the review closes.
private struct DoneRow: View {
    let tracker: Tracker
    let outcome: ReviewOutcome
    let onUndo: () -> Void

    var body: some View {
        let item = outcome.item
        let time = tracker.time
        let putOff: Bool = { if case .notNow = outcome.kind { return true } else { return false } }()
        // "Added Atlas 14:00–14:47" says its times already.
        let saysTimes: Bool = { if case .taken = outcome.kind, item.entry == nil { return true } else { return false } }()
        HStack(spacing: 8) {
            Image(systemName: putOff ? "moon.zzz" : "checkmark.circle.fill")
                .foregroundStyle(putOff ? Color.secondary : Color.brandText)
            Text(outcome.title)
                .fontWeight(.medium)
                .foregroundStyle(putOff ? .secondary : .primary)
                .monospacedDigit()
            if !saysTimes {
                Text("\(time.clock(item.range.start))–\(time.clock(item.range.end)) · \(item.entry.map { $0.description.isEmpty ? "No description" : $0.description } ?? "Not tracked")")
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            if outcome.canUndo {
                Button("Undo", action: onUndo)
                    .buttonStyle(.borderless)
                    .controlSize(.small)
                    .help(putOff ? "Bring it back into the review" : "Put the time back as it was")
                    .accessibilityLabel("Undo: \(outcome.title)")
            }
        }
        .font(.callout)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(putOff ? Color.clear : Color.primary.opacity(0.03), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10)
            .strokeBorder(Color.primary.opacity(0.12), style: StrokeStyle(lineWidth: 1, dash: putOff ? [4, 3] : [])))
        .accessibilityElement(children: .contain)
    }
}

/// Another entry of the day, between the suggestions, so they read in place.
private struct ContextRow: View {
    let tracker: Tracker
    let entry: TimeEntry

    var body: some View {
        let catalog = tracker.catalog
        HStack(spacing: 12) {
            RoundedRectangle(cornerRadius: 1.5)
                .fill(catalog.project(entry.projectId).map { Color(hex: $0.color).opacity(0.5) } ?? Color.secondary.opacity(0.4))
                .frame(width: 3, height: 16)
            Text(entry.description.isEmpty ? "No description" : entry.description)
                .lineLimit(1)
            Group {
                if entry.isSplit {
                    SplitLabel(catalog: catalog, parts: entry.parts)
                } else if let project = catalog.project(entry.projectId) {
                    ProjectLabel(project: project, task: catalog.task(entry.taskId))
                }
            }
            .lineLimit(1)
            Spacer(minLength: 8)
            Text("\(tracker.time.clock(entry.start)) – \(entry.stop.map(tracker.time.clock) ?? "now")")
                .monospacedDigit()
        }
        .font(.callout)
        .foregroundStyle(.secondary)
        .padding(.horizontal, 12)
        .padding(.vertical, 2)
        .accessibilityElement(children: .combine)
    }
}

/// The day at a glance: its entries as bars, the time each suggestion is about outlined in marigold over them, and
/// untracked time as a hatched outline, as in the calendar. A click on an outline selects its item.
private struct ReviewStrip: View {
    let tracker: Tracker
    let day: DayKey
    let items: [ReviewItem]
    let selected: String?
    let onSelect: (String) -> Void

    var body: some View {
        let time = tracker.time
        let dayStart = time.start(of: day)
        let entries = tracker.entries(overlapping: [day])
        let spans = entries.map { DateInterval(start: $0.start, end: max($0.start, $0.stop ?? Date())) } + items.map(\.range)
        let first = spans.map(\.start).min() ?? dayStart.addingTimeInterval(9 * 3600)
        let last = spans.map(\.end).max() ?? first.addingTimeInterval(8 * 3600)
        // Whole hours around what is there, at least four of them.
        let from = max(dayStart, Date(timeIntervalSinceReferenceDate: (first.timeIntervalSinceReferenceDate / 3600).rounded(.down) * 3600))
        let to = max(from.addingTimeInterval(4 * 3600),
                     Date(timeIntervalSinceReferenceDate: (last.timeIntervalSinceReferenceDate / 3600).rounded(.up) * 3600))
        let hours = Int(to.timeIntervalSince(from) / 3600)
        let step = hours > 12 ? 3 : hours > 6 ? 2 : 1
        GeometryReader { geometry in
            let width = geometry.size.width
            let x = { (date: Date) in CGFloat(date.timeIntervalSince(from) / to.timeIntervalSince(from)) * width }
            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 6)
                    .fill(Color.primary.opacity(0.04))
                    .frame(height: 30)
                ForEach(Array(stride(from: 0, through: hours, by: step)), id: \.self) { hour in
                    let at = from.addingTimeInterval(TimeInterval(hour) * 3600)
                    Rectangle()
                        .fill(Color.primary.opacity(0.08))
                        .frame(width: 1, height: 30)
                        .offset(x: x(at))
                    Text(time.clock(at))
                        .font(.system(size: 9).monospacedDigit())
                        .foregroundStyle(.secondary)
                        .fixedSize()
                        .offset(x: min(max(0, x(at) - 12), width - 26), y: 33)
                }
                ForEach(entries) { entry in
                    let color = tracker.catalog.project(entry.projectId).map { Color(hex: $0.color) } ?? .secondary
                    let span = clip(DateInterval(start: entry.start, end: max(entry.start, entry.stop ?? Date())), from, to)
                    RoundedRectangle(cornerRadius: 4)
                        .fill(color.opacity(0.35))
                        .frame(width: max(2, x(span.end) - x(span.start)), height: 18)
                        .offset(x: x(span.start), y: 6)
                }
                ForEach(items) { item in
                    let span = clip(item.range, from, to)
                    let emphasis = item.id == selected
                    RoundedRectangle(cornerRadius: 4)
                        .strokeBorder(Color.brand, style: StrokeStyle(lineWidth: emphasis ? 2 : 1.25, dash: item.entry == nil ? [3, 2] : []))
                        .background(Color.brand.opacity(emphasis ? 0.25 : 0.08), in: RoundedRectangle(cornerRadius: 4))
                        .frame(width: max(4, x(span.end) - x(span.start)), height: 26)
                        .contentShape(Rectangle())
                        .onTapGesture { onSelect(item.id) }
                        .offset(x: x(span.start), y: 2)
                }
            }
        }
        .frame(height: 46)
        .accessibilityHidden(true)
    }

    private func clip(_ span: DateInterval, _ from: Date, _ to: Date) -> DateInterval {
        let start = min(max(span.start, from), to)
        return DateInterval(start: start, end: max(start, min(span.end, to)))
    }
}

/// "✦ 3 to review": opens a day's review, beside the day in the entry list and the calendar.
struct ReviewDayButton: View {
    @Environment(Desktop.self) private var desktop
    let count: Int
    let scope: ReviewScope

    var body: some View {
        Button {
            desktop.openReview(scope)
        } label: {
            HStack(spacing: 4) {
                Image(systemName: ActivityStyle.symbol)
                    .foregroundStyle(Color.brandText)
                Text("\(count) to review")
                    .monospacedDigit()
            }
            .font(.caption.weight(.medium))
            .padding(.horizontal, 8)
            .padding(.vertical, 2)
            .background(Color.brand.opacity(0.16), in: Capsule())
            .foregroundStyle(.primary)
        }
        .buttonStyle(.plain)
        .fixedSize()
        .help("Review this day's suggestions in one place")
    }
}

/// "Yesterday: 3 to review", above the timer the next morning: the last working day's suggestions, waiting quietly
/// rather than in a dialog. Gone once dealt with, or put off for that day.
struct ReviewCard: View {
    @Environment(Desktop.self) private var desktop
    let tracker: Tracker

    @AppStorage("reviewCardDismissed") private var dismissed = ""

    /// How far back the card looks for the last day with something to review, e.g. Friday on a Monday.
    static let lookBack = 3

    private var time: UserTime { tracker.time }

    var body: some View {
        let today = time.day(of: Date())
        let earlier = (1...Self.lookBack).map { time.adding(-$0, to: today) }
        Group {
            if desktop.splits.isEnabled, let (day, count) = waiting(in: earlier), day.description != dismissed {
                HStack(spacing: 12) {
                    Image(systemName: ActivityStyle.symbol)
                        .foregroundStyle(Color.brandText)
                        .font(.title3)
                    VStack(alignment: .leading, spacing: 1) {
                        Text("\(time.dayHeading(day, now: Date())): \(count) to review")
                            .font(.callout.weight(.semibold))
                        Text(ReviewItem.breakdown(desktop.review(on: [day], tracker: tracker)))
                            .font(.callout)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                    Spacer(minLength: 12)
                    Button("Not Now") { dismissed = day.description }
                        .buttonStyle(.borderless)
                        .foregroundStyle(.secondary)
                        .help("Hide this until the next day; the review stays in the day's header")
                    Button("Review…") { desktop.openReview(.day(day)) }
                        .buttonStyle(.brand)
                }
                .controlSize(.small)
                .padding(.horizontal, 14)
                .padding(.vertical, 10)
                .cardSurface()
                .padding(.horizontal, 20)
                .padding(.top, 16)
                .transition(.opacity)
            }
        }
        .task(id: today) {
            await desktop.loadReview(earlier, tracker: tracker)
        }
    }

    /// The most recent of these days with something to review.
    private func waiting(in days: [DayKey]) -> (DayKey, Int)? {
        for day in days {
            let count = desktop.review(on: [day], tracker: tracker).count
            if count > 0 { return (day, count) }
        }
        return nil
    }
}
