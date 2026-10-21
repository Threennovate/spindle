import SwiftUI
import SpindleKit

enum CalendarMode: String {
    case day, week
}

/// The person's time as blocks on a day or week grid. Drag on empty space to add an entry, drag a block to move it
/// (to another day too), or drag its top or bottom edge to change when it starts or ends. Clicking opens an entry.
struct CalendarView: View {
    @Environment(AppModel.self) private var model
    @Environment(Desktop.self) private var desktop
    let tracker: Tracker
    /// The days shown and their agent activity, kept by the model so going back to the calendar shows them at once.
    let state: CalendarState

    @AppStorage("calendarMode") private var savedMode = CalendarMode.week
    @State private var snapshotMode = ActivitySnapshot.mode
    private var mode: CalendarMode { snapshotMode ?? savedMode }
    private var modeBinding: Binding<CalendarMode> {
        Binding(get: { mode }, set: { value in
            if ActivitySnapshot.mode != nil { snapshotMode = value } else { savedMode = value }
        })
    }
    private var anchor: DayKey? {
        get { state.anchor }
        nonmutating set { state.anchor = newValue }
    }
    @State private var loadError: String?
    private var activity: [ActivityDay] {
        get { state.activity }
        nonmutating set { state.activity = newValue }
    }
    @State private var activityError: String?
    @State private var loadingActivity = false

    @State private var snapshotActivity = true
    /// Agent activity in the calendar is a beta feature: off, and its toggle hidden, unless beta features are on.
    private var activityAvailable: Bool { desktop.canShowActivity }
    /// Drawn only while agent logs are read; hiding it doesn't stop them being read for suggestions.
    private var activityEnabled: Bool {
        ActivitySnapshot.root != nil ? snapshotActivity : desktop.showsActivity
    }
    private var activityBinding: Binding<Bool> {
        Binding(get: { activityEnabled }, set: { value in
            if ActivitySnapshot.root != nil { snapshotActivity = value; return }
            desktop.setShowsActivity(value)
        })
    }
    private var activitySources: Set<AgentSource> {
        ActivitySnapshot.root != nil ? Set(AgentSource.allCases) : desktop.settings.activitySources
    }
    private var activityRequest: ActivityRequest {
        ActivityRequest(days: days, timeZone: time.timeZone.identifier, enabled: activityEnabled, sources: activitySources,
                        context: desktop.splits.context(time: time), revision: days.contains(today) ? desktop.splits.revision : 0)
    }
    private struct ActivityRequest: Equatable {
        let days: [DayKey]
        let timeZone: String
        let enabled: Bool
        let sources: Set<AgentSource>
        let context: SplitAdvisor.Context
        /// Moves when agents write their logs, so a calendar showing today follows along.
        let revision: Int
    }

    private var time: UserTime { tracker.time }
    private var today: DayKey { time.day(of: Date()) }

    private var days: [DayKey] {
        let day = anchor ?? ActivitySnapshot.day ?? today
        return mode == .day ? [day] : CalendarLayout.week(containing: day, in: time)
    }

    var body: some View {
        let days = days
        VStack(spacing: 0) {
            header(days)
                .padding(.horizontal, 20)
                .padding(.vertical, 12)
            CalendarGrid(tracker: tracker, days: days, activity: activityEnabled ? activity : [],
                         activitySources: activityEnabled ? AgentSource.allCases.filter(activitySources.contains) : nil)
                .overlay {
                    if let loadError {
                        ContentUnavailableView {
                            Label("Couldn't load these days", systemImage: "exclamationmark.triangle")
                        } description: {
                            Text(loadError)
                        } actions: {
                            Button("Try Again") { Task { await load(days) } }
                        }
                        .background(.background)
                    }
                }
        }
        .task(id: activityRequest) {
            let request = activityRequest, context = desktop.splits.context(time: time)
            let time = time
            // The previous days stay until the new ones arrive; the grid only shows activity for the days on screen.
            activityError = nil
            // Reading today again as agents work happens quietly; only new days show progress.
            loadingActivity = activityEnabled && activity.map(\.day) != days
            guard activityEnabled else {
                activity = []
                return
            }
            defer { if !Task.isCancelled { loadingActivity = false } }
            do {
                let loaded = try await Performance.measure("Calendar activity") {
                    try await AgentActivityLoader.shared.load(days: request.days, time: time, sources: request.sources, ignoring: context.ignoring,
                                                              fixtureRoot: ActivitySnapshot.root)
                }
                try Task.checkCancellation()
                guard request == activityRequest, context == desktop.splits.context(time: tracker.time) else { return }
                activity = loaded
                desktop.splits.absorb(loaded, entries: tracker.entries, time: time, revision: request.revision)
            } catch is CancellationError { }
            catch { activityError = "Couldn't read agent activity." }
        }
        .task(id: days) {
            await load(days)
        }
        .task(id: desktop.splits.request(days, time: time)) {
            await desktop.splits.load(days, tracker: tracker)
        }
    }

    private func header(_ days: [DayKey]) -> some View {
        HStack(spacing: 12) {
            HStack(spacing: 2) {
                Button {
                    move(by: -1)
                } label: {
                    Image(systemName: "chevron.left")
                }
                .help(mode == .day ? "Previous day" : "Previous week")
                .keyboardShortcut(.leftArrow, modifiers: [.command])
                Button("Today") { anchor = nil }
                    .disabled(days.contains(today))
                    .keyboardShortcut("t", modifiers: [.command])
                Button {
                    move(by: 1)
                } label: {
                    Image(systemName: "chevron.right")
                }
                .help(mode == .day ? "Next day" : "Next week")
                .keyboardShortcut(.rightArrow, modifiers: [.command])
            }
            .buttonStyle(.bordered)
            Text(CalendarLayout.label(days, in: time))
                .font(.title3.weight(.semibold))
            Spacer()
            if loadingActivity { ProgressView().controlSize(.small).help("Reading local agent logs…") }
            let toReview = desktop.review(on: days, tracker: tracker).count
            if toReview > 0 {
                Button {
                    desktop.openReview(mode == .day ? .day(days[0]) : ReviewScope(days: days))
                } label: {
                    Label(mode == .day ? "Review Day (\(toReview))" : "Review Week (\(toReview))", systemImage: ActivityStyle.symbol)
                }
                .help("Every suggestion for the days shown, in one place")
            }
            if let activityError { Text(activityError).font(.caption).foregroundStyle(.secondary) }
            if activityAvailable {
                ActivityToggle(isOn: activityBinding)
            }
            Picker("View", selection: modeBinding) {
                Text("Day").tag(CalendarMode.day)
                Text("Week").tag(CalendarMode.week)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()
            Button {
                addEntry(days)
            } label: {
                Label("Add Entry", systemImage: "plus")
            }
            .buttonStyle(.brand)
        }
    }

    private func move(by steps: Int) {
        anchor = time.adding(steps * (mode == .day ? 1 : 7), to: anchor ?? today)
    }

    private func load(_ days: [DayKey]) async {
        do {
            try await Performance.measure("Calendar entries") { try await tracker.loadCalendar(days) }
            loadError = nil
        } catch {
            if !Task.isCancelled {
                loadError = error.localizedDescription
            }
        }
    }

    /// A new hour-long entry starting at the quarter hour now, today or on the first day shown.
    private func addEntry(_ days: [DayKey]) {
        let shown = days.contains(today) ? today : days[0]
        let day = tracker.rules?.isLocked(shown) == true ? today : shown
        let start = CalendarLayout.snap(CalendarLayout.minuteOfDay(Date(), in: time) - 7.5, step: 15)
        model.editing = EntryEditing(newRange: DateInterval(
            start: CalendarLayout.instant(day, minute: start, in: time),
            end: CalendarLayout.instant(day, minute: min(start + 60, CalendarLayout.minutesPerDay), in: time)))
    }
}

/// Agent activity in the calendar as an icon beside Day and Week: filled and tinted while the lanes show, so it says at
/// a glance whether they do. View › Show Agent Activity (⌥⌘A) does the same.
private struct ActivityToggle: View {
    @Binding var isOn: Bool

