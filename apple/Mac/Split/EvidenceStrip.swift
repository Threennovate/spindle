import SwiftUI
import SpindleKit

/// What a split suggestion is built from, at a glance: the entry's time as a thin line, each prompt as a tick in its
/// project's colour (or its repository's, while it has no project), the agents' attention as thin bars under them, and
/// the time the logs don't account for hatched in the colour of the project it stays on. It draws only what the
/// suggestion already holds, so the editor, the card after a stop and the chip's popover show the same thing.
struct EvidenceStrip: View {
    let suggestion: SplitSuggestion
    let catalog: Catalog
    let time: UserTime
    /// Whether to show the entry's start and end under the strip.
    var showsTimes = true

    static let tickHeight: CGFloat = 9
    static let barHeight: CGFloat = 4
    static let lineHeight: CGFloat = 1

    var body: some View {
        VStack(spacing: 2) {
            Canvas { context, size in draw(in: &context, size: size) }
                .frame(height: Self.tickHeight + 2 + Self.barHeight + 2)
            if showsTimes {
                HStack {
                    Text(time.clock(suggestion.range.start))
                    Spacer()
                    Text(time.clock(suggestion.range.end))
                }
                .font(.caption2)
                .foregroundStyle(.tertiary)
                .monospacedDigit()
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Self.spoken(suggestion, catalog: catalog, time: time))
    }

    private func draw(in context: inout GraphicsContext, size: CGSize) {
        let range = suggestion.range
        guard range.duration > 0, size.width > 0 else { return }
        func x(_ date: Date) -> CGFloat {
            CGFloat(date.timeIntervalSince(range.start) / range.duration) * size.width
        }
        let barY = Self.tickHeight + 2
        // The entry's time, behind everything else.
        context.fill(Path(CGRect(x: 0, y: barY + (Self.barHeight - Self.lineHeight) / 2, width: size.width, height: Self.lineHeight)),
                     with: .color(.primary.opacity(0.25)))

        // Time the logs don't account for, hatched in the colour of the project that keeps it.
        let openColor = suggestion.fallback.map { Self.color($0, catalog: catalog) } ?? .secondary
        for open in suggestion.open {
            let rect = CGRect(x: x(open.start), y: barY - 1.5, width: max(1, x(open.end) - x(open.start)), height: Self.barHeight + 3)
            var hatch = context
            hatch.clip(to: Path(rect))
            hatch.fill(Path(rect), with: .color(openColor.opacity(0.18)))
            var stripes = Path()
            var offset = rect.minX - rect.height
            while offset < rect.maxX {
                stripes.move(to: CGPoint(x: offset, y: rect.maxY))
                stripes.addLine(to: CGPoint(x: offset + rect.height, y: rect.minY))
                offset += 3.5
            }
            hatch.stroke(stripes, with: .color(openColor), lineWidth: 1.25)
        }

        // Attention, as thin bars where the logs place it.
        for piece in suggestion.pieces {
            let rect = CGRect(x: x(piece.range.start), y: barY, width: max(1, x(piece.range.end) - x(piece.range.start)), height: Self.barHeight)
            context.fill(Path(roundedRect: rect, cornerRadius: Self.barHeight / 2), with: .color(Self.color(piece.target, catalog: catalog)))
        }

        // Each prompt, as a tick above its attention.
        for prompt in suggestion.prompts {
            let rect = CGRect(x: min(size.width - 1.5, max(0, x(prompt.date) - 0.75)), y: 0, width: 1.5, height: Self.tickHeight)
            context.fill(Path(roundedRect: rect, cornerRadius: 0.75), with: .color(Self.color(prompt.target, catalog: catalog)))
        }
    }

    /// A target's colour: its project's, or its repository's while it has no project.
    static func color(_ target: SplitSuggestion.Target, catalog: Catalog) -> Color {
        switch target {
        case .project(let id): catalog.project(id).map { Color(hex: $0.color) } ?? .secondary
        case .repository(let repository): ActivityStyle.color(repository.key)
        }
    }

    static func name(_ target: SplitSuggestion.Target, catalog: Catalog) -> String {
        switch target {
        case .project(let id): catalog.project(id)?.name ?? "a project"
        case .repository(let repository): repository.name
        }
    }

    /// The strip in words, for VoiceOver: each part's time and prompts, what the logs leave open, and where the work
    /// moved. "Atlas 50m, 4 prompts; Beacon 1h 10m, 6 prompts. The work moved to Beacon at 09:50."
    static func spoken(_ suggestion: SplitSuggestion, catalog: Catalog, time: UserTime) -> String {
        let touches = suggestion.touches
        let parts = suggestion.parts.map { part in
            let prompts = touches[part.target, default: 0]
            var text = "\(name(part.target, catalog: catalog)) \(Durations.hoursMinutes(part.duration)), \(prompts) \(prompts == 1 ? "prompt" : "prompts")"
            if part.unattributed > 0 {
                text += ", with \(Durations.hoursMinutes(part.unattributed)) the agent logs don't account for"
            }
            return text
        }
        var sentences = ["Agent activity from \(time.clock(suggestion.range.start)) to \(time.clock(suggestion.range.end)): "
            + parts.joined(separator: "; ") + "."]
        if let stretches = suggestion.stretches {
            sentences += stretches.dropFirst().map { "The work moved to \(name($0.target, catalog: catalog)) at \(time.clock($0.range.start))." }
        }
        return sentences.joined(separator: " ")
    }
}

/// The strip with its parts named, for the chip's popover: enough to judge the suggestion without opening the entry.
struct EvidencePopover: View {
    let suggestion: SplitSuggestion
    let catalog: Catalog
    let time: UserTime

    var body: some View {
        let touches = suggestion.touches
        VStack(alignment: .leading, spacing: 8) {
            EvidenceStrip(suggestion: suggestion, catalog: catalog, time: time)
            ForEach(suggestion.parts.indices, id: \.self) { index in
                let part = suggestion.parts[index]
                let prompts = touches[part.target, default: 0]
                HStack(spacing: 6) {
                    Circle().fill(EvidenceStrip.color(part.target, catalog: catalog)).frame(width: 8, height: 8)
                    Text(EvidenceStrip.name(part.target, catalog: catalog)).fontWeight(.medium)
                    Spacer(minLength: 12)
                    Text("\(prompts) \(prompts == 1 ? "prompt" : "prompts") · \(Durations.hoursMinutes(part.duration))")
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                }
                .font(.callout)
            }
            if let open = suggestion.parts.first(where: { $0.unattributed > 0 }) {
                Text("Hatched: \(Durations.hoursMinutes(open.unattributed)) the logs don't account for, which stays on \(EvidenceStrip.name(open.target, catalog: catalog)).")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(12)
        .frame(width: 280)
        .accessibilityElement(children: .combine)
    }
}
