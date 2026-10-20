import SwiftUI
import SpindleKit

/// Splits an entry's time between projects, as the Mac and the web do: a bar whose dividers are dragged, and a row per
/// part that opens its project, share and billable flag. Removing all but one part ends the split.
struct SplitSection: View {
    let tracker: Tracker
    @Binding var parts: [SplitPart]
    /// The entry's whole duration, to show each part's time.
    let seconds: TimeInterval
    /// A running entry's duration keeps changing, so its parts show their share only.
    let running: Bool
    var errors: FormErrors

    private var catalog: Catalog { tracker.catalog }
    private var percents: [Int] { parts.map(\.percent) }

    var body: some View {
        Section {
            TouchSplitBar(catalog: catalog, parts: parts) { setPercents($0) }
                .listRowInsets(EdgeInsets(top: 12, leading: 16, bottom: 12, trailing: 16))

            let minutes = SplitMath.allocate(Int((seconds / 60).rounded()), percents)
            ForEach(parts.indices, id: \.self) { index in
                NavigationLink {
                    SplitPartView(tracker: tracker, parts: $parts, index: index, seconds: seconds, running: running)
                } label: {
                    row(index, seconds: TimeInterval(minutes[index] * 60))
                }
                .swipeActions {
                    Button("Remove", role: .destructive) { remove(index) }
                }
            }

            NavigationLink {
                ProjectPickerView(tracker: tracker, projectId: nil, taskId: nil, onChange: add, title: "Add to Split", taken: SplitSection.taken(in: parts, except: nil), choosingAnew: true)
            } label: {
                Label("Add Project", systemImage: "plus")
            }
            .disabled(parts.count >= SplitMath.maxParts)

            Button {
                setPercents(SplitMath.equal(parts.count))
            } label: {
                Label("Split Equally", systemImage: "equal")
            }
            .disabled(percents == SplitMath.equal(parts.count))
        } header: {
            Text("Split between projects")
        } footer: {
            if let message = errors["shares"] {
                Text(message).foregroundStyle(.red)
            } else {
                Text("Drag a divider to move time between projects. Swipe a project to take it out of the split.")
            }
        }
    }

    private func row(_ index: Int, seconds partSeconds: TimeInterval) -> some View {
        let part = parts[index]
        return VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 8) {
                Circle().fill(catalog.color(of: part)).frame(width: 9, height: 9)
                Text(catalog.name(of: part))
                    .lineLimit(1)
                    .foregroundStyle(part.projectId == nil ? .secondary : .primary)
                if part.isBillable {
                    Image(systemName: "dollarsign")
                        .font(.caption.weight(.bold))
                        .foregroundStyle(Color.brandText)
                        .accessibilityLabel("Billable")
                }
                Spacer(minLength: 8)
                if !running {
                    Text(Durations.hoursMinutes(partSeconds))
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
                Text("\(part.percent)%")
                    .fontWeight(.medium)
                    .frame(minWidth: 40, alignment: .trailing)
            }
            .monospacedDigit()
            if let error = SplitSection.partErrors(errors)[index] {
                Text(error).font(.footnote).foregroundStyle(.red)
            }
        }
    }

    private func setPercents(_ next: [Int]) {
        for index in parts.indices { parts[index].percent = next[index] }
    }

    private func add(_ projectId: String?, _ taskId: String?) {
        let next = SplitMath.adding(to: percents)
        setPercents(Array(next.dropLast()))
        // A part added while the others have their own descriptions gets one too, to be written.
        parts.append(SplitPart(
            projectId: projectId, taskId: taskId, isBillable: catalog.project(projectId)?.isBillable ?? false, percent: next.last!,
            description: SplitPart.describesEach(parts) ? "" : nil))
    }

    private func remove(_ index: Int) {
        let next = SplitMath.removing(percents, at: index)
        parts.remove(at: index)
        setPercents(next)
    }

    /// What other parts already have, which a part can't have too.
    static func taken(in parts: [SplitPart], except index: Int?) -> (String?, String?) -> Bool {
        let others = parts.indices.filter { $0 != index }.map { parts[$0] }
        return { projectId, taskId in others.contains { $0.projectId == projectId && $0.taskId == taskId } }
    }

    /// The server's error keys for a split: `shares` for the whole, `shares[1].projectId` for a part.
    static let knownErrorFields: Set<String> = Set(["shares"]).union(
        (0..<SplitMath.maxParts).flatMap { ["shares[\($0)].projectId", "shares[\($0)].taskId", "shares[\($0)].description"] })

    /// The errors of each part's project and task, by position; a part's own description shows its own.
    static func partErrors(_ errors: FormErrors) -> [Int: String] {
        var result: [Int: String] = [:]
        for index in 0..<SplitMath.maxParts {
            if let message = errors["shares[\(index)].projectId"] ?? errors["shares[\(index)].taskId"] {
                result[index] = message
            }
        }
        return result
    }

    /// Parts' errors as form errors, the way the server names them.
    static func errorFields(_ errors: [Int: String]) -> [String: String] {
        Dictionary(uniqueKeysWithValues: errors.map { index, message in
            ("shares[\(index)].\(message == EntryRules.descriptionRequired ? "description" : "projectId")", message)
        })
    }
}

