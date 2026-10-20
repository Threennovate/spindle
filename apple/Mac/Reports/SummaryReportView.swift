import Charts
import SwiftUI
import SpindleKit

/// Where the time went: totals, a chart over the period, and the time by group and sub-group.
struct SummaryReportView: View {
    let report: SummaryReport
    let tracker: Tracker
    let groupBy: ReportGrouping
    let subGroupBy: ReportGrouping?
    let filtered: Bool
    let onGrouping: (ReportGrouping, ReportGrouping?) -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                ReportTotalsView(totals: report.totals, currency: report.currency, time: tracker.time)
                Card(title: "Time by \(report.resolution.rawValue)") {
                    TimeChart(buckets: report.buckets, resolution: report.resolution)
                        .frame(height: 220)
                }
                Card(title: "Breakdown") {
                    groupingPickers
                } content: {
                    if report.groups.isEmpty {
                        Text("No time tracked in this period\(filtered ? " with these filters" : "").")
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 40)
                    } else {
                        // Side by side when there's room, otherwise the chart above the table.
                        ViewThatFits(in: .horizontal) {
                            HStack(alignment: .top, spacing: 28) {
                                donut
                                table
                            }
                            VStack(spacing: 20) {
                                donut
                                table
                            }
                        }
                    }
                }
            }
            .padding(20)
        }
    }

    private var donut: some View {
        Donut(groups: report.groups, total: Double(report.totals.seconds), time: tracker.time)
            .frame(width: 180, height: 180)
    }

    private var table: some View {
        GroupTable(
            groups: report.groups, total: Double(report.totals.seconds), currency: report.currency,
            showMoney: report.totals.amount != nil, grouping: groupBy, time: tracker.time)
    }

    private var groupingPickers: some View {
        HStack(spacing: 6) {
            Text("Group by").foregroundStyle(.secondary)
            Picker("Group by", selection: Binding(get: { groupBy }, set: { onGrouping($0, subGroupBy == $0 ? nil : subGroupBy) })) {
                ForEach(ReportGrouping.allCases, id: \.self) { Text($0.label).tag($0) }
            }
            .labelsHidden()
            .fixedSize()
            Text("then").foregroundStyle(.secondary)
            Picker("Then by", selection: Binding(get: { subGroupBy }, set: { onGrouping(groupBy, $0) })) {
                Text("Nothing").tag(ReportGrouping?.none)
                ForEach(ReportGrouping.allCases.filter { $0 != groupBy }, id: \.self) { Text($0.label).tag(ReportGrouping?.some($0)) }
            }
            .labelsHidden()
            .fixedSize()
        }
        .font(.callout)
    }
}

/// A titled panel of a report.
struct Card<Accessory: View, Content: View>: View {
    let title: String
    @ViewBuilder var accessory: Accessory
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text(title).font(.headline)
                Spacer()
                accessory
            }
            content
        }
        .padding(16)
        .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.separator))
    }
}

extension Card where Accessory == EmptyView {
    init(title: String, @ViewBuilder content: () -> Content) {
        self.init(title: title, accessory: { EmptyView() }, content: content)
    }
}

/// The report's totals: time, billable time, and money when the workspace has rates.
struct ReportTotalsView: View {
    let totals: ReportTotals
    let currency: String
    let time: UserTime

    private var figures: [ReportFigure] {
        ReportFigure.list(totals, currency: currency, time: time)
    }

    var body: some View {
        let figures = figures
        // All in a row when there's room, otherwise two by two.
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 12) {
                ForEach(figures) { figure($0) }
            }
            // As tall as the tallest, when a note takes two lines.
            .fixedSize(horizontal: false, vertical: true)
            Grid(horizontalSpacing: 12, verticalSpacing: 12) {
                ForEach(Array(stride(from: 0, to: figures.count, by: 2)), id: \.self) { index in
                    GridRow {
                        figure(figures[index])
                        if index + 1 < figures.count {
                            figure(figures[index + 1])
                        } else {
                            Color.clear.gridCellUnsizedAxes([.horizontal, .vertical])
                        }
                    }
                    .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }

    private func figure(_ figure: ReportFigure) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(figure.label).font(.callout).foregroundStyle(.secondary)
            Text(figure.value).font(.title2.weight(.semibold).monospacedDigit())
            Text(figure.note).font(.caption).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .padding(14)
        .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.separator))
        .accessibilityElement(children: .combine)
    }
}

