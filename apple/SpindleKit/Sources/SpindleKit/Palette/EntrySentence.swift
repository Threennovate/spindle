import Foundation

/// One line that says what someone works on: "Review PR @Acme #review $ since 9:40", read the same as on the web
/// (`features/command/sentence.ts`).
///
/// Free text is the description. Projects and tags come as chips the person picked from a list, never from the text
/// itself, so "Fix #482" stays as typed. `$` on its own makes the entry billable. A time is read only at the end of
/// the line and only in explicit forms, so a description that merely contains numbers stays text.
///
/// Both apps run the cases in `tests/vectors/entry-sentence.json`, so a line means the same everywhere.
public enum EntrySentence {
    /// What was picked from the lists, shown as chips before the text.
    public enum Chip: Hashable, Sendable {
        case project(projectID: String, taskID: String?)
        case client(String)
        case tag(String)
        case billable
    }

    public struct Options: Sendable, Equatable {
        /// With a 12-hour clock, a bare `3` since the morning reads as 15:00 when that is the more recent.
        public var hour12: Bool
        /// The person's date format, e.g. `MM/dd/yyyy`: it decides whether `10/3` is 10 March or 3 October.
        public var dateFormat: String

        public init(hour12: Bool = false, dateFormat: String = "dd.MM.yyyy") {
            self.hour12 = hour12
            self.dateFormat = dateFormat
        }
    }

    /// A day named before the time: `yesterday`, `mon`, `3.10.`, `2026-10-03`.
    public enum DayRef: Equatable, Sendable {
        case today
        case yesterday
        /// 0 is Sunday.
        case weekday(Int)
        case date(year: Int?, month: Int, day: Int)
    }

    /// A clock time as typed, and whether it said am or pm.
    public struct TypedClock: Equatable, Sendable {
        public var hours: Int
        public var minutes: Int
        public var meridiem: Bool

        var totalMinutes: Int { hours * 60 + minutes }
    }

    public enum Time: Equatable, Sendable {
        /// `25m ago`, `-25`: running since then.
        case ago(seconds: Int)
        /// `since 9:40`, `from 9:40`: running since then.
        case since(day: DayRef?, at: TypedClock)
        /// `9:00-10:30`: a finished entry.
        case range(day: DayRef?, from: TypedClock, to: TypedClock)
        /// `for 1h30`, `45m`: finished, ending now (or on another day, after its last entry).
        case duration(day: DayRef?, seconds: Int)

        func with(day: DayRef) -> Time {
            switch self {
            case .ago: self
            case let .since(_, at): .since(day: day, at: at)
            case let .range(_, from, to): .range(day: day, from: from, to: to)
            case let .duration(_, seconds): .duration(day: day, seconds: seconds)
            }
        }
    }

    public struct Reading: Equatable, Sendable {
        public var description: String
        public var projectID: String?
        public var taskID: String?
        public var clientID: String?
        public var tagIDs: [String]
        public var billable: Bool
        public var time: Time?
        /// The words read as the time, e.g. `yesterday 9:00-10:30`, so the preview can show what was understood.
        public var timeText: String
    }

    /// Reads a line and the chips before it.
    public static func read(_ text: String, chips: [Chip] = [], options: Options = Options()) -> Reading {
        let tokens = text.split(whereSeparator: \.isWhitespace).map(String.init)
        let found = readTime(tokens, options: options)
        let rest = found.map { Array(tokens[..<$0.index]) } ?? tokens

        var project: (String, String?)?
        var client: String?
        var tags: [String] = []
        var billable = rest.contains("$")
        for chip in chips {
            switch chip {
            case let .project(projectID, taskID): project = (projectID, taskID)
            case let .client(id): client = id
            case let .tag(id): if !tags.contains(id) { tags.append(id) }
            case .billable: billable = true
            }
        }
        return Reading(
            description: rest.filter { $0 != "$" }.joined(separator: " "),
            projectID: project?.0,
            taskID: project?.1,
            clientID: client,
            tagIDs: tags,
            billable: billable,
            time: found?.time,
            timeText: found.map { tokens[$0.index...].joined(separator: " ") } ?? "")
    }

