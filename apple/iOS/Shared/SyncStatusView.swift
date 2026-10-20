import SwiftUI
import SpindleKit

/// Whether changes reach the server: nothing when all is well, otherwise that the phone is offline or catching up, or
/// that changes wait for the person because their entries changed on another device.
struct SyncStatusView: View {
    @Environment(AppModel.self) private var model
    @State private var explaining = false
    @State private var reviewing = false

    var body: some View {
        if let outbox = model.account?.outbox {
            let waiting = outbox.pending.count
            let conflicts = outbox.conflicts.count
            if conflicts > 0, let tracker = model.tracker {
                Button {
                    reviewing = true
                } label: {
                    Label(conflicts == 1 ? "1 to review" : "\(conflicts) to review", systemImage: "exclamationmark.arrow.triangle.2.circlepath")
                        .labelStyle(.titleAndIcon)
                        .font(.footnote.weight(.medium))
                }
                .tint(.orange)
                .sheet(isPresented: $reviewing) {
                    EntryConflicts(tracker: tracker) { reviewing = false }
                        .presentationDetents([.medium, .large])
                }
            } else if !model.connectivity.isOnline || outbox.isOffline {
                Button {
                    explaining = true
                } label: {
                    Label(waiting > 0 ? "Offline · \(waiting)" : "Offline", systemImage: "wifi.slash")
                        .labelStyle(.titleAndIcon)
                        .font(.footnote.weight(.medium))
                }
                .tint(.secondary)
                .alert("You're offline", isPresented: $explaining) {
                    Button("OK", role: .cancel) {}
                } message: {
                    Text(waiting > 0
                        ? "Spindle can't reach the server. \(waiting == 1 ? "1 change is" : "\(waiting) changes are") kept on this iPhone and sync when it's back."
                        : "Spindle can't reach the server. You can go on tracking; changes sync when it's back.")
                }
            } else if waiting > 0 {
                Label("Syncing…", systemImage: "arrow.triangle.2.circlepath")
                    .labelStyle(.titleAndIcon)
                    .font(.footnote.weight(.medium))
                    .foregroundStyle(.secondary)
            }
        }
    }
}
