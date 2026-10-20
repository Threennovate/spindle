import SwiftUI
import SpindleKit

/// All visual choices for activity evidence, kept together for design review.
enum ActivityStyle {
    static let focusWidth: CGFloat = 44
    static let laneWidth: CGFloat = 36
    static let weekWidth: CGFloat = 5
    /// Room kept free at the trailing edge of each week column, so the strip never covers entries.
    static let weekReserve: CGFloat = weekWidth + 4
    static let inset: CGFloat = 5
    static let corner: CGFloat = 4
    /// Focus is the person's time, so it is drawn stronger than the agents' runs beside it.
    static let focusOpacity = 0.8
    static let runOpacity = 0.5
    static let hoverBoost = 0.15
    static let shareGap: CGFloat = 1
    static let minimumHeight: CGFloat = 3
    static let maximumLanes = 3
    static let tint = Color.primary.opacity(0.025)
    static let symbol = "sparkles"
    static let palette: [Color] = [.init(hex: "#497CC4"), .init(hex: "#9470C1"), .init(hex: "#BE698B"), .init(hex: "#C48245"),
                                    .init(hex: "#799643"), .init(hex: "#3B9A83"), .init(hex: "#399CB2"), .init(hex: "#BA6858")]

    static func color(_ key: String) -> Color {
        let hash = key.utf8.reduce(UInt64(14695981039346656037)) { ($0 ^ UInt64($1)) &* 1099511628211 }
        return palette[Int(hash % UInt64(palette.count))]
    }
}

/// Snapshot overrides exist only in debug builds and never change saved settings.
enum ActivitySnapshot {
    static var root: URL? {
        #if DEBUG
        return ProcessInfo.processInfo.environment["SPINDLE_SNAPSHOT_AGENT_LOGS"].map { URL(fileURLWithPath: $0) }
        #else
        return nil
        #endif
    }
    static var day: DayKey? {
        #if DEBUG
        return ProcessInfo.processInfo.environment["SPINDLE_SNAPSHOT_DAY"].flatMap(DayKey.init)
        #else
        return nil
        #endif
    }
    static var mode: CalendarMode? {
        #if DEBUG
        return ProcessInfo.processInfo.environment["SPINDLE_SNAPSHOT_CALENDAR_MODE"].flatMap(CalendarMode.init)
        #else
        return nil
        #endif
    }
}

/// The evidence beside a single day: Focus, then a lane per source, on a tinted band that carries the hour lines
/// on, so it reads as part of the day but apart from its entries.
struct ActivityColumn: View {
    let day: ActivityDay?
    let sources: [AgentSource]
    let time: UserTime
    let hourHeight: CGFloat

    static func width(_ sources: [AgentSource]) -> CGFloat {
        ActivityStyle.focusWidth + CGFloat(sources.count) * ActivityStyle.laneWidth
    }

    var body: some View {
        HStack(spacing: 0) {
            ActivityLane(day: day, source: nil, time: time, hourHeight: hourHeight)
            ForEach(sources, id: \.self) { source in
                ActivityLane(day: day, source: source, time: time, hourHeight: hourHeight)
            }
        }
        .frame(width: Self.width(sources), height: 24 * hourHeight)
        .background {
            Canvas { context, size in
                context.fill(Path(CGRect(origin: .zero, size: size)), with: .color(ActivityStyle.tint))
                for hour in 1..<24 {
                    let y = CGFloat(hour) * hourHeight
                    context.stroke(Path { $0.move(to: CGPoint(x: 0, y: y)); $0.addLine(to: CGPoint(x: size.width, y: y)) }, with: .color(.primary.opacity(0.08)))
                }
                context.stroke(Path { $0.move(to: .zero); $0.addLine(to: CGPoint(x: 0, y: size.height)) }, with: .color(.primary.opacity(0.1)))
            }
        }
    }
}

/// One lane of evidence: Focus blocks when `source` is nil, otherwise that source's agent runs. In a week column
/// it is an inert strip of Focus blocks.
struct ActivityLane: View {
    let day: ActivityDay?
    let source: AgentSource?
    let time: UserTime
    let hourHeight: CGFloat
    var interactive = true

    private var width: CGFloat {
        interactive ? (source == nil ? ActivityStyle.focusWidth : ActivityStyle.laneWidth) : ActivityStyle.weekWidth
    }
    private var inset: CGFloat { interactive ? ActivityStyle.inset : 0 }

    private struct Placed: Identifiable {
        let run: AgentRun
        var lane = 0
        var lanes = 1
        var id: String { run.id }
    }

