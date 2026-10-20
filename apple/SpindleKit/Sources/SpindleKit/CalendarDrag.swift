import Foundation

/// What dragging on the calendar does, as on the web app: on empty space it adds an entry for the time dragged
/// over (a click adds an hour), on a block it moves the entry, and on a block's top or bottom edge it changes when
/// the entry starts or ends. Everything snaps to quarter hours. Clicking a block opens it; locked and running
/// entries only open.
public struct CalendarDrag {
    public static let step = 15.0

    public enum Edge: Sendable {
        case start, end
    }

    /// What the drag started on.
    public enum Target {
        case empty
        case entry(DaySegment, edge: Edge?)
    }

    public enum State {
        case create(day: DayKey, anchor: Double, current: Double)
        case open(TimeEntry)
        case move(entry: TimeEntry, offset: Double, duration: Double, day: DayKey, start: Double, moved: Bool)
        case resize(edge: Edge, entry: TimeEntry, day: DayKey, start: Double, end: Double, moved: Bool)
    }

    public enum Outcome: Equatable {
        case open(String)
        case create(DateInterval)
        case save(entryId: String, start: Date, stop: Date)
    }

    public let time: UserTime
    /// Whether an entry that starts then can no longer be changed.
    public let isLocked: (Date) -> Bool
    /// Whether nothing can be added to or moved onto a day.
    public let isDayLocked: (DayKey) -> Bool

    public init(time: UserTime, isLocked: @escaping (Date) -> Bool, isDayLocked: @escaping (DayKey) -> Bool) {
        self.time = time
        self.isLocked = isLocked
        self.isDayLocked = isDayLocked
    }

    /// Whether a block can be moved or resized, rather than only opened.
    public func canChange(_ entry: TimeEntry) -> Bool {
        entry.stop != nil && !isLocked(entry.start)
    }

    /// The drag begins at a minute of a day, on `target`.
    public func begin(on target: Target, day: DayKey, minute: Double) -> State? {
        switch target {
        case .entry(let segment, let edge):
            let entry = segment.entry
            guard let stop = entry.stop, canChange(entry) else { return .open(entry) }
            if let edge {
                return .resize(edge: edge, entry: entry, day: segment.day, start: segment.startMinute, end: segment.endMinute, moved: false)
            }
            // Only the part of an entry on the day it starts can be moved.
            if segment.continuesBefore { return .open(entry) }
            return .move(
                entry: entry, offset: minute - segment.startMinute, duration: stop.timeIntervalSince(entry.start) / 60,
                day: segment.day, start: segment.startMinute, moved: false)
        case .empty:
            guard !isDayLocked(day) else { return nil }
            let start = CalendarLayout.snap(minute - Self.step / 2, step: Self.step)
            return .create(day: day, anchor: start, current: start)
        }
    }

    /// The pointer is now at a minute of a day, `distance` points from where the drag began.
    public func move(_ state: State, to day: DayKey, minute: Double, distance: Double) -> State {
        switch state {
        case .create(let createDay, let anchor, _):
            return .create(day: createDay, anchor: anchor, current: CalendarLayout.snap(minute, step: Self.step))
        case .move(let entry, let offset, let duration, let from, _, let moved):
            let start = min(CalendarLayout.minutesPerDay - Self.step, CalendarLayout.snap(minute - offset, step: Self.step))
            // A block can't move onto a locked day; it stays on the last open one.
            return .move(entry: entry, offset: offset, duration: duration, day: isDayLocked(day) ? from : day, start: start, moved: moved || distance > 4)
        case .resize(.end, let entry, let resizeDay, let start, _, _):
            return .resize(edge: .end, entry: entry, day: resizeDay, start: start, end: max(start + Self.step, CalendarLayout.snap(minute, step: Self.step)), moved: true)
        case .resize(.start, let entry, let resizeDay, _, let end, _):
            return .resize(edge: .start, entry: entry, day: resizeDay, start: min(end - Self.step, CalendarLayout.snap(minute, step: Self.step)), end: end, moved: true)
        case .open:
            return state
        }
    }

    /// The drag ended: what to do.
    public func finish(_ state: State) -> Outcome {
        switch state {
        case .open(let entry):
            return .open(entry.id)
        case .create(let day, let anchor, let current):
            // A click adds an hour; a drag, the time dragged over.
            let (from, to) = abs(current - anchor) < Self.step
                ? (anchor, min(anchor + 60, CalendarLayout.minutesPerDay))
                : (min(anchor, current), max(anchor, current))
            return .create(DateInterval(start: CalendarLayout.instant(day, minute: from, in: time), end: CalendarLayout.instant(day, minute: to, in: time)))
        case .move(let entry, _, let duration, let day, let start, let moved):
            guard moved else { return .open(entry.id) }
            let newStart = CalendarLayout.instant(day, minute: start, in: time)
            return .save(entryId: entry.id, start: newStart, stop: newStart.addingTimeInterval(duration * 60))
        case .resize(_, let entry, _, _, _, false):
            return .open(entry.id)
        case .resize(.end, let entry, let day, _, let end, true):
            return .save(entryId: entry.id, start: entry.start, stop: CalendarLayout.instant(day, minute: end, in: time))
        case .resize(.start, let entry, let day, let start, _, true):
            return .save(entryId: entry.id, start: CalendarLayout.instant(day, minute: start, in: time), stop: entry.stop ?? Date())
        }
    }

    /// The block to draw while dragging, where it would land; nil when nothing has moved yet.
    public func preview(_ state: State) -> DaySegment? {
        switch state {
        case .move(let entry, _, let duration, let day, let start, true):
            DaySegment(entry: entry, day: day, startMinute: start, endMinute: min(start + duration, CalendarLayout.minutesPerDay))
        case .resize(_, let entry, let day, let start, let end, true):
            DaySegment(entry: entry, day: day, startMinute: start, endMinute: end)
        default:
            nil
        }
    }
}