    var body: some View {
        Button {
            isOn.toggle()
        } label: {
            Image(systemName: ActivityStyle.symbol)
                .symbolVariant(isOn ? .fill : .none)
                .foregroundStyle(isOn ? Color.brandText : .secondary)
                .frame(width: 18, height: 16)
        }
        .buttonStyle(.bordered)
        .background(isOn ? Color.brand.opacity(0.22) : .clear, in: RoundedRectangle(cornerRadius: 6))
        .help(isOn ? "Hide agent activity (⌥⌘A)" : "Show agent activity (⌥⌘A): your focus and agent runs from \(AgentSource.names) beside your entries. Beta")
        .accessibilityLabel("Agent activity")
        .accessibilityValue(isOn ? "On" : "Off")
        .accessibilityAddTraits(isOn ? .isSelected : [])
    }
}

/// Where the calendar was in one workspace: kept by the model rather than the view, so going back to the calendar shows
/// the same days with their agent activity, without the lanes flashing while they are read again.
@Observable
final class CalendarState {
    /// A day in the days shown, or nil for today's.
    var anchor: DayKey?
    /// The agent activity last read for the days shown.
    var activity: [ActivityDay] = []
}

/// The hours of the days side by side, with the entries on them.
private struct CalendarGrid: View {
    @Environment(AppModel.self) private var model
    @Environment(Desktop.self) private var desktop
    let tracker: Tracker
    let days: [DayKey]
    let activity: [ActivityDay]
    /// The sources whose lanes to show, or nil with activity off. Lanes keep their place while logs load, so the
    /// grid doesn't jump when they arrive.
    let activitySources: [AgentSource]?

    private var showsColumn: Bool { days.count == 1 && activitySources != nil }
    private var columnWidth: CGFloat { showsColumn ? ActivityColumn.width(activitySources ?? []) : 0 }
    private var weekReserve: CGFloat { days.count > 1 && activitySources != nil ? ActivityStyle.weekReserve : 0 }

    private func activity(on day: DayKey) -> ActivityDay? {
        activity.first { $0.day == day }
    }

    @State private var interaction: CalendarDrag.State?
    /// The entry whose ✦ the pointer is on, whose suggestion is drawn on its block until the pointer leaves.
    @State private var previewing: String?
    /// The entry whose ✦ was just clicked: no preview again until the pointer has left the ✦.
    @State private var clicked: String?
    /// Where the pointer last rested on the grid, and the days' width then: a redraw that moves a ✦ away from it
    /// takes its preview away, without waiting for the pointer to move.
    @State private var pointer: (at: CGPoint, width: CGFloat)?
    @State private var scrolledFor: [DayKey] = []
    /// The hours' width, which a visible scroll bar makes narrower than the headings above them.
    @State private var contentWidth: CGFloat?
    /// Counts up every minute while a running entry is on the grid, so its block grows, and otherwise at midnight,
    /// for today's mark. The hours and the line at now follow the clock on their own (`CalendarClock`).
    @State private var tick = 0

    static let hourHeight: CGFloat = 48
    static let perMinute = hourHeight / 60
    static let gutter: CGFloat = 56

    private var time: UserTime { tracker.time }

    private var rules: CalendarDrag {
        CalendarDrag(
            time: time,
            isLocked: { [tracker] in tracker.isLocked($0) },
            isDayLocked: { [tracker] in tracker.rules?.isLocked($0) == true })
    }

    var body: some View {
        let _ = tick
        let _ = Performance.event("Calendar grid drawn")
        let segments = Performance.measure("Calendar layout") {
            CalendarLayout.layout(tracker.entries(overlapping: days), days: days, in: time, now: Date())
        }
        let running = segments.contains { $0.entry.isRunning }
        VStack(spacing: 0) {
            headings(segments)
                .frame(width: contentWidth)
                .frame(maxWidth: .infinity, alignment: .leading)
            Divider()
            ScrollViewReader { proxy in
                ScrollView(.vertical) {
                    // The hours are what scrolls, so the view can open at the working day.
                    VStack(spacing: 0) {
                        // Half hours, so the working day can open with a little room above it.
                        ForEach(0..<48, id: \.self) { half in
                            Color.clear.frame(height: Self.hourHeight / 2).id(half)
                        }
                    }
                    .frame(maxWidth: .infinity)
                    .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { contentWidth = $0 }
                    .overlay {
                        GeometryReader { geometry in
                            let gridWidth = geometry.size.width - columnWidth
                            let width = (gridWidth - Self.gutter) / CGFloat(days.count)
                            HStack(spacing: 0) {
                                ZStack(alignment: .topLeading) {
                                    CalendarBackground(shades: days.map(shade), width: width).equatable()
                                    ForEach(shown(segments)) { segment in
                                        block(segment, width: width, locked: tracker.isLocked(segment.entry.start), dragging: false)
                                    }
                                    untracked(width: width)
                                    proposals(width: width)
                                    preview(width: width)
                                    previewHint(segments, width: width, gridWidth: gridWidth)
                                    CalendarClock(days: days, time: time, width: width)
                                }
                                .frame(width: gridWidth)
                                .contentShape(Rectangle())
                                .gesture(drag(segments: segments, width: width))
                                .onContinuousHover { phase in
                                    hover(phase, segments: segments, width: width)
                                }
                                .overlay(alignment: .topLeading) {
                                    if weekReserve > 0 {
                                        ForEach(Array(days.enumerated()), id: \.offset) { index, day in
                                            if let value = activity(on: day) {
                                                ActivityLane(day: value, source: nil, time: time, hourHeight: Self.hourHeight, interactive: false)
                                                    .offset(x: Self.gutter + CGFloat(index + 1) * width - ActivityStyle.weekWidth - 2)
                                            }
                                        }
                                        .allowsHitTesting(false)
                                    }
                                }
                                if showsColumn, let sources = activitySources {
                                    ActivityColumn(day: activity(on: days[0]), sources: sources, time: time, hourHeight: Self.hourHeight)
                                }
                            }
                        }
                    }
                }
                .onAppear { scroll(proxy, segments) }
                #if DEBUG
                // A snapshot of the suggestion drawn on an entry's block, as when the pointer is on its ✦.
                .task(id: segments.map(\.id)) {
                    if let text = ProcessInfo.processInfo.environment["SPINDLE_SNAPSHOT_SPLIT_PREVIEW"] {
                        previewing = segments.first { $0.entry.description == text }?.entry.id
                    }
                }
                #endif
                .onChange(of: days) { scroll(proxy, segments) }
            }
        }
        .onChange(of: segments.map { "\($0.id)|\($0.startMinute)|\($0.endMinute)|\($0.lane)/\($0.lanes)" }) { recheckPreview(segments) }
        .task(id: running) {
            while !Task.isCancelled {
                let now = Date()
                let next = running
                    ? Date(timeIntervalSinceReferenceDate: (now.timeIntervalSinceReferenceDate / 60).rounded(.down) * 60 + 60)
                    : time.start(of: time.adding(1, to: time.day(of: now)))
                try? await Task.sleep(for: .seconds(next.timeIntervalSince(now) + 0.05))
                guard !Task.isCancelled else { return }
                tick += 1
            }
        }
    }

