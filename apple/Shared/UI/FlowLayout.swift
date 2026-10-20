import SwiftUI

/// Lays views out in a row and wraps onto more rows when they don't fit, e.g. a report's filters in a narrow window.
struct FlowLayout: Layout {
    var spacing: CGFloat = 6
    var lineSpacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let lines = arrange(subviews, width: proposal.width ?? .infinity)
        let width = lines.map(\.width).max() ?? 0
        let height = lines.map(\.height).reduce(0, +) + lineSpacing * CGFloat(max(lines.count - 1, 0))
        return CGSize(width: width, height: height)
    }

    /// Where the first line's middle is, for `.firstLineCenter`.
    func explicitAlignment(of guide: VerticalAlignment, in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGFloat? {
        guard guide == .firstLineCenter, let first = arrange(subviews, width: bounds.width).first else { return nil }
        return bounds.minY + first.height / 2
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var y = bounds.minY
        for line in arrange(subviews, width: bounds.width) {
            var x = bounds.minX
            for index in line.indices {
                let size = subviews[index].sizeThatFits(.unspecified)
                // Centered in the line, so a text field and a menu of different heights line up.
                subviews[index].place(at: CGPoint(x: x, y: y + (line.height - size.height) / 2), proposal: ProposedViewSize(size))
                x += size.width + spacing
            }
            y += line.height + lineSpacing
        }
    }

    private struct Line {
        var indices: [Int] = []
        var width: CGFloat = 0
        var height: CGFloat = 0
    }

    /// Which views go on which line: as many as fit, and always at least one.
    private func arrange(_ subviews: Subviews, width: CGFloat) -> [Line] {
        var lines: [Line] = []
        var line = Line()
        for index in subviews.indices {
            let size = subviews[index].sizeThatFits(.unspecified)
            if !line.indices.isEmpty && line.width + spacing + size.width > width {
                lines.append(line)
                line = Line()
            }
            line.width += (line.indices.isEmpty ? 0 : spacing) + size.width
            line.height = max(line.height, size.height)
            line.indices.append(index)
        }
        if !line.indices.isEmpty {
            lines.append(line)
        }
        return lines
    }
}

extension VerticalAlignment {
    private nonisolated enum FirstLineCenter: AlignmentID {
        static func defaultValue(in context: ViewDimensions) -> CGFloat {
            context[VerticalAlignment.center]
        }
    }

    /// The middle of a flow layout's first line, to line up a view beside it; other views line up by their middle.
    nonisolated static let firstLineCenter = VerticalAlignment(FirstLineCenter.self)
}
