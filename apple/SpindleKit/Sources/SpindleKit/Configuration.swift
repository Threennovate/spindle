import Foundation
import OpenAPIRuntime

#if canImport(AppKit)
import AppKit
#endif
#if canImport(UIKit)
import UIKit
#endif

extension Configuration {
    /// Dates as the API writes them: ISO 8601, with or without fractions of a second (.NET writes up to seven digits).
    static let spindle = Configuration(dateTranscoder: LenientISO8601DateTranscoder())
}

/// Reads ISO 8601 dates with any number of fractional digits, and writes them in UTC with milliseconds.
struct LenientISO8601DateTranscoder: DateTranscoder {
    func encode(_ date: Date) throws -> String {
        date.formatted(Date.ISO8601FormatStyle(includingFractionalSeconds: true))
    }

    func decode(_ text: String) throws -> Date {
        if let date = Self.parse(text) {
            return date
        }
        throw DecodingError.dataCorrupted(.init(codingPath: [], debugDescription: "Not an ISO 8601 date: \(text)"))
    }

    static func parse(_ text: String) -> Date? {
        var normalized = text
        // Keep at most three fractional digits, which the formatter reads.
        if let dot = normalized.firstIndex(of: ".") {
            let fraction = normalized[normalized.index(after: dot)...].prefix { $0.isNumber }
            if fraction.count > 3 {
                let end = normalized.index(dot, offsetBy: fraction.count + 1)
                normalized.replaceSubrange(normalized.index(dot, offsetBy: 4)..<end, with: "")
            }
            return try? Date(normalized, strategy: Date.ISO8601FormatStyle(includingFractionalSeconds: true))
        }
        return try? Date(normalized, strategy: Date.ISO8601FormatStyle())
    }
}

/// How this device is listed among the account's signed-in devices, e.g. "Mac app on Martin's MacBook Pro".
public enum DeviceName {
    public static var current: String {
        #if os(macOS)
        let name = Host.current().localizedName ?? "a Mac"
        return String("Mac app on \(name)".prefix(100))
        #else
        return "iPhone app"
        #endif
    }
}
