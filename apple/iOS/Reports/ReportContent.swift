import Charts
import SwiftUI
import SpindleKit

/// The report's totals, two by two.
struct TotalsSection: View {
    let totals: ReportTotals
    let currency: String
    let time: UserTime

    var body: some View {
        let figures = ReportFigure.list(totals, currency: currency, time: time)
        Section {
            Grid(horizontalSpacing: 10, verticalSpacing: 10) {
                ForEach(Array(stride(from: 0, to: figures.count, by: 2)), id: \.self) { index in
                    GridRow {
                        card(figures[index])
                        if index + 1 < figures.count {
                            card(figures[index + 1])
                        } else {
                            Color.clear.gridCellUnsizedAxes([.horizontal, .vertical])
                        }
                    }
                    .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .listRowBackground(Color.clear)
        .listRowInsets(EdgeInsets())
    }

    private func card(_ figure: ReportFigure) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(figure.label).font(.subheadline).foregroundStyle(.secondary)
            Text(figure.value).font(.title3.weight(.semibold).monospacedDigit())
                .lineLimit(1)
                .minimumScaleFactor(0.7)
            Text(figure.note).font(.caption).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .padding(12)
        .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 14))
        .accessibilityElement(children: .combine)
    }
}

// MARK: - Summary

struct SummaryContent: View {
    let report: SummaryReport
    let tracker: Tracker
    @Binding var settings: ReportSettings
    let filtered: Bool
    @State private var open: Set<String> = []
    static let breakdownId = "breakdown"

    private var time: UserTime { tracker.time }

    var body: some View {
        TotalsSection(totals: report.totals, currency: report.currency, time: time)
        Section("Time by \(report.resolution.rawValue)") {
            TimeChart(buckets: report.buckets, resolution: report.resolution)
                .frame(height: 200)
                .padding(.vertical, 8)
        }
        Section {
            if report.groups.isEmpty {
                Text("No time tracked in this period\(filtered ? " with these filters" : "").")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 20)
            } else {
                Donut(groups: report.groups, total: Double(report.totals.seconds), time: time)
                    .frame(width: 190, height: 190)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 8)
                    .id(Self.breakdownId)
                ForEach(Array(report.groups.enumerated()), id: \.element.key) { index, group in
                    groupRow(group, color: ReportFormat.color(of: group.key, color: group.color, index: index), level: 0)
                    if open.contains(group.key) {
                        ForEach(group.subGroups) { sub in
                            groupRow(sub, color: nil, level: 1)
                        }
                    }
                }
            }
        } header: {
            HStack {
                Text("Breakdown")
                Spacer()
                groupingMenu
            }
        }
    }

    private var groupingMenu: some View {
        let groupBy = settings.currentGroupBy
        let subGroupBy = settings.currentSubGroupBy
        return Menu {
            Picker("Group by", selection: Binding(get: { groupBy }, set: { value in
                settings.groupBy = value
                if subGroupBy == value { settings.subGroupBy = "none" }
            })) {
                ForEach(ReportGrouping.allCases, id: \.self) { Text($0.label).tag($0) }
            }
            Picker("Then by", selection: Binding(get: { subGroupBy }, set: { settings.subGroupBy = $0?.rawValue ?? "none" })) {
                Text("Nothing").tag(ReportGrouping?.none)
                ForEach(ReportGrouping.allCases.filter { $0 != groupBy }, id: \.self) { Text($0.label).tag(ReportGrouping?.some($0)) }
            }
        } label: {
            Label(subGroupBy.map { "\(groupBy.label), \($0.label)" } ?? groupBy.label, systemImage: "rectangle.3.group")
                .font(.footnote.weight(.medium))
                .textCase(nil)
        }
    }

    private func groupRow(_ group: SummaryGroup, color: String?, level: Int) -> some View {
        let total = Double(report.totals.seconds)
        let expandable = level == 0 && !group.subGroups.isEmpty
        return Button {
            guard expandable else { return }
            withAnimation(.snappy) {
                if open.contains(group.key) { open.remove(group.key) } else { open.insert(group.key) }
            }
        } label: {
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 8) {
                    if level == 0 {
                        Circle()
                            .fill(color.map { Color(hex: $0) } ?? Color.secondary.opacity(0.5))
                            .frame(width: 10, height: 10)
                    }
                    Text(group.name)
                        .foregroundStyle(group.key == "none" ? .secondary : .primary)
                        .lineLimit(1)
                    if let detail = group.detail {
                        Text(detail).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer(minLength: 8)
                    Text(time.duration(Double(group.seconds)))
                        .monospacedDigit()
                        .fontWeight(level == 0 ? .medium : .regular)
                    if expandable {
                        Image(systemName: "chevron.right")
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.tertiary)
                            .rotationEffect(.degrees(open.contains(group.key) ? 90 : 0))
                    }
                }
                HStack(spacing: 8) {
                    GeometryReader { geometry in
                        Capsule()
                            .fill(Color.primary.opacity(level == 0 ? 0.55 : 0.25))
                            .frame(width: total > 0 ? geometry.size.width * Double(group.seconds) / total : 0)
                    }
                    .frame(height: 5)
                    Text(ReportFormat.percent(Double(group.seconds), of: total))
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                        .frame(width: 44, alignment: .trailing)
                    if report.totals.amount != nil {
                        Text(ReportFormat.money(group.amount, currency: report.currency))
                            .font(.caption.monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                }
            }
            .padding(.leading, level == 1 ? 18 : 0)
            .contentShape(Rectangle())
        }
        .tint(.primary)
        .font(level == 0 ? .body : .subheadline)
        .accessibilityElement(children: .combine)
    }
}

