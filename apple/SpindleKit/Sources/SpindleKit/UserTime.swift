import Foundation

/// A calendar day, as the API writes it: `2026-09-29`.
public struct DayKey: Hashable, Comparable, Sendable, CustomStringConvertible {
    public let year: Int
    public let month: Int
    public let day: Int

    public init(year: Int, month: Int, day: Int) {
        self.year = year
        self.month = month
        self.day = day
    }

    /// Reads `yyyy-MM-dd`.
    public init?(_ text: String) {
        guard let match = text.wholeMatch(of: /(\d{4})-(\d{2})-(\d{2})/) else { return nil }
        self.init(year: Int(match.1)!, month: Int(match.2)!, day: Int(match.3)!)
    }

    public var description: String {
        String(format: "%04d-%02d-%02d", year, month, day)
    }

    public static func < (lhs: DayKey, rhs: DayKey) -> Bool {
        (lhs.year, lhs.month, lhs.day) < (rhs.year, rhs.month, rhs.day)
    }

    var components: DateComponents {
        DateComponents(year: year, month: month, day: day)
    }
}

/// Time in the person's own time zone and preferences, not the Mac's: a day, a week and a clock time mean the same
/// on every device they sign in from, as on the web app.
public struct UserTime: Sendable, Equatable {
    public let timeZone: TimeZone
    /// 0 for Sunday, 1 for Monday, and so on.
    public let weekStart: Int
    public let hour12: Bool
    public let durationFormat: Durations.Format
    public let calendar: Calendar

    public init(timeZone: TimeZone, weekStart: Int = 1, hour12: Bool = false, durationFormat: Durations.Format = .hms) {
        self.timeZone = timeZone
        self.weekStart = weekStart
        self.hour12 = hour12
        self.durationFormat = durationFormat
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        calendar.firstWeekday = weekStart + 1
        calendar.locale = Self.english
        self.calendar = calendar
    }

    /// The person's time, from their account.
    public init(_ me: Me) {
        self.init(
            timeZone: TimeZone(identifier: me.timeZone) ?? .current,
            weekStart: Int(me.preferences.weekStart),
            hour12: me.preferences.timeFormat == "12h",
            durationFormat: Durations.Format(rawValue: me.preferences.durationFormat) ?? .hms)
    }

    public static func == (lhs: UserTime, rhs: UserTime) -> Bool {
        (lhs.timeZone.identifier, lhs.weekStart, lhs.hour12, lhs.durationFormat)
            == (rhs.timeZone.identifier, rhs.weekStart, rhs.hour12, rhs.durationFormat)
    }

    // MARK: - Days

    /// The calendar day an instant falls on.
    public func day(of instant: Date) -> DayKey {
        let parts = calendar.dateComponents([.year, .month, .day], from: instant)
        return DayKey(year: parts.year!, month: parts.month!, day: parts.day!)
    }

    /// Midnight at the start of a day.
    public func start(of day: DayKey) -> Date {
        calendar.date(from: day.components)!
    }

    /// The instant for a day and a clock time. A time that daylight saving skips moves forward, as on the web app.
    public func date(_ day: DayKey, hours: Int, minutes: Int) -> Date {
        var parts = day.components
        parts.hour = hours
        parts.minute = minutes
        return calendar.date(from: parts)!
    }

    public func adding(_ days: Int, to day: DayKey) -> DayKey {
        self.day(of: calendar.date(byAdding: .day, value: days, to: date(day, hours: 12, minutes: 0))!)
    }

    /// Whole calendar days from one day to another.
    public func days(from: DayKey, to: DayKey) -> Int {
        calendar.dateComponents([.day], from: start(of: from), to: start(of: to)).day!
    }

    // MARK: - Weeks

    /// The first day of the week an instant falls in.
    public func weekStartDay(of instant: Date) -> DayKey {
        let today = day(of: instant)
        let weekday = calendar.component(.weekday, from: start(of: today)) - 1
        return adding(-((weekday - weekStart + 7) % 7), to: today)
    }

    /// The week `weeksBack` weeks before the current one, as [start, end). Counted in calendar days, so a week is
    /// always seven days even when daylight saving time makes it 167 or 169 hours long.
    public func week(weeksBack: Int, from now: Date) -> DateInterval {
        let first = adding(-7 * weeksBack, to: weekStartDay(of: now))
        return DateInterval(start: start(of: first), end: start(of: adding(7, to: first)))
    }

    // MARK: - Writing times

    /// A clock time: `09:05`, or `9:05 am` with a 12-hour clock.
    public func clock(_ instant: Date) -> String {
        format(instant, hour12 ? "h:mm a" : "HH:mm")
    }

    /// A clock time for a field: `09:05`, or `9:05 am` with a 12-hour clock.
    public func clockField(_ instant: Date) -> String {
        ClockTime.field(clockTime(of: instant), hour12: hour12)
    }

    // MARK: - Reading times

    /// The hours and minutes on the clock at an instant.
    public func clockTime(of instant: Date) -> ClockTime.Clock {
        let parts = calendar.dateComponents([.hour, .minute], from: instant)
        return (parts.hour!, parts.minute!)
    }

    /// Reads what was typed in a clock field: a clock time such as `930` or `9:30` (see ``ClockTime/parse(_:)``),
    /// `now`, or how long ago after a minus: `-15` is 15 minutes ago, `-1:30` or `-1.5` an hour and a half ago.
    public func readClockField(_ text: String, now: Date = AppClock.now) -> ClockTime.Clock? {
        let value = text.trimmingCharacters(in: .whitespaces).lowercased()
        if value == "now" {
            return clockTime(of: now)
        }
        if value.hasPrefix("-") {
            return Durations.parse(String(value.dropFirst())).map { clockTime(of: now.addingTimeInterval(-TimeInterval($0))) }
        }
        return ClockTime.parse(value)
    }

