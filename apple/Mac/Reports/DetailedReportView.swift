import SwiftUI
import SpindleKit

/// The entries themselves, sorted as asked and a page at a time.
struct DetailedReportView: View {
    let report: DetailedReport
    let tracker: Tracker
    @Binding var settings: ReportSettings

    private var time: UserTime { tracker.time }
    private var showMoney: Bool { report.totals.amount != nil }
    private var pages: Int { max(1, Int((Double(report.totalCount) / Double(report.pageSize)).rounded(.up))) }
    private var pageSize: Int { Int(report.pageSize) }
    private var totalCount: Int { Int(report.totalCount) }
    /// The room the list has, to choose between columns and a compact row per entry.
    @State private var width: CGFloat = 0

    /// What the columns need; in less room, each entry gets a few lines instead.
    private var columnsWidth: CGFloat {
        180 + 170 + 150 + 80 + 12 * 3 + (tracker.isAdmin ? 130 + 12 : 0) + (showMoney ? 90 + 12 : 0)
    }

    private var compact: Bool { width > 0 && width < columnsWidth }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                ReportTotalsView(totals: report.totals, currency: report.currency, time: time)
                VStack(spacing: 0) {
                    if compact {
                        compactHeader
                    } else {
                        header
                    }
                    Divider()
                    if report.entries.isEmpty {
                        Text("No time entries in this period\(settings.hasFilters ? " with these filters" : "").")
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 40)
                    }
                    ForEach(report.entries) { entry in
                        if compact {
                            compactRow(entry)
                        } else {
                            row(entry)
                        }
                        Divider()
                    }
                    if pages > 1 {
                        pager.padding(.top, 10)
                    }
                }
                .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { width = $0 }
                .padding(16)
                .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 12))
                .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.separator))
            }
            .padding(20)
        }
    }

    private var header: some View {
        HStack(spacing: 12) {
            sortButton(.description, "Description").frame(maxWidth: .infinity, alignment: .leading)
            sortButton(.project, "Project").frame(width: 170, alignment: .leading)
            if tracker.isAdmin {
                sortButton(.user, "Member").frame(width: 130, alignment: .leading)
            }
            sortButton(.date, "Date").frame(width: 150, alignment: .leading)
            sortButton(.duration, "Duration").frame(width: 80, alignment: .trailing)
            if showMoney {
                Text("Amount").frame(width: 90, alignment: .trailing)
            }
        }
        .font(.caption.weight(.medium))
        .foregroundStyle(.secondary)
        .padding(.bottom, 6)
    }

    /// The sorts, without columns to head.
    private var compactHeader: some View {
        HStack(spacing: 14) {
            Text("Sort by")
            sortButton(.date, "Date")
            sortButton(.description, "Description")
            sortButton(.project, "Project")
            if tracker.isAdmin {
                sortButton(.user, "Member")
            }
            Spacer(minLength: 8)
            sortButton(.duration, "Duration").frame(width: 80, alignment: .trailing)
            if showMoney {
                Text("Amount").frame(width: 90, alignment: .trailing)
            }
        }
        .font(.caption.weight(.medium))
        .foregroundStyle(.secondary)
        .padding(.bottom, 6)
    }

    /// An entry on a few lines: what, the project, and when and who, with the duration beside it.
    private func compactRow(_ entry: DetailedEntry) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(entry.description.isEmpty ? "No description" : entry.description)
                    .foregroundStyle(entry.description.isEmpty ? .secondary : .primary)
                    .lineLimit(2)
                HStack(spacing: 5) {
                    if let name = entry.projectName {
                        ProjectDot(color: entry.projectColor ?? "#888888")
                        Text(name).lineLimit(1)
                        if let detail = [entry.taskName, entry.clientName].compactMap({ $0 }).first {
                            Text(detail).foregroundStyle(.secondary).lineLimit(1)
                        }
                    } else {
                        Text("No project").foregroundStyle(.secondary)
                    }
                }
                .font(.callout)
                FlowLayout(spacing: 6, lineSpacing: 4) {
                    Text("\(time.longDay(time.day(of: entry.start))) · \(time.clock(entry.start)) – \(time.clock(entry.stop))")
                        .monospacedDigit()
                    if tracker.isAdmin {
                        Text(entry.userName)
                    }
                    if entry.isBillable {
                        Image(systemName: "dollarsign").help("Billable")
                    }
                    ForEach(entry.tags, id: \.self) { TagChip(name: $0) }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Text(time.duration(Double(entry.seconds)))
                .monospacedDigit()
                .frame(width: 80, alignment: .trailing)
            if showMoney {
                Text(ReportFormat.money(entry.amount, currency: report.currency))
                    .monospacedDigit()
                    .frame(width: 90, alignment: .trailing)
            }
        }
        .padding(.vertical, 8)
        .accessibilityElement(children: .combine)
    }

    /// Sorts by a column; the date and the duration sort largest first the first time.
    private func sortButton(_ sort: ReportSort, _ label: String) -> some View {
        let active = (settings.sort ?? .date) == sort
        let descending = settings.asc != true
        return Button {
            let nextDescending = active ? !descending : (sort == .date || sort == .duration)
            settings.sort = sort == .date ? nil : sort
            settings.asc = nextDescending ? nil : true
            settings.page = nil
        } label: {
            HStack(spacing: 3) {
                Text(label)
                if active {
                    Image(systemName: descending ? "chevron.down" : "chevron.up").font(.caption2)
                }
            }
            .foregroundStyle(active ? Color.primary : .secondary)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(active ? "\(label), sorted \(descending ? "descending" : "ascending")" : "Sort by \(label)")
    }

    private func row(_ entry: DetailedEntry) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            VStack(alignment: .leading, spacing: 3) {
                Text(entry.description.isEmpty ? "No description" : entry.description)
                    .foregroundStyle(entry.description.isEmpty ? .secondary : .primary)
                    .lineLimit(2)
                if !entry.tags.isEmpty || entry.isBillable {
                    HStack(spacing: 4) {
                        if entry.isBillable {
                            Image(systemName: "dollarsign").font(.caption).foregroundStyle(.secondary).help("Billable")
                        }
                        ForEach(entry.tags, id: \.self) { TagChip(name: $0) }
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            HStack(spacing: 5) {
                if let name = entry.projectName {
                    ProjectDot(color: entry.projectColor ?? "#888888")
                    VStack(alignment: .leading, spacing: 1) {
                        Text(name).lineLimit(1)
                        if let detail = [entry.taskName, entry.clientName].compactMap({ $0 }).first {
                            Text(detail).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                        }
                    }
                } else {
                    Text("No project").foregroundStyle(.secondary)
                }
            }
            .frame(width: 170, alignment: .leading)
            if tracker.isAdmin {
                Text(entry.userName).lineLimit(1).frame(width: 130, alignment: .leading)
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(time.longDay(time.day(of: entry.start)))
                Text("\(time.clock(entry.start)) – \(time.clock(entry.stop))")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .monospacedDigit()
            .frame(width: 150, alignment: .leading)
            Text(time.duration(Double(entry.seconds)))
                .monospacedDigit()
                .frame(width: 80, alignment: .trailing)
            if showMoney {
                Text(ReportFormat.money(entry.amount, currency: report.currency))
                    .monospacedDigit()
                    .frame(width: 90, alignment: .trailing)
            }
        }
        .padding(.vertical, 8)
        .accessibilityElement(children: .combine)
    }

    private var pager: some View {
        let page = settings.page ?? 1
        let first = (page - 1) * pageSize + 1
        let last = min(page * pageSize, totalCount)
        return HStack {
            Text("\(first)–\(last) of \(totalCount) entries")
                .font(.callout)
                .foregroundStyle(.secondary)
            Spacer()
            Button {
                settings.page = page - 1 > 1 ? page - 1 : nil
            } label: {
                Label("Previous", systemImage: "chevron.left")
            }
            .disabled(page <= 1)
            Text("Page \(page) of \(pages)").font(.callout).monospacedDigit()
            Button {
                settings.page = page + 1
            } label: {
                Label("Next", systemImage: "chevron.right").labelStyle(.titleAndIcon)
            }
            .disabled(page >= pages)
        }
    }
}

/// A week's time by day, a row per group, with the totals of each row and day.
struct WeeklyReportView: View {
    let report: WeeklyReport
    let tracker: Tracker
    @Binding var settings: ReportSettings

    private var time: UserTime { tracker.time }
    private var showMoney: Bool { report.totals.amount != nil }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                ReportTotalsView(totals: report.totals, currency: report.currency, time: time)
                VStack(alignment: .leading, spacing: 12) {
                    HStack(spacing: 6) {
                        Text("Rows").foregroundStyle(.secondary)
                        Picker("Rows", selection: Binding(
                            get: { settings.currentGroupBy == .description ? .project : settings.currentGroupBy },
                            set: { settings.groupBy = $0 })) {
                            ForEach(ReportGrouping.allCases.filter { $0 != .description }, id: \.self) { Text($0.label).tag($0) }
                        }
                        .labelsHidden()
                        .fixedSize()
                    }
                    .font(.callout)
                    // Scrolls sideways when the week doesn't fit.
                    ViewThatFits(in: .horizontal) {
                        grid
                        ScrollView(.horizontal) {
                            grid.padding(.bottom, 12)
                        }
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(16)
                .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 12))
                .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.separator))
            }
            .padding(20)
        }
    }

    private var grid: some View {
        Grid(alignment: .trailing, horizontalSpacing: 12, verticalSpacing: 0) {
            GridRow {
                Text((settings.currentGroupBy == .description ? ReportGrouping.project : settings.currentGroupBy).label)
                    .gridColumnAlignment(.leading)
                ForEach(report.days, id: \.self) { day in
                    Text(DayKey(day).map { $0.formatted("EEE d") } ?? day)
                }
                Text("Total")
                if showMoney {
                    Text("Amount")
                }
            }
            .font(.caption.weight(.medium))
            .foregroundStyle(.secondary)
            .padding(.bottom, 6)
            Divider().gridCellUnsizedAxes(.horizontal)
            if report.rows.isEmpty {
                GridRow {
                    Text("No time tracked this week\(settings.hasFilters ? " with these filters" : "").")
                        .foregroundStyle(.secondary)
                        .gridCellColumns(report.days.count + 2 + (showMoney ? 1 : 0))
                        .padding(.vertical, 30)
                }
            }
            ForEach(Array(report.rows.enumerated()), id: \.element.key) { index, row in
                GridRow {
                    HStack(spacing: 6) {
                        Circle()
                            .fill(ReportFormat.color(of: row.key, color: row.color, index: index).map { Color(hex: $0) } ?? Color.secondary.opacity(0.5))
                            .frame(width: 8, height: 8)
                        Text(row.name).lineLimit(1).foregroundStyle(row.key == "none" ? .secondary : .primary)
                        if let detail = row.detail {
                            Text(detail).foregroundStyle(.secondary).lineLimit(1)
                        }
                    }
                    // The names take the room the numbers leave, so the table fills the card.
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .gridColumnAlignment(.leading)
                    ForEach(Array(row.days.enumerated()), id: \.offset) { _, seconds in
                        cell(seconds)
                    }
                    Text(time.duration(Double(row.seconds))).fontWeight(.semibold)
                    if showMoney {
                        Text(ReportFormat.money(row.amount, currency: report.currency))
                    }
                }
                .monospacedDigit()
                .padding(.vertical, 7)
                Divider().gridCellUnsizedAxes(.horizontal)
            }
            GridRow {
                Text("Total").fontWeight(.semibold).gridColumnAlignment(.leading)
                ForEach(Array(report.dayTotals.enumerated()), id: \.offset) { _, seconds in
                    cell(seconds).fontWeight(.semibold)
                }
                Text(time.duration(Double(report.totals.seconds))).fontWeight(.bold)
                if showMoney {
                    Text(ReportFormat.money(report.totals.amount, currency: report.currency)).fontWeight(.semibold)
                }
            }
            .monospacedDigit()
            .padding(.top, 8)
        }
    }

    private func cell(_ seconds: Int64) -> some View {
        Text(seconds > 0 ? time.duration(Double(seconds)) : "–")
            .foregroundStyle(seconds > 0 ? .primary : .secondary)
    }
}
