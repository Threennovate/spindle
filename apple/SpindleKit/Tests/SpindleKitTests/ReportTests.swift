import Foundation
import Testing
@testable import SpindleKit

/// The same cases as the web app's report tests.
struct ReportPeriodTests {
    // Wednesday, 30 September 2026.
    let today = DayKey("2026-09-30")!

    func range(_ from: String, _ to: String) -> DayRange {
        DayRange(from: DayKey(from)!, to: DayKey(to)!)
    }

    @Test(arguments: [
        (ReportPeriod.today, "2026-09-30", "2026-09-30"),
        (.yesterday, "2026-09-29", "2026-09-29"),
        (.thisWeek, "2026-09-28", "2026-10-04"),
        (.lastWeek, "2026-09-21", "2026-09-27"),
        (.thisMonth, "2026-09-01", "2026-09-30"),
        (.lastMonth, "2026-08-01", "2026-08-31"),
        (.thisYear, "2026-01-01", "2026-12-31"),
        (.lastYear, "2025-01-01", "2025-12-31"),
    ])
    func resolvesPeriods(period: ReportPeriod, from: String, to: String) {
        #expect(DayRange.resolve(period, custom: nil, today: today, weekStart: 1) == range(from, to))
    }

    @Test func startsWeeksOnTheChosenDay() {
        #expect(DayRange.resolve(.thisWeek, custom: nil, today: today, weekStart: 0) == range("2026-09-27", "2026-10-03"))
        #expect(DayRange.week(containing: DayKey("2026-09-27")!, weekStart: 1) == range("2026-09-21", "2026-09-27"))
    }

    @Test func takesACustomRangeAsItIsUnlessItRunsBackwards() {
        #expect(DayRange.resolve(.custom, custom: range("2026-09-03", "2026-09-17"), today: today, weekStart: 1) == range("2026-09-03", "2026-09-17"))
        #expect(DayRange.resolve(.custom, custom: range("2026-09-17", "2026-09-03"), today: today, weekStart: 1) == range("2026-09-28", "2026-10-04"))
        #expect(DayRange.resolve(.lastMonth, custom: nil, today: DayKey("2026-01-15")!, weekStart: 1) == range("2025-12-01", "2025-12-31"))
    }

    @Test func movesWeeksAndCustomRangesByTheirLength() {
        #expect(range("2026-09-28", "2026-10-04").shifted(-1) == range("2026-09-21", "2026-09-27"))
        #expect(range("2026-09-03", "2026-09-05").shifted(1) == range("2026-09-06", "2026-09-08"))
    }

    @Test func movesMonthsAndYearsByTheCalendar() {
        #expect(range("2026-01-01", "2026-01-31").shifted(1) == range("2026-02-01", "2026-02-28"))
        #expect(range("2026-03-01", "2026-03-31").shifted(-1) == range("2026-02-01", "2026-02-28"))
        #expect(range("2026-01-01", "2026-12-31").shifted(-1) == range("2025-01-01", "2025-12-31"))
        #expect(range("2026-11-01", "2027-01-31").shifted(1) == range("2027-02-01", "2027-04-30"))
    }

    @Test func namesRangesCompactly() {
        #expect(range("2026-09-14", "2026-09-20").label == "14 – 20 Sep 2026")
        #expect(range("2026-09-28", "2026-10-04").label == "28 Sep – 4 Oct 2026")
        #expect(range("2026-09-30", "2026-09-30").label == "Wed, 30 Sep 2026")
        #expect(range("2026-12-28", "2027-01-03").label == "28 Dec 2026 – 3 Jan 2027")
        #expect(range("2026-01-01", "2026-12-31").dayCount == 365)
    }

    @Test func theWeeklyReportAlwaysShowsAWholeWeek() {
        var settings = ReportSettings()
        settings.tab = .weekly
        settings.period = .thisMonth
        #expect(settings.range(today: today, weekStart: 1) == range("2026-09-28", "2026-10-04"))
        settings.period = .lastMonth
        #expect(settings.range(today: today, weekStart: 1) == range("2026-08-31", "2026-09-06"))
        settings.period = .custom
        settings.from = "2026-09-16"
        settings.to = "2026-09-20"
        #expect(settings.range(today: today, weekStart: 1) == range("2026-09-14", "2026-09-20"))
    }
}

