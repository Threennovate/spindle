import SwiftUI
import SpindleKit

/// A day of time on a timeline, with its week above to move between days.
struct CalendarTab: View {
    @Environment(AppModel.self) private var model
    let tracker: Tracker

    /// The day shown; none means today.
    @State private var chosen: DayKey?
    @State private var loadError: String?

    private var time: UserTime { tracker.time }
    private var today: DayKey { time.day(of: AppClock.now) }
    private var day: DayKey { chosen ?? today }
    private var week: [DayKey] { CalendarLayout.week(containing: day, in: time) }

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                WeekStrip(tracker: tracker, week: week, selected: day, today: today) { chosen = $0 == today ? nil : $0 } move: { weeks in
                    chosen = time.adding(7 * weeks, to: day)
                }
                Divider()
                if let organization = model.readOnlyOrganization {
                    ReadOnlyNotice(organization: organization)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 16)
                        .padding(.vertical, 10)
                        .background(Color.red.opacity(0.08))
                    Divider()
                }
                if let loadError {
                    Label(loadError, systemImage: "exclamationmark.triangle")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                        .padding(8)
                }
                DayTimeline(tracker: tracker, day: day, today: today)
                    .id(day)
                    .transition(.opacity)
                    .simultaneousGesture(
                        DragGesture(minimumDistance: 40).onEnded { drag in
                            let across = drag.translation.width
                            guard abs(across) > 80, abs(across) > 2 * abs(drag.translation.height) else { return }
                            withAnimation(.snappy) {
                                chosen = time.adding(across < 0 ? 1 : -1, to: day)
                            }
                        })
            }
            .navigationTitle(time.dayHeading(day, now: AppClock.now))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button("Today") { withAnimation(.snappy) { chosen = nil } }
                        .disabled(day == today)
                }
                ToolbarItem(placement: .topBarTrailing) {
                    Button {
                        addEntry()
                    } label: {
                        Image(systemName: "plus")
                    }
                    .accessibilityLabel("Add a time entry")
                    .disabled(model.readOnlyOrganization != nil)
                }
            }
            .task(id: week) { await load(week) }
            .refreshable { await load(week) }
        }
    }

    private func load(_ days: [DayKey]) async {
        do {
            try await tracker.loadCalendar(days)
            loadError = nil
        } catch {
            if !Task.isCancelled {
                loadError = error.localizedDescription
            }
        }
    }

    /// A new hour-long entry starting at the quarter hour now, on the day shown (or today, if that day is locked).
    private func addEntry() {
        let target = tracker.rules?.isLocked(day) == true ? today : day
        let start = CalendarLayout.snap(CalendarLayout.minuteOfDay(AppClock.now, in: time) - 7.5, step: CalendarDrag.step)
        model.editing = EntryEditing(newRange: DateInterval(
            start: CalendarLayout.instant(target, minute: start, in: time),
            end: CalendarLayout.instant(target, minute: min(start + 60, CalendarLayout.minutesPerDay), in: time)))
    }
}

/// The week of the day shown: each day with the time tracked on it; tapping one shows it.
private struct WeekStrip: View {
    let tracker: Tracker
    let week: [DayKey]
    let selected: DayKey
    let today: DayKey
    let select: (DayKey) -> Void
    let move: (Int) -> Void