    /// Runs that overlap sit side by side, up to a few; more share the last lane.
    private func placed(_ runs: [AgentRun]) -> [Placed] {
        var placed: [Placed] = [], cluster: [Placed] = [], ends: [Date] = []
        var clusterEnd = Date.distantPast
        func close() {
            placed += cluster.map { value in var value = value; value.lanes = ends.count; return value }
            cluster = []; ends = []
        }
        for run in runs.sorted(by: { $0.range.start < $1.range.start }) {
            var value = Placed(run: run)
            if value.run.range.start >= clusterEnd { close() }
            let end = max(run.range.end, run.range.start.addingTimeInterval(60))
            if let lane = ends.firstIndex(where: { $0 <= run.range.start }) { value.lane = lane; ends[lane] = end }
            else if ends.count < ActivityStyle.maximumLanes { value.lane = ends.count; ends.append(end) }
            else { value.lane = ends.count - 1; ends[value.lane] = max(ends[value.lane], end) }
            clusterEnd = max(clusterEnd, end)
            cluster.append(value)
        }
        close()
        return placed
    }

    private func frame(_ range: DateInterval, lane: Int = 0, lanes: Int = 1) -> CGRect {
        guard let day else { return .zero }
        let perMinute = hourHeight / 60
        let start = CalendarLayout.minuteOfDay(range.start, in: time)
        let end = range.end >= time.start(of: time.adding(1, to: day.day)) ? CalendarLayout.minutesPerDay : CalendarLayout.minuteOfDay(range.end, in: time)
        let column = (width - inset * 2) / CGFloat(lanes)
        return CGRect(x: inset + CGFloat(lane) * column, y: CGFloat(start) * perMinute,
                      width: max(1, column - (lanes > 1 ? 1 : 0)), height: max(ActivityStyle.minimumHeight, CGFloat(end - start) * perMinute))
    }

    var body: some View {
        ZStack(alignment: .topLeading) {
            if let day {
                if let source {
                    ForEach(placed(day.runs.filter { $0.source == source })) { value in
                        let rect = frame(value.run.range, lane: value.lane, lanes: value.lanes)
                        RunBar(run: value.run, day: day, time: time)
                            .frame(width: rect.width, height: rect.height)
                            .offset(x: rect.minX, y: rect.minY)
                    }
                } else {
                    ForEach(day.focusBlocks) { block in
                        let rect = frame(block.range)
                        FocusBar(block: block, height: rect.height, day: day, time: time, interactive: interactive)
                            .frame(width: rect.width, height: rect.height)
                            .offset(x: rect.minX, y: rect.minY)
                    }
                }
            }
        }
        .frame(width: width, height: 24 * hourHeight, alignment: .topLeading)
    }
}

/// A Focus block, banded by how its time divided between repositories, in the order each first had attention.
private struct FocusBar: View {
    let block: FocusBlock
    let height: CGFloat
    let day: ActivityDay
    let time: UserTime
    let interactive: Bool
    @State private var presented = false
    @State private var hovering = false

    var body: some View {
        let gap = height > CGFloat(block.shares.count) * 4 ? ActivityStyle.shareGap : 0
        let available = height - gap * CGFloat(max(0, block.shares.count - 1))
        VStack(spacing: gap) {
            ForEach(block.shares) { share in
                Rectangle()
                    .fill(ActivityStyle.color(share.repository.key).opacity(ActivityStyle.focusOpacity + (hovering ? ActivityStyle.hoverBoost : 0)))
                    .frame(height: max(1, available * share.duration / max(1, block.range.duration)))
            }
        }
        .frame(height: height, alignment: .top)
        .clipShape(RoundedRectangle(cornerRadius: interactive ? ActivityStyle.corner : ActivityStyle.weekWidth / 2))
        .contentShape(Rectangle())
        .activityButton(interactive: interactive, presented: $presented, hovering: $hovering)
        .popover(isPresented: $presented, arrowEdge: .leading) {
            ActivityDetails(heading: "Focus", range: block.range, shares: block.shares, switches: block.switches, day: day, time: time)
        }
        .accessibilityLabel("Focus, \(time.clock(block.range.start)) to \(time.clock(block.range.end)), "
            + block.shares.map { "\($0.repository.name) \(Durations.hoursMinutes($0.duration))" }.joined(separator: ", "))
    }
}

/// One session's run of agent activity.
private struct RunBar: View {
    let run: AgentRun
    let day: ActivityDay
    let time: UserTime
    @State private var presented = false
    @State private var hovering = false