/// One part of a split: its project, its share, whether it is billable, and its own description when the parts are
/// described one by one.
private struct SplitPartView: View {
    let tracker: Tracker
    @Binding var parts: [SplitPart]
    let index: Int
    let seconds: TimeInterval
    let running: Bool

    @Environment(\.dismiss) private var dismiss

    private var catalog: Catalog { tracker.catalog }

    var body: some View {
        Form {
            // The part may be gone while the view leaves.
            if parts.indices.contains(index) {
                let part = parts[index]
                if part.description != nil {
                    Section("Description") {
                        TextField(
                            "What did you do on \(catalog.project(part.projectId)?.name ?? "this part")?",
                            text: Binding(get: { part.description ?? "" }, set: { parts[index].description = $0 }), axis: .vertical)
                    }
                }
                Section {
                    NavigationLink {
                        ProjectPickerView(tracker: tracker, projectId: part.projectId, taskId: part.taskId, onChange: { projectId, taskId in
                            parts[index].projectId = projectId
                            parts[index].taskId = taskId
                            parts[index].isBillable = catalog.project(projectId)?.isBillable ?? part.isBillable
                        }, taken: SplitSection.taken(in: parts, except: index))
                    } label: {
                        LabeledContent("Project") {
                            if let project = catalog.project(part.projectId) {
                                ProjectLabel(project: project, task: catalog.task(part.taskId))
                            } else {
                                Text("None")
                            }
                        }
                    }
                    Toggle("Billable", isOn: Binding(get: { part.isBillable }, set: { parts[index].isBillable = $0 }))
                }

                Section {
                    Stepper(value: Binding(get: { part.percent }, set: { setPercent($0) }), in: SplitMath.minPercent...(100 - (parts.count - 1) * SplitMath.minPercent)) {
                        LabeledContent("Share", value: "\(part.percent)%")
                            .monospacedDigit()
                    }
                    if !running {
                        let minutes = SplitMath.allocate(Int((seconds / 60).rounded()), parts.map(\.percent))[index]
                        LabeledContent("Time", value: Durations.hoursMinutes(TimeInterval(minutes * 60)))
                            .monospacedDigit()
                    }
                } footer: {
                    Text("The other projects make room in proportion to their shares.")
                }

                Section {
                    Button("Remove from Split", role: .destructive) {
                        dismiss()
                        let next = SplitMath.removing(parts.map(\.percent), at: index)
                        parts.remove(at: index)
                        for position in parts.indices { parts[position].percent = next[position] }
                    }
                }
            }
        }
        .navigationTitle(parts.indices.contains(index) ? catalog.name(of: parts[index]) : "")
        .navigationBarTitleDisplayMode(.inline)
    }

