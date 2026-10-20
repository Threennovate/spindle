import AppKit
import SwiftUI
import SpindleKit

/// Splits an entry's time between projects: a bar whose dividers are dragged, and a row per part where its project,
/// share, time and billable flag are set, and its own description when the parts are described one by one. Removing
/// all but one part ends the split.
struct SplitEditor: View {
    let tracker: Tracker
    @Binding var parts: [SplitPart]
    /// The entry's whole duration, to show each part's time.
    let seconds: TimeInterval
    /// A running entry's duration keeps changing, so its parts are set in percent only.
    let running: Bool
    /// Errors for parts, by position, and for the split as a whole.
    var errors: [Int: String] = [:]
    var splitError: String?
    /// The part whose own description has the keyboard, by position.
    var descriptionFocus: FocusState<Int?>.Binding
    /// Goes back to one description for all parts, while each part has its own.
    let onDescribeAllAsOne: () -> Void

    @State private var choosing: Int?
    @State private var adding = false

    private var catalog: Catalog { tracker.catalog }
    private var percents: [Int] { parts.map(\.percent) }
    private var perPart: Bool { SplitPart.describesEach(parts) }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            SplitBar(catalog: catalog, parts: parts) { setPercents($0) }

            VStack(spacing: 2) {
                let minutes = SplitMath.allocate(Int((seconds / 60).rounded()), percents)
                ForEach(parts.indices, id: \.self) { index in
                    row(index, seconds: TimeInterval(minutes[index] * 60))
                    if parts[index].description != nil {
                        description(index)
                    }
                    if let error = errors[index] {
                        Text(error)
                            .font(.callout)
                            .foregroundStyle(.red)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.leading, 8)
                    }
                }
            }

            HStack {
                Button {
                    adding = true
                } label: {
                    Label("Add Project", systemImage: "plus")
                }
                .disabled(parts.count >= SplitMath.maxParts)
                .popover(isPresented: $adding, arrowEdge: .bottom) {
                    ProjectPicker(
                        tracker: tracker, projectId: nil, taskId: nil,
                        onChange: add, taken: taken(except: nil), choosingAnew: true,
                        placeholder: "Add which project to the split?")
                }
                Spacer()
                if perPart {
                    Button {
                        onDescribeAllAsOne()
                    } label: {
                        Label("One Description for All", systemImage: "text.quote")
                    }
                    .buttonStyle(.brandLink)
                    .help("Show one description on every project, timesheet links included")
                    .padding(.trailing, 8)
                }
                Button {
                    setPercents(SplitMath.equal(parts.count))
                } label: {
                    Label("Split Equally", systemImage: "equal")
                }
                .buttonStyle(.brandLink)
                .disabled(percents == SplitMath.equal(parts.count))
            }
            if let splitError {
                Text(splitError)
                    .font(.callout)
                    .foregroundStyle(.red)
            }
        }
    }

    private func row(_ index: Int, seconds partSeconds: TimeInterval) -> some View {
        let part = parts[index]
        return HStack(spacing: 8) {
            Button {
                choosing = index
            } label: {
                HStack {
                    if let project = catalog.project(part.projectId) {
                        ProjectLabel(project: project, task: catalog.task(part.taskId))
                    } else {
                        HStack(spacing: 6) {
                            Circle().strokeBorder(.secondary, style: StrokeStyle(lineWidth: 1, dash: [2])).frame(width: 9, height: 9)
                            Text("No project").foregroundStyle(.secondary)
                        }
                    }
                    Spacer(minLength: 4)
                }
                .padding(.horizontal, 8)
                .frame(minHeight: 26)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Part \(index + 1): \(catalog.name(of: part))")
            .popover(isPresented: Binding(get: { choosing == index }, set: { if !$0 { choosing = nil } }), arrowEdge: .bottom) {
                ProjectPicker(tracker: tracker, projectId: part.projectId, taskId: part.taskId, onChange: { projectId, taskId in
                    parts[index].projectId = projectId
                    parts[index].taskId = taskId
                    parts[index].isBillable = catalog.project(projectId)?.isBillable ?? part.isBillable
                }, taken: taken(except: index))
            }

            PercentField(value: part.percent) { setPercents(SplitMath.setting(percents, at: index, to: $0)) }
                .accessibilityLabel("Share of \(catalog.name(of: part))")

            PartDuration(seconds: partSeconds, disabled: running) { typed in
                setPercents(SplitMath.setting(percents, at: index, to: SplitMath.percent(of: Int(typed), in: Int(seconds))))
            }
            .accessibilityLabel("Time on \(catalog.name(of: part))")

            Button {
                parts[index].isBillable.toggle()
            } label: {
                Image(systemName: "dollarsign")
                    .fontWeight(part.isBillable ? .bold : .regular)
                    .foregroundStyle(part.isBillable ? Color.brandText : .secondary)
                    .frame(width: 22, height: 22)
                    .background(part.isBillable ? Color.primary.opacity(0.07) : .clear, in: RoundedRectangle(cornerRadius: 5))
            }
            .buttonStyle(.plain)
            .help(part.isBillable ? "Billable" : "Non-billable")
            .accessibilityLabel("Billable: \(catalog.name(of: part))")
            .accessibilityValue(part.isBillable ? "On" : "Off")

            Button {
                remove(index)
            } label: {
                Image(systemName: "xmark")
            }
            .buttonStyle(.borderless)
            .foregroundStyle(.secondary)
            .help("Remove from the split")
            .accessibilityLabel("Remove \(catalog.name(of: part)) from the split")
        }
    }

    /// A part's own description, under its project, as a line of text that is edited in place.
    private func description(_ index: Int) -> some View {
        let name = catalog.project(parts[index].projectId)?.name ?? "this part"
        return TextField(
            "What did you do on \(name)?",
            text: Binding(get: { parts.indices.contains(index) ? parts[index].description ?? "" : "" }, set: { parts[index].description = $0 }))
            .textFieldStyle(.plain)
            .focused(descriptionFocus, equals: index)
            .descriptionSuggestions(tracker, text: parts[index].description ?? "", projectId: parts[index].projectId)
            .padding(.leading, 23)
            .padding(.trailing, 8)
            .padding(.bottom, 6)
            .accessibilityLabel("Description for \(catalog.name(of: parts[index]))")
    }

    /// The server's error keys for a split: `shares` for the whole, `shares[1].projectId` for a part.
    static let knownErrorFields: Set<String> = Set(["shares"]).union(
        (0..<SplitMath.maxParts).flatMap { ["shares[\($0)].projectId", "shares[\($0)].taskId", "shares[\($0)].description"] })

    /// The errors of each part, by position.
    static func partErrors(_ errors: FormErrors) -> [Int: String] {
        var result: [Int: String] = [:]
        for index in 0..<SplitMath.maxParts {
            if let message = errors["shares[\(index)].projectId"] ?? errors["shares[\(index)].taskId"] ?? errors["shares[\(index)].description"] {
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

    private func taken(except index: Int?) -> (String?, String?) -> Bool {
        let others = parts.indices.filter { $0 != index }.map { parts[$0] }
        return { projectId, taskId in others.contains { $0.projectId == projectId && $0.taskId == taskId } }
    }

    private func setPercents(_ next: [Int]) {
        for index in parts.indices { parts[index].percent = next[index] }
    }

    private func add(_ projectId: String?, _ taskId: String?) {
        let next = SplitMath.adding(to: percents)
        setPercents(Array(next.dropLast()))
        parts.append(SplitPart(
            projectId: projectId, taskId: taskId, isBillable: catalog.project(projectId)?.isBillable ?? false, percent: next.last!,
            description: perPart ? "" : nil))
    }

    private func remove(_ index: Int) {
        let next = SplitMath.removing(percents, at: index)
        parts.remove(at: index)
        setPercents(next)
    }
}

/// Says under the description of a split that every project shows it, with a way to describe each on its own.
struct DescriptionReach: View {
    let projects: Int
    let onDescribePerProject: () -> Void

    var body: some View {
        HStack(spacing: 5) {
            Image(systemName: "eye")
                .accessibilityHidden(true)
            Text("Shown on all \(projects) projects, timesheet links included")
            Text("·").accessibilityHidden(true)
            Button("Describe per Project", action: onDescribePerProject)
                .buttonStyle(.brandLink)
        }
        .font(.callout)
        .foregroundStyle(.secondary)
    }
}

/// The parts as segments of one bar, each as wide as its share. Dragging anywhere on the bar moves the nearest
/// divider, snapping to 5% (1% with Option held); double-clicking a divider evens out the parts beside it. The
/// dividers are adjustable from the keyboard and VoiceOver.
struct SplitBar: View {
    let catalog: Catalog
    let parts: [SplitPart]
    let onPercents: ([Int]) -> Void

    @State private var dragging: Int?
    @State private var hovering = false

    static let step = 5
    private static let height: CGFloat = 40

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
                .clipShape(RoundedRectangle(cornerRadius: 7))

                ForEach(bounds.indices, id: \.self) { index in
                    divider(index)
                        .position(x: width * CGFloat(bounds[index]) / 100, y: Self.height / 2)
                }
            }
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { value in
                        let index = dragging ?? nearest(to: value.startLocation.x / width * 100)
                        dragging = index
                        let fine = NSEvent.modifierFlags.contains(.option)
                        onPercents(SplitMath.moveBoundary(percents, after: index, to: value.location.x / width * 100, step: fine ? 1 : Self.step))
                    }
                    .onEnded { _ in dragging = nil })
            .simultaneousGesture(
                SpatialTapGesture(count: 2).onEnded { tap in
                    let index = nearest(to: tap.location.x / width * 100)
                    evenOut(index)
                })
            .onContinuousHover { phase in
                switch phase {
                case .active: NSCursor.resizeLeftRight.set()
                case .ended: NSCursor.arrow.set()
                }
            }
        }
        .frame(height: Self.height)
        .animation(.snappy(duration: 0.12), value: percents)
    }

    private func segment(_ index: Int) -> some View {
        let part = parts[index]
        let color = catalog.color(of: part)
        return ZStack(alignment: .bottom) {
            color.opacity(0.2)
            color.frame(height: 3)
            HStack(spacing: 5) {
                if part.percent >= 18 {
                    Text(catalog.project(part.projectId)?.name ?? "No project")
                        .fontWeight(.medium)
                        .lineLimit(1)
                }
                if part.percent >= 8 {
                    Text("\(part.percent)%")
                        .fontWeight(.semibold)
                        .monospacedDigit()
                }
                Spacer(minLength: 0)
            }
            .font(.caption)
            .padding(.horizontal, 9)
            .frame(maxHeight: .infinity)
        }
    }

    private func divider(_ index: Int) -> some View {
        ZStack {
            Rectangle().fill(.background).frame(width: 2)
            Capsule()
                .fill(.primary)
                .overlay(Capsule().strokeBorder(.background, lineWidth: 2))
                .frame(width: 7, height: 24)
                .scaleEffect(dragging == index ? 1.15 : 1)
            if dragging == index {
                Text("\(percents[index]) · \(percents[index + 1])")
                    .font(.caption.weight(.medium).monospacedDigit())
                    .padding(.horizontal, 6)
                    .padding(.vertical, 2)
                    .background(.primary, in: RoundedRectangle(cornerRadius: 5))
                    .foregroundStyle(.background)
                    .fixedSize()
                    .offset(y: -Self.height / 2 - 12)
            }
        }
        .frame(width: 16, height: Self.height)
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

    /// Evens out the two parts beside a divider.
    private func evenOut(_ index: Int) {
        let pair = percents[index] + percents[index + 1]
        onPercents(SplitMath.moveBoundary(percents, after: index, to: Double(bounds[index] - percents[index] + (pair + 1) / 2)))
    }
}