    // MARK: - Drawing

    private func headings(_ segments: [DaySegment]) -> some View {
        // Top-aligned, so weekday names stay in one row when some days have a Focus total and others don't.
        // Worked out once for the days shown, not once for each day.
        let toReview = Dictionary(grouping: desktop.review(on: days, tracker: tracker)) { time.day(of: $0.range.start) }
        return HStack(alignment: .top, spacing: 0) {
            Color.clear.frame(width: Self.gutter, height: 1)
            ForEach(days, id: \.self) { day in
                let seconds = segments.filter { $0.day == day }.reduce(0) { $0 + ($1.endMinute - $1.startMinute) * 60 }
                let isToday = day == time.day(of: Date())
                VStack(spacing: 1) {
                    HStack(spacing: 3) {
                        Text(weekday(day).uppercased()).tracking(0.4)
                        if tracker.rules?.isLocked(day) == true {
                            Image(systemName: "lock.fill").help("Locked").accessibilityLabel("Locked")
                        }
                    }
                    .font(.caption2.weight(.medium))
                    .foregroundStyle(.secondary)
                    // Today in marigold, the colour of now.
                    Text("\(day.day)")
                        .font(.title3.weight(isToday ? .semibold : .medium).monospacedDigit())
                        .foregroundStyle(isToday ? Color.ink : .primary)
                        .frame(width: 30, height: 30)
                        .background(isToday ? Color.brand : .clear, in: Circle())
                    Text(seconds > 0 ? Durations.hoursMinutes(seconds) : "–")
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                    if let waiting = toReview[day], !waiting.isEmpty {
                        ReviewDayButton(count: waiting.count, scope: .day(day))
                            .padding(.top, 2)
                    }
                    if let value = activity(on: day), value.hasActivity {
                        Label(Durations.hoursMinutes(value.attentionTotal), systemImage: ActivityStyle.symbol)
                            .labelStyle(.titleAndIcon)
                            .font(.caption.monospacedDigit())
                            .foregroundStyle(.secondary)
                            .help("Focus in agent logs")
                            .accessibilityLabel("Focus in agent logs, \(Durations.hoursMinutes(value.attentionTotal))")
                    }
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 6)
                .accessibilityElement(children: .combine)
            }
            if showsColumn, let sources = activitySources {
                HStack(spacing: 0) {
                    Text("Focus").frame(width: ActivityStyle.focusWidth)
                    ForEach(sources, id: \.self) { source in
                        Text(source.shortLabel).frame(width: ActivityStyle.laneWidth).help(source.label)
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                .padding(.top, 6)
            }
        }
    }

    private func isWeekend(_ day: DayKey) -> Bool {
        Calendar(identifier: .gregorian).isDateInWeekend(time.date(day, hours: 12, minutes: 0))
    }

    private func weekday(_ day: DayKey) -> String {
        time.format(time.date(day, hours: 12, minutes: 0), "EEE")
    }

    /// How dark a day's column is: locked days more, weekends a little.
    private func shade(_ day: DayKey) -> Double {
        tracker.rules?.isLocked(day) == true ? 0.05 : isWeekend(day) ? 0.025 : 0
    }

    /// The segments to draw: while one is dragged, it is drawn where it would land instead.
    private func shown(_ segments: [DaySegment]) -> [DaySegment] {
        guard let interaction, let dragged = rules.preview(interaction)?.entry.id else { return segments }
        return segments.filter { $0.entry.id != dragged }
    }

    private func rect(_ segment: DaySegment, width: CGFloat) -> CGRect {
        guard let index = days.firstIndex(of: segment.day) else { return .zero }
        let laneWidth = (width - weekReserve) / CGFloat(segment.lanes)
        return CGRect(
            x: Self.gutter + CGFloat(index) * width + CGFloat(segment.lane) * laneWidth + 2,
            y: CGFloat(segment.startMinute) * Self.perMinute,
            width: max(laneWidth - 4, 4),
            height: max(CGFloat(segment.endMinute - segment.startMinute) * Self.perMinute, 14))
    }

    /// A block as a value, so while one is dragged SwiftUI draws only the blocks that changed.
    private func block(_ segment: DaySegment, width: CGFloat, locked: Bool, dragging: Bool) -> some View {
        let entry = segment.entry
        let dayStart = time.start(of: segment.day)
        let frame = rect(segment, width: width)
        return CalendarBlock(
            entry: entry, frame: frame, sparkInset: sparkInset(frame, width: width), catalog: tracker.catalog, time: time,
            locked: locked, dragging: dragging,
            suggestion: dragging || locked ? nil : desktop.splits.suggestion(for: entry, tracker: tracker),
            shown: DateInterval(start: dayStart + segment.startMinute * 60, end: dayStart + max(segment.startMinute, segment.endMinute) * 60),
            previewing: !dragging && previewing == entry.id,
            onTake: { [desktop] in
                if let suggestion = desktop.splits.suggestion(for: entry, tracker: tracker) {
                    desktop.take(SplitOffer(entry: entry, suggestion: suggestion))
                }
            })
            .equatable()
    }

    /// The suggestion whose ✦ is under a point, with its entry: the corner of a block that has one.
    private func spark(at point: CGPoint, segments: [DaySegment], width: CGFloat) -> SplitOffer? {
        for segment in segments.reversed() {
            let frame = rect(segment, width: width)
            guard frame.contains(point) else { continue }
            guard CalendarBlock.sparkFrame(in: frame, inset: sparkInset(frame, width: width))?.contains(point) == true, !tracker.isLocked(segment.entry.start),
                  let suggestion = desktop.splits.suggestion(for: segment.entry, tracker: tracker) else { return nil }
            return SplitOffer(entry: segment.entry, suggestion: suggestion)
        }
        return nil
    }

    /// How far a block's ✦ moves in from its right edge, to stay clear of an overlay scroll bar, which takes the
    /// pointer near the grid's right edge. With the activity column there, nothing reaches that edge.
    private func sparkInset(_ frame: CGRect, width: CGFloat) -> CGFloat {
        guard !showsColumn, NSScroller.preferredScrollerStyle == .overlay else { return 0 }
        let edge = Self.gutter + CGFloat(days.count) * width - Self.scrollerReserve
        return max(0, frame.maxX - edge)
    }

    /// The width an overlay scroll bar takes from the right edge while the pointer is near it.
    static let scrollerReserve: CGFloat = 16

    /// One line under the block whose suggestion is drawn: its parts by name and time, and what a click on the ✦
    /// does. Narrow and short blocks can't name every part inside, so this is the one place each is named; under
    /// the block, so the block's own words stay clear. Above it at the end of the day.
    @ViewBuilder private func previewHint(_ segments: [DaySegment], width: CGFloat, gridWidth: CGFloat) -> some View {
        let entryId = interaction == nil ? previewing : nil
        let shown = segments.filter { $0.entry.id == entryId }
        // The part of the day the pointer is on, for an entry over midnight.
        if let segment = shown.first(where: { segment in pointer.map { rect(segment, width: width).contains($0.at) } ?? false }) ?? shown.first,
           !tracker.isLocked(segment.entry.start),
           let suggestion = desktop.splits.suggestion(for: segment.entry, tracker: tracker) {
            let frame = rect(segment, width: width)
            let entry = segment.entry
            let seconds = max(0, (entry.stop ?? Date()).timeIntervalSince(entry.start))
            let opens = desktop.splits.blocker(.split(SplitOffer(entry: entry, suggestion: suggestion)), tracker: tracker) != nil
            let below = frame.maxY + 22 < CGFloat(CalendarLayout.minutesPerDay) * Self.perMinute
            // From the block's left edge, or ending at its right edge in the right half of the grid.
            let leading = frame.midX < gridWidth / 2
            SplitPreview.Hint(text: SplitPreview.hint(suggestion, seconds: seconds, opens: opens, catalog: tracker.catalog, time: time),
                              weak: suggestion.strength == .weak)
                .frame(width: leading ? max(0, gridWidth - frame.minX - 4) : max(0, frame.maxX - Self.gutter), alignment: leading ? .leading : .trailing)
                .offset(x: leading ? frame.minX : Self.gutter, y: below ? frame.maxY + 3 : frame.minY - 21)
                .allowsHitTesting(false)
                .accessibilityHidden(true)
                .transition(.opacity)
        }
    }

    /// Draws or takes away the preview for where the pointer rests, e.g. once a redraw moved a block under it.
    private func recheckPreview(_ segments: [DaySegment]) {
        guard interaction == nil, let pointer else { return }
        let offer = spark(at: pointer.at, segments: segments, width: pointer.width)
        let shown = clicked == nil ? offer?.entry.id : nil
        if previewing != shown { previewing = shown }
    }

    @ViewBuilder private func preview(width: CGFloat) -> some View {
        if let interaction {
            if let segment = rules.preview(interaction) {
                block(segment, width: width, locked: false, dragging: true)
            } else if case .create(let day, let anchor, let current) = interaction, let index = days.firstIndex(of: day) {
                let from = min(anchor, current)
                let to = max(anchor, current, from + CalendarDrag.step)
                RoundedRectangle(cornerRadius: 5)
                    .strokeBorder(Color.brand, style: StrokeStyle(lineWidth: 1.5, dash: [4, 3]))
                    .background(Color.brand.opacity(0.1), in: RoundedRectangle(cornerRadius: 5))
                    .frame(width: width - 8, height: CGFloat(to - from) * Self.perMinute)
                    .offset(x: Self.gutter + CGFloat(index) * width + 4, y: CGFloat(from) * Self.perMinute)
                    .allowsHitTesting(false)
            }
        }
    }

    /// Where the time agents proposed would go, day by day, beside the entries already there.
    private func ghosts(width: CGFloat) -> [(id: String, frame: CGRect, proposal: AgentProposal)] {
        desktop.proposals.pending(in: tracker).flatMap { proposal -> [(id: String, frame: CGRect, proposal: AgentProposal)] in
            let request = proposal.request(current: proposal.entryId.flatMap(tracker.entry(id:)))
            guard let stop = request.stop, proposal.isNew || proposal.fields.start != nil || proposal.fields.stop != nil else { return [] }
            return days.enumerated().compactMap { index, day in
                let dayStart = time.start(of: day)
                let dayEnd = time.start(of: time.adding(1, to: day))
                guard request.start < dayEnd, stop > dayStart else { return nil }
                let from = max(request.start, dayStart).timeIntervalSince(dayStart) / 60
                let to = min(stop, dayEnd).timeIntervalSince(dayStart) / 60
                // Over an entry, it takes the right half of the day, so both can be read.
                let overlaps = tracker.entries(overlapping: [day]).contains { $0.start < stop && ($0.stop ?? Date()) > request.start }
                let column = width - weekReserve
                let frame = CGRect(
                    x: Self.gutter + CGFloat(index) * width + (overlaps ? column / 2 : 4), y: CGFloat(from) * Self.perMinute,
                    width: max(overlaps ? column / 2 - 4 : column - 8, 4), height: max(CGFloat(to - from) * Self.perMinute, 14))
                return ("\(proposal.id)|\(day)", frame, proposal)
            }
        }
    }

    @ViewBuilder private func proposals(width: CGFloat) -> some View {
        ForEach(ghosts(width: width), id: \.id) { ghost in
            let request = ghost.proposal.request(current: ghost.proposal.entryId.flatMap(tracker.entry(id:)))
            let color = tracker.catalog.project(request.details.projectId).map { Color(hex: $0.color) } ?? Color.brand
            VStack(alignment: .leading, spacing: 1) {
                Label(request.details.description.isEmpty ? "No description" : request.details.description, systemImage: ActivityStyle.symbol)
                    .fontWeight(.medium)
                    .lineLimit(1)
                if ghost.frame.height > 40 {
                    Text(ghost.proposal.isNew ? "Proposed · not saved" : "Proposed change · not applied")
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
            .font(.caption)
            .padding(.horizontal, 6)
            .padding(.top, ghost.frame.height < 24 ? 0 : 3)
            .frame(width: ghost.frame.width, height: ghost.frame.height, alignment: .topLeading)
            .background(color.opacity(0.1), in: RoundedRectangle(cornerRadius: 5))
            .background(Color(nsColor: .windowBackgroundColor), in: RoundedRectangle(cornerRadius: 5))
            .overlay(RoundedRectangle(cornerRadius: 5).strokeBorder(color.opacity(0.8), style: StrokeStyle(lineWidth: 1.5, dash: [5, 3])))
            .offset(x: ghost.frame.minX, y: ghost.frame.minY)
            .help("Proposed by \(ghost.proposal.client ?? "an agent"). Click to review.")
            .accessibilityElement(children: .combine)
            .accessibilityAddTraits(.isButton)
            .accessibilityAction { desktop.proposals.reviewing = true }
        }
    }

    /// Past agent attention with no entry, day by day; time agents proposed entries for is spoken for already.
    private func untrackedBlocks(width: CGFloat) -> [(block: UntrackedBlock, frame: CGRect)] {
        let proposed = desktop.proposals.pending(in: tracker).compactMap { proposal -> DateInterval? in
            let request = proposal.request(current: proposal.entryId.flatMap(tracker.entry(id:)))
            return request.stop.map { DateInterval(start: request.start, end: max(request.start, $0)) }
        }
        let column = width - weekReserve
        return desktop.splits.untracked(on: days, tracker: tracker, covered: proposed).compactMap { block in
            guard let index = days.firstIndex(of: block.day) else { return nil }
            let dayStart = time.start(of: block.day)
            let from = block.range.start.timeIntervalSince(dayStart) / 60
            let to = block.range.end.timeIntervalSince(dayStart) / 60
            let frame = CGRect(x: Self.gutter + CGFloat(index) * width + 4, y: CGFloat(from) * Self.perMinute,
                               width: max(column - 8, 4), height: max(CGFloat(to - from) * Self.perMinute, 14))
            return (block, frame)
        }
    }

    /// "✦ Atlas · 14:00–15:30": agents were at work and nothing was tracked. No fill and a hatched edge, unlike the
    /// dashed blocks of what agents proposed. A click opens a new entry for it.
    @ViewBuilder private func untracked(width: CGFloat) -> some View {
        ForEach(untrackedBlocks(width: width), id: \.block.id) { ghost in
            let projects = ghost.block.stretch.projects.compactMap { tracker.catalog.project($0) }
            let color = projects.first.map { Color(hex: $0.color) } ?? Color.secondary
            let names = projects.isEmpty ? "Not tracked" : projects.prefix(3).map(\.name).joined(separator: ", ")
            let times = "\(time.clock(ghost.block.range.start))–\(time.clock(ghost.block.range.end))"
            VStack(alignment: .leading, spacing: 1) {
                Label("\(names) · \(times)", systemImage: ActivityStyle.symbol)
                    .fontWeight(.medium)
                    .monospacedDigit()
                    .lineLimit(1)
                if ghost.frame.height > 40 {
                    Text("Agents at work · not tracked")
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 8)
            .padding(.top, ghost.frame.height < 24 ? 0 : 3)
            .frame(width: ghost.frame.width, height: ghost.frame.height, alignment: .topLeading)
            .overlay(HatchedEdge(color: color.opacity(0.7)))
            .contentShape(RoundedRectangle(cornerRadius: 5))
            .contextMenu {
                Button("Add Entry…") { model.editing = desktop.splits.editing(ghost.block, tracker: tracker) }
                Button("Dismiss") { desktop.splits.dismiss(ghost.block) }
            }
            .offset(x: ghost.frame.minX, y: ghost.frame.minY)
            .help("Agents were at work \(times) with nothing tracked. Click to add an entry for it.")
            .accessibilityElement(children: .combine)
            .accessibilityAddTraits(.isButton)
            .accessibilityAction { model.editing = desktop.splits.editing(ghost.block, tracker: tracker) }
            .accessibilityAction(named: "Dismiss") { desktop.splits.dismiss(ghost.block) }
        }
    }

    private func scroll(_ proxy: ScrollViewProxy, _ segments: [DaySegment]) {
        guard scrolledFor != days else { return }
        scrolledFor = days
        let earliest = segments.filter { !$0.continuesBefore }.map(\.startMinute).min() ?? 7 * 60
        // After the grid is laid out, or there is nothing to scroll to yet.
        DispatchQueue.main.async {
            proxy.scrollTo(max(0, min(7, Int(earliest / 60)) * 2 - 1), anchor: .top)
        }
    }

    // MARK: - Dragging

    /// Where a point on the grid falls: which day, and which minute of it.
    private func slot(at point: CGPoint, width: CGFloat) -> (day: DayKey, minute: Double) {
        let index = min(days.count - 1, max(0, Int((point.x - Self.gutter) / width)))
        let minute = min(CalendarLayout.minutesPerDay, max(0, Double(point.y / Self.perMinute)))
        return (days[index], minute)
    }

    /// The block under a point, and whether it is near its top or bottom edge.
    private func hit(_ point: CGPoint, segments: [DaySegment], width: CGFloat) -> CalendarDrag.Target {
        for segment in segments.reversed() {
            let frame = rect(segment, width: width)
            guard frame.contains(point) else { continue }
            let resizable = rules.canChange(segment.entry)
            if resizable && !segment.continuesBefore && point.y - frame.minY < 6 { return .entry(segment, edge: .start) }
            if resizable && !segment.continuesAfter && frame.maxY - point.y < 6 { return .entry(segment, edge: .end) }
            return .entry(segment, edge: nil)
        }
        return .empty
    }

    private func hover(_ phase: HoverPhase, segments: [DaySegment], width: CGFloat) {
        guard interaction == nil else { return }
        switch phase {
        case .active(let point):
            pointer = (point, width)
            // On a block's ✦, its suggestion is drawn on the block, to take with a click.
            let offer = spark(at: point, segments: segments, width: width)
            if offer?.entry.id != clicked { clicked = nil }
            let shown = clicked == nil ? offer?.entry.id : nil
            if previewing != shown { withAnimation(.easeOut(duration: 0.12)) { previewing = shown } }
            if offer != nil {
                NSCursor.pointingHand.set()
                return
            }
            switch hit(point, segments: segments, width: width) {
            case .entry(_, edge: .some):
                NSCursor.resizeUpDown.set()
            case .entry(let segment, edge: nil):
                (rules.canChange(segment.entry) && !segment.continuesBefore ? NSCursor.openHand : NSCursor.pointingHand).set()
            case .empty:
                (tracker.rules?.isLocked(slot(at: point, width: width).day) == true ? NSCursor.arrow : NSCursor.crosshair).set()
            }
        case .ended:
            previewing = nil
            clicked = nil
            pointer = nil
            NSCursor.arrow.set()
        }
    }

    private func drag(segments: [DaySegment], width: CGFloat) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { value in
                let slot = slot(at: value.location, width: width)
                // A press on a ✦ is a click on it, not the start of a move.
                if interaction == nil, spark(at: value.startLocation, segments: segments, width: width) != nil { return }
                if interaction == nil {
                    let start = self.slot(at: value.startLocation, width: width)
                    interaction = rules.begin(on: hit(value.startLocation, segments: segments, width: width), day: start.day, minute: start.minute)
                }
                if let current = interaction {
                    interaction = rules.move(current, to: slot.day, minute: slot.minute, distance: hypot(value.translation.width, value.translation.height))
                }
            }
            .onEnded { value in
                // A click on a block's ✦ takes the suggestion drawn on it, with Undo.
                if interaction == nil, hypot(value.translation.width, value.translation.height) < 4,
                   let offer = spark(at: value.startLocation, segments: segments, width: width) {
                    previewing = nil
                    clicked = offer.entry.id
                    desktop.take(offer)
                    return
                }
                guard let done = interaction else { return }
                interaction = nil
                // A click on a proposal opens the review, rather than a new entry under it.
                if hypot(value.translation.width, value.translation.height) < 4,
                   ghosts(width: width).contains(where: { $0.frame.contains(value.startLocation) }) {
                    desktop.proposals.reviewing = true
                    return
                }
                // A click on untracked time opens a new entry for it, with what the agents worked on filled in.
                if hypot(value.translation.width, value.translation.height) < 4,
                   let ghost = untrackedBlocks(width: width).first(where: { $0.frame.contains(value.startLocation) }) {
                    model.editing = desktop.splits.editing(ghost.block, tracker: tracker)
                    return
                }
                finish(rules.finish(done), segments: segments)
            }
    }

    private func finish(_ outcome: CalendarDrag.Outcome, segments: [DaySegment]) {
        let entry = { (id: String) in segments.first { $0.entry.id == id }?.entry }
        switch outcome {
        case .open(let id):
            if let entry = entry(id) {
                model.editing = EntryEditing(entry: entry)
            }
        case .create(let range):
            model.editing = EntryEditing(newRange: range)
        case .save(let id, let start, let stop):
            guard let entry = entry(id) else { return }
            var request = EntryRequest(entry)
            request.start = start
            request.stop = stop
            Task {
                do {
                    try await tracker.update(entry, to: request)
                } catch {
                    model.notices.show(error, fallback: "Couldn't change the time entry.")
                }
            }
        }
    }
}

/// The edge of untracked time: diagonal hatching in a band inside the block's outline, with nothing filled in.
private struct HatchedEdge: View {
    let color: Color

    var body: some View {
        Canvas { context, size in
            var lines = Path()
            var x = -size.height
            while x < size.width {
                lines.move(to: CGPoint(x: x, y: size.height))
                lines.addLine(to: CGPoint(x: x + size.height, y: 0))
                x += 5
            }
            context.stroke(lines, with: .color(color), lineWidth: 1)
        }
        .mask(RoundedRectangle(cornerRadius: 5).strokeBorder(lineWidth: 4))
        .allowsHitTesting(false)
    }
}

/// An entry's block on the grid. Drawn from values alone, so SwiftUI can leave it as it is while another block is
/// dragged; names and colours come from the catalog, which it follows by itself.
private struct CalendarBlock: View, Equatable {
    @Environment(AppModel.self) private var model
    @Environment(\.colorScheme) private var colorScheme
    let entry: TimeEntry
    let frame: CGRect
    /// How far the ✦ sits in from the right edge, clear of an overlay scroll bar.
    let sparkInset: CGFloat
    let catalog: Catalog
    let time: UserTime
    let locked: Bool
    let dragging: Bool
    /// The split agent activity suggests for it, worked out by the grid from the advisor's kept suggestions.
    let suggestion: SplitSuggestion?
    /// The time the block shows: the entry's, or its part of the day when it runs over midnight.
    let shown: DateInterval
    /// The pointer is on the ✦: the suggestion is drawn on the block.
    let previewing: Bool

    private var suggested: Bool { suggestion != nil }
    /// The entry's whole time, a running one's up to now.
    private var seconds: TimeInterval { max(0, (entry.stop ?? Date()).timeIntervalSince(entry.start)) }
    /// Takes the suggestion, as a click on the ✦ does; for screen readers.
    var onTake: (() -> Void)?

    nonisolated static func == (lhs: CalendarBlock, rhs: CalendarBlock) -> Bool {
        lhs.entry == rhs.entry && lhs.frame == rhs.frame && lhs.sparkInset == rhs.sparkInset && lhs.catalog === rhs.catalog && lhs.time == rhs.time
            && lhs.locked == rhs.locked && lhs.dragging == rhs.dragging && lhs.suggestion == rhs.suggestion
            && lhs.shown == rhs.shown && lhs.previewing == rhs.previewing
    }

    /// Where a block in `frame` has its ✦, if it is wide enough for one.
    static func sparkFrame(in frame: CGRect, inset: CGFloat = 0) -> CGRect? {
        frame.width > 40 + inset ? CGRect(x: frame.maxX - inset - 18, y: frame.minY, width: 18, height: 17) : nil
    }

    var body: some View {
        let _ = Performance.event("Calendar block drawn")
        // A split entry takes the colour of its largest part, the project it mirrors; the rest show in its thread.
        let project = catalog.project(entry.projectId)
        let color = project.map { Color(hex: $0.color) } ?? .secondary
        let parts = entry.isSplit ? entry.parts.sorted { $0.percent > $1.percent } : nil
        // Tall enough for the thread under the text; shorter split blocks show dots by the description instead.
        let threaded = parts != nil && frame.height >= 30
        let label = entry.description.isEmpty ? "No description" : entry.description
        let span = "\(time.clock(entry.start)) – \(entry.stop.map(time.clock) ?? "now")"
        VStack(alignment: .leading, spacing: 1) {
            HStack(spacing: 3) {
                if entry.isRunning {
                    Circle().fill(Color.brand).frame(width: 5, height: 5)
                }
                if locked {
                    Image(systemName: "lock.fill").font(.system(size: 8)).foregroundStyle(.secondary)
                }
                Text(label)
                    .fontWeight(.medium)
                    .foregroundStyle(entry.description.isEmpty ? .secondary : .primary)
                    .lineLimit(1)
                if let parts, !threaded {
                    Spacer(minLength: 2)
                    SplitDots(catalog: catalog, parts: parts, size: 7)
                }
            }
            if frame.height > 40 {
                catalog.projectLine(project, parts: parts, in: colorScheme).lineLimit(1)
            }
            if frame.height > 52 {
                Text(span).foregroundStyle(.secondary).monospacedDigit().lineLimit(1)
            }
        }
        // The block's own title, project and time stay readable over a proposed split.
        .background {
            if previewing && suggested {
                RoundedRectangle(cornerRadius: 3).fill(.background.opacity(0.85)).padding(-2)
            }
        }
        .font(.caption)
        .padding(.horizontal, 6)
        .padding(.top, frame.height < 24 ? 0 : 3)
        .padding(.bottom, threaded ? 10 : 2)
        .frame(width: frame.width, height: frame.height, alignment: .topLeading)
        .background {
            if previewing, let suggestion {
                SplitPreview(suggestion: suggestion, shown: shown, seconds: seconds, size: frame.size, catalog: catalog, time: time, labels: false)
                    .transition(.opacity)
            }
        }
        .background(tint(color))
        .overlay {
            RoundedRectangle(cornerRadius: Self.radius).strokeBorder(color.opacity(0.3), lineWidth: 1)
        }
        .overlay(alignment: .bottom) {
            if let parts, threaded {
                SplitThread(catalog: catalog, parts: parts).padding(.horizontal, 6).padding(.bottom, 5)
            }
        }
        .overlay {
            if previewing, let suggestion {
                SplitPreview(suggestion: suggestion, shown: shown, seconds: seconds, size: frame.size, catalog: catalog, time: time, labels: true)
                    .transition(.opacity)
            }
        }
        .overlay(alignment: .topTrailing) {
            // No help tag: it would open over the preview it explains, and the line under the block says it all.
            if suggested && Self.sparkFrame(in: frame, inset: sparkInset) != nil {
                Image(systemName: ActivityStyle.symbol)
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(previewing ? Color.ink : Color.brandText)
                    .padding(3)
                    .background(previewing ? Color.brand : .clear, in: RoundedRectangle(cornerRadius: 4))
                    .padding(.trailing, sparkInset)
            }
        }
        .overlay {
            if dragging {
                RoundedRectangle(cornerRadius: Self.radius).strokeBorder(Color.brand, lineWidth: 2)
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: Self.radius))
        .shadow(color: .black.opacity(dragging ? 0.2 : 0), radius: dragging ? 6 : 0, y: dragging ? 1 : 0)
        .offset(x: frame.minX, y: frame.minY)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(
            "\(label), \(parts.map { "split: \(catalog.describe($0))" } ?? project?.name ?? "no project"), \(span)"
                + (locked ? ", locked" : "") + (suggested ? ", split suggested" : ""))
        .accessibilityAddTraits(.isButton)
        .accessibilityAction { model.editing = EntryEditing(entry: entry) }
        .accessibilityActions {
            if let suggestion, let onTake {
                Button("\(SplitPreview.title(suggestion, seconds: seconds, time: time)): \(SplitPreview.spoken(suggestion, seconds: seconds, catalog: catalog))",
                       action: onTake)
            }
        }
    }

    static let radius: CGFloat = 5

    /// The fill: the project's colour, light enough for text on it, a touch stronger over graphite.
    private func tint(_ color: Color) -> Color {
        color.opacity(dragging ? 0.32 : colorScheme == .dark ? 0.2 : 0.15)
    }
}

/// A suggestion drawn on its block while the pointer is on the block's ✦, as a proposal rather than a split: the
/// block stays as it is, and the proposed parts are hatched and translucent over it, with dashed lines between them.
/// Drawn twice: `labels: false` behind the block's own text (the parts), `labels: true` over it (each part's project
/// and time), placed clear of the title. The grid draws what a click does in a line under the block (`Hint`). Nothing
/// is saved until the ✦ is clicked.
private struct SplitPreview: View {
    let suggestion: SplitSuggestion
    let shown: DateInterval
    /// The entry's whole time, which the parts' times divide.
    let seconds: TimeInterval
    let size: CGSize
    let catalog: Catalog
    let time: UserTime
    let labels: Bool

    private var weak: Bool { suggestion.strength == .weak }

    var body: some View {
        ZStack(alignment: .topLeading) {
            if let stretches = suggestion.stretches {
                cuts(stretches)
            } else {
                columns
            }
        }
        .frame(width: size.width, height: size.height, alignment: .topLeading)
        .overlay {
            if !labels {
                RoundedRectangle(cornerRadius: CalendarBlock.radius)
                    .strokeBorder(Color.brand.opacity(weak ? 0.5 : 1), style: StrokeStyle(lineWidth: 1.5, dash: [5, 3]))
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    /// "Split 0:50 · 1:10", or "Split at 10:50": what a click on the ✦ does.
    static func title(_ suggestion: SplitSuggestion, seconds: TimeInterval, time: UserTime) -> String {
        guard suggestion.stretches == nil else { return SplitSuggestionChip.separateTitle(suggestion, time: time) }
        return "Split " + SplitApplier.durations(suggestion, seconds: seconds).map { Durations.format($0, as: .hm) }.joined(separator: " · ")
    }

    /// "Split Atlas 0:50 · Beacon 1:10 · Click ✦ to Apply", "Split at 09:40: Atlas 0:40 · Beacon 0:50 · Click ✦ to
    /// Apply", or "… · Click ✦ to Open" when one click can't take it and the editor opens instead.
    static func hint(_ suggestion: SplitSuggestion, seconds: TimeInterval, opens: Bool, catalog: Catalog, time: UserTime) -> String {
        let parts = spoken(suggestion, seconds: seconds, catalog: catalog)
        let what = suggestion.stretches == nil ? "Split \(parts)" : "\(SplitSuggestionChip.separateTitle(suggestion, time: time)): \(parts)"
        return "\(what) · Click ✦ to \(opens ? "Open" : "Apply")"
    }

    /// The hint's line, on a backing of its own.
    struct Hint: View {
        let text: String
        let weak: Bool

        var body: some View {
            Text(text)
                .font(.caption2.weight(.semibold))
                .monospacedDigit()
                .foregroundStyle(weak ? .secondary : .primary)
                .lineLimit(1)
                .truncationMode(.middle)
                .padding(.horizontal, 6)
                .padding(.vertical, 2)
                .background(.background, in: RoundedRectangle(cornerRadius: 4))
                .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(Color.primary.opacity(0.15)))
                .shadow(color: .black.opacity(0.08), radius: 2, y: 1)
        }
    }

    /// "Atlas 0:50 · Beacon 1:10", for the hint and screen readers.
    static func spoken(_ suggestion: SplitSuggestion, seconds: TimeInterval, catalog: Catalog) -> String {
        if let stretches = suggestion.stretches {
            return stretches.map { "\(EvidenceStrip.name($0.target, catalog: catalog)) \(Durations.format(minutes($0.range.duration), as: .hm))" }
                .joined(separator: " · ")
        }
        return zip(suggestion.parts, SplitApplier.durations(suggestion, seconds: seconds))
            .map { "\(EvidenceStrip.name($0.target, catalog: catalog)) \(Durations.format($1, as: .hm))" }
            .joined(separator: " · ")
    }

    private static func minutes(_ seconds: TimeInterval) -> TimeInterval { (seconds / 60).rounded() * 60 }

    /// Where `date` is down the block.
    private func y(_ date: Date) -> CGFloat {
        guard shown.duration > 0 else { return 0 }
        return CGFloat(min(max(date.timeIntervalSince(shown.start) / shown.duration, 0), 1)) * size.height
    }

    /// A proposed part: its project's colour, faint and hatched.
    private func proposed(_ color: Color) -> some View {
        ZStack {
            color.opacity(weak ? 0.06 : 0.12)
            Hatching(color: color.opacity(weak ? 0.18 : 0.4))
        }
    }

    private func dashed(_ from: CGPoint, _ to: CGPoint) -> some View {
        Path { $0.move(to: from); $0.addLine(to: to) }
            .stroke(Color.primary.opacity(weak ? 0.4 : 0.75), style: StrokeStyle(lineWidth: 1.5, dash: [4, 3]))
    }

    /// Each stretch hatched in its project's colour, with a dashed line and its time where the next one begins.
    private func cuts(_ stretches: [SplitSuggestion.Stretch]) -> some View {
        ZStack(alignment: .topLeading) {
            ForEach(stretches.indices, id: \.self) { index in
                let stretch = stretches[index]
                let top = y(stretch.range.start), bottom = y(stretch.range.end)
                if bottom > top {
                    let color = EvidenceStrip.color(stretch.target, catalog: catalog)
                    if labels {
                        // Each stretch's project and time, on the right, clear of the title and the ✦.
                        let text = "\(name(stretch.target)) \(Durations.format(Self.minutes(stretch.range.duration), as: .hm))"
                        if bottom - top >= (index == 0 ? 34 : 16) {
                            label(text, color: color)
                                .frame(width: size.width - 6, alignment: .trailing)
                                .offset(y: top + (index == 0 ? 18 : 11))
                        }
                        if index > 0 {
                            // On the cut, kept inside the block when the last stretch is short, and left of the ✦
                            // near the top.
                            let y = min(max(0, top - 7), max(0, size.height - 15))
                            Text(time.clock(stretch.range.start))
                                .font(.caption2.weight(.semibold).monospacedDigit())
                                .foregroundStyle(Color.ink)
                                .padding(.horizontal, 4)
                                .background(Color.brand.opacity(weak ? 0.6 : 1), in: Capsule())
                                .frame(width: max(0, size.width - (y < 17 ? 24 : 6)), alignment: .trailing)
                                .offset(y: y)
                        }
                    } else {
                        proposed(color)
                            .frame(width: size.width, height: bottom - top)
                            .offset(y: top)
                        if index > 0 {
                            dashed(CGPoint(x: 0, y: top), CGPoint(x: size.width, y: top))
                        }
                    }
                }
            }
        }
    }

    /// The parts side by side, as wide as their share, with dashed lines between them.
    private var columns: some View {
        let durations = SplitApplier.durations(suggestion, seconds: seconds)
        let widths = suggestion.parts.map { size.width * CGFloat($0.percent) / 100 }
        return ZStack(alignment: .topLeading) {
            ForEach(suggestion.parts.indices, id: \.self) { index in
                let part = suggestion.parts[index]
                let color = EvidenceStrip.color(part.target, catalog: catalog)
                let x = widths.prefix(index).reduce(0, +)
                if labels {
                    // At the bottom, clear of the block's own words; the time alone where the column is too narrow
                    // for the name. The line under the block names every part.
                    if size.height >= 66 {
                        let duration = Durations.format(durations[index], as: .hm)
                        let full = "\(name(part.target)) \(duration)"
                        label(widths[index] >= CGFloat(full.count) * 6 + 14 ? full : duration, color: color)
                            .frame(width: max(0, widths[index] - 4), alignment: .leading)
                            .offset(x: x + 4, y: size.height - 20)
                    }
                } else {
                    proposed(color)
                        .frame(width: widths[index], height: size.height)
                        .offset(x: x)
                    if index > 0 {
                        dashed(CGPoint(x: x, y: 0), CGPoint(x: x, y: size.height))
                    }
                }
            }
        }
    }

    /// The target's name, with the task a branch rule names for its part.
    private func name(_ target: SplitSuggestion.Target) -> String {
        let task = catalog.task(suggestion.parts.first { $0.target == target }?.taskId)
        return EvidenceStrip.name(target, catalog: catalog) + (task.map { " · \($0.name)" } ?? "")
    }

    private func label(_ text: String, color: Color) -> some View {
        HStack(spacing: 3) {
            Circle().fill(color).frame(width: 6, height: 6)
            Text(text).monospacedDigit().lineLimit(1)
        }
        .font(.caption2.weight(.semibold))
        .foregroundStyle(weak ? .secondary : .primary)
        .padding(.horizontal, 4)
        .padding(.vertical, 1)
        .background(.background.opacity(0.9), in: RoundedRectangle(cornerRadius: 3))
        .fixedSize()
    }
}

/// Diagonal lines across the whole area: a part that is only proposed, or time nobody tracked.
struct Hatching: View {
    let color: Color
    var spacing: CGFloat = 6

    var body: some View {
        Canvas { context, size in
            var lines = Path()
            var x = -size.height
            while x < size.width {
                lines.move(to: CGPoint(x: x, y: size.height))
                lines.addLine(to: CGPoint(x: x + size.height, y: 0))
                x += spacing
            }
            context.stroke(lines, with: .color(color), lineWidth: 1)
        }
    }
}

/// The grid's columns and hour lines, which only change with the days shown and the grid's width.
private struct CalendarBackground: View, Equatable {
    /// How dark each day's column is, from 0 for none.
    let shades: [Double]
    let width: CGFloat

    var body: some View {
        let gutter = CalendarGrid.gutter, hourHeight = CalendarGrid.hourHeight
        ZStack(alignment: .topLeading) {
            ForEach(Array(shades.enumerated()), id: \.offset) { index, shade in
                Rectangle()
                    .fill(shade > 0 ? Color.primary.opacity(shade) : .clear)
                    .frame(width: width, height: 24 * hourHeight)
                    .offset(x: gutter + CGFloat(index) * width)
            }
            Canvas { context, size in
                // Hours, and half hours fainter.
                for half in 1..<48 {
                    let y = CGFloat(half) * hourHeight / 2
                    context.stroke(Path { $0.move(to: CGPoint(x: gutter, y: y)); $0.addLine(to: CGPoint(x: size.width, y: y)) }, with: .color(.primary.opacity(half.isMultiple(of: 2) ? 0.08 : 0.035)))
                }
                for index in 0...shades.count {
                    let x = gutter + CGFloat(index) * width
                    context.stroke(Path { $0.move(to: CGPoint(x: x, y: 0)); $0.addLine(to: CGPoint(x: x, y: size.height)) }, with: .color(.primary.opacity(0.1)))
                }
            }
        }
    }
}

/// The hours down the side and the line at now: the parts of the grid that move with the clock, drawn again every
/// minute without the rest.
private struct CalendarClock: View {
    let days: [DayKey]
    let time: UserTime
    let width: CGFloat

    var body: some View {
        TimelineView(.everyMinute) { context in
            let now = context.date
            ZStack(alignment: .topLeading) {
                hours(now)
                nowLine(now)
            }
        }
        .allowsHitTesting(false)
    }

    /// The hours in the gutter. The now label takes the place of an hour it would cover.
    private func hours(_ now: Date) -> some View {
        let today = time.day(of: now)
        let minute = Int(CalendarLayout.minuteOfDay(now, in: time))
        return ForEach(Array(1..<24).filter { !days.contains(today) || abs($0 * 60 - minute) > 20 }, id: \.self) { hour in
            Text(time.clock(time.date(today, hours: hour, minutes: 0)))
                .font(.caption2.monospacedDigit())
                .foregroundStyle(.secondary)
                .frame(width: CalendarGrid.gutter - 8, alignment: .trailing)
                .offset(y: CGFloat(hour) * CalendarGrid.hourHeight - 7)
        }
        .accessibilityHidden(true)
    }

    @ViewBuilder private func nowLine(_ now: Date) -> some View {
        if let index = days.firstIndex(of: time.day(of: now)) {
            let y = CGFloat(CalendarLayout.minuteOfDay(now, in: time)) * CalendarGrid.perMinute
            // Now, in marigold: the colour of the middle bar of the mark, your hour.
            HStack(spacing: 0) {
                Circle().fill(Color.brand).frame(width: 8, height: 8)
                Rectangle().fill(Color.brand).frame(height: 2)
            }
            .frame(width: width + 4)
            .offset(x: CalendarGrid.gutter + CGFloat(index) * width - 4, y: y - 4)
            .accessibilityHidden(true)
            Text(time.clock(now))
                .font(.caption2.weight(.semibold).monospacedDigit())
                .foregroundStyle(Color.ink)
                .padding(.horizontal, 5)
                .padding(.vertical, 1)
                .background(Color.brand, in: Capsule())
                .frame(width: CalendarGrid.gutter - 4, alignment: .trailing)
                .offset(y: y - 8)
                .accessibilityHidden(true)
        }
    }
}
