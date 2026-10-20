import Foundation
import OpenAPIRuntime
import SpindleAPI

/// A report file's format.
public enum ExportFormat: String, CaseIterable, Sendable {
    case csv, xlsx, pdf

    public var label: String {
        switch self {
        case .csv: "CSV"
        case .xlsx: "Excel"
        case .pdf: "PDF"
        }
    }
}

extension SpindleClient {
    // MARK: - Reports

    /// Where the time went: totals, time over the period, and the time by group and sub-group.
    public func summaryReport(in workspaceId: String, _ settings: ReportSettings, range: DayRange) async throws -> SummaryReport {
        let body = summaryRequest(settings, range: range)
        return try await call { try await api.summaryReport(path: .init(workspaceId: workspaceId), body: .json(body)).ok.body.json }
    }

    /// The entries themselves, a page at a time.
    public func detailedReport(in workspaceId: String, _ settings: ReportSettings, range: DayRange) async throws -> DetailedReport {
        let body = detailedRequest(settings, range: range)
        return try await call { try await api.detailedReport(path: .init(workspaceId: workspaceId), body: .json(body)).ok.body.json }
    }

    /// A week's time by day, a row per group.
    public func weeklyReport(in workspaceId: String, _ settings: ReportSettings, range: DayRange) async throws -> WeeklyReport {
        let body = weeklyRequest(settings, range: range)
        return try await call { try await api.weeklyReport(path: .init(workspaceId: workspaceId), body: .json(body)).ok.body.json }
    }

    /// The report shown, as a file, and the name to give it: `spindle-summary-2026-09-28-to-2026-10-04.pdf`.
    public func exportReport(in workspaceId: String, _ settings: ReportSettings, range: DayRange, format: ExportFormat) async throws -> (data: Data, fileName: String) {
        let path = Operations.ExportSummaryReport.Input.Path(workspaceId: workspaceId)
        let tab = settings.currentTab
        let body: HTTPBody = try await call {
            switch tab {
            case .summary:
                try await api.exportSummaryReport(path: path, query: .init(format: format.rawValue), body: .json(summaryRequest(settings, range: range))).ok.body.file
            case .detailed:
                try await api.exportDetailedReport(
                    path: .init(workspaceId: workspaceId), query: .init(format: format.rawValue), body: .json(detailedRequest(settings, range: range))
                ).ok.body.file
            case .weekly:
                try await api.exportWeeklyReport(
                    path: .init(workspaceId: workspaceId), query: .init(format: format.rawValue), body: .json(weeklyRequest(settings, range: range))
                ).ok.body.file
            }
        }
        let data = try await call { try await Data(collecting: body, upTo: 100 * 1024 * 1024) }
        return (data, "spindle-\(tab.rawValue)-\(range.from)-to-\(range.to).\(format.rawValue)")
    }

    // MARK: - Saved reports

    public func savedReports(in workspaceId: String) async throws -> [SavedReport] {
        try await call { try await api.listSavedReports(path: .init(workspaceId: workspaceId)).ok.body.json }
    }

    /// Saves the settings under a name, as a new report or over `id`. The web app opens it the same way.
    public func saveReport(_ settings: ReportSettings, named name: String, id: String? = nil, in workspaceId: String) async throws -> SavedReport {
        // The kind tells other apps which report it is; the settings say everything.
        let body = Components.Schemas.SavedReportRequest(name: name, kind: .init(rawValue: settings.currentTab.rawValue), parameters: settings.savedJSON)
        return try await call {
            if let id {
                try await api.updateSavedReport(path: .init(workspaceId: workspaceId, reportId: id), body: .json(body)).ok.body.json
            } else {
                try await api.createSavedReport(path: .init(workspaceId: workspaceId), body: .json(body)).created.body.json
            }
        }
    }

    public func deleteSavedReport(_ id: String, in workspaceId: String) async throws {
        try await call { _ = try await api.deleteSavedReport(path: .init(workspaceId: workspaceId, reportId: id)).noContent }
    }

    // MARK: - Who to report on

    /// The workspace's members; admins see everyone.
    public func members(in workspaceId: String) async throws -> [Member] {
        try await call { try await api.listMembers(path: .init(workspaceId: workspaceId)).ok.body.json }
    }

    public func groups(in workspaceId: String) async throws -> [WorkspaceGroup] {
        try await call { try await api.listGroups(path: .init(workspaceId: workspaceId)).ok.body.json }
    }

    // MARK: - Requests

    func summaryRequest(_ settings: ReportSettings, range: DayRange) -> Components.Schemas.SummaryReportRequest {
        let filter = settings.filter(range)
        return .init(
            groupBy: settings.currentGroupBy, subGroupBy: settings.currentSubGroupBy, from: filter.from, to: filter.to,
            userIds: filter.userIds, groupIds: filter.groupIds, projectIds: filter.projectIds, clientIds: filter.clientIds,
            tagIds: filter.tagIds, billable: filter.billable, description: filter.description, rounding: filter.rounding,
            roundingMinutes: Int32(filter.roundingMinutes))
    }

    func detailedRequest(_ settings: ReportSettings, range: DayRange) -> Components.Schemas.DetailedReportRequest {
        let filter = settings.filter(range)
        return .init(
            sort: settings.sort ?? .date, descending: settings.asc != true, page: Int32(settings.page ?? 1), pageSize: Int32(ReportSettings.pageSize),
            from: filter.from, to: filter.to, userIds: filter.userIds, groupIds: filter.groupIds, projectIds: filter.projectIds,
            clientIds: filter.clientIds, tagIds: filter.tagIds, billable: filter.billable, description: filter.description,
            rounding: filter.rounding, roundingMinutes: Int32(filter.roundingMinutes))
    }

    func weeklyRequest(_ settings: ReportSettings, range: DayRange) -> Components.Schemas.WeeklyReportRequest {
        let filter = settings.filter(range)
        // Descriptions make no rows in a weekly report; it shows projects instead.
        let groupBy = settings.currentGroupBy == .description ? ReportGrouping.project : settings.currentGroupBy
        return .init(
            groupBy: groupBy, from: filter.from, to: filter.to, userIds: filter.userIds, groupIds: filter.groupIds,
            projectIds: filter.projectIds, clientIds: filter.clientIds, tagIds: filter.tagIds, billable: filter.billable,
            description: filter.description, rounding: filter.rounding, roundingMinutes: Int32(filter.roundingMinutes))
    }
}

// Each export answers with the file type asked for; the file is the same either way.
extension Operations.ExportSummaryReport.Output.Ok.Body {
    var file: HTTPBody {
        switch self {
        case .csv(let body), .applicationVnd_openxmlformatsOfficedocument_spreadsheetml_sheet(let body), .pdf(let body): body
        }
    }
}

extension Operations.ExportDetailedReport.Output.Ok.Body {
    var file: HTTPBody {
        switch self {
        case .csv(let body), .applicationVnd_openxmlformatsOfficedocument_spreadsheetml_sheet(let body), .pdf(let body): body
        }
    }
}

extension Operations.ExportWeeklyReport.Output.Ok.Body {
    var file: HTTPBody {
        switch self {
        case .csv(let body), .applicationVnd_openxmlformatsOfficedocument_spreadsheetml_sheet(let body), .pdf(let body): body
        }
    }
}
