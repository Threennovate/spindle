import Foundation

/// The app's "now": the system clock, except that a debug build can be told to run from another moment (the
/// screenshots' made-up 16:30) so that the timer, the calendar's now line and today's date agree. Release builds
/// never move it.
public enum AppClock {
    private static let offset = OffsetBox()

    public static var now: Date { Date(timeIntervalSinceNow: offset.value) }

    #if DEBUG
    /// From now on, `now` is `moment` and goes on from there.
    public static func run(from moment: Date) { offset.value = moment.timeIntervalSinceNow }
    #endif
}

private final class OffsetBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: TimeInterval = 0
    var value: TimeInterval {
        get { lock.lock(); defer { lock.unlock() }; return stored }
        set { lock.lock(); defer { lock.unlock() }; stored = newValue }
    }
}
