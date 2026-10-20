import Foundation

/// The part of an entry that falls on one day, in minutes of that day's wall clock.
public struct DaySegment: Identifiable, Equatable, Sendable {
    public let entry: TimeEntry
    public let day: DayKey
    public var startMinute: Double
    public var endMinute: Double
    /// The entry continues from the day before, or into the next one.
    public let continuesBefore: Bool
    public let continuesAfter: Bool
    /// Where it sits among overlapping segments: this segment's lane out of `lanes`.
    public var lane = 0
    public var lanes = 1

    public var id: String { "\(entry.id)|\(day)" }

    public init(entry: TimeEntry, day: DayKey, startMinute: Double, endMinute: Double, continuesBefore: Bool = false, continuesAfter: Bool = false) {
        self.entry = entry
        self.day = day
        self.startMinute = startMinute
        self.endMinute = endMinute
        self.continuesBefore = continuesBefore
        self.continuesAfter = continuesAfter
    }
}

/// Where entries go on a day or week calendar, as on the web app.
public enum CalendarLayout {
    public static let minutesPerDay = 24.0 * 60

    /// The wall-clock minute of the day (0–1440) of an instant.
    public static func minuteOfDay(_ instant: Date, in time: UserTime) -> Double {
        let parts = time.calendar.dateComponents([.hour, .minute, .second], from: instant)
        return Double(parts.hour! * 60 + parts.minute!) + Double(parts.second!) / 60
    }

    /// The instant at a minute of a day; minute 1440 is midnight after it.
    public static func instant(_ day: DayKey, minute: Double, in time: UserTime) -> Date {
        if minute >= minutesPerDay {
            return time.start(of: time.adding(1, to: day))
        }
        let whole = Int(minute.rounded(.down))
        return time.date(day, hours: whole / 60, minutes: whole % 60)
    }

    /// Splits entries into a segment per day they touch (an entry past midnight shows on both days), and places
    /// overlapping segments side by side, as calendars show double bookings.
    public static func layout(_ entries: [TimeEntry], days: [DayKey], in time: UserTime, now: Date) -> [DaySegment] {
        days.flatMap { day in
            let dayStart = time.start(of: day)
            let dayEnd = time.start(of: time.adding(1, to: day))
            let segments = entries.compactMap { entry -> DaySegment? in
                let end = entry.stop ?? now
                guard entry.start < dayEnd, end > dayStart else { return nil }
                return DaySegment(
                    entry: entry,
                    day: day,
                    startMinute: entry.start <= dayStart ? 0 : minuteOfDay(entry.start, in: time),
                    endMinute: end >= dayEnd ? minutesPerDay : minuteOfDay(end, in: time),
                    continuesBefore: entry.start < dayStart,
                    continuesAfter: end > dayEnd)
            }
            return assignLanes(segments)
        }
    }

    /// Segments that overlap, directly or through each other, share their column's width.
    private static func assignLanes(_ segments: [DaySegment]) -> [DaySegment] {
        let sorted = segments.sorted { ($0.startMinute, -$0.endMinute) < ($1.startMinute, -$1.endMinute) }
        var placed: [DaySegment] = []
        var cluster: [DaySegment] = []
        var laneEnds: [Double] = []
        var clusterEnd = -1.0

        func closeCluster() {
            placed += cluster.map { segment in
                var segment = segment
                segment.lanes = laneEnds.count
                return segment
            }
            cluster = []
            laneEnds = []
        }

        for var segment in sorted {
            if segment.startMinute >= clusterEnd && !cluster.isEmpty {
                closeCluster()
            }
            // A segment of no length still needs a sliver of room.
            let end = max(segment.endMinute, segment.startMinute + 1)
            if let lane = laneEnds.firstIndex(where: { $0 <= segment.startMinute }) {
                segment.lane = lane
                laneEnds[lane] = end
            } else {
                segment.lane = laneEnds.count
                laneEnds.append(end)
            }
            cluster.append(segment)
            clusterEnd = max(clusterEnd, end)
        }
        closeCluster()
        return placed
    }

    /// Rounds minutes to the nearest step, e.g. 15 minutes, within the day.
    public static func snap(_ minutes: Double, step: Double) -> Double {
        min(minutesPerDay, max(0, (minutes / step).rounded() * step))
    }

    /// The seven days of the week that contains `day`.
    public static func week(containing day: DayKey, in time: UserTime) -> [DayKey] {
        let first = time.weekStartDay(of: time.date(day, hours: 12, minutes: 0))
        return (0..<7).map { time.adding($0, to: first) }
    }

    /// "22 – 28 Sep 2026", "29 Sep – 5 Oct 2026", or one day in full.
    public static func label(_ days: [DayKey], in time: UserTime) -> String {
        guard let first = days.first, let last = days.last else { return "" }
        func format(_ day: DayKey, _ pattern: String) -> String {
            time.format(time.date(day, hours: 12, minutes: 0), pattern)
        }
        if first == last {
            return format(first, "EEEE d MMMM yyyy")
        }
        let sameMonth = (first.year, first.month) == (last.year, last.month)
        return "\(format(first, sameMonth ? "d" : "d MMM")) – \(format(last, "d MMM yyyy"))"
    }
}