// MARK: - Detailed

struct DetailedContent: View {
    let report: DetailedReport
    let tracker: Tracker
    @Binding var settings: ReportSettings

    private var time: UserTime { tracker.time }
    private var showMoney: Bool { report.totals.amount != nil }
    private var pages: Int { max(1, Int((Double(report.totalCount) / Double(report.pageSize)).rounded(.up))) }

    var body: some View {
        TotalsSection(totals: report.totals, currency: report.currency, time: time)
        Section {
            if report.entries.isEmpty {
                Text("No time entries in this period\(settings.hasFilters ? " with these filters" : "").")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 20)
            }
            ForEach(report.entries) { entry in
                row(entry)
            }
        } header: {
            HStack {
                Text("\(report.totalCount) \(report.totalCount == 1 ? "entry" : "entries")")
                Spacer()
                sortMenu
            }
        } footer: {
            if pages > 1 {
                pager
            }
        }
    }

    private var sortMenu: some View {
        let sort = settings.sort ?? .date
        let descending = settings.asc != true
        return Menu {
            Picker("Sort by", selection: Binding(get: { sort }, set: { value in
                settings.sort = value == .date ? nil : value
                // The date and the duration sort largest first, the rest from A.
                settings.asc = (value == .date || value == .duration) ? nil : true
                settings.page = nil
            })) {
                ForEach([ReportSort.date, .duration, .description, .project] + (tracker.isAdmin ? [.user] : []), id: \.self) { Text($0.label).tag($0) }
            }
            Button(descending ? "Ascending" : "Descending") {
                settings.asc = descending ? true : nil
                settings.page = nil
            }
        } label: {
            Label(sort.label, systemImage: descending ? "arrow.down" : "arrow.up")
                .font(.footnote.weight(.medium))
                .textCase(nil)
        }
    }

    private func row(_ entry: DetailedEntry) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .firstTextBaseline) {
                Text(entry.description.isEmpty ? "No description" : entry.description)
                    .foregroundStyle(entry.description.isEmpty ? .secondary : .primary)
                    .lineLimit(2)
                Spacer(minLength: 8)
                VStack(alignment: .trailing, spacing: 2) {
                    Text(time.duration(Double(entry.seconds))).monospacedDigit().fontWeight(.medium)
                    if showMoney, entry.amount != nil {
                        Text(ReportFormat.money(entry.amount, currency: report.currency))
                            .font(.caption.monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                }
            }
            HStack(spacing: 5) {
                if let name = entry.projectName {
                    ProjectDot(color: entry.projectColor ?? "#888888")
                    Text(name).foregroundStyle(Color(hex: entry.projectColor ?? "#888888")).lineLimit(1)
                    if let detail = [entry.taskName, entry.clientName].compactMap({ $0 }).first {
                        Text("· \(detail)").foregroundStyle(.secondary).lineLimit(1)
                    }
                } else {
                    Text("No project").foregroundStyle(.secondary)
                }
            }
            .font(.subheadline)
            FlowLayout(spacing: 6, lineSpacing: 4) {
                Text("\(time.longDay(time.day(of: entry.start))) · \(time.clock(entry.start)) – \(time.clock(entry.stop))")
                    .monospacedDigit()
                if tracker.isAdmin {
                    Text(entry.userName)
                }
                if entry.isBillable {
                    Image(systemName: "dollarsign")
                }
                ForEach(entry.tags, id: \.self) { TagChip(name: $0) }
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
        .padding(.vertical, 2)
        .accessibilityElement(children: .combine)
    }

    private var pager: some View {
        let page = settings.page ?? 1
        let first = (page - 1) * Int(report.pageSize) + 1
        let last = min(page * Int(report.pageSize), Int(report.totalCount))
        return HStack {
            Button {
                settings.page = page - 1 > 1 ? page - 1 : nil
            } label: {
                Label("Previous", systemImage: "chevron.left")
            }
            .disabled(page <= 1)
            Spacer()
            Text("\(first)–\(last) of \(report.totalCount)").monospacedDigit()
            Spacer()
            Button {
                settings.page = page + 1
            } label: {
                Label("Next", systemImage: "chevron.right").labelStyle(.titleAndIcon)
            }
            .disabled(page >= pages)
        }
        .font(.subheadline)
        .padding(.top, 8)
    }
}

// MARK: - Weekly

struct WeeklyContent: View {
    let report: WeeklyReport
    let tracker: Tracker
    @Binding var settings: ReportSettings

    private var time: UserTime { tracker.time }
    private var showMoney: Bool { report.totals.amount != nil }
    private var rowsBy: ReportGrouping { settings.currentGroupBy == .description ? .project : settings.currentGroupBy }

    var body: some View {
        TotalsSection(totals: report.totals, currency: report.currency, time: time)
        Section {
            if report.rows.isEmpty {
                Text("No time tracked this week\(settings.hasFilters ? " with these filters" : "").")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 20)
            } else {
                // Sideways, when the week is wider than the phone.
                ScrollView(.horizontal) {
                    grid.padding(.vertical, 8)
                }
            }
        } header: {
            HStack {
                Text("By day")
                Spacer()
                Menu {
                    Picker("Rows", selection: Binding(get: { rowsBy }, set: { settings.groupBy = $0 })) {
                        ForEach(ReportGrouping.allCases.filter { $0 != .description }, id: \.self) { Text($0.label).tag($0) }
                    }
                } label: {
                    Label(rowsBy.label, systemImage: "rectangle.3.group")
                        .font(.footnote.weight(.medium))
                        .textCase(nil)
                }
            }
        }
    }

    private var grid: some View {
        Grid(alignment: .trailing, horizontalSpacing: 14, verticalSpacing: 0) {
            GridRow {
                Text(rowsBy.label).gridColumnAlignment(.leading)
                ForEach(report.days, id: \.self) { day in
                    Text(DayKey(day).map { $0.formatted("EEE d") } ?? day)
                }
                Text("Total")
                if showMoney { Text("Amount") }
            }
            .font(.caption.weight(.medium))
            .foregroundStyle(.secondary)
            .padding(.bottom, 6)
            Divider().gridCellUnsizedAxes(.horizontal)
            ForEach(Array(report.rows.enumerated()), id: \.element.key) { index, row in
                GridRow {
                    HStack(spacing: 6) {
                        Circle()
                            .fill(ReportFormat.color(of: row.key, color: row.color, index: index).map { Color(hex: $0) } ?? Color.secondary.opacity(0.5))
                            .frame(width: 8, height: 8)
                        Text(row.name).lineLimit(1).foregroundStyle(row.key == "none" ? .secondary : .primary)
                    }
                    .frame(maxWidth: 160, alignment: .leading)
                    .gridColumnAlignment(.leading)
                    ForEach(Array(row.days.enumerated()), id: \.offset) { _, seconds in
                        cell(seconds)
                    }
                    Text(time.duration(Double(row.seconds))).fontWeight(.semibold)
                    if showMoney {
                        Text(ReportFormat.money(row.amount, currency: report.currency))
                    }
                }
                .font(.subheadline.monospacedDigit())
                .padding(.vertical, 8)
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
            .font(.subheadline.monospacedDigit())
            .padding(.top, 8)
        }
    }

    private func cell(_ seconds: Int64) -> some View {
        Text(seconds > 0 ? time.duration(Double(seconds)) : "–")
            .foregroundStyle(seconds > 0 ? .primary : .secondary)
    }
}