    var body: some View {
        let time = tracker.time
        let segments = CalendarLayout.layout(tracker.entries(overlapping: week), days: week, in: time, now: AppClock.now)
        HStack(spacing: 0) {
            Button { move(-1) } label: { Image(systemName: "chevron.left") }
                .frame(width: 32, height: 44)
                .accessibilityLabel("Previous week")
            ForEach(week, id: \.self) { day in
                let minutes = segments.filter { $0.day == day }.reduce(0) { $0 + $1.endMinute - $1.startMinute }
                Button {
                    withAnimation(.snappy) { select(day) }
                } label: {
                    VStack(spacing: 3) {
                        Text(day.formatted("EEEEE"))
                            .font(.caption2.weight(.medium))
                            .foregroundStyle(.secondary)
                        Text(day.formatted("d"))
                            .font(.callout.weight(day == selected ? .bold : .medium))
                            .foregroundStyle(day == selected ? (day == today ? Color.ink : Color(uiColor: .systemBackground)) : day == today ? Color.brandText : .primary)
                            .frame(width: 32, height: 32)
                            .background {
                                if day == selected {
                                    Circle().fill(day == today ? Color.brand : Color.primary)
                                }
                            }
                        Text(minutes > 0 ? Durations.hoursMinutes(minutes * 60) : " ")
                            .font(.caption2.monospacedDigit())
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                            .minimumScaleFactor(0.8)
                    }
                    .frame(maxWidth: .infinity)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel("\(time.longDay(day)), \(minutes > 0 ? Durations.hoursMinutes(minutes * 60) : "nothing tracked")")
                .accessibilityAddTraits(day == selected ? .isSelected : [])
            }
            Button { move(1) } label: { Image(systemName: "chevron.right") }
                .frame(width: 32, height: 44)
                .accessibilityLabel("Next week")
        }
        .padding(.horizontal, 4)
        .padding(.vertical, 8)
    }
}

/// The hours of one day, with its entries as blocks. Tapping a block opens it, tapping empty space adds an hour
/// there, and a long press on a block lets it be dragged to another time.
private struct DayTimeline: View {
    @Environment(AppModel.self) private var model
    let tracker: Tracker
    let day: DayKey
    let today: DayKey

    @State private var dragging: CalendarDrag.State?
    @State private var liftedId: String?
    /// How tall the visible part of the timeline is.
    @State private var viewport: CGFloat = 0

    static let hourHeight: CGFloat = 56
    static let perMinute = hourHeight / 60
    static let gutter: CGFloat = 54

    private var time: UserTime { tracker.time }