    var body: some View {
        RoundedRectangle(cornerRadius: ActivityStyle.corner)
            .fill(ActivityStyle.color(run.repository.key).opacity(ActivityStyle.runOpacity + (hovering ? ActivityStyle.hoverBoost : 0)))
            .contentShape(Rectangle())
            .activityButton(interactive: true, presented: $presented, hovering: $hovering)
            .popover(isPresented: $presented, arrowEdge: .leading) {
                ActivityDetails(heading: run.source.label, range: run.range,
                                shares: [FocusShare(repository: run.repository, duration: run.range.duration, sessionIDs: [run.sessionID])],
                                switches: nil, day: day, time: time)
            }
            .accessibilityLabel("\(run.source.label), \(run.repository.name), \(time.clock(run.range.start)) to \(time.clock(run.range.end)), \(Durations.hoursMinutes(run.range.duration))")
    }
}

private extension View {
    func activityButton(interactive: Bool, presented: Binding<Bool>, hovering: Binding<Bool>) -> some View {
        self
            .onTapGesture { if interactive { presented.wrappedValue = true } }
            .onHover { inside in
                guard interactive else { return }
                hovering.wrappedValue = inside
                (inside ? NSCursor.pointingHand : NSCursor.arrow).set()
            }
            .accessibilityElement(children: .ignore)
            .accessibilityAddTraits(interactive ? .isButton : [])
            .accessibilityAction { if interactive { presented.wrappedValue = true } }
            .allowsHitTesting(interactive)
    }
}

/// What a Focus block or a run consists of. Also rendered as a standalone snapshot panel, because native popover
/// materials cannot always be captured.
struct ActivityDetails: View {
    let heading: String
    let range: DateInterval
    let shares: [FocusShare]
    /// Only for Focus: how often attention moved between repositories.
    let switches: Int?
    let day: ActivityDay
    let time: UserTime

    private static let sessionLimit = 6

    private var sessions: [AgentSession] {
        let ids = shares.reduce(into: Set<String>()) { $0.formUnion($1.sessionIDs) }
        return day.sessions.filter { ids.contains($0.id) }
            .sorted { (prompts(in: $0), $0.title) > (prompts(in: $1), $1.title) }
    }

    private func prompts(in session: AgentSession) -> Int {
        session.events.filter { $0.isTouch && range.contains($0.date) }.count
    }

    private func agentTime(of session: AgentSession) -> TimeInterval {
        day.runs.filter { $0.sessionID == session.id }.reduce(0) { total, run in
            total + (run.range.intersection(with: range)?.duration ?? 0)
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Label(heading, systemImage: ActivityStyle.symbol)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text("\(time.clock(range.start)) – \(time.clock(range.end))")
                    .font(.headline.monospacedDigit())
                Text([Durations.hoursMinutes(range.duration), switches.flatMap { $0 > 0 ? "\($0) switches" : nil }]
                    .compactMap { $0 }.joined(separator: " · "))
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
            VStack(alignment: .leading, spacing: 8) {
                ForEach(shares) { share in
                    shareRow(share)
                }
            }
            let sessions = sessions
            if !sessions.isEmpty {
                Divider()
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(sessions.prefix(Self.sessionLimit)) { session in
                        sessionRow(session)
                    }
                    if sessions.count > Self.sessionLimit {
                        Text("\(sessions.count - Self.sessionLimit) more sessions")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            }
        }
        .padding(16)
        .frame(width: 320, alignment: .leading)
    }

    private func shareRow(_ share: FocusShare) -> some View {
        let branches = day.sessions.filter { share.sessionIDs.contains($0.id) }.flatMap(\.branches)
        return HStack(alignment: .firstTextBaseline, spacing: 8) {
            RoundedRectangle(cornerRadius: 2)
                .fill(ActivityStyle.color(share.repository.key))
                .frame(width: 9, height: 9)
            VStack(alignment: .leading, spacing: 1) {
                Text(share.repository.name).fontWeight(.medium)
                if !share.repository.isRepository {
                    Text("Not a repository").font(.caption).foregroundStyle(.secondary)
                } else if !branches.isEmpty {
                    Label(Set(branches).sorted().joined(separator: ", "), systemImage: "arrow.triangle.branch")
                        .font(.caption.monospaced())
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .textSelection(.enabled)
                }
            }
            Spacer(minLength: 8)
            if shares.count > 1 {
                Text(Durations.hoursMinutes(share.duration))
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func sessionRow(_ session: AgentSession) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text(session.source.shortLabel)
                .font(.caption)
                .foregroundStyle(.secondary)
                .frame(width: 42, alignment: .leading)
            VStack(alignment: .leading, spacing: 1) {
                Text(session.title.isEmpty ? "Untitled session" : session.title)
                    .lineLimit(2)
                let prompts = prompts(in: session)
                Text([prompts > 0 ? "\(prompts) \(prompts == 1 ? "prompt" : "prompts")" : nil,
                      "agent \(Durations.hoursMinutes(agentTime(of: session)))"].compactMap { $0 }.joined(separator: " · "))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }
}