struct ReportFormatTests {
    @Test func picksFriendlyStepsThatCoverTheTallestBar() {
        #expect(ReportFormat.hourTicks(7.5) == [0, 2, 4, 6, 8])
        #expect(ReportFormat.hourTicks(0.4) == [0, 0.25, 0.5])
        #expect(ReportFormat.hourTicks(0) == [0, 0.25])
        #expect(ReportFormat.hourTicks(160) == [0, 40, 80, 120, 160])
        #expect(ReportFormat.hourTicks(9000).last! >= 9000)
    }

    @Test func labelsHoursAndMinutes() {
        #expect([0, 0.25, 1.5, 8].map(ReportFormat.hourTick) == ["0", "15m", "1h 30m", "8h"])
    }

    @Test func roundsSharesSensibly() {
        #expect(ReportFormat.percent(1, of: 3) == "33%")
        #expect(ReportFormat.percent(1, of: 30) == "3.3%")
        #expect(ReportFormat.percent(0, of: 30) == "0%")
        #expect(ReportFormat.percent(5, of: 0) == "0%")
    }

    @Test func writesMoneyInTheWorkspaceCurrency() {
        #expect(ReportFormat.money(1234.5, currency: "EUR") == "€1,234.50")
        #expect(ReportFormat.money(nil, currency: "EUR") == "—")
    }
}

struct ReportSettingsTests {
    @Test func readsWhatTheWebAppSavedAndDropsWhatIsNotValid() {
        let json = #"{"tab":"detailed","period":"custom","from":"2026-09-01","to":"2026-09-30","projects":["01a0ebf4-6dd2-7e6f-a29e-a07dbef1119b","not-an-id"],"billable":"maybe","q":"  ","groupBy":"client","subGroupBy":"none","sort":"duration","asc":true,"page":3,"rounding":"up","roundTo":7}"#

        let settings = ReportSettings(json: json)

        #expect(settings.currentTab == .detailed)
        #expect(settings.period == .custom)
        #expect(settings.from == "2026-09-01")
        #expect(settings.projects == ["01a0ebf4-6dd2-7e6f-a29e-a07dbef1119b"])
        #expect(settings.billable == nil)
        #expect(settings.q == nil)
        #expect(settings.groupBy == .client)
        #expect(settings.currentSubGroupBy == nil)
        #expect(settings.sort == .duration)
        #expect(settings.asc == true)
        #expect(settings.page == nil)
        #expect(settings.rounding == .up)
        #expect(settings.roundTo == nil)
    }

    @Test func savesTheSameKeysLeavingOutWhatIsNotSet() throws {
        var settings = ReportSettings()
        settings.tab = .summary
        settings.tags = ["01a0ebf4-6dfe-7dc6-944e-ac379b2e2b9e"]
        settings.page = 2

        #expect(settings.savedJSON == #"{"tab":"summary","tags":["01a0ebf4-6dfe-7dc6-944e-ac379b2e2b9e"]}"#)
        #expect(ReportSettings(json: settings.savedJSON).tags == settings.tags)
        #expect(ReportSettings(json: "not json") == ReportSettings())
    }

    @Test func groupsByDescriptionUnderAnythingElseByDefault() {
        var settings = ReportSettings()
        #expect(settings.currentGroupBy == .project)
        #expect(settings.currentSubGroupBy == .description)
        settings.groupBy = .description
        #expect(settings.currentSubGroupBy == nil)
    }

    @Test func buildsTheFilterFromTheSettings() {
        var settings = ReportSettings()
        settings.billable = "no"
        settings.q = "review"
        let filter = settings.filter(DayRange(from: DayKey("2026-09-28")!, to: DayKey("2026-10-04")!))
        #expect(filter.from == "2026-09-28")
        #expect(filter.billable == false)
        #expect(filter.description == "review")
        #expect(filter.rounding == .none)
        #expect(filter.roundingMinutes == 15)
        #expect(settings.hasFilters)
        #expect(!settings.withoutFilters.hasFilters)
    }
}
