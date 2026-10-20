import Foundation

/// A day's entries, for lists grouped by day.
public struct DayGroup: Identifiable, Sendable {
    public let day: DayKey
    public let entries: [TimeEntry]

    public var id: DayKey { day }

    public func totalSeconds(at now: Date) -> TimeInterval {
        entries.reduce(0) { $0 + $1.seconds(at: now) }
    }

    /// Groups entries by the day they start on, in the person's time zone, newest day and entry first.
    public static func grouping(_ entries: [TimeEntry], in time: UserTime) -> [DayGroup] {
        Dictionary(grouping: entries) { time.day(of: $0.start) }
            .map { DayGroup(day: $0.key, entries: $0.value.sorted { ($0.start, $0.id) > ($1.start, $1.id) }) }
            .sorted { $0.day > $1.day }
    }
}
