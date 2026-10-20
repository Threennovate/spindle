import Foundation
import Testing
@testable import SpindleKit

/// The same cases as the web app's time zone tests, so both apps put time on the same days and weeks.
struct UserTimeTests {
    let zagreb = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!, weekStart: 1)

    func instant(_ text: String) -> Date {
        try! Date(text, strategy: .iso8601)
    }

    @Test func putsAnInstantOnTheDayItIsInThePersonsZoneNotInUTC() {
        let lateEvening = instant("2026-09-28T22:30:00Z") // 00:30 on 29 Sep in Zagreb, 07:30 in Tokyo

        #expect(zagreb.day(of: lateEvening).description == "2026-09-29")
        #expect(UserTime(timeZone: TimeZone(identifier: "UTC")!).day(of: lateEvening).description == "2026-09-28")
        #expect(UserTime(timeZone: TimeZone(identifier: "Asia/Tokyo")!).day(of: lateEvening).description == "2026-09-29")
    }

    @Test func findsMidnightAndClockTimesAcrossDaylightSavingChanges() {
        #expect(zagreb.start(of: DayKey("2026-09-28")!) == instant("2026-09-27T22:00:00Z"))
        #expect(zagreb.start(of: DayKey("2026-12-01")!) == instant("2026-11-30T23:00:00Z"))
        // The clocks go back on 25 October 2026: 09:00 that day is UTC+1.
        #expect(zagreb.date(DayKey("2026-10-25")!, hours: 9, minutes: 0) == instant("2026-10-25T08:00:00Z"))
    }

    @Test func movesBetweenDaysByCalendarNotBy24Hours() {
        #expect(zagreb.adding(1, to: DayKey("2026-10-24")!).description == "2026-10-25")
        #expect(zagreb.adding(-1, to: DayKey("2026-03-01")!).description == "2026-02-28")
        #expect(zagreb.adding(1, to: DayKey("2026-10-25")!).description == "2026-10-26")
        #expect(zagreb.days(from: DayKey("2026-10-24")!, to: DayKey("2026-10-27")!) == 3)
    }

    @Test func findsTheWeekStartingOnTheChosenWeekday() {
        let wednesday = instant("2026-09-30T10:00:00Z")

        let mondayWeek = zagreb.week(weeksBack: 0, from: wednesday)
        let sundayWeek = UserTime(timeZone: zagreb.timeZone, weekStart: 0).week(weeksBack: 0, from: wednesday)

        #expect(mondayWeek.start == instant("2026-09-27T22:00:00Z"))
        #expect(mondayWeek.end == instant("2026-10-04T22:00:00Z"))
        #expect(sundayWeek.start == instant("2026-09-26T22:00:00Z"))
    }

    @Test func stepsBackWholeCalendarWeeksAcrossTheDaylightSavingChange() {
        let now = instant("2026-10-28T10:00:00Z") // Wednesday after the clocks went back on Sunday 25 Oct

        let thisWeek = zagreb.week(weeksBack: 0, from: now)
        let lastWeek = zagreb.week(weeksBack: 1, from: now)

        #expect(thisWeek.start == instant("2026-10-25T23:00:00Z"))
        #expect(lastWeek.start == instant("2026-10-18T22:00:00Z"))
        #expect(lastWeek.end == thisWeek.start)
    }

    @Test func writesClockTimesInTheZone() {
        #expect(zagreb.clock(instant("2026-09-28T07:05:00Z")) == "09:05")
        let twelveHour = UserTime(timeZone: zagreb.timeZone, hour12: true)
        #expect(twelveHour.clock(instant("2026-09-28T19:05:00Z")) == "9:05 pm")
        #expect(zagreb.clockField(instant("2026-09-28T19:05:00Z")) == "21:05")
        #expect(twelveHour.clockField(instant("2026-09-28T19:05:00Z")) == "9:05 pm")
    }

    /// The same cases as the web app's `readClockField` and `readStartField` tests.
    @Test func readsWhatWasTypedInAClockField() {
        let now = instant("2026-10-02T18:27:40Z") // 20:27:40 in Zagreb

        #expect(zagreb.readClockField("2015", now: now)! == (20, 15))
        #expect(zagreb.readClockField("NOW", now: now)! == (20, 27))
        #expect(zagreb.readClockField("-15", now: now)! == (20, 12))
        #expect(zagreb.readClockField("-1:30", now: now)! == (18, 57))
        #expect(zagreb.readClockField("-1.5", now: now)! == (18, 57))
        #expect(zagreb.readClockField("-30", now: instant("2026-10-01T22:10:00Z"))! == (23, 40))
        #expect(zagreb.readClockField("-", now: now) == nil)
        #expect(zagreb.readClockField("later", now: now) == nil)

        #expect(zagreb.readStartField("2015", now: now) == instant("2026-10-02T18:15:00Z"))
        #expect(zagreb.readStartField("2115", now: now) == instant("2026-10-01T19:15:00Z"))
        #expect(zagreb.readStartField("-15", now: now) == instant("2026-10-02T18:12:00Z"))
        #expect(zagreb.readStartField("-25h", now: now) == instant("2026-10-01T17:27:00Z"))
        #expect(zagreb.readStartField("soon", now: now) == nil)
    }

    @Test func namesDaysRelativeToToday() {
        let now = instant("2026-09-28T10:00:00Z")

        #expect(zagreb.dayHeading(DayKey("2026-09-28")!, now: now) == "Today")
        #expect(zagreb.dayHeading(DayKey("2026-09-27")!, now: now) == "Yesterday")
        #expect(zagreb.dayHeading(DayKey("2026-09-22")!, now: now) == "Tue, 22 Sep")
        #expect(zagreb.dayHeading(DayKey("2025-12-31")!, now: now) == "Wed, 31 Dec 2025")
        #expect(zagreb.longDay(DayKey("2026-09-01")!) == "1 September 2026")
    }

    @Test func readsAndComparesDays() {
        #expect(DayKey("2026-09-29")! < DayKey("2026-10-01")!)
        #expect(DayKey("29.09.2026") == nil)
    }

    @Test func writesADayForAFieldTheWayTheRegionDoes() {
        let day = DayKey("2026-09-05")!

        #expect(zagreb.dayField(day, locale: Locale(identifier: "hr_HR")) == "05. 09. 2026.")
        // English with Croatian formats, as Macs in Croatia are often set up.
        #expect(zagreb.dayField(day, locale: Locale(identifier: "en_US@rg=hrzzzz")) == "05. 09. 2026.")
        #expect(zagreb.dayField(day, locale: Locale(identifier: "en_GB")) == "05/09/2026")
        #expect(zagreb.dayField(day, locale: Locale(identifier: "en_US")) == "9/5/2026")
    }

    @Test func readsADayTypedInAFieldInTheRegionsOrder() {
        let near = DayKey("2026-09-29")!
        let croatia = Locale(identifier: "hr_HR"), us = Locale(identifier: "en_US")
        func read(_ text: String, _ locale: Locale) -> String? {
            zagreb.parseDayField(text, near: near, locale: locale)?.description
        }

        #expect(read("05. 09. 2026.", croatia) == "2026-09-05")
        #expect(read("5.9.2026", croatia) == "2026-09-05")
        #expect(read("5/9/26", croatia) == "2026-09-05")
        #expect(read("09/05/2026", us) == "2026-09-05")
        #expect(read("2026-09-05", croatia) == "2026-09-05")
        // Without the year, in the one being edited; a day alone, in its month.
        #expect(read("5.10.", croatia) == "2026-10-05")
        #expect(read("10/5", us) == "2026-10-05")
        #expect(read("5", croatia) == "2026-09-05")
        // Too many digits for a number, rather than a crash.
        #expect(read("5.9.99999999999999999999", croatia) == nil)
        #expect(read(String(repeating: "9", count: 40), croatia) == nil)
        // Days that don't exist, and text that isn't a day.
        #expect(read("31.6.2026", croatia) == nil)
        #expect(read("29.2.2026", croatia) == nil)
        #expect(read("13/13/2026", us) == nil)
        #expect(read("today", croatia) == nil)
        #expect(read("1.2.3.4", croatia) == nil)
    }

    @Test func reusedFormattersWriteWhatNewOnesDidInEveryZoneAndLocale() async {
        // How the app wrote times before it kept its formatters: a new one for every call.
        func fresh(_ time: UserTime, _ instant: Date, _ pattern: String) -> String {
            let formatter = DateFormatter()
            formatter.calendar = time.calendar
            formatter.timeZone = time.timeZone
            formatter.locale = Locale(identifier: "en_GB")
            formatter.dateFormat = pattern
            return formatter.string(from: instant)
        }
        func freshField(_ time: UserTime, _ day: DayKey, _ locale: Locale) -> String {
            let formatter = DateFormatter()
            formatter.calendar = time.calendar
            formatter.timeZone = time.timeZone
            formatter.locale = locale
            formatter.setLocalizedDateFormatFromTemplate("yMd")
            return formatter.string(from: time.start(of: day))
        }
        let zones = ["Europe/Zagreb", "UTC", "Asia/Tokyo", "America/New_York", "Asia/Kolkata", "Pacific/Chatham"]
        let times = zones.flatMap { zone in
            [false, true].map { UserTime(timeZone: TimeZone(identifier: zone)!, weekStart: $0 ? 0 : 1, hour12: $0) }
        }
        let locales = ["hr_HR", "en_US", "en_GB", "en_US@rg=hrzzzz", "de_DE", "ja_JP", "ar_EG"].map(Locale.init(identifier:))
            + [.autoupdatingCurrent]
        // Around the clocks going back in Zagreb and New York, and an evening that is the next day in Tokyo.
        let instants = ["2026-10-25T00:30:00Z", "2026-10-25T01:30:00Z", "2026-11-01T06:15:00Z", "2026-09-28T22:30:00Z",
                        "2026-01-01T00:00:00Z", "2027-03-28T01:59:00Z"].map(instant)
        let days = ["2026-09-05", "2026-12-31", "2027-02-28"].map { DayKey($0)! }

        // Twice over, so the second round uses formatters the first one made, with zones and locales in between.
        for _ in 0..<2 {
            for time in times {
                for at in instants {
                    #expect(time.clock(at) == fresh(time, at, time.hour12 ? "h:mm a" : "HH:mm"))
                    #expect(time.format(at, "EEE, d MMM yyyy") == fresh(time, at, "EEE, d MMM yyyy"))
                }
                for day in days {
                    #expect(time.longDay(day) == fresh(time, time.start(of: day), "d MMMM yyyy"))
                    #expect(CalendarLayout.label([day], in: time) == fresh(time, time.date(day, hours: 12, minutes: 0), "EEEE d MMMM yyyy"))
                    for locale in locales {
                        #expect(time.dayField(day, locale: locale) == freshField(time, day, locale))
                    }
                }
            }
        }

        // From many threads at once, as the activity loader and the MCP server write times off the main thread.
        let zagreb = zagreb
        let expected = fresh(zagreb, instants[0], "HH:mm")
        let written = await withTaskGroup(of: [String].self) { group in
            for _ in 0..<16 {
                group.addTask { (0..<200).map { _ in zagreb.clock(instants[0]) } }
            }
            return await group.reduce(into: []) { $0 += $1 }
        }
        #expect(written.count == 3200)
        #expect(written.allSatisfy { $0 == expected })
    }
}
