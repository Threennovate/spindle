import Testing
@testable import SpindleKit

@Suite struct DurationsTests {
    @Test func writesDurationsLikeTheWebApp() {
        #expect(Durations.clock(309) == "0:05:09")
        #expect(Durations.clock(482_400) == "134:00:00")
        #expect(Durations.clock(-3) == "0:00:00")
        #expect(Durations.format(5400, as: .hms) == "1:30:00")
        #expect(Durations.format(5459, as: .hm) == "1:30")
        #expect(Durations.format(5400, as: .decimal) == "1.50 h")
        #expect(Durations.hoursMinutes(43_500) == "12h 05m")
        #expect(Durations.hoursMinutes(2_700) == "45m")
    }

    @Test(arguments: [
        ("1:30", 5400), ("1:30:15", 5415), ("1h 30m", 5400), ("90m", 5400), ("45s", 45), ("2h", 7200),
        ("1.5", 5400), ("1,5", 5400), ("90", 5400), ("1.5h", 5400), ("30 min", 1800),
    ])
    func readsTypedDurations(text: String, seconds: Int) {
        #expect(Durations.parse(text) == seconds)
    }

    @Test(arguments: ["", "abc", "1:75", "-5"])
    func rejectsWhatIsNotADuration(text: String) {
        #expect(Durations.parse(text) == nil)
    }

    /// Digits held down in a field are too many to count, not a crash.
    @Test(arguments: [
        "999999999999999999999:00", "1000000000000000000", "99999999999999999999h", "9999999999999999999m",
        "9999999999999999999999999.5", String(repeating: "9", count: 400),
    ])
    func rejectsDurationsTooLongToCount(text: String) {
        #expect(Durations.parse(text) == nil)
    }

    /// The same cases as the web app's `parseClock` tests.
    @Test(arguments: [
        ("9", 9, 0), ("09", 9, 0), ("21", 21, 0), ("930", 9, 30), ("0930", 9, 30), ("2130", 21, 30), ("005", 0, 5),
        ("9:30", 9, 30), ("09.30", 9, 30), ("9,30", 9, 30), ("9 30", 9, 30), ("9h30", 9, 30), ("9h", 9, 0), ("9:", 9, 0),
        ("9:5", 9, 5), ("21:30", 21, 30), ("9:30pm", 21, 30), ("930pm", 21, 30), ("9 am", 9, 0), ("9p", 21, 0),
        ("9:30 p.m.", 21, 30), ("12am", 0, 0), ("12:15 pm", 12, 15), ("0", 0, 0), ("  7:45  ", 7, 45),
    ])
    func readsClockTimes(text: String, hours: Int, minutes: Int) {
        #expect(ClockTime.parse(text)! == (hours, minutes))
    }

    @Test(arguments: ["", "24", "93", "960", "2400", "9:60", "13pm", "0am", "noon", "12345", "930:15", "9::30", "-15"])
    func rejectsWhatIsNotAClockTime(text: String) {
        #expect(ClockTime.parse(text) == nil)
    }

    @Test func writesClockTimesForFields() {
        #expect(ClockTime.field((9, 5), hour12: false) == "09:05")
        #expect(ClockTime.field((0, 0), hour12: true) == "12:00 am")
        #expect(ClockTime.field((12, 15), hour12: true) == "12:15 pm")
        #expect(ClockTime.field((21, 30), hour12: true) == "9:30 pm")
        for hour12 in [false, true] {
            for total in stride(from: 0, to: 1440, by: 7) {
                #expect(ClockTime.parse(ClockTime.field((total / 60, total % 60), hour12: hour12))! == (total / 60, total % 60))
            }
        }
    }

    @Test func movesClockTimesRoundMidnight() {
        #expect(ClockTime.adding(1, to: (9, 59)) == (10, 0))
        #expect(ClockTime.adding(-15, to: (9, 0)) == (8, 45))
        #expect(ClockTime.adding(15, to: (23, 50)) == (0, 5))
        #expect(ClockTime.adding(-15, to: (0, 5)) == (23, 50))
    }
}
