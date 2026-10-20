import Foundation
import Testing
@testable import SpindleKit

/// The same cases as the web app's Pomodoro tests.
struct PomodoroTests {
    func minutes(_ value: Double) -> TimeInterval { value * 60 }

    @Test func countsWholeFocusIntervals() {
        #expect(Pomodoro.intervalsDone(elapsed: minutes(24), focusMinutes: 25) == 0)
        #expect(Pomodoro.intervalsDone(elapsed: minutes(25), focusMinutes: 25) == 1)
        #expect(Pomodoro.intervalsDone(elapsed: minutes(51), focusMinutes: 25) == 2)
        #expect(Pomodoro.intervalsDone(elapsed: -5, focusMinutes: 25) == 0)
    }

    @Test func countsDownTheCurrentInterval() {
        #expect(Pomodoro.focusLeft(elapsed: 0, focusMinutes: 25) == minutes(25))
        #expect(Pomodoro.focusLeft(elapsed: minutes(10), focusMinutes: 25) == minutes(15))
        #expect(Pomodoro.focusLeft(elapsed: minutes(26), focusMinutes: 25) == minutes(24))
    }

    @Test func showsCountdownsAsMinutesAndSeconds() {
        #expect(Pomodoro.countdown(minutes(12) + 5) == "12:05")
        #expect(Pomodoro.countdown(0.4) == "0:01")
        #expect(Pomodoro.countdown(-0.01) == "0:00")
    }
}

struct ReminderScheduleTests {
    let zagreb = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
    let schedule = ReminderSchedule(days: [1, 2, 3, 4, 5], from: 9 * 60, to: 17 * 60, everyMinutes: 30)

    func instant(_ text: String) -> Date {
        try! Date(text, strategy: .iso8601)
    }

    @Test func theDaysReviewComesInTheHoursAfterWorkOnWorkingDays() {
        // Tuesday 29 September 2026, in Zagreb (UTC+2).
        #expect(schedule.endedWorkingDay(at: instant("2026-09-29T14:59:00Z"), in: zagreb) == nil)
        #expect(schedule.endedWorkingDay(at: instant("2026-09-29T15:00:00Z"), in: zagreb) == DayKey("2026-09-29"))
        #expect(schedule.endedWorkingDay(at: instant("2026-09-29T16:59:00Z"), in: zagreb) == DayKey("2026-09-29"))
        #expect(schedule.endedWorkingDay(at: instant("2026-09-29T17:00:00Z"), in: zagreb) == nil)
        // Saturday isn't a working day.
        #expect(schedule.endedWorkingDay(at: instant("2026-10-03T15:30:00Z"), in: zagreb) == nil)
    }

    @Test func remindsOnlyInWorkingHoursInThePersonsZone() {
        // Tuesday 29 September 2026: 09:30 in Zagreb is 07:30 UTC.
        #expect(schedule.isWorkingTime(instant("2026-09-29T07:30:00Z"), in: zagreb))
        #expect(!schedule.isWorkingTime(instant("2026-09-29T06:30:00Z"), in: zagreb))
        #expect(!schedule.isWorkingTime(instant("2026-09-29T15:00:00Z"), in: zagreb))
        // Saturday.
        #expect(!schedule.isWorkingTime(instant("2026-10-03T08:00:00Z"), in: zagreb))
    }

    @Test func remindsEveryIntervalWhileNothingRunsAndSomeoneIsThere() {
        let now = instant("2026-09-29T08:00:00Z")
        #expect(schedule.shouldRemind(now: now, in: zagreb, isRunning: false, lastRemindedOrStopped: nil, idleSeconds: 0))
        #expect(!schedule.shouldRemind(now: now, in: zagreb, isRunning: true, lastRemindedOrStopped: nil, idleSeconds: 0))
        #expect(!schedule.shouldRemind(now: now, in: zagreb, isRunning: false, lastRemindedOrStopped: now.addingTimeInterval(-600), idleSeconds: 0))
        #expect(schedule.shouldRemind(now: now, in: zagreb, isRunning: false, lastRemindedOrStopped: now.addingTimeInterval(-1800), idleSeconds: 0))
        // Away from the computer: no point.
        #expect(!schedule.shouldRemind(now: now, in: zagreb, isRunning: false, lastRemindedOrStopped: nil, idleSeconds: 600))
    }
}
