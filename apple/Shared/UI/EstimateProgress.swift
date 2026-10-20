import SwiftUI
import SpindleKit

/// The time tracked against an estimate: "5h of 10h", with a bar that turns red past it.
struct EstimateProgress: View {
    let tracked: Int64
    let estimate: Int64?

    var body: some View {
        if let estimate, estimate > 0 {
            let share = Double(tracked) / Double(estimate)
            HStack(spacing: 8) {
                ProgressView(value: min(share, 1))
                    .tint(share > 1 ? .red : .brand)
                    .frame(maxWidth: 80)
                Text("\(Durations.hoursMinutes(Double(tracked))) of \(Durations.hoursMinutes(Double(estimate)))")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(share > 1 ? .red : .secondary)
                    .lineLimit(1)
            }
        } else {
            Text("—").foregroundStyle(.secondary)
        }
    }
}
