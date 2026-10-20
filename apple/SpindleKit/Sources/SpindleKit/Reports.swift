import Foundation
import SpindleAPI

public typealias ReportGrouping = Components.Schemas.ReportGrouping
public typealias ReportSort = Components.Schemas.ReportSort
public typealias RoundingMode = Components.Schemas.RoundingMode
public typealias SummaryReport = Components.Schemas.SummaryReport
public typealias SummaryGroup = Components.Schemas.SummaryGroup
public typealias DetailedReport = Components.Schemas.DetailedReport
public typealias DetailedEntry = Components.Schemas.DetailedEntry
public typealias WeeklyReport = Components.Schemas.WeeklyReport
public typealias WeeklyRow = Components.Schemas.WeeklyRow
public typealias ReportTotals = Components.Schemas.ReportTotals
public typealias ReportBucket = Components.Schemas.ReportBucket
public typealias SavedReport = Components.Schemas.SavedReportResponse
public typealias Member = Components.Schemas.MemberResponse
public typealias WorkspaceGroup = Components.Schemas.GroupResponse

extension SummaryGroup: Identifiable {
    public var id: String { key }
}

extension WeeklyRow: Identifiable {
    public var id: String { key }
}

extension DetailedEntry: Identifiable {}
extension SavedReport: Identifiable {}
extension Member: Identifiable {
    public var id: String { userId }
}
extension WorkspaceGroup: Identifiable {}

public enum ReportTab: String, Codable, CaseIterable, Sendable {
    case summary, detailed, weekly

    public var label: String {
        switch self {
        case .summary: "Summary"
        case .detailed: "Detailed"
        case .weekly: "Weekly"
        }
    }
}

extension ReportGrouping {
    public var label: String {
        switch self {
        case .project: "Project"
        case .client: "Client"
        case .user: "Member"
        case .tag: "Tag"
        case .task: "Task"
        case .description: "Description"
        }
    }
}

extension ReportSort {
    public var label: String {
        switch self {
        case .date: "Date"
        case .duration: "Duration"
        case .description: "Description"
        case .user: "Member"
        case .project: "Project"
        }
    }
}

extension RoundingMode {
    public var label: String {
        switch self {
        case .none: "No rounding"
        case .up: "Round up"
        case .nearest: "Round to nearest"
        case .down: "Round down"
        }
    }
}

// MARK: - Periods

/// Ready-made periods, as in Toggl, plus any range the person picks.
public enum ReportPeriod: String, Codable, CaseIterable, Sendable {
    case today, yesterday, thisWeek, lastWeek, thisMonth, lastMonth, thisYear, lastYear, custom

    public var label: String {
        switch self {
        case .today: "Today"
        case .yesterday: "Yesterday"
        case .thisWeek: "This week"
        case .lastWeek: "Last week"
        case .thisMonth: "This month"
        case .lastMonth: "Last month"
        case .thisYear: "This year"
        case .lastYear: "Last year"
        case .custom: "Custom"
        }
    }
}

/// A range of whole days, both included.
public struct DayRange: Equatable, Hashable, Sendable {
    public var from: DayKey
    public var to: DayKey

    public init(from: DayKey, to: DayKey) {
        self.from = from
        self.to = to
    }

    public var dayCount: Int {
        from.days(until: to) + 1
    }

    /// The days the period covers, counted from today, with weeks starting on `weekStart` (0 for Sunday).
    public static func resolve(_ period: ReportPeriod, custom: DayRange?, today: DayKey, weekStart: Int) -> DayRange {
        switch period {
        case .today:
            return DayRange(from: today, to: today)
        case .yesterday:
            let yesterday = today.adding(days: -1)
            return DayRange(from: yesterday, to: yesterday)
        case .thisWeek:
            return week(containing: today, weekStart: weekStart)
        case .lastWeek:
            return week(containing: today.adding(days: -7), weekStart: weekStart)
        case .thisMonth:
            return month(containing: today)
        case .lastMonth:
            return month(containing: DayKey(year: today.year, month: today.month, day: 1).adding(days: -1))
        case .thisYear:
            return DayRange(from: DayKey(year: today.year, month: 1, day: 1), to: DayKey(year: today.year, month: 12, day: 31))
        case .lastYear:
            return DayRange(from: DayKey(year: today.year - 1, month: 1, day: 1), to: DayKey(year: today.year - 1, month: 12, day: 31))
        case .custom:
            if let custom, custom.from <= custom.to {
                return custom
            }
            return week(containing: today, weekStart: weekStart)
        }
    }