    private func setPercent(_ value: Int) {
        let next = SplitMath.setting(parts.map(\.percent), at: index, to: value)
        for position in parts.indices { parts[position].percent = next[position] }
    }
}

/// The parts as segments of one bar, each as wide as its share. Dragging on the bar moves the nearest divider in 5%
/// steps, with a tick at each; the dividers are adjustable with VoiceOver.
struct TouchSplitBar: View {
    let catalog: Catalog
    let parts: [SplitPart]
    let onPercents: ([Int]) -> Void

    @State private var dragging: Int?

    static let step = 5
    private static let height: CGFloat = 44

    private var percents: [Int] { parts.map(\.percent) }
    private var bounds: [Int] { percents.dropLast().indices.map { percents[...$0].reduce(0, +) } }

    var body: some View {
        GeometryReader { geometry in
            let width = geometry.size.width
            ZStack(alignment: .topLeading) {
                HStack(spacing: 0) {
                    ForEach(parts.indices, id: \.self) { index in
                        segment(index)
                            .frame(width: width * CGFloat(parts[index].percent) / 100)
                    }
                }
                .clipShape(RoundedRectangle(cornerRadius: 8))

                ForEach(bounds.indices, id: \.self) { index in
                    divider(index)
                        .position(x: width * CGFloat(bounds[index]) / 100, y: Self.height / 2)
                }
            }
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 2)
                    .onChanged { value in
                        guard width > 0, !bounds.isEmpty else { return }
                        let index = dragging ?? nearest(to: value.startLocation.x / width * 100)
                        dragging = index
                        onPercents(SplitMath.moveBoundary(percents, after: index, to: value.location.x / width * 100, step: Self.step))
                    }
                    .onEnded { _ in dragging = nil })
        }
        .frame(height: Self.height)
        .animation(.snappy(duration: 0.12), value: percents)
        .sensoryFeedback(.selection, trigger: percents)
    }

    private func segment(_ index: Int) -> some View {
        let part = parts[index]
        let color = catalog.color(of: part)
        return ZStack(alignment: .bottom) {
            color.opacity(0.2)
            color.frame(height: 3)
            // The project's name where it fits whole, otherwise only the share.
            ViewThatFits(in: .horizontal) {
                label(part, named: true)
                label(part, named: false)
                Color.clear
            }
        }
    }

    private func label(_ part: SplitPart, named: Bool) -> some View {
        HStack(spacing: 4) {
            if named {
                Text(catalog.project(part.projectId)?.name ?? "No project").fontWeight(.medium)
            }
            Text("\(part.percent)%").fontWeight(.semibold).monospacedDigit()
        }
        .font(.caption)
        .lineLimit(1)
        .fixedSize()
        .padding(.horizontal, 8)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }

    private func divider(_ index: Int) -> some View {
        ZStack {
            Rectangle().fill(Color(.secondarySystemGroupedBackground)).frame(width: 2)
            Capsule()
                .fill(.primary)
                .overlay(Capsule().strokeBorder(Color(.secondarySystemGroupedBackground), lineWidth: 2))
                .frame(width: 8, height: 26)
                .scaleEffect(dragging == index ? 1.2 : 1)
        }
        .frame(width: 24, height: Self.height)
        .accessibilityElement()
        .accessibilityLabel("Divider between \(catalog.name(of: parts[index])) and \(catalog.name(of: parts[index + 1]))")
        .accessibilityValue("\(catalog.name(of: parts[index])) \(percents[index])%, \(catalog.name(of: parts[index + 1])) \(percents[index + 1])%")
        .accessibilityAdjustableAction { direction in
            let delta = direction == .increment ? Self.step : -Self.step
            onPercents(SplitMath.moveBoundary(percents, after: index, to: Double(bounds[index] + delta)))
        }
    }

    private func nearest(to position: Double) -> Int {
        bounds.indices.min { abs(Double(bounds[$0]) - position) < abs(Double(bounds[$1]) - position) } ?? 0
    }
}
