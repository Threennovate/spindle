import Foundation
import Testing
@testable import SpindleKit

/// The same cases as the web app's calendar layout tests.
struct CalendarLayoutTests {
    let zagreb = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!, weekStart: 1)

    func instant(_ text: String) -> Date {
        try! Date(text, strategy: .iso8601)
    }

    func entry(_ id: String, _ start: String, _ stop: String?) -> TimeEntry {
        TimeEntry(id: id, workspaceId: "w", description: id, projectId: nil, taskId: nil, tagIds: [], isBillable: false, start: instant(start), stop: stop.map(instant), shares: [], version: "1")
    }

    @Test func placesEntriesByWallClockMinutesInThePersonsZone() {
        let segments = CalendarLayout.layout([entry("a", "2026-09-28T07:00:00Z", "2026-09-28T08:30:00Z")], days: [DayKey("2026-09-28")!], in: zagreb, now: .distantPast)

        #expect(segments.count == 1)
        #expect(segments[0].startMinute == 540)
        #expect(segments[0].endMinute == 630)
        #expect((segments[0].lane, segments[0].lanes) == (0, 1))
    }

    @Test func splitsAnEntryThatRunsPastMidnightAcrossBothDays() {
        let late = entry("late", "2026-09-28T20:00:00Z", "2026-09-28T23:30:00Z") // 22:00–01:30 in Zagreb

        let segments = CalendarLayout.layout([late], days: [DayKey("2026-09-28")!, DayKey("2026-09-29")!], in: zagreb, now: .distantPast)

        #expect(segments.map(\.day.description) == ["2026-09-28", "2026-09-29"])
        #expect((segments[0].startMinute, segments[0].endMinute, segments[0].continuesAfter) == (1320, 1440, true))
        #expect((segments[1].startMinute, segments[1].endMinute, segments[1].continuesBefore) == (0, 90, true))
    }

    @Test func showsARunningEntryUntilNow() {
        let segments = CalendarLayout.layout(
            [entry("running", "2026-09-28T09:00:00Z", nil)], days: [DayKey("2026-09-28")!], in: zagreb, now: instant("2026-09-28T09:15:00Z"))

        #expect((segments[0].startMinute, segments[0].endMinute) == (660, 675))
    }

    @Test func putsOverlappingEntriesSideBySideAndTheRestFullWidth() {
        let segments = CalendarLayout.layout(
            [
                entry("a", "2026-09-28T07:00:00Z", "2026-09-28T09:00:00Z"),
                entry("b", "2026-09-28T08:00:00Z", "2026-09-28T08:30:00Z"),
                entry("c", "2026-09-28T08:45:00Z", "2026-09-28T09:30:00Z"),
                entry("d", "2026-09-28T12:00:00Z", "2026-09-28T13:00:00Z"),
            ],
            days: [DayKey("2026-09-28")!], in: zagreb, now: .distantPast)
        let byId = Dictionary(uniqueKeysWithValues: segments.map { ($0.entry.id, $0) })

        #expect((byId["a"]!.lane, byId["a"]!.lanes) == (0, 2))
        #expect((byId["b"]!.lane, byId["b"]!.lanes) == (1, 2))
        // c starts after b ends, so it takes b's lane next to a.
        #expect((byId["c"]!.lane, byId["c"]!.lanes) == (1, 2))
        #expect((byId["d"]!.lane, byId["d"]!.lanes) == (0, 1))
    }

    @Test func leavesOutDaysAnEntryDoesNotTouch() {
        let segments = CalendarLayout.layout(
            [entry("a", "2026-09-28T07:00:00Z", "2026-09-28T08:00:00Z")], days: [DayKey("2026-09-27")!, DayKey("2026-09-29")!], in: zagreb, now: .distantPast)
        #expect(segments.isEmpty)
    }

    @Test(arguments: [(7.0, 0.0), (8, 15), (542, 540), (1439, 1440), (-20, 0)])
    func snapsToQuarterHours(minutes: Double, snapped: Double) {
        #expect(CalendarLayout.snap(minutes, step: 15) == snapped)
    }

    @Test func findsTheWeekAndNamesIt() {
        let days = CalendarLayout.week(containing: DayKey("2026-09-30")!, in: zagreb)
        #expect(days.first?.description == "2026-09-28")
        #expect(days.last?.description == "2026-10-04")
        #expect(CalendarLayout.label(days, in: zagreb) == "28 Sep – 4 Oct 2026")
        #expect(CalendarLayout.label(Array(days.prefix(3)), in: zagreb) == "28 – 30 Sep 2026")
        #expect(CalendarLayout.label([DayKey("2026-09-29")!], in: zagreb) == "Tuesday 29 September 2026")
    }

    @Test func turnsMinutesBackIntoInstants() {
        let day = DayKey("2026-09-28")!
        #expect(CalendarLayout.instant(day, minute: 570, in: zagreb) == instant("2026-09-28T07:30:00Z"))
        #expect(CalendarLayout.instant(day, minute: 1440, in: zagreb) == instant("2026-09-28T22:00:00Z"))
        #expect(CalendarLayout.minuteOfDay(instant("2026-09-28T07:30:30Z"), in: zagreb) == 570.5)
    }
}