/// A part's share in whole percent, applied on Return or when the field is left. Up and down arrows step by one.
private struct PercentField: View {
    let value: Int
    let onCommit: (Int) -> Void

    @State private var text = ""
    @FocusState private var focused: Bool

    var body: some View {
        HStack(spacing: 2) {
            TextField("", text: $text)
                .multilineTextAlignment(.trailing)
                .monospacedDigit()
                .focused($focused)
                .onSubmit(commit)
                .onKeyPress(.upArrow) { onCommit(value + 1); return .handled }
                .onKeyPress(.downArrow) { onCommit(value - 1); return .handled }
            Text("%").foregroundStyle(.secondary)
        }
        .frame(width: 58)
        .onAppear { text = "\(value)" }
        .onChange(of: value) { _, new in text = "\(new)" }
        .onChange(of: focused) { _, isFocused in if !isFocused { commit() } }
    }

    private func commit() {
        if let typed = Int(text.filter(\.isNumber)), typed != value { onCommit(typed) }
        text = "\(value)"
    }
}

/// A part's time; typing a duration sets the share closest to it.
private struct PartDuration: View {
    let seconds: TimeInterval
    let disabled: Bool
    let onCommit: (TimeInterval) -> Void

    @State private var text: String?
    @FocusState private var focused: Bool

    var body: some View {
        TextField("", text: Binding(get: { text ?? Durations.hoursMinutes(seconds) }, set: { text = $0 }))
            .multilineTextAlignment(.trailing)
            .font(.callout.monospacedDigit())
            .foregroundStyle(.secondary)
            .textFieldStyle(.plain)
            .frame(width: 64)
            .disabled(disabled)
            .focused($focused)
            .onSubmit(commit)
            .onChange(of: focused) { _, isFocused in if !isFocused { commit() } }
            .help(disabled ? "The timer is running; set the share in percent." : "Type a duration, e.g. 1:15")
    }

    private func commit() {
        if let text, let typed = Durations.parse(text) { onCommit(TimeInterval(typed)) }
        text = nil
    }
}
