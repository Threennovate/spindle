import SwiftUI
import SpindleKit

extension Catalog {
    /// The color a part is shown in: its project's, or grey without one.
    func color(of part: SplitPart) -> Color {
        project(part.projectId).map { Color(hex: $0.color) } ?? .secondary
    }

    func name(of part: SplitPart) -> String {
        guard let project = project(part.projectId) else { return "No project" }
        return task(part.taskId).map { "\(project.name) · \($0.name)" } ?? project.name
    }

    /// "Website 60%, Research 40%", for labels read aloud.
    func describe(_ parts: [SplitPart]) -> String {
        parts.map { "\(name(of: $0)) \($0.percent)%" }.joined(separator: ", ")
    }
}

extension Catalog {
    /// The project under an entry's description in the calendar, in its colour; a split's projects each in theirs,
    /// with their shares.
    func projectLine(_ project: Project?, parts: [SplitPart]?, in scheme: ColorScheme) -> Text {
        guard let parts else {
            return Text(project?.name ?? "No project").fontWeight(.medium).foregroundStyle(Color.readable(project?.color, in: scheme))
        }
        return parts.indices.reduce(Text("")) { line, index in
            let part = parts[index]
            let name = Text(self.project(part.projectId)?.name ?? "No project")
                .fontWeight(.medium)
                .foregroundStyle(Color.readable(self.project(part.projectId)?.color, in: scheme))
            let share = Text(" \(part.percent)%").foregroundStyle(.secondary)
            return (index == 0 ? Text("") : line + Text(" · ").foregroundStyle(.secondary)) + name + share
        }
        .monospacedDigit()
    }
}

extension Color {
    /// A project's colour for text on its tint: drawn a quarter of the way towards the text colour, so light colours
    /// still read on a light tint and dark ones on graphite.
    static func readable(_ hex: String?, in scheme: ColorScheme) -> Color {
        guard let hex else { return .secondary }
        let digits = hex.hasPrefix("#") ? String(hex.dropFirst()) : hex
        let value = UInt32(digits, radix: 16) ?? 0x808080
        let target: Double = scheme == .dark ? 0.95 : 0.08
        func channel(_ shift: UInt32) -> Double { Double((value >> shift) & 0xFF) / 255 * 0.75 + target * 0.25 }
        return Color(.sRGB, red: channel(16), green: channel(8), blue: channel(0))
    }
}

/// Where a split entry's time went, as a thread along the bottom of its calendar block: a segment per project, as
/// long as its share, as in the split bars elsewhere.
struct SplitThread: View {
    let catalog: Catalog
    let parts: [SplitPart]

    var body: some View {
        GeometryReader { geometry in
            let gap: CGFloat = 2
            let room = geometry.size.width - gap * CGFloat(parts.count - 1)
            HStack(spacing: gap) {
                ForEach(parts.indices, id: \.self) { index in
                    Capsule()
                        .fill(catalog.color(of: parts[index]))
                        .frame(width: max(room * CGFloat(parts[index].percent) / 100, 3))
                }
            }
        }
        .frame(height: 3)
        .accessibilityHidden(true)
    }
}

/// Overlapping dots, one for each part's project.
struct SplitDots: View {
    let catalog: Catalog
    let parts: [SplitPart]
    var size: CGFloat = 9

    var body: some View {
        HStack(spacing: -size * 0.35) {
            ForEach(parts.indices, id: \.self) { index in
                Circle()
                    .fill(catalog.color(of: parts[index]))
                    .frame(width: size, height: size)
                    .overlay(Circle().strokeBorder(.background, lineWidth: 1.5))
            }
        }
        .accessibilityHidden(true)
    }
}

/// A thin bar with a segment per part, as wide as its share of the time.
struct SplitMeter: View {
    let catalog: Catalog
    let parts: [SplitPart]

    var body: some View {
        GeometryReader { geometry in
            HStack(spacing: 1) {
                ForEach(parts.indices, id: \.self) { index in
                    catalog.color(of: parts[index])
                        .frame(width: max(0, geometry.size.width * CGFloat(parts[index].percent) / 100 - 1))
                }
            }
        }
        .frame(height: 4)
        .clipShape(Capsule())
        .accessibilityHidden(true)
    }
}

/// "●● Website 60% · Research 40%": where a split entry's time went.
struct SplitLabel: View {
    let catalog: Catalog
    let parts: [SplitPart]

    var body: some View {
        HStack(spacing: 6) {
            SplitDots(catalog: catalog, parts: parts)
            parts.indices.reduce(Text("")) { text, index in
                let part = parts[index]
                let name = Text(catalog.project(part.projectId)?.name ?? "No project")
                    .fontWeight(.medium)
                    .foregroundColor(catalog.color(of: part))
                let percent = Text(" \(part.percent)%").foregroundColor(.secondary)
                return index == 0 ? name + percent : text + Text(" · ").foregroundColor(.secondary) + name + percent
            }
            .lineLimit(1)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Split: \(catalog.describe(parts))")
    }
}
