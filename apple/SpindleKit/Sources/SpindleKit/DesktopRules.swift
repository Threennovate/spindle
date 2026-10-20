import Foundation

/// Pomodoro: focus for a set time, then a short break. While the timer runs, each full focus interval ends with a
/// reminder; with auto-stop, the timer stops there and a break counts down. The same rules as the web app.
public enum Pomodoro {
    /// How many focus intervals have fully passed since the timer started.
    public static func intervalsDone(elapsed: TimeInterval, focusMinutes: Int) -> Int {
        max(0, Int((elapsed / (Double(focusMinutes) * 60)).rounded(.down)))
    }

    /// Time left in the current focus interval.
    public static func focusLeft(elapsed: TimeInterval, focusMinutes: Int) -> TimeInterval {
        let focus = Double(focusMinutes) * 60
        return focus - max(0, elapsed).truncatingRemainder(dividingBy: focus)
    }

    /// "12:05": minutes and seconds of a countdown.
    public static func countdown(_ seconds: TimeInterval) -> String {
        let whole = max(0, Int(seconds.rounded(.up)))
        return String(format: "%d:%02d", whole / 60, whole % 60)
    }
}

/// When to remind someone who isn't tracking: on their working days, between two times, every so often.
public struct ReminderSchedule: Codable, Hashable, Sendable {
    /// 0 for Sunday, 1 for Monday, and so on.
    public var days: Set<Int>
    /// Minutes after midnight.
    public var from: Int
    public var to: Int
    public var everyMinutes: Int

    public init(days: Set<Int> = [1, 2, 3, 4, 5], from: Int = 9 * 60, to: Int = 17 * 60, everyMinutes: Int = 30) {
        self.days = days
        self.from = from
        self.to = to
        self.everyMinutes = everyMinutes
    }

    /// Whether this moment is in working hours, in the person's time zone.
    public func isWorkingTime(_ now: Date, in time: UserTime) -> Bool {
        let parts = time.calendar.dateComponents([.weekday, .hour, .minute], from: now)
        let minute = parts.hour! * 60 + parts.minute!
        return days.contains(parts.weekday! - 1) && minute >= from && minute < to
    }

    /// How long after working hours end the day's review may still be offered.
    public static let dayEndWindow: TimeInterval = 2 * 3600

    /// The working day that just ended, when this moment is within `dayEndWindow` after its working hours: the
    /// moment to offer the day's review once.
    public func endedWorkingDay(at now: Date, in time: UserTime) -> DayKey? {
        let parts = time.calendar.dateComponents([.weekday, .hour, .minute], from: now)
        let minute = parts.hour! * 60 + parts.minute!
        guard days.contains(parts.weekday! - 1), minute >= to, Double(minute - to) * 60 < Self.dayEndWindow else { return nil }
        return time.day(of: now)
    }

    /// Whether to remind now: in working hours, nothing running, the person at the computer, and the last
    /// reminder (or the last timer stopping) long enough ago.
    public func shouldRemind(now: Date, in time: UserTime, isRunning: Bool, lastRemindedOrStopped: Date?, idleSeconds: TimeInterval) -> Bool {
        guard !isRunning, isWorkingTime(now, in: time), idleSeconds < 5 * 60 else { return false }
        guard let last = lastRemindedOrStopped else { return true }
        return now.timeIntervalSince(last) >= Double(everyMinutes) * 60
    }
}