    // MARK: - The time at the end

    private static let weekdays: [String: Int] = [
        "sun": 0, "sunday": 0, "mon": 1, "monday": 1, "tue": 2, "tues": 2, "tuesday": 2, "wed": 3, "wednesday": 3,
        "thu": 4, "thur": 4, "thurs": 4, "thursday": 4, "fri": 5, "friday": 5, "sat": 6, "saturday": 6,
    ]

    /// The time at the end of the tokens, and where it begins. The longest reading wins, e.g. a day before a range.
    private static func readTime(_ tokens: [String], options: Options) -> (index: Int, time: Time)? {
        let lower = tokens.map { $0.lowercased() }
        let n = lower.count
        guard let last = lower.last else { return nil }

        // Times without a day: "25m ago", "-25"
        if last == "ago" {
            for width in [2, 1] where n - 1 - width >= 0 {
                if let seconds = readDuration(Array(lower[(n - 1 - width)..<(n - 1)]), needsUnit: false) {
                    return (n - 1 - width, .ago(seconds: seconds))
                }
            }
            return nil
        }
        if last.wholeMatch(of: /[-−]\d.*/) != nil {
            return readDuration([String(last.dropFirst())], needsUnit: false).map { (n - 1, .ago(seconds: $0)) }
        }

        // Times that may have a day before them
        guard let timed = readDayTime(lower, isDay: { readDay($0, options: options) != nil }) else { return nil }
        if timed.index > 0, let day = readDay(lower[timed.index - 1], options: options) {
            return (timed.index - 1, timed.time.with(day: day))
        }
        return timed
    }

    private static func readDayTime(_ lower: [String], isDay: (String) -> Bool) -> (index: Int, time: Time)? {
        let n = lower.count
        let last = lower[n - 1]
        // A day or `from` before a range says it is a time, so "yesterday 9-10" is one while "Sprint 1-2" stays text.
        func saysTime(_ index: Int) -> Bool { index > 0 && (lower[index - 1] == "from" || isDay(lower[index - 1])) }

        // "9:00-10:30", "9-10:30pm", "9 - 10:30", "from 9 to 10:30", "yesterday 9-10"
        var range: (index: Int, from: TypedClock, to: TypedClock)?
        if let dash = last.wholeMatch(of: /([^-–]+)[-–]([^-–]+)/) {
            range = readRange(String(dash.1), String(dash.2), saysTime: saysTime(n - 1)).map { (n - 1, $0.from, $0.to) }
        } else if n >= 3, lower[n - 2] == "-" || lower[n - 2] == "–" {
            range = readRange(lower[n - 3], last, saysTime: saysTime(n - 3)).map { (n - 3, $0.from, $0.to) }
        } else if n >= 4, lower[n - 2] == "to", lower[n - 4] == "from" {
            range = readRange(lower[n - 3], last, saysTime: true).map { (n - 4, $0.from, $0.to) }
        }
        if let range {
            let index = lower[range.index] != "from" && range.index > 0 && lower[range.index - 1] == "from" ? range.index - 1 : range.index
            return (index, .range(day: nil, from: range.from, to: range.to))
        }

        // "since 9:40", "from 9:40"
        if n >= 2, lower[n - 2] == "since" || lower[n - 2] == "from", let at = readClock(last) {
            return (n - 2, .since(day: nil, at: at))
        }

        // "for 1h30", "for 1h 30m", "for 90"
        for width in [2, 1] where n - 1 - width >= 0 && lower[n - 1 - width] == "for" {
            if let seconds = readDuration(Array(lower[(n - width)...]), needsUnit: false) {
                return (n - 1 - width, .duration(day: nil, seconds: seconds))
            }
        }
        // "45m", "1h30", "1h 30m": only with a unit, as a bare number is too often part of the description.
        for width in [2, 1] where n - width >= 0 {
            if let seconds = readDuration(Array(lower[(n - width)...]), needsUnit: true) {
                return (n - width, .duration(day: nil, seconds: seconds))
            }
        }
        return nil
    }

