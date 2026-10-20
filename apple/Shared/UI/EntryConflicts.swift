import SwiftUI
import SpindleKit

/// Changes from this device to entries that another device changed since: each shows both versions, and the person
/// keeps one. Until then the change stays, shown as they left it, and isn't saved over the other device's.
struct EntryConflicts: View {
    let tracker: Tracker
    var onDone: () -> Void = {}

    var body: some View {
        let conflicts = tracker.outbox.conflicts
        VStack(alignment: .leading, spacing: 16) {
            Text("Changed on another device")
                .font(.headline)
            Text("These entries changed elsewhere after you changed them here. Choose which version to keep.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    ForEach(conflicts) { pending in
                        if let theirs = pending.conflict {
                            EntryConflictRow(tracker: tracker, pending: pending, theirs: theirs)
                        }
                    }
                }
            }
            HStack {
                Spacer()
                Button("Done", action: onDone)
                    .keyboardShortcut(.cancelAction)
            }
        }
        .padding(20)
        .onChange(of: conflicts.isEmpty) { _, settled in
            if settled { onDone() }
        }
    }
}

private struct EntryConflictRow: View {
    let tracker: Tracker
    let pending: Outbox.Pending
    let theirs: TimeEntry
    @State private var working = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 12) {
                side("Yours", mine)
                side("On the server", theirs)
            }
            HStack {
                Button("Keep mine") { settle { await tracker.outbox.keepMine(pending.seq) } }
                Button("Keep theirs") { settle { await tracker.outbox.keepTheirs(pending.seq) } }
            }
            .disabled(working)
        }
        .padding(12)
        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 9))
    }

    /// The entry as this device shows it, with its changes; nil when one of them deletes it.
    private var mine: TimeEntry? {
        tracker.entry(id: pending.change.entryId) ?? (tracker.running?.id == pending.change.entryId ? tracker.running : nil)
    }

    private func side(_ title: String, _ entry: TimeEntry?) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
            if let entry {
                Text(entry.description.isEmpty ? "No description" : entry.description)
                    .lineLimit(2)
                if let project = tracker.catalog.project(entry.projectId) {
                    ProjectLabel(project: project)
                        .font(.callout)
                }
                Text(when(entry))
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            } else {
                Text("Deleted")
                    .foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func when(_ entry: TimeEntry) -> String {
        let time = tracker.time
        let day = time.dayHeading(time.day(of: entry.start), now: AppClock.now)
        let end = entry.stop.map { time.clock($0) } ?? "running"
        return "\(day), \(time.clock(entry.start))–\(end)"
    }

    private func settle(_ action: @escaping () async -> Void) {
        working = true
        Task {
            await action()
            working = false
        }
    }
}