/// The time by group, with the share of each and, opened, its sub-groups.
private struct GroupTable: View {
    let groups: [SummaryGroup]
    let total: Double
    let currency: String
    let showMoney: Bool
    let grouping: ReportGrouping
    let time: UserTime
    @State private var open: Set<String> = []

    var body: some View {
        let expandable = groups.contains { !$0.subGroups.isEmpty }
        let allOpen = expandable && groups.allSatisfy { $0.subGroups.isEmpty || open.contains($0.key) }
        VStack(spacing: 0) {
            HStack {
                Text(grouping.label)
                if expandable {
                    Button(allOpen ? "Collapse all" : "Expand all") {
                        open = allOpen ? [] : Set(groups.map(\.key))
                    }
                    .buttonStyle(.brandLink)
                }
                Spacer()
                Text("Duration").frame(width: 90, alignment: .trailing)
                Text("Share").frame(width: 150, alignment: .leading).padding(.leading, 12)
                if showMoney {
                    Text("Amount").frame(width: 100, alignment: .trailing)
                }
            }
            .font(.caption.weight(.medium))
            .foregroundStyle(.secondary)
            .padding(.bottom, 6)
            Divider()
            ForEach(Array(groups.enumerated()), id: \.element.key) { index, group in
                row(group, color: ReportFormat.color(of: group.key, color: group.color, index: index), level: 0)
                if open.contains(group.key) {
                    ForEach(group.subGroups) { sub in
                        row(sub, color: nil, level: 1)
                    }
                }
                Divider()
            }
        }
    }

    private func row(_ group: SummaryGroup, color: String?, level: Int) -> some View {
        HStack(spacing: 8) {
            if level == 0 {
                Button {
                    if open.contains(group.key) { open.remove(group.key) } else { open.insert(group.key) }
                } label: {
                    Image(systemName: "chevron.right")
                        .font(.caption.weight(.semibold))
                        .rotationEffect(.degrees(open.contains(group.key) ? 90 : 0))
                        .foregroundStyle(.secondary)
                        .frame(width: 12)
                }
                .buttonStyle(.plain)
                .opacity(group.subGroups.isEmpty ? 0 : 1)
                .disabled(group.subGroups.isEmpty)
                .accessibilityLabel(open.contains(group.key) ? "Collapse \(group.name)" : "Expand \(group.name)")
                Circle()
                    .fill(color.map { Color(hex: $0) } ?? Color.secondary.opacity(0.5))
                    .frame(width: 9, height: 9)
            } else {
                Color.clear.frame(width: 29, height: 1)
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
                .frame(width: 90, alignment: .trailing)
            HStack(spacing: 8) {
                GeometryReader { geometry in
                    Capsule()
                        .fill(Color.primary.opacity(level == 0 ? 0.55 : 0.25))
                        .frame(width: total > 0 ? geometry.size.width * Double(group.seconds) / total : 0)
                }
                .frame(height: 6)
                Text(ReportFormat.percent(Double(group.seconds), of: total))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                    .frame(width: 40, alignment: .trailing)
            }
            .frame(width: 150)
            .padding(.leading, 12)
            if showMoney {
                Text(ReportFormat.money(group.amount, currency: currency))
                    .monospacedDigit()
                    .frame(width: 100, alignment: .trailing)
            }
        }
        .font(level == 0 ? .body : .callout)
        .padding(.vertical, 7)
        .accessibilityElement(children: .combine)
    }
}