    /// A clock time as typed, e.g. `9`, `9:40`, `940` or `9:40pm`.
    private static func readClock(_ token: String) -> TypedClock? {
        ClockTime.parse(token).map { TypedClock(hours: $0.hours, minutes: $0.minutes, meridiem: token.contains(/[ap]\.?m?\.?$/)) }
    }

    /// Both ends of a range. Unless the words before it say it is a time, at least one end has to look like a clock time
    /// (`9:00`, `9.30`, `9h`, `9pm`), so "2025-2026" or "Sprint 1-2" stay text. A bare end takes the other end's am or pm
    /// when that keeps the range in order.
    private static func readRange(_ fromText: String, _ toText: String, saysTime: Bool) -> (from: TypedClock, to: TypedClock)? {
        func looksLikeClock(_ text: String) -> Bool { text.contains(/[:.h]|[ap]\.?m?\.?$/) }
        guard saysTime || looksLikeClock(fromText) || looksLikeClock(toText), var from = readClock(fromText), var to = readClock(toText) else {
            return nil
        }

        // "9-10:30pm" is 21:00 to 22:30, and "11-1pm" 11:00 to 13:00.
        if to.meridiem, !from.meridiem, from.hours <= 12 {
            let sameHalf = TypedClock(hours: from.hours % 12 + (to.hours >= 12 ? 12 : 0), minutes: from.minutes, meridiem: true)
            from = sameHalf.totalMinutes < to.totalMinutes ? sameHalf : TypedClock(hours: from.hours, minutes: from.minutes, meridiem: true)
        } else if from.meridiem, !to.meridiem, to.hours <= 12 {
            let sameHalf = TypedClock(hours: to.hours % 12 + (from.hours >= 12 ? 12 : 0), minutes: to.minutes, meridiem: true)
            to = sameHalf.totalMinutes > from.totalMinutes ? sameHalf : TypedClock(hours: to.hours, minutes: to.minutes, meridiem: true)
        }
        return from.totalMinutes == to.totalMinutes ? nil : (from, to)
    }

    private static let units: [String: String] = [
        "h": "h", "hr": "h", "hrs": "h", "hour": "h", "hours": "h", "m": "m", "min": "m", "mins": "m", "minute": "m", "minutes": "m",
    ]

    /// A duration in one or two tokens: `45m`, `25 min`, `1h30`, `1h 30m`, `1.5h`, `1:30`, `90`. With `needsUnit`, only
    /// forms with hours or minutes written out count. Seconds, or nil when it is not a duration or is nothing.
    private static func readDuration(_ tokens: [String], needsUnit: Bool) -> Int? {
        var text: String
        if tokens.count == 2 {
            let (first, second) = (tokens[0], tokens[1])
            if let unit = units[second], first.wholeMatch(of: /\d+(?:[.,]\d+)?/) != nil {
                text = first + unit
            } else if first.wholeMatch(of: /\d+(?:[.,]\d+)?h/) != nil, second.wholeMatch(of: /\d{1,2}(?:m|min)?/) != nil {
                text = first + second
            } else {
                return nil
            }
        } else {
            text = tokens[0]
            if let match = text.wholeMatch(of: /(\d+(?:[.,]\d+)?)([a-z]+)/), let unit = units[String(match.2)] {
                text = String(match.1) + unit
            }
        }

        let seconds: Int?
        if let hours = text.wholeMatch(of: /(\d+(?:[.,]\d+)?)h(?:(\d{1,2})(?:m|min)?)?/) {
            let whole = Double(hours.1.replacingOccurrences(of: ",", with: ".")) ?? 0
            seconds = Durations.seconds(whole * 3600 + (hours.2.flatMap { Double($0) } ?? 0) * 60)
        } else if let minutes = text.wholeMatch(of: /(\d+)(?:m|min)/) {
            seconds = Durations.seconds((Double(minutes.1) ?? 0) * 60)
        } else {
            seconds = needsUnit ? nil : Durations.parse(text)
        }
        return seconds.flatMap { $0 > 0 ? $0 : nil }
    }

