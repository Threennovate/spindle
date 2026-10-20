import Foundation
import Testing
@testable import SpindleKit

struct CalendarDragTests {
    let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
    let monday = DayKey("2026-09-28")!
    let tuesday = DayKey("2026-09-29")!
    let locked = DayKey("2026-09-27")!

    var drag: CalendarDrag {
        CalendarDrag(time: time, isLocked: { $0 < time.start(of: DayKey("2026-09-28")!) }, isDayLocked: { $0 < DayKey("2026-09-28")! })
    }

    func instant(_ day: DayKey, _ hours: Int, _ minutes: Int = 0) -> Date {
        time.date(day, hours: hours, minutes: minutes)
    }

    /// 9:00–10:30 on Monday.
    func segment(stop: Bool = true, day: DayKey? = nil) -> DaySegment {
        let day = day ?? monday
        let entry = TimeEntry(
            id: "e1", workspaceId: "w", description: "Review", projectId: nil, taskId: nil, tagIds: [], isBillable: false,
            start: instant(day, 9), stop: stop ? instant(day, 10, 30) : nil, shares: [], version: "1")
        return DaySegment(entry: entry, day: day, startMinute: 540, endMinute: 630)
    }

    @Test func aClickOnEmptySpaceAddsAnHourFromTheQuarterHour() throws {
        let state = try #require(drag.begin(on: .empty, day: monday, minute: 9 * 60 + 5))

        #expect(drag.finish(state) == .create(DateInterval(start: instant(monday, 9), end: instant(monday, 10))))
    }

    @Test func draggingOverEmptySpaceAddsTheTimeDraggedOver() throws {
        var state = try #require(drag.begin(on: .empty, day: monday, minute: 14 * 60))
        state = drag.move(state, to: monday, minute: 15 * 60 + 40, distance: 80)

        #expect(drag.finish(state) == .create(DateInterval(start: instant(monday, 14), end: instant(monday, 15, 45))))
    }

    @Test func nothingIsAddedOnALockedDay() {
        #expect(drag.begin(on: .empty, day: locked, minute: 600) == nil)
    }

    @Test func aClickOnABlockOpensIt() throws {
        let state = try #require(drag.begin(on: .entry(segment(), edge: nil), day: monday, minute: 560))
        #expect(drag.finish(state) == .open("e1"))
    }

    @Test func movingKeepsTheDurationAndSnapsToQuarterHours() throws {
        // Grabbed 20 minutes into the block and dropped on Tuesday at 13:37, so it starts at 13:17: a quarter past.
        var state = try #require(drag.begin(on: .entry(segment(), edge: nil), day: monday, minute: 560))
        state = drag.move(state, to: tuesday, minute: 13 * 60 + 37, distance: 200)

        #expect(drag.finish(state) == .save(entryId: "e1", start: instant(tuesday, 13, 15), stop: instant(tuesday, 14, 45)))
        #expect(drag.preview(state)?.day == tuesday)
    }

    @Test func aBlockDoesNotMoveOntoALockedDay() throws {
        var state = try #require(drag.begin(on: .entry(segment(), edge: nil), day: monday, minute: 540))
        state = drag.move(state, to: locked, minute: 600, distance: 200)

        #expect(drag.finish(state) == .save(entryId: "e1", start: instant(monday, 10), stop: instant(monday, 11, 30)))
    }

    @Test func resizingChangesOneEndAndKeepsAQuarterHourAtLeast() throws {
        var end = try #require(drag.begin(on: .entry(segment(), edge: .end), day: monday, minute: 630))
        end = drag.move(end, to: monday, minute: 11 * 60 + 50, distance: 60)
        #expect(drag.finish(end) == .save(entryId: "e1", start: instant(monday, 9), stop: instant(monday, 11, 45)))

        var start = try #require(drag.begin(on: .entry(segment(), edge: .start), day: monday, minute: 540))
        start = drag.move(start, to: monday, minute: 11 * 60, distance: 60)
        #expect(drag.finish(start) == .save(entryId: "e1", start: instant(monday, 10, 15), stop: instant(monday, 10, 30)))
    }

    @Test func runningAndLockedEntriesOnlyOpen() throws {
        let running = try #require(drag.begin(on: .entry(segment(stop: false), edge: .end), day: monday, minute: 600))
        #expect(drag.finish(running) == .open("e1"))

        let old = try #require(drag.begin(on: .entry(segment(day: locked), edge: nil), day: locked, minute: 560))
        #expect(drag.finish(drag.move(old, to: monday, minute: 700, distance: 100)) == .open("e1"))
    }
}