    public static func week(containing day: DayKey, weekStart: Int) -> DayRange {
        let from = day.adding(days: -((day.weekday - weekStart + 7) % 7))
        return DayRange(from: from, to: from.adding(days: 6))
    }

    static func month(containing day: DayKey) -> DayRange {
        let from = DayKey(year: day.year, month: day.month, day: 1)
        return DayRange(from: from, to: from.addingMonths(1).adding(days: -1))
    }

    /// The period before (-1) or after (1): a month moves by a month and a year by a year, so February follows
    /// January whatever their lengths; anything else moves by its own number of days.
    public func shifted(_ direction: Int) -> DayRange {
        let next = to.adding(days: 1)
        if from.day == 1, next.day == 1 {
            let months = (next.year - from.year) * 12 + next.month - from.month
            let start = from.addingMonths(months * direction)
            return DayRange(from: start, to: start.addingMonths(months).adding(days: -1))
        }
        let days = dayCount * direction
        return DayRange(from: from.adding(days: days), to: to.adding(days: days))
    }

    /// "14 – 20 Sep 2026", "28 Sep – 4 Oct 2026", "28 Dec 2026 – 3 Jan 2027", "Wed, 30 Sep 2026".
    public var label: String {
        if from == to {
            return from.formatted("EEE, d MMM yyyy")
        }
        if from.year != to.year {
            return "\(from.formatted("d MMM yyyy")) – \(to.formatted("d MMM yyyy"))"
        }
        let sameMonth = from.month == to.month
        return "\(from.formatted(sameMonth ? "d" : "d MMM")) – \(to.formatted("d MMM yyyy"))"
    }
}

extension DayKey {
    /// Calendar arithmetic on days alone, the same in every time zone.
    private static let gregorian: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        return calendar
    }()

    private var noon: Date {
        Self.gregorian.date(from: DateComponents(year: year, month: month, day: day, hour: 12))!
    }

    private init(_ date: Date) {
        let parts = Self.gregorian.dateComponents([.year, .month, .day], from: date)
        self.init(year: parts.year!, month: parts.month!, day: parts.day!)
    }

    public func adding(days: Int) -> DayKey {
        DayKey(Self.gregorian.date(byAdding: .day, value: days, to: noon)!)
    }

    func addingMonths(_ months: Int) -> DayKey {
        DayKey(Self.gregorian.date(byAdding: .month, value: months, to: noon)!)
    }

    /// 0 for Sunday, 1 for Monday, and so on.
    public var weekday: Int {
        Self.gregorian.component(.weekday, from: noon) - 1
    }

    public func days(until other: DayKey) -> Int {
        Self.gregorian.dateComponents([.day], from: noon, to: other.noon).day!
    }

    /// The day written with a date pattern, in English, e.g. `EEE d` for "Mon 28".
    public func formatted(_ pattern: String) -> String {
        DateFormatters.pattern(pattern, calendar: Self.gregorian, timeZone: Self.gregorian.timeZone, locale: Locale(identifier: "en_GB"))
            .string(from: noon)
    }
}

// MARK: - A report's settings

/// A report's settings, in the same form as the web app keeps them in its address bar, so a report saved on one
/// opens the same on the other. Everything is optional; what is left out takes its default.
public struct ReportSettings: Codable, Equatable, Sendable {
    public var tab: ReportTab?
    public var period: ReportPeriod?
    /// The days of a custom period, as `yyyy-MM-dd`.
    public var from: String?
    public var to: String?
    public var users: [String]?
    public var groups: [String]?
    public var projects: [String]?
    public var clients: [String]?
    public var tags: [String]?
    /// `yes` or `no`.
    public var billable: String?
    /// Text the descriptions contain.
    public var q: String?
    public var groupBy: ReportGrouping?
    /// A grouping, or `none`.
    public var subGroupBy: String?
    public var sort: ReportSort?
    public var asc: Bool?
    public var page: Int?
    public var rounding: RoundingMode?
    public var roundTo: Int?