    /// When something started, from what was typed in a clock field: `-15` is 15 minutes before `now`, and a clock
    /// time is the latest instant at that time that isn't after `now`. Counted in whole minutes.
    public func readStartField(_ text: String, now: Date = AppClock.now) -> Date? {
        let value = text.trimmingCharacters(in: .whitespaces)
        if value.hasPrefix("-") {
            return Durations.parse(String(value.dropFirst())).map { seconds in
                let instant = now.timeIntervalSinceReferenceDate - TimeInterval(seconds)
                return Date(timeIntervalSinceReferenceDate: (instant / 60).rounded(.down) * 60)
            }
        }
        return readClockField(value, now: now).map { latest(at: $0, now: now) }
    }

    /// The latest instant at a clock time that isn't after `now`: today's, or yesterday's if today's is still to come.
    public func latest(at clock: ClockTime.Clock, now: Date) -> Date {
        let today = day(of: now)
        let start = date(today, hours: clock.hours, minutes: clock.minutes)
        return start > now ? date(adding(-1, to: today), hours: clock.hours, minutes: clock.minutes) : start
    }

    /// `Today`, `Yesterday`, `Mon, 22 Sep`, or with the year when it isn't this year.
    public func dayHeading(_ day: DayKey, now: Date) -> String {
        let today = self.day(of: now)
        if day == today {
            return "Today"
        }
        if day == adding(-1, to: today) {
            return "Yesterday"
        }
        return format(start(of: day), day.year == today.year ? "EEE, d MMM" : "EEE, d MMM yyyy")
    }

    /// A day written out, e.g. `1 September 2026`.
    public func longDay(_ day: DayKey) -> String {
        format(start(of: day), "d MMMM yyyy")
    }

    // MARK: - Date fields

    /// A day for a field, written the way the device's region writes dates, as its own date fields do: `30. 09. 2026.`,
    /// `30/09/2026` or `9/30/2026`. That's how people type them.
    public func dayField(_ day: DayKey, locale: Locale = .autoupdatingCurrent) -> String {
        DateFormatters.template(Self.dayFieldTemplate, calendar: calendar, timeZone: timeZone, locale: locale)
            .string(from: start(of: day))
    }

    private static let dayFieldTemplate = "yMd"

    /// Reads a day typed in a field: day, month and year in the region's order (or year first, as in `2026-09-30`),
    /// day and month in `near`'s year, or a day alone in `near`'s month. Nil for anything else, such as 31 June.
    public func parseDayField(_ text: String, near: DayKey, locale: Locale = .autoupdatingCurrent) -> DayKey? {
        var numbers: [(value: Int, digits: Int)] = []
        for match in text.matches(of: /\d+/) {
            // Too many digits for a number is no day either.
            guard let value = Int(match.output) else { return nil }
            numbers.append((value, match.output.count))
        }
        var order = Self.dayFieldOrder(locale)
        if numbers.first?.digits == 4 {
            order = [.year, .month, .day]
        }
        var parts = (year: near.year, month: near.month, day: near.day)
        switch numbers.count {
        case 1:
            parts.day = numbers[0].value
        case 2, 3:
            let fields = numbers.count == 3 ? order : order.filter { $0 != .year }
            for (field, number) in zip(fields, numbers) {
                switch field {
                case .year: parts.year = number.digits <= 2 ? 2000 + number.value : number.value
                case .month: parts.month = number.value
                case .day: parts.day = number.value
                }
            }
        default:
            return nil
        }
        let typed = DayKey(year: parts.year, month: parts.month, day: parts.day)
        // Calendar rolls 31 June over to 1 July; a day that doesn't come back the same doesn't exist.
        guard (1...12).contains(typed.month), (1...31).contains(typed.day),
              let date = calendar.date(from: typed.components), day(of: date) == typed else { return nil }
        return typed
    }

    private enum DayFieldPart { case day, month, year }

    /// The order the region writes a date's parts in, e.g. day, month, year in Croatia and month, day, year in the US.
    private static func dayFieldOrder(_ locale: Locale) -> [DayFieldPart] {
        let pattern = DateFormatter.dateFormat(fromTemplate: dayFieldTemplate, options: 0, locale: locale) ?? "d.M.y"
        let positions: [(DayFieldPart, String.Index)] = [(.day, "d"), (.month, "M"), (.year, "y")].compactMap { part, letter in
            pattern.firstIndex(of: Character(letter)).map { (part, $0) }
        }
        let order = positions.sorted { $0.1 < $1.1 }.map(\.0)
        return order.count == 3 ? order : [.day, .month, .year]
    }

    /// A duration in the person's chosen format; a running timer always counts in seconds.
    public func duration(_ seconds: TimeInterval, running: Bool = false) -> String {
        running ? Durations.clock(seconds) : Durations.format(seconds, as: durationFormat)
    }

    /// An instant written with a date pattern, in English and the person's time zone, e.g. `EEE` for "Mon".
    public func format(_ instant: Date, _ pattern: String) -> String {
        DateFormatters.pattern(pattern, calendar: calendar, timeZone: timeZone, locale: Self.english).string(from: instant)
    }

    private static let english = Locale(identifier: "en_GB")
}