    private var rules: CalendarDrag {
        CalendarDrag(
            time: time,
            isLocked: { [tracker] in tracker.isLocked($0) },
            isDayLocked: { [tracker] in tracker.rules?.isLocked($0) == true })
    }

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                TimelineView(.periodic(from: .now, by: 60)) { _ in
                    let now = AppClock.now
                    let segments = CalendarLayout.layout(tracker.entries(overlapping: [day]), days: [day], in: time, now: now)
                    GeometryReader { geometry in
                        let laneArea = geometry.size.width - Self.gutter - 8
                        ZStack(alignment: .topLeading) {
                            hours(now: now)
                            ForEach(segments) { segment in
                                // A lifted block stays, faded, where it was: removing it would end the drag it holds.
                                block(segment, laneArea: laneArea)
                                    .opacity(liftedId == segment.entry.id ? 0.35 : 1)
                            }
                            if let dragging, let preview = rules.preview(dragging) {
                                EntryBlock(segment: preview, tracker: tracker, lifted: true)
                                    .frame(width: laneArea, height: height(preview))
                                    .offset(x: Self.gutter, y: y(preview.startMinute))
                                    .allowsHitTesting(false)
                            }
                            if day == today {
                                nowLine(now, width: geometry.size.width)
                            }
                        }
                        .contentShape(Rectangle())
                        .onTapGesture { point in addHour(at: point) }
                    }
                    .frame(height: 24 * Self.hourHeight)
                }
                .padding(.vertical, 12)
            }
            // While a block is lifted, the finger moves it rather than the timeline.
            .scrollDisabled(dragging != nil)
            .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { viewport = $0 }
            .task(id: viewport > 0) {
                guard viewport > 0 else { return }
                // Opens on the day's first entry, or at 7:00. Today it keeps now in view too, and at the latest opens
                // an hour before it.
                let first = tracker.entries(overlapping: [day]).map { CalendarLayout.minuteOfDay(max($0.start, time.start(of: day)), in: time) }.min()
                var minute = first ?? 7 * 60
                if day == today {
                    let now = CalendarLayout.minuteOfDay(AppClock.now, in: time)
                    let shown = Double(viewport / Self.perMinute)
                    minute = min(max(minute, now - shown + 90), now - 60)
                }
                try? await Task.sleep(for: .milliseconds(50))
                proxy.scrollTo(max(0, min(23, Int(minute / 60))), anchor: .top)
            }
        }
        .sensoryFeedback(.selection, trigger: liftedId)
    }

    private func hours(now: Date) -> some View {
        // The now label takes the place of an hour it would cover.
        let nowMinute = day == today ? CalendarLayout.minuteOfDay(now, in: time) : nil
        return VStack(spacing: 0) {
            ForEach(0..<24, id: \.self) { hour in
                HStack(alignment: .top, spacing: 6) {
                    Text(hour == 0 || nowMinute.map({ abs(Double(hour * 60) - $0) <= 20 }) == true ? "" : time.clock(time.date(day, hours: hour, minutes: 0)))
                        .font(.caption2.monospacedDigit())
                        .foregroundStyle(.secondary)
                        .frame(width: Self.gutter - 6, alignment: .trailing)
                        .offset(y: -7)
                    // The hour, and the half hour fainter.
                    VStack { Divider() }
                        .frame(maxHeight: .infinity, alignment: .top)
                        .overlay {
                            Rectangle().fill(Color.primary.opacity(0.05)).frame(height: 1)
                        }
                }
                .frame(height: Self.hourHeight, alignment: .top)
                .id(hour)
            }
        }
    }

    private func block(_ segment: DaySegment, laneArea: CGFloat) -> some View {
        let laneWidth = laneArea / CGFloat(segment.lanes)
        return EntryBlock(segment: segment, tracker: tracker, lifted: false)
            .frame(width: max(laneWidth - 2, 20), height: height(segment))
            .offset(x: Self.gutter + CGFloat(segment.lane) * laneWidth, y: y(segment.startMinute))
            .onTapGesture {
                model.editing = EntryEditing(entry: segment.entry)
            }
            .gesture(
                LongPressGesture(minimumDuration: 0.35)
                    .sequenced(before: DragGesture(minimumDistance: 0))
                    .onChanged { value in
                        guard case .second(true, let drag) = value else { return }
                        if dragging == nil {
                            guard rules.canChange(segment.entry),
                                  let state = rules.begin(on: .entry(segment, edge: nil), day: day, minute: segment.startMinute) else { return }
                            dragging = state
                            liftedId = segment.entry.id
                        }
                        if let dragging, let drag {
                            // The block follows the finger, from where it was when lifted.
                            let minute = segment.startMinute + Double(drag.translation.height / Self.perMinute)
                            self.dragging = rules.move(dragging, to: day, minute: minute, distance: abs(drag.translation.height))
                        }
                    }
                    .onEnded { _ in
                        if let dragging {
                            finish(rules.finish(dragging), segments: [segment])
                        }
                        dragging = nil
                        liftedId = nil
                    })
    }

    private func nowLine(_ now: Date, width: CGFloat) -> some View {
        let y = y(CalendarLayout.minuteOfDay(now, in: time))
        // Now, in marigold: the colour of the middle bar of the mark, with its time in the hour column.
        return ZStack(alignment: .topLeading) {
            HStack(spacing: 0) {
                Circle().fill(Color.brand).frame(width: 8, height: 8)
                Rectangle().fill(Color.brand).frame(height: 2)
            }
            .frame(width: width - Self.gutter + 4)
            .offset(x: Self.gutter - 4, y: y - 4)
            Text(time.clock(now))
                .font(.caption2.weight(.semibold).monospacedDigit())
                .foregroundStyle(Color.ink)
                .padding(.horizontal, 5)
                .padding(.vertical, 1)
                .background(Color.brand, in: Capsule())
                .frame(width: Self.gutter - 4, alignment: .trailing)
                .offset(y: y - 8)
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    private func y(_ minute: Double) -> CGFloat {
        CGFloat(minute) * Self.perMinute
    }

    private func height(_ segment: DaySegment) -> CGFloat {
        max(CGFloat(segment.endMinute - segment.startMinute) * Self.perMinute - 1, 18)
    }

    private func addHour(at point: CGPoint) {
        guard point.x > Self.gutter else { return }
        if model.readOnlyOrganization != nil {
            model.notices.show(APIError.readOnlyMessage, kind: .error)
            return
        }
        let minute = min(CalendarLayout.minutesPerDay - CalendarDrag.step, max(0, Double(point.y / Self.perMinute)))
        guard let state = rules.begin(on: .empty, day: day, minute: minute) else {
            model.notices.show("This day is locked; nothing can be added to it.", kind: .error)
            return
        }
        finish(rules.finish(state), segments: [])
    }

    private func finish(_ outcome: CalendarDrag.Outcome, segments: [DaySegment]) {
        switch outcome {
        case .open(let id):
            if let entry = segments.first(where: { $0.entry.id == id })?.entry {
                model.editing = EntryEditing(entry: entry)
            }
        case .create(let range):
            model.editing = EntryEditing(newRange: range)
        case .save(let id, let start, let stop):
            guard let entry = segments.first(where: { $0.entry.id == id })?.entry else { return }
            var request = EntryRequest(entry)
            request.start = start
            request.stop = stop
            Task {
                do {
                    try await tracker.update(entry, to: request)
                } catch {
                    model.notices.show(error, fallback: "Couldn't move the time entry.")
                }
            }
        }
    }
}

/// An entry on the timeline, tinted in its project's colour and held by a hairline in it, as on the Mac. A split entry
/// takes the colour of its largest part, the project it mirrors; a thread along its bottom shows every part.
private struct EntryBlock: View {
    @Environment(\.colorScheme) private var colorScheme
    let segment: DaySegment
    let tracker: Tracker
    let lifted: Bool

    var body: some View {
        let entry = segment.entry
        let project = tracker.catalog.project(entry.projectId)
        let color = project.map { Color(hex: $0.color) } ?? Color.gray
        let minutes = segment.endMinute - segment.startMinute
        let parts = entry.isSplit ? entry.parts.sorted { $0.percent > $1.percent } : nil
        // Half an hour has room for the thread under the text; shorter split blocks show dots by the description.
        let threaded = parts != nil && minutes >= 30
        VStack(alignment: .leading, spacing: 1) {
            HStack(spacing: 3) {
                if entry.isRunning {
                    Circle().fill(Color.brand).frame(width: 5, height: 5)
                }
                if tracker.isLocked(entry.start) {
                    Image(systemName: "lock.fill").font(.system(size: 8)).foregroundStyle(.secondary)
                }
                Text(entry.description.isEmpty ? "No description" : entry.description)
                    .fontWeight(.semibold)
                    .foregroundStyle(entry.description.isEmpty ? .secondary : .primary)
                    .lineLimit(minutes >= (parts == nil ? 45 : 75) ? 2 : 1)
                if let parts, !threaded {
                    Spacer(minLength: 2)
                    SplitDots(catalog: tracker.catalog, parts: parts, size: 7)
                }
            }
            if minutes >= (parts == nil ? 30 : 45) {
                tracker.catalog.projectLine(project, parts: parts, in: colorScheme).lineLimit(1)
            }
            if minutes >= 60 {
                Text("\(tracker.time.clock(entry.start)) – \(entry.stop.map(tracker.time.clock) ?? "now")")
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
            }
        }
        .font(.caption2)
        .padding(.horizontal, 6)
        .padding(.top, minutes >= 30 ? 3 : 1)
        .padding(.bottom, threaded ? 10 : 1)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(color.opacity(lifted ? 0.32 : colorScheme == .dark ? 0.2 : 0.15), in: RoundedRectangle(cornerRadius: 6))
        .overlay {
            RoundedRectangle(cornerRadius: 6).strokeBorder(color.opacity(0.3), lineWidth: 1)
        }
        .overlay(alignment: .bottom) {
            if let parts, threaded {
                SplitThread(catalog: tracker.catalog, parts: parts).padding(.horizontal, 6).padding(.bottom, 5)
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 6))
        .shadow(color: .black.opacity(lifted ? 0.25 : 0), radius: 8, y: 4)
        .contentShape(Rectangle())
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibilityLabel(entry, project: project, parts: parts))
        .accessibilityHint("Opens the entry; press and hold to move it")
    }

    /// In parts, so older compilers type-check it in time.
    private func accessibilityLabel(_ entry: TimeEntry, project: Project?, parts: [SplitPart]?) -> String {
        let description = entry.description.isEmpty ? "No description" : entry.description
        let projects = parts.map { "split: \(tracker.catalog.describe($0))" } ?? project?.name ?? "no project"
        let stop = entry.stop.map(tracker.time.clock) ?? "now"
        let locked = tracker.isLocked(entry.start) ? ", locked" : ""
        return "\(description), \(projects), \(tracker.time.clock(entry.start)) – \(stop)\(locked)"
    }
}