    public static let pageSize = 50
    /// In the ID filters, the empty ID stands for "without", e.g. time without a project.
    public static let noneId = "00000000-0000-0000-0000-000000000000"
    public static let roundingMinutes = [1, 5, 6, 10, 12, 15, 30, 60]

    public init() {}

    /// Reads settings as carefully as the web app reads its address bar: what isn't valid is left out.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        func string(_ key: CodingKeys) -> String? {
            (try? container.decodeIfPresent(String.self, forKey: key)).flatMap { $0 }
        }
        func ids(_ key: CodingKeys) -> [String]? {
            let valid = ((try? container.decodeIfPresent([String].self, forKey: key)) ?? nil)?
                .filter { $0.wholeMatch(of: /[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/) != nil }
            guard let valid, !valid.isEmpty else { return nil }
            var seen = Set<String>()
            return valid.filter { seen.insert($0).inserted }
        }
        func day(_ key: CodingKeys) -> String? {
            string(key).flatMap { DayKey($0) != nil ? $0 : nil }
        }
        tab = string(.tab).flatMap(ReportTab.init(rawValue:))
        period = string(.period).flatMap(ReportPeriod.init(rawValue:))
        from = day(.from)
        to = day(.to)
        users = ids(.users)
        groups = ids(.groups)
        projects = ids(.projects)
        clients = ids(.clients)
        tags = ids(.tags)
        billable = string(.billable).flatMap { ["yes", "no"].contains($0) ? $0 : nil }
        q = string(.q).flatMap { $0.trimmingCharacters(in: .whitespaces).isEmpty ? nil : String($0.prefix(200)) }
        groupBy = string(.groupBy).flatMap(ReportGrouping.init(rawValue:))
        subGroupBy = string(.subGroupBy).flatMap { $0 == "none" || ReportGrouping(rawValue: $0) != nil ? $0 : nil }
        sort = string(.sort).flatMap(ReportSort.init(rawValue:))
        asc = (try? container.decodeIfPresent(Bool.self, forKey: .asc)) == true ? true : nil
        page = ((try? container.decodeIfPresent(Int.self, forKey: .page)) ?? nil).flatMap { $0 > 1 ? $0 : nil }
        rounding = string(.rounding).flatMap(RoundingMode.init(rawValue:))
        roundTo = ((try? container.decodeIfPresent(Int.self, forKey: .roundTo)) ?? nil).flatMap { Self.roundingMinutes.contains($0) ? $0 : nil }
    }

    /// Settings saved on the web app or here, or none if they can't be read.
    public init(json: String) {
        self = (try? JSONDecoder().decode(ReportSettings.self, from: Data(json.utf8))) ?? ReportSettings()
        page = nil
    }

    /// What is saved with a report: everything but the page shown.
    public var savedJSON: String {
        var saved = self
        saved.page = nil
        let encoder = JSONEncoder()
        encoder.outputFormatting = .sortedKeys
        return String(decoding: (try? encoder.encode(saved)) ?? Data("{}".utf8), as: UTF8.self)
    }

    /// Whether anything narrows the report down beyond its period.
    public var hasFilters: Bool {
        users != nil || groups != nil || projects != nil || clients != nil || tags != nil || billable != nil || q != nil
    }

    /// The settings with every filter removed, keeping the period, grouping and rounding.
    public var withoutFilters: ReportSettings {
        var settings = self
        settings.users = nil
        settings.groups = nil
        settings.projects = nil
        settings.clients = nil
        settings.tags = nil
        settings.billable = nil
        settings.q = nil
        settings.page = nil
        return settings
    }

    public var currentTab: ReportTab { tab ?? .summary }
    public var currentPeriod: ReportPeriod { period ?? .thisWeek }
    public var currentGroupBy: ReportGrouping { groupBy ?? .project }

    /// The second grouping of the summary: description by default, nothing when grouping by description.
    public var currentSubGroupBy: ReportGrouping? {
        if subGroupBy == "none" { return nil }
        return subGroupBy.flatMap(ReportGrouping.init(rawValue:)) ?? (currentGroupBy == .description ? nil : .description)
    }

    /// The days the report covers. The weekly report always shows a whole week: this or last week, the week of a
    /// custom day, or otherwise the latest week of the chosen period that has begun.
    public func range(today: DayKey, weekStart: Int) -> DayRange {
        let custom = from.flatMap(DayKey.init).flatMap { from in to.flatMap(DayKey.init).map { DayRange(from: from, to: $0) } }
        let range = DayRange.resolve(currentPeriod, custom: custom, today: today, weekStart: weekStart)
        guard currentTab == .weekly else { return range }
        if currentPeriod == .custom {
            return DayRange.week(containing: range.from, weekStart: weekStart)
        }
        return DayRange.week(containing: range.to < today ? range.to : today, weekStart: weekStart)
    }

    /// The filter a report request carries, for these days.
    public func filter(_ range: DayRange) -> ReportFilter {
        ReportFilter(
            from: range.from.description,
            to: range.to.description,
            userIds: users ?? [],
            groupIds: groups ?? [],
            projectIds: projects ?? [],
            clientIds: clients ?? [],
            tagIds: tags ?? [],
            billable: billable == "yes" ? true : billable == "no" ? false : nil,
            description: q,
            rounding: rounding ?? RoundingMode.none,
            roundingMinutes: roundTo ?? 15)
    }
}

