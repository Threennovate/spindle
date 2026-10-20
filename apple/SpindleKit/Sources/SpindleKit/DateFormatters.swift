import Foundation
import os

/// Date formatters made once for each pattern, zone, calendar and locale, and reused: making one costs far more than
/// using it, and the calendar writes dozens of times while an entry is dragged. A formatter that is set up can be
/// used from any thread; making and keeping them is behind a lock.
enum DateFormatters {
    private struct Key: Hashable {
        let format: String
        let isTemplate: Bool
        let timeZone: TimeZone
        let calendar: Calendar
        let locale: Locale
        /// The locale's identifier when asked, so a locale that follows the Mac's region gets a new formatter once the
        /// region changes.
        let localeIdentifier: String
    }

    private static let cache = OSAllocatedUnfairLock(uncheckedState: [Key: DateFormatter]())

    /// A formatter for a fixed pattern, e.g. `HH:mm`.
    static func pattern(_ pattern: String, calendar: Calendar, timeZone: TimeZone, locale: Locale) -> DateFormatter {
        formatter(Key(format: pattern, isTemplate: false, timeZone: timeZone, calendar: calendar, locale: locale,
                      localeIdentifier: locale.identifier))
    }

    /// A formatter for a template, e.g. `yMd`, written the way the locale writes it.
    static func template(_ template: String, calendar: Calendar, timeZone: TimeZone, locale: Locale) -> DateFormatter {
        formatter(Key(format: template, isTemplate: true, timeZone: timeZone, calendar: calendar, locale: locale,
                      localeIdentifier: locale.identifier))
    }

    private static func formatter(_ key: Key) -> DateFormatter {
        cache.withLockUnchecked { cache in
            if let formatter = cache[key] { return formatter }
            // Set up in the order the app always did, as setting the locale can change the calendar's details.
            let formatter = DateFormatter()
            formatter.calendar = key.calendar
            formatter.timeZone = key.timeZone
            formatter.locale = key.locale
            if key.isTemplate {
                formatter.setLocalizedDateFormatFromTemplate(key.format)
            } else {
                formatter.dateFormat = key.format
            }
            cache[key] = formatter
            return formatter
        }
    }
}
