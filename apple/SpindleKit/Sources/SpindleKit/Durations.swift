import Foundation

/// Durations as Spindle shows and reads them, the same as the web app.
public enum Durations {
    /// How a person likes finished durations written; see the account's preferences.
    public enum Format: String, Sendable, CaseIterable {
        /// 1:30:00
        case hms
        /// 1:30
        case hm
        /// 1.50 h
        case decimal
    }

    /// `h:mm:ss`, as Toggl shows durations: 0:05:09, 12:30:00, 134:00:00.
    public static func clock(_ totalSeconds: Double) -> String {
        let seconds = max(0, Int(totalSeconds.rounded(.down)))
        return String(format: "%d:%02d:%02d", seconds / 3600, seconds / 60 % 60, seconds % 60)
    }

    /// A finished duration in the person's format. A running timer always shows seconds, with ``clock(_:)``.
    public static func format(_ totalSeconds: Double, as format: Format) -> String {
        let seconds = max(0, Int(totalSeconds.rounded(.down)))
        switch format {
        case .hms: return clock(Double(seconds))
        case .hm: return String(format: "%d:%02d", seconds / 3600, seconds / 60 % 60)
        case .decimal: return String(format: "%.2f h", Double(seconds) / 3600)
        }
    }

    /// `12h 05m`, `45m`, `0m`, for totals and estimates.
    public static func hoursMinutes(_ totalSeconds: Double) -> String {
        let minutes = max(0, Int(totalSeconds.rounded(.down))) / 60
        return minutes >= 60 ? String(format: "%dh %02dm", minutes / 60, minutes % 60) : "\(minutes)m"
    }

    /// Reads a duration typed by a person, in seconds, or nil if it is not one.
    ///
    /// - `1:30` is an hour and a half, `1:30:15` adds seconds
    /// - `1h 30m`, `90m`, `45s`, `2h` are units
    /// - `1.5` or `1,5` is hours; a whole number such as `90` is minutes
    public static func parse(_ text: String) -> Int? {
        let value = text.trimmingCharacters(in: .whitespaces).lowercased()
        guard !value.isEmpty else { return nil }

        if let match = value.wholeMatch(of: /(\d+):([0-5]?\d)(?::([0-5]?\d))?/) {
            return seconds(decimal(match.1) * 3600 + decimal(match.2) * 60 + decimal(match.3))
        }

        if let match = value.wholeMatch(of: /(?:(\d+(?:[.,]\d+)?)\s*h)?\s*(?:(\d+)\s*m(?:in)?)?\s*(?:(\d+)\s*s)?/),
           match.1 != nil || match.2 != nil || match.3 != nil {
            return seconds(decimal(match.1) * 3600 + decimal(match.2) * 60 + decimal(match.3))
        }

        if value.wholeMatch(of: /\d+[.,]\d+/) != nil {
            return seconds(decimal(value) * 3600)
        }

        if value.wholeMatch(of: /\d+/) != nil {
            return seconds(decimal(value) * 60)
        }

        return nil
    }

    /// Whole seconds, or nil when there are too many to count, e.g. from a long row of digits typed in a field.
    static func seconds(_ value: Double) -> Int? {
        Int(exactly: value.rounded())
    }

    private static func decimal<Text: StringProtocol>(_ text: Text?) -> Double {
        text.flatMap { Double($0.replacingOccurrences(of: ",", with: ".")) } ?? 0
    }
}

/// Clock times as people type them, the same as the web app.
public enum ClockTime {
    public typealias Clock = (hours: Int, minutes: Int)

    /// Reads a clock time as hours (0–23) and minutes. Digits alone need no separator: `9` is 9:00, `930` is 9:30 and
    /// `2130` is 21:30. With a separator (`:`, `.`, `,`, `h` or a space) minutes may be short: `9:5` is 9:05 and `9h`
    /// is 9:00. `am`/`pm` (also `a`, `p`, `a.m.`) read the hours on a 12-hour clock. Nil if the text isn't a time.
    public static func parse(_ text: String) -> Clock? {
        let value = text.trimmingCharacters(in: .whitespaces).lowercased()
        guard let match = value.wholeMatch(of: /(\d{1,4})(?:\s*[:.,h\s]\s*(\d{0,2}))?\s*(?:([ap])\.?(?:m\.?)?)?/) else {
            return nil
        }
        let digits = match.1
        var hours: Int
        let minutes: Int
        if let typedMinutes = match.2 {
            // With a separator the hours come first and can't run into the minutes.
            guard digits.count <= 2 else { return nil }
            hours = Int(digits)!
            minutes = Int(typedMinutes) ?? 0
        } else if digits.count <= 2 {
            hours = Int(digits)!
            minutes = 0
        } else {
            // `930` and `0930`: the last two digits are the minutes.
            hours = Int(digits.dropLast(2))!
            minutes = Int(digits.suffix(2))!
        }

        guard minutes <= 59 else { return nil }
        if let meridiem = match.3 {
            guard (1...12).contains(hours) else { return nil }
            hours = hours % 12 + (meridiem == "p" ? 12 : 0)
        } else if hours > 23 {
            return nil
        }
        return (hours, minutes)
    }

    /// A clock time for a field: `09:05`, or `9:05 am` with a 12-hour clock.
    public static func field(_ clock: Clock, hour12: Bool) -> String {
        guard hour12 else { return String(format: "%02d:%02d", clock.hours, clock.minutes) }
        let hours = clock.hours % 12 == 0 ? 12 : clock.hours % 12
        return String(format: "%d:%02d %@", hours, clock.minutes, clock.hours < 12 ? "am" : "pm")
    }

    /// The clock time `minutes` later (or earlier, when negative), going round midnight.
    public static func adding(_ minutes: Int, to clock: Clock) -> Clock {
        let total = ((clock.hours * 60 + clock.minutes + minutes) % 1440 + 1440) % 1440
        return (total / 60, total % 60)
    }
}
