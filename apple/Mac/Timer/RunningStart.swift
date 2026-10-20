import SwiftUI
import SpindleKit

/// Corrects when the running timer started, as on the web app. Shows the day and duration the typed time gives before
/// it is saved, asks again when that makes the timer run for half a day, and offers the times people usually mean.
struct RunningStart: View {
    let entry: TimeEntry
    let tracker: Tracker
    let onSave: (Date) async throws -> Void
    let onDone: () -> Void

    @State private var text: String
    @State private var focused = true
    @State private var error: String?
    @State private var confirmingLong = false
    @State private var saving = false

    /// A timer running this long was most likely started at the wrong time, e.g. 21:15 yesterday instead of 20:15 today.
    private static let longRun: TimeInterval = 12 * 3600
    private static let agoMinutes = [5, 15, 30]

    init(entry: TimeEntry, tracker: Tracker, onSave: @escaping (Date) async throws -> Void, onDone: @escaping () -> Void) {
        self.entry = entry
        self.tracker = tracker
        self.onSave = onSave
        self.onDone = onDone
        _text = State(initialValue: tracker.time.clockField(entry.start))
    }

    private var time: UserTime { tracker.time }

    var body: some View {
        TimelineView(.periodic(from: .now, by: 1)) { context in
            content(now: context.date)
        }
        .padding(14)
        .frame(width: 310)
    }

    private func content(now: Date) -> some View {
        let start = start(now: now)
        let seconds = start.map { now.timeIntervalSince($0) } ?? 0
        let long = seconds >= Self.longRun
        let locked = start.map(tracker.isLocked) ?? false
        let previous = previous(now: now)
        let overlap = previous.flatMap { previous in start.map { previous.stop!.timeIntervalSince($0) } } ?? 0

        return VStack(alignment: .leading, spacing: 10) {
            Text("Started at").font(.callout.weight(.medium))
            HStack {
                ClockField(text: change, time: time, isFocused: $focused, onSubmit: save)
                    .accessibilityLabel("Started at")
                Button(confirmingLong ? "Save Anyway" : "Save", action: save)
                    .keyboardShortcut(.defaultAction)
                    .tint(confirmingLong ? .red : nil)
                    .disabled(saving || locked)
            }

            VStack(alignment: .leading, spacing: 6) {
                if let error {
                    Text(error).foregroundStyle(.red)
                } else if let start {
                    Text("\(time.dayHeading(time.day(of: start), now: now)), \(time.clock(start)) · \(Text(Durations.clock(seconds)).fontWeight(long ? .semibold : .regular))")
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                    if locked, let rules = tracker.rules {
                        Text(rules.lockMessage(time)).foregroundStyle(.red)
                    } else if long {
                        warning("The timer would have been running for over 12 hours." + (confirmingLong ? " Save anyway to keep it." : ""))
                    } else if let previous, overlap >= 60 {
                        warning("Overlaps \(previous.description.isEmpty ? "the previous entry" : "“\(previous.description)”"), which ended at \(time.clock(previous.stop!)).")
                    }
                }
            }
            .font(.callout)
            .fixedSize(horizontal: false, vertical: true)

            FlowLayout(spacing: 6, lineSpacing: 6) {
                if let previous, let stop = previous.stop, now.timeIntervalSince(stop) < 24 * 3600 {
                    suggestion("Last entry ended · \(time.clock(stop))", value: time.clockField(stop))
                        .help(previous.description.isEmpty ? "When the previous entry ended" : "When “\(previous.description)” ended")
                }
                ForEach(Self.agoMinutes, id: \.self) { minutes in
                    suggestion("\(minutes) min ago", value: "-\(minutes)")
                }
            }

            Text("Type 930, 9:30 or -15 for 15 min ago. ↑ ↓ change it by a minute, with ⇧ by 15.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    /// The text binding: a new time takes back an error and the question about a long run.
    private var change: Binding<String> {
        Binding(get: { text }, set: { value in
            guard value != text else { return }
            text = value
            error = nil
            confirmingLong = false
        })
    }

    /// The start the typed time gives. The minute it started in already is kept to the second, so an unchanged time
    /// changes nothing.
    private func start(now: Date) -> Date? {
        guard let typed = time.readStartField(text, now: now) else { return nil }
        let startMinute = (entry.start.timeIntervalSinceReferenceDate / 60).rounded(.down) * 60
        return typed.timeIntervalSinceReferenceDate == startMinute ? entry.start : typed
    }

    /// The entry that ended last, which the timer usually picks up from.
    private func previous(now: Date) -> TimeEntry? {
        tracker.entries
            .filter { $0.id != entry.id && $0.stop.map { $0 <= now } == true }
            .max { $0.stop! < $1.stop! }
    }

    private func warning(_ text: String) -> some View {
        Label(text, systemImage: "exclamationmark.triangle")
            .foregroundStyle(.orange)
    }

    /// Puts a time in the field and goes back to it, so Return saves it.
    private func suggestion(_ title: String, value: String) -> some View {
        Button(title) {
            change.wrappedValue = value
            focused = true
        }
        .buttonStyle(.bordered)
        .controlSize(.small)
    }

    private func save() {
        let now = Date()
        guard let start = start(now: now) else {
            error = "Enter a time such as 930, 9:30 or -15 for 15 minutes ago."
            return
        }
        if tracker.isLocked(start) {
            error = tracker.rules?.lockMessage(time)
            return
        }
        if now.timeIntervalSince(start) >= Self.longRun && !confirmingLong {
            confirmingLong = true
            return
        }
        if start == entry.start {
            onDone()
            return
        }
        saving = true
        Task {
            defer { saving = false }
            do {
                try await onSave(start)
                onDone()
            } catch {
                self.error = error.asSpindleError.localizedDescription
            }
        }
    }
}
