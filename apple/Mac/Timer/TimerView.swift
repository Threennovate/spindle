import SwiftUI
import SpindleKit

/// The timer: the bar to start and stop it, and the entries below.
struct TimerView: View {
    @Environment(AppModel.self) private var model
    @Environment(Desktop.self) private var desktop
    let tracker: Tracker

    var body: some View {
        VStack(spacing: 0) {
            ReviewCard(tracker: tracker)
            TimerBar(tracker: tracker)
                .padding(.horizontal, 20)
                .padding(.top, 16)
                .padding(.bottom, 8)
                // Above the list, so the suggestions can hang over it.
                .zIndex(1)
            if AgentLogsOffer.applies(desktop.settings) {
                AgentLogsOffer()
                    .padding(.horizontal, 20)
                    .padding(.bottom, 8)
            }
            EntryList(tracker: tracker)
        }
        .environment(model.notices)
        .navigationSubtitle(subtitle)
    }

    private var subtitle: String {
        guard let running = tracker.running else { return "" }
        return running.description.isEmpty ? "Running" : "Running: \(running.description)"
    }
}