    /// A day as typed: `yesterday`, `mon`, `3.10.`, `10/3` (in the person's date order) or `2026-10-03`.
    public static func readDay(_ token: String, options: Options = Options()) -> DayRef? {
        switch token {
        case "today": return .today
        case "yesterday": return .yesterday
        default: break
        }
        if let weekday = weekdays[token] { return .weekday(weekday) }

        var year: Int?
        let month: Int
        let day: Int
        if let iso = token.wholeMatch(of: /(\d{4})-(\d{1,2})-(\d{1,2})/) {
            (year, month, day) = (Int(iso.1)!, Int(iso.2)!, Int(iso.3)!)
        } else if let dotted = token.wholeMatch(of: /(\d{1,2})\.(\d{1,2})\.(\d{2}|\d{4})?/) {
            // Dots are always day first: 3.10. is 3 October.
            (day, month) = (Int(dotted.1)!, Int(dotted.2)!)
            year = dotted.3.map { fullYear(String($0)) }
        } else if let slashed = token.wholeMatch(of: /(\d{1,2})\/(\d{1,2})(?:\/(\d{2}|\d{4}))?/) {
            let monthFirst = options.dateFormat.hasPrefix("M")
            (month, day) = monthFirst ? (Int(slashed.1)!, Int(slashed.2)!) : (Int(slashed.2)!, Int(slashed.1)!)
            year = slashed.3.map { fullYear(String($0)) }
        } else {
            return nil
        }
        guard (1...12).contains(month), day >= 1, day <= daysIn(year: year ?? 2000, month: month) else { return nil }
        return .date(year: year, month: month, day: day)
    }

    private static func fullYear(_ text: String) -> Int {
        text.count == 2 ? 2000 + Int(text)! : Int(text)!
    }

    private static func daysIn(year: Int, month: Int) -> Int {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        let first = calendar.date(from: DateComponents(year: year, month: month, day: 1))!
        return calendar.range(of: .day, in: .month, for: first)!.count
    }

    // MARK: - Resolving to instants

    /// When the line's entry starts and stops; `stop` is nil while it runs.
    public struct Span: Equatable, Sendable {
        public var start: Date
        public var stop: Date?
    }

    private static let defaultStartHour = 9

    /// The day a day reference points to: the latest such day that is not after today.
    public static func resolve(_ ref: DayRef, today: DayKey, in time: UserTime) -> DayKey {
        switch ref {
        case .today:
            return today
        case .yesterday:
            return time.adding(-1, to: today)
        case let .weekday(weekday):
            let current = time.calendar.component(.weekday, from: time.date(today, hours: 12, minutes: 0)) - 1
            return time.adding(-((current - weekday + 7) % 7), to: today)
        case let .date(year, month, day):
            if let year { return DayKey(year: year, month: month, day: day) }
            let thisYear = DayKey(year: today.year, month: month, day: day)
            return thisYear <= today ? thisYear : DayKey(year: today.year - 1, month: month, day: day)
        }
    }

    /// When the entry starts and stops. Without a time, it starts now. `dayEnds` tells when a day's entries end, to
    /// place a duration on another day after them (or at 9:00).
    public static func resolve(_ time: Time?, now: Date, in userTime: UserTime, dayEnds: (DayKey) -> [Date] = { _ in [] }) -> Span {
        let today = userTime.day(of: now)
        let thisMinute = Date(timeIntervalSince1970: (now.timeIntervalSince1970 / 60).rounded(.down) * 60)

        guard let time else { return Span(start: now, stop: nil) }
        switch time {
        case let .ago(seconds):
            let start = ((now.timeIntervalSince1970 - TimeInterval(seconds)) / 60).rounded(.down) * 60
            return Span(start: Date(timeIntervalSince1970: start), stop: nil)
        case let .since(day, at):
            let start = day.map { atClock(resolve($0, today: today, in: userTime), at, now: now, in: userTime) } ?? latestStart(at, now: now, in: userTime)
            return Span(start: start, stop: nil)
        case let .range(day, from, to):
            let start = day.map { atClock(resolve($0, today: today, in: userTime), from, now: now, in: userTime) } ?? latestStart(from, now: now, in: userTime)
            let startDay = userTime.day(of: start)
            var stop = userTime.date(startDay, hours: to.hours, minutes: to.minutes)
            if stop <= start {
                // "9-5" on a 12-hour clock ends at 17:00; otherwise the range goes past midnight.
                let afternoon = userTime.hour12 && !to.meridiem && to.hours < 12 ? userTime.date(startDay, hours: to.hours + 12, minutes: to.minutes) : nil
                if let afternoon, afternoon > start {
                    stop = afternoon
                } else {
                    stop = userTime.date(userTime.adding(1, to: startDay), hours: to.hours, minutes: to.minutes)
                }
            }
            return Span(start: start, stop: stop)
        case let .duration(day, seconds):
            let length = TimeInterval(seconds)
            guard let day = day.map({ resolve($0, today: today, in: userTime) }), day != today else {
                return Span(start: thisMinute.addingTimeInterval(-length), stop: thisMinute)
            }
            // Another day: after that day's last entry, or at 9:00, as the timesheet adds time.
            let morning = userTime.date(day, hours: defaultStartHour, minutes: 0)
            let dayEnd = userTime.start(of: userTime.adding(1, to: day))
            let start = min(([morning] + dayEnds(day)).max()!, dayEnd.addingTimeInterval(-length))
            return Span(start: start, stop: start.addingTimeInterval(length))
        }
    }

