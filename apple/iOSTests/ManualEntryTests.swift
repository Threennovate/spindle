import Foundation
import Testing
import SpindleKit
@testable import Spindle

struct ManualEntryTests {
    let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)

    private func date(_ hour: Int, _ minute: Int, second: Int = 0, day: Int = 29) -> Date {
        time.date(DayKey(year: 2026, month: 9, day: day), hours: hour, minutes: minute).addingTimeInterval(TimeInterval(second))
    }

    @Test func startsHalfAnHourAgoWhenNothingWasTrackedToday() {
        let range = ManualEntry.defaultRange(now: date(14, 7, second: 42), entries: [], time: time)

        #expect(range.end == date(14, 7))
        #expect(range.start == date(13, 37))
    }

    @Test func startsWhereTheLastEntryTodayEnded() {
        let entries = [
            entry(stop: date(9, 30)),
            entry(stop: date(12, 15)),
            // Yesterday's, and one still running, don't count.
            entry(stop: date(18, 0, day: 28)),
            entry(stop: nil),
        ]

        let range = ManualEntry.defaultRange(now: date(14, 7), entries: entries, time: time)

        #expect(range.start == date(12, 15))
        #expect(range.end == date(14, 7))
    }

    @Test func anEntryThatJustEndedLeavesTheUsualHalfHour() {
        let range = ManualEntry.defaultRange(now: date(14, 7, second: 30), entries: [entry(stop: date(14, 7))], time: time)

        #expect(range.start == date(13, 37))
    }

    private func entry(stop: Date?) -> TimeEntry {
        TimeEntry(
            id: UUID().uuidString, workspaceId: "w", description: "", projectId: nil, taskId: nil, tagIds: [], isBillable: false,
            start: (stop ?? date(14, 0)).addingTimeInterval(-3600), stop: stop, shares: [], version: "1")
    }
}