/// What every report request narrows the time down to.
public struct ReportFilter: Hashable, Sendable {
    public var from: String
    public var to: String
    public var userIds: [String]
    public var groupIds: [String]
    public var projectIds: [String]
    public var clientIds: [String]
    public var tagIds: [String]
    public var billable: Bool?
    public var description: String?
    public var rounding: RoundingMode
    public var roundingMinutes: Int
}

// MARK: - Showing figures

public enum ReportFormat {
    /// Round hour marks for a chart's axis that reach at least `maxHours`: about four steps of a friendly size.
    public static func hourTicks(_ maxHours: Double) -> [Double] {
        let steps: [Double] = [0.25, 0.5, 1, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 30, 40, 50, 60, 80, 100, 150, 200, 250, 300, 400, 500, 750, 1000, 1500, 2000, 2500, 5000]
        let step = steps.first { $0 * 4 >= maxHours } ?? (maxHours / 4 / 1000).rounded(.up) * 1000
        let count = max(1, Int((maxHours / step).rounded(.up)))
        return (0...count).map { Double($0) * step }
    }

    /// An axis label for a number of hours: 30m, 1h 30m, 8h.
    public static func hourTick(_ hours: Double) -> String {
        if hours == 0 { return "0" }
        let whole = Int(hours.rounded(.down))
        let minutes = Int(((hours - Double(whole)) * 60).rounded())
        if whole == 0 { return "\(minutes)m" }
        return minutes > 0 ? "\(whole)h \(minutes)m" : "\(whole)h"
    }

    /// The share of a whole as a rounded percentage, e.g. 33%, 3.3%.
    public static func percent(_ part: Double, of whole: Double) -> String {
        guard whole > 0 else { return "0%" }
        let value = part / whole * 100
        return value >= 10 || value == 0 ? "\(Int(value.rounded()))%" : String(format: "%.1f%%", value)
    }

    /// An amount in the workspace's currency, or a dash where there is none.
    public static func money(_ amount: Double?, currency: String) -> String {
        guard let amount else { return "—" }
        return amount.formatted(.currency(code: currency).locale(Locale(identifier: "en_GB")).presentation(.narrow))
    }

    /// A group keeps its project's color; the rest get one from the palette by position, and "without" none.
    public static func color(of key: String, color: String?, index: Int) -> String? {
        if let color { return color }
        return key == "none" ? nil : ProjectColors.all[index % ProjectColors.all.count]
    }
}