    /// A clock time on a day, on a 12-hour clock the afternoon's when only that has passed, e.g. `since 3` at 16:00.
    private static func atClock(_ day: DayKey, _ at: TypedClock, now: Date, in time: UserTime) -> Date {
        let morning = time.date(day, hours: at.hours, minutes: at.minutes)
        guard time.hour12, !at.meridiem, at.hours < 12 else { return morning }
        let afternoon = time.date(day, hours: at.hours + 12, minutes: at.minutes)
        return afternoon <= now ? afternoon : morning
    }

    /// The latest instant at this clock time that has passed; on a 12-hour clock, the morning's or the afternoon's.
    private static func latestStart(_ at: TypedClock, now: Date, in time: UserTime) -> Date {
        let latest = time.latest(at: (at.hours, at.minutes), now: now)
        guard time.hour12, !at.meridiem, at.hours < 12 else { return latest }
        return max(latest, time.latest(at: (at.hours + 12, at.minutes), now: now))
    }

    // MARK: - What is typed at the caret

    /// A `@project` or `#tag` being typed at the caret, which the panel completes from a list. Offsets are in UTF-16
    /// units, as text fields count them.
    public struct Mention: Equatable, Sendable {
        public var sign: Character
        /// What is typed after the sign.
        public var query: String
        public var start: Int
        public var end: Int

        public init(sign: Character, query: String, start: Int, end: Int) {
            self.sign = sign
            self.query = query
            self.start = start
            self.end = end
        }
    }

    /// The mention the caret is in or right after, if any.
    public static func mention(in text: String, caret: Int) -> Mention? {
        let units = Array(text.utf16)
        func isSpace(_ unit: UInt16) -> Bool { Unicode.Scalar(unit).map { CharacterSet.whitespacesAndNewlines.contains($0) } ?? false }
        var start = min(caret, units.count)
        while start > 0, !isSpace(units[start - 1]) { start -= 1 }
        var end = min(caret, units.count)
        while end < units.count, !isSpace(units[end]) { end += 1 }
        guard start < units.count, units[start] == 0x40 || units[start] == 0x23 else { return nil }
        let query = String(decoding: units[(start + 1)..<end], as: UTF16.self)
        return Mention(sign: units[start] == 0x40 ? "@" : "#", query: query, start: start, end: end)
    }

    /// The text without the mention, keeping one space where it was, and where the caret goes.
    public static func removing(_ mention: Mention, from text: String) -> (text: String, caret: Int) {
        let units = Array(text.utf16)
        let before = String(decoding: units[..<mention.start], as: UTF16.self).replacing(/\s+$/, with: "")
        let after = String(decoding: units[mention.end...], as: UTF16.self).replacing(/^\s+/, with: "")
        let joined = !before.isEmpty && !after.isEmpty ? "\(before) \(after)" : before.isEmpty ? after : before
        let result = !before.isEmpty && after.isEmpty ? "\(joined) " : joined
        return (result, before.isEmpty ? 0 : before.utf16.count + 1)
    }
}
