import Charts
import SwiftUI
import SpindleKit

/// Billable and non-billable hours per day, week or month.
struct TimeChart: View {
    let buckets: [ReportBucket]
    let resolution: Components.Schemas.ReportResolution

    private struct Bar: Identifiable {
        let index: Int
        let label: String
        let kind: String
        let hours: Double
        var id: String { "\(index)|\(kind)" }
    }

    var body: some View {
        let bars = buckets.enumerated().flatMap { index, bucket in
            [
                Bar(index: index, label: label(bucket.start), kind: "Billable", hours: Double(bucket.billableSeconds) / 3600),
                Bar(index: index, label: label(bucket.start), kind: "Non-billable", hours: Double(bucket.seconds - bucket.billableSeconds) / 3600),
            ]
        }
        let ticks = ReportFormat.hourTicks((buckets.map { Double($0.seconds) }.max() ?? 0) / 3600)
        // Crowded axes label every few bars.
        let every = max(1, buckets.count / 12)
        Chart(bars) { bar in
            BarMark(x: .value("When", "\(bar.index)"), y: .value("Hours", bar.hours))
                .foregroundStyle(by: .value("Kind", bar.kind))
        }
        .chartForegroundStyleScale(["Billable": Color.primary.opacity(0.72), "Non-billable": Color.primary.opacity(0.2)])
        .chartYScale(domain: 0...(ticks.last ?? 1))
        .chartYAxis {
            AxisMarks(position: .leading, values: ticks) { value in
                AxisGridLine()
                AxisValueLabel { Text(ReportFormat.hourTick(value.as(Double.self) ?? 0)) }
            }
        }
        .chartXAxis {
            AxisMarks { value in
                if let index = Int(value.as(String.self) ?? ""), index % every == 0, index < buckets.count {
                    AxisValueLabel { Text(label(buckets[index].start)) }
                }
            }
        }
        .chartLegend(position: .top, alignment: .trailing)
        .accessibilityLabel("Chart of the time by \(resolution.rawValue)")
    }

    private func label(_ start: String) -> String {
        guard let day = DayKey(start) else { return start }
        switch resolution {
        case .month: return day.formatted(buckets.first.flatMap { DayKey($0.start) }?.year == buckets.last.flatMap { DayKey($0.start) }?.year ? "MMM" : "MMM yy")
        case .week: return day.formatted("d MMM")
        case .day: return day.formatted(buckets.count <= 7 ? "EEE d" : "d")
        }
    }
}

/// Each group's share of the time, around the total.
struct Donut: View {
    let groups: [SummaryGroup]
    let total: Double
    let time: UserTime

    var body: some View {
        Chart(Array(groups.enumerated()), id: \.element.key) { index, group in
            SectorMark(angle: .value("Time", group.seconds), innerRadius: .ratio(0.62), angularInset: 1)
                .foregroundStyle(ReportFormat.color(of: group.key, color: group.color, index: index).map { Color(hex: $0) } ?? Color.secondary.opacity(0.5))
        }
        .chartBackground { _ in
            VStack(spacing: 2) {
                Text(Durations.hoursMinutes(total)).font(.headline.monospacedDigit())
                Text("total").font(.caption).foregroundStyle(.secondary)
            }
        }
        .accessibilityHidden(true)
    }
}

/// One of a report's totals, e.g. the billable time, with a note on what it is.
struct ReportFigure: Identifiable {
    let label: String
    let value: String
    let note: String
    var id: String { label }

    /// The time and billable time (or the billed time, where some was entered separately), and with rates the amount
    /// and what each tracked hour earned, and with labor costs the profit.
    static func list(_ totals: ReportTotals, currency: String, time: UserTime) -> [ReportFigure] {
        let tracked = Double(totals.seconds)
        var figures = [ReportFigure(label: "Total", value: time.duration(tracked), note: totals.entryCount == 1 ? "1 entry" : "\(totals.entryCount) entries")]
        if let billed = totals.billedSeconds, billed != totals.billableSeconds {
            let ratio = tracked > 0 ? String(format: "%.2f× the time tracked · ", Double(billed) / tracked) : ""
            figures.append(ReportFigure(
                label: "Billed", value: (billed < 0 ? "−" : "") + time.duration(Double(abs(billed))),
                note: "\(ratio)\(time.duration(Double(totals.billableSeconds))) billable tracked"))
        } else {
            figures.append(ReportFigure(
                label: "Billable", value: time.duration(Double(totals.billableSeconds)),
                note: "\(ReportFormat.percent(Double(totals.billableSeconds), of: tracked)) of the total"))
        }
        if let amount = totals.amount {
            let note = tracked > 0 ? "\(ReportFormat.money(amount / (tracked / 3600), currency: currency))/h per hour tracked" : "Billed time at its rates"
            figures.append(ReportFigure(label: "Amount", value: ReportFormat.money(amount, currency: currency), note: note))
            // Admins get a cost of 0 when no labor costs are set; there is no profit to speak of then.
            if let cost = totals.laborCost, cost > 0 {
                let profit = amount - cost
                figures.append(ReportFigure(
                    label: "Profit", value: ReportFormat.money(profit, currency: currency),
                    note: "\(ReportFormat.money(cost, currency: currency)) labor cost · \(ReportFormat.percent(max(profit, 0), of: amount)) margin"))
            }
        }
        return figures
    }
}
