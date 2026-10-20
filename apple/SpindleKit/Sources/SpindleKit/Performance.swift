import os

/// Marks where the apps spend their time, for Instruments to show as Points of Interest: the work that may keep the
/// interface waiting, and how often views are drawn. It costs next to nothing unless Instruments is recording, and
/// `apple/scripts/profile-mac.sh` adds it up.
public enum Performance {
    public static let signposter = OSSignposter(subsystem: "com.spindletrack.performance", category: .pointsOfInterest)

    /// Runs `work` as one interval named `name`.
    public static func measure<T>(_ name: StaticString, _ work: () throws -> T) rethrows -> T {
        let state = signposter.beginInterval(name, id: signposter.makeSignpostID())
        defer { signposter.endInterval(name, state) }
        return try work()
    }

    /// Runs async `work` as one interval named `name`, including the time it waits. It runs where the caller does, e.g.
    /// on the main actor, so measuring doesn't move the work elsewhere.
    public static func measure<T>(_ name: StaticString, isolation: isolated (any Actor)? = #isolation,
                                  _ work: () async throws -> T) async rethrows -> T {
        let state = signposter.beginInterval(name, id: signposter.makeSignpostID())
        defer { signposter.endInterval(name, state) }
        return try await work()
    }

    /// Marks a moment, e.g. a view drawn again, to count how often it happens.
    public static func event(_ name: StaticString) {
        signposter.emitEvent(name)
    }

    /// Marks a moment with a detail, e.g. which section was opened.
    public static func event(_ name: StaticString, _ detail: String) {
        signposter.emitEvent(name, "\(detail, privacy: .public)")
    }
}
