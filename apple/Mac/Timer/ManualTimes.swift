import SwiftUI
import SpindleKit

/// The day, start and end for adding time that wasn't tracked with the timer.
struct ManualTimes: View {
    @Environment(AppModel.self) private var model
    @Environment(Desktop.self) private var desktop
    let tracker: Tracker
    let draft: EntryDetails
    let onAdded: (EntryDetails) -> Void

    @State private var day: DayKey?
    @State private var startText = ""
    @State private var endText = ""
    @State private var durationText: String?
    @State private var adding = false
    @FocusState private var durationFocused: Bool

    private var time: UserTime { tracker.time }

    var body: some View {
        let range = self.range
        HStack(spacing: 6) {
            DayPicker(day: dayBinding, time: time, earliest: tracker.rules?.firstOpenDay)
            ClockField(text: $startText, time: time, alignment: .center)
                .frame(width: clockWidth)
                .accessibilityLabel("Start time")
            Text("–").foregroundStyle(.secondary)
            ClockField(text: $endText, time: time, alignment: .center)
                .frame(width: clockWidth)
                .accessibilityLabel("End time")
            TextField("Duration", text: Binding(
                get: { durationText ?? range.map { Durations.clock($0.duration) } ?? "" },
                set: { durationText = $0 }))
                .frame(width: 76)
                .multilineTextAlignment(.trailing)
                .font(.body.monospacedDigit())
                .focused($durationFocused)
                .onSubmit(applyDuration)
                .accessibilityLabel("Duration")
            if let range, let suggestion = suggestion(for: range) {
                SplitSuggestionChip(catalog: tracker.catalog, time: time, suggestion: suggestion, start: range.start, stop: range.end) {
                    review(range, as: suggestion)
                }
            }
            Button(adding ? "Adding…" : "Add", action: add)
                .buttonStyle(.brand)
                .disabled(adding)
        }
        .task(id: desktop.splits.request(range.map { [time.day(of: $0.start), time.day(of: $0.end)] } ?? [], time: time)) {
            // The agent activity of the typed time, to offer splitting it before it is added.
            if let range {
                await desktop.splits.load([time.day(of: range.start), time.day(of: range.end)], tracker: tracker)
            }
        }
        .textFieldStyle(.roundedBorder)
        // The description gives way before the times do.
        .fixedSize()
        .onAppear {
            if startText.isEmpty {
                startText = time.clockField(Date().addingTimeInterval(-3600))
                endText = time.clockField(Date())
            }
        }
        .onChange(of: durationFocused) { _, focused in
            if focused {
                durationText = range.map { Durations.clock($0.duration) } ?? ""
            } else {
                applyDuration()
            }
        }
    }

    private var clockWidth: CGFloat { time.hour12 ? 76 : 56 }

    private var dayBinding: Binding<DayKey> {
        Binding(get: { day ?? time.day(of: Date()) }, set: { day = $0 })
    }

    /// The instants for the day and the two clock times; an end before the start means the entry ran past midnight.
    private var range: DateInterval? {
        guard let start = time.readClockField(startText), let end = time.readClockField(endText) else { return nil }
        let day = dayBinding.wrappedValue
        let from = time.date(day, hours: start.hours, minutes: start.minutes)
        var to = time.date(day, hours: end.hours, minutes: end.minutes)
        if to < from {
            to = time.date(time.adding(1, to: day), hours: end.hours, minutes: end.minutes)
        }
        return DateInterval(start: from, end: to)
    }

    /// The split agent activity suggests for the typed time, kept on the project chosen in the bar where the logs
    /// say nothing.
    private func suggestion(for range: DateInterval) -> SplitSuggestion? {
        desktop.splits.suggestion(from: range.start, to: range.end, on: draft.projectId, tracker: tracker)
    }

    /// Opens the new entry with the suggested split, to add from the editor.
    private func review(_ range: DateInterval, as suggestion: SplitSuggestion) {
        let used = draft
        let usedEnd = endText
        var details = draft
        details.isBillable = draft.isBillable ?? tracker.catalog.project(draft.projectId)?.isBillable
        model.editing = SplitAdvisor.editing(newRange: range, details: details, as: suggestion, catalog: tracker.catalog) {
            // As after adding it here: the next entry usually starts where this one ended.
            startText = usedEnd
            onAdded(used)
        }
    }

    /// A duration typed in moves the end.
    private func applyDuration() {
        if let text = durationText, let seconds = Durations.parse(text), let range {
            endText = time.clockField(range.start.addingTimeInterval(TimeInterval(seconds)))
        }
        durationText = nil
    }

    private func add() {
        guard let range else {
            model.notices.show("Enter a start and an end time, such as 9:00 and 10:30.", kind: .error)
            return
        }
        if let rules = tracker.rules {
            if rules.isLocked(dayBinding.wrappedValue) {
                model.notices.show(rules.lockMessage(time), kind: .error)
                return
            }
            // What is missing is shown one thing at a time, in the order the fields appear in the bar.
            if let missing = rules.missingDetails(draft, project: tracker.catalog.project(draft.projectId)).first {
                model.notices.show(missing.message, kind: .error)
                return
            }
        }
        let used = draft
        let usedTimes = (start: startText, end: endText)
        var details = draft
        details.isBillable = draft.isBillable ?? tracker.catalog.project(draft.projectId)?.isBillable
        adding = true
        Task {
            defer { adding = false }
            do {
                try await tracker.save(nil, as: EntryRequest(details: details, start: range.start, stop: range.end))
                // The next entry usually starts where this one ended, unless another start was typed meanwhile.
                if startText == usedTimes.start {
                    startText = usedTimes.end
                }
                onAdded(used)
            } catch {
                model.notices.show(error, fallback: "Couldn't add the time entry.")
            }
        }
    }
}

/// A day in the person's time zone, typed like the times beside it and written the way the Mac writes dates, e.g.
/// `30. 09. 2026.`; the arrow keys move it a day. A text field rather than a date picker, which on macOS 26 is drawn
/// smaller than the fields around it.
struct DayPicker: View {
    @Binding var day: DayKey
    let time: UserTime
    /// Days before this one can't be picked, e.g. locked ones.
    var earliest: DayKey?

    @State private var text: String?
    @FocusState private var focused: Bool

    var body: some View {
        TextField("Date", text: Binding(get: { text ?? time.dayField(day) }, set: { text = $0 }))
            .focused($focused)
            .onSubmit(commit)
            .onChange(of: focused) { _, focused in
                if !focused {
                    commit()
                }
            }
            .onKeyPress(.upArrow) { move(1) }
            .onKeyPress(.downArrow) { move(-1) }
            .frame(width: 104)
            .accessibilityLabel("Date")
    }

    /// What was typed becomes the day, if it is one; otherwise the field shows the day again.
    private func commit() {
        if let text, let typed = time.parseDayField(text, near: day) {
            pick(typed)
        }
        text = nil
    }

    private func move(_ days: Int) -> KeyPress.Result {
        commit()
        pick(time.adding(days, to: day))
        return .handled
    }

    private func pick(_ newDay: DayKey) {
        let allowed = max(newDay, earliest ?? newDay)
        if allowed != day {
            day = allowed
        }
    }
}
