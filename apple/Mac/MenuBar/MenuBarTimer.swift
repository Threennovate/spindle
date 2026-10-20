import SwiftUI
import SpindleKit

/// The timer in the menu bar: how long it has been running, with the icon alone when nothing runs. A ✦ marks a
/// split to look at, when the running entry's time went to more than one project.
struct MenuBarLabel: View {
    let model: AppModel
    let desktop: Desktop

    var body: some View {
        let _ = Performance.event("Menu bar label drawn")
        if model.menuBarClock == nil, hasSuggestion {
            Label {
                Text("✦")
            } icon: {
                Image("MenuBarMark")
            }
            .labelStyle(.titleAndIcon)
            .accessibilityLabel("Spindle, proposals to review")
        } else if let clock = model.menuBarClock {
            Label {
                Text(hasSuggestion ? "\(clock) ✦" : clock)
            } icon: {
                Image("MenuBarMark")
            }
            .labelStyle(.titleAndIcon)
            .monospacedDigit()
        } else {
            Image("MenuBarMark")
                .accessibilityLabel("Spindle")
        }
    }

    private var hasSuggestion: Bool {
        guard let tracker = model.tracker else { return false }
        if !desktop.proposals.pending(in: tracker).isEmpty { return true }
        guard let running = tracker.running else { return false }
        // Only a suggestion the logs back firmly marks the menu bar; a weak one waits in the panel and beside the entry.
        return desktop.splits.suggestion(for: running, tracker: tracker)?.strength == .strong || desktop.splits.drift(for: running, tracker: tracker) != nil
    }
}

/// What the menu bar item opens: the running timer to stop, or a field to start one, and recent entries to continue.
struct MenuBarPanel: View {
    let model: AppModel
    @Environment(Desktop.self) private var desktop
    @Environment(\.openWindow) private var openWindow
    @State private var description = ""
    @State private var focused = true

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let tracker = model.tracker {
                if let running = tracker.running {
                    runningCard(running, tracker: tracker)
                    if let suggestion = desktop.splits.suggestion(for: running, tracker: tracker),
                       suggestion.splitParts(billable: { _ in false }) != nil {
                        offerCard(SplitOffer(entry: running, suggestion: suggestion), tracker: tracker)
                    } else if let drift = desktop.splits.drift(for: running, tracker: tracker) {
                        driftCard(drift, tracker: tracker)
                    }
                } else {
                    startField
                }
                let pending = desktop.proposals.pending(in: tracker)
                if !pending.isEmpty {
                    Button {
                        desktop.reviewProposals()
                    } label: {
                        HStack(spacing: 8) {
                            Image(systemName: ActivityStyle.symbol)
                                .foregroundStyle(Color.brandText)
                            VStack(alignment: .leading, spacing: 1) {
                                Text("\(pending[0].client ?? "An agent") proposed \(ProposalReview.count(pending))")
                                    .font(.callout.weight(.semibold))
                                    .lineLimit(1)
                                Text("Review them before they are saved")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                            Spacer()
                            Image(systemName: "chevron.right")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        .padding(10)
                        .background(Color.brand.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
                let today = tracker.time.day(of: Date())
                let toReview = desktop.review(on: [today], tracker: tracker).count
                if toReview > 0 {
                    Button {
                        desktop.openReview(.day(today))
                    } label: {
                        HStack(spacing: 8) {
                            Image(systemName: ActivityStyle.symbol)
                                .foregroundStyle(Color.brandText)
                            Text("Today: \(toReview) to review")
                                .font(.callout.weight(.semibold))
                            Spacer()
                            Image(systemName: "chevron.right")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        .padding(10)
                        .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 8))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help("Splits and agent time nobody tracked today, in one place")
                }
                recent(tracker)
            } else {
                Text(model.phase == .signedIn ? "Loading…" : "Sign in to start tracking your time.")
                    .foregroundStyle(.secondary)
            }
            Divider()
            Button {
                desktop.quickEntry.show()
            } label: {
                HStack {
                    Text("Quick Entry…")
                    Spacer()
                    if let combo = desktop.settings.shortcuts[.quickEntry] {
                        Text(combo.display).foregroundStyle(.secondary)
                    }
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.borderless)
            .foregroundStyle(.primary)
            .help("Type what you work on from any app")
            HStack {
                Button("Open Spindle") { openMainWindow() }
                    .keyboardShortcut("o")
                Spacer()
                Button("Quit") { NSApp.terminate(nil) }
                    .keyboardShortcut("q")
            }
            .buttonStyle(.borderless)
            .foregroundStyle(.primary)
        }
        .padding(14)
        .frame(width: 320)
    }

    private func runningCard(_ running: TimeEntry, tracker: Tracker) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(running.description.isEmpty ? "No description" : running.description)
                    .font(.headline)
                    .foregroundStyle(running.description.isEmpty ? .secondary : .primary)
                    .lineLimit(2)
                if running.isSplit {
                    SplitLabel(catalog: tracker.catalog, parts: running.parts)
                        .font(.callout)
                } else if let project = tracker.catalog.project(running.projectId) {
                    ProjectLabel(project: project, task: tracker.catalog.task(running.taskId))
                        .font(.callout)
                }
                TimelineView(.periodic(from: .now, by: 1)) { context in
                    Text(Durations.clock(running.seconds(at: context.date)))
                        .font(.system(size: 22, weight: .semibold).monospacedDigit())
                }
            }
            Spacer()
            Button {
                Task {
                    await model.stop()
                    // The entry needs details the workspace requires: they are asked for in the window.
                    if model.editing != nil { openMainWindow() }
                }
            } label: {
                Image(systemName: "stop.fill")
                    .font(.system(size: 14, weight: .bold))
                    .foregroundStyle(.white)
                    .frame(width: 38, height: 38)
                    .background(Color.red, in: Circle())
            }
            .buttonStyle(.plain)
            .help("Stop timer")
            .accessibilityLabel("Stop timer")
        }
    }

    /// The split the running entry's agent activity suggests, to take from here.
    private func offerCard(_ offer: SplitOffer, tracker: Tracker) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Label("Agents worked on more than one project", systemImage: ActivityStyle.symbol)
                .font(.callout.weight(.semibold))
            Text(SplitApplier.summary(offer.suggestion, entry: offer.entry, catalog: tracker.catalog, time: tracker.time))
                .font(.callout)
                .foregroundStyle(.secondary)
                .monospacedDigit()
            HStack {
                Button("Keep as Is") { desktop.keep(offer) }
                    .help("Leave the timer on one project and stop suggesting a split for it")
                Spacer()
                Button(offer.move.map { "Switch at \(tracker.time.clock($0.at))" }
                       ?? SplitSuggestionChip.separateTitle(offer.suggestion, time: tracker.time)) {
                    desktop.take(offer)
                }
                .buttonStyle(.brand)
            }
            .controlSize(.small)
        }
        .padding(10)
        .cardSurface(cornerRadius: 8)
    }

    /// The running split's work changed after it was set: end it there and carry on, from here.
    private func driftCard(_ offer: DriftOffer, tracker: Tracker) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Label(offer.headline(catalog: tracker.catalog, time: tracker.time), systemImage: ActivityStyle.symbol)
                .font(.callout.weight(.semibold))
                .monospacedDigit()
            Text(offer.question(catalog: tracker.catalog, time: tracker.time))
                .font(.callout)
                .foregroundStyle(.secondary)
                .monospacedDigit()
            HStack {
                Button("Not Now") { desktop.splits.answer(offer) }
                    .help("Leave the timer as it is; nothing more is offered until its split changes")
                Spacer()
                Button(offer.title(catalog: tracker.catalog, time: tracker.time)) { desktop.take(offer) }
                    .buttonStyle(.brand)
            }
            .controlSize(.small)
        }
        .padding(10)
        .cardSurface(cornerRadius: 8)
    }

    private var startField: some View {
        HStack(spacing: 8) {
            CommandTextField(
                text: $description,
                placeholder: "What are you working on?",
                isFocused: $focused,
                onCommand: { command in
                    guard command == .submit else { return false }
                    start()
                    return true
                })
                .frame(height: 22)
                .padding(.horizontal, 8)
                .padding(.vertical, 4)
                .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6))
                .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(.separator))
                .accessibilityLabel("Description")
            Button(action: start) {
                Image(systemName: "play.fill")
                    .font(.system(size: 13, weight: .bold))
                    .foregroundStyle(Color.ink)
                    .frame(width: 32, height: 32)
                    .background(Color.brand, in: Circle())
            }
            .buttonStyle(.plain)
            .help("Start timer")
            .accessibilityLabel("Start timer")
        }
    }

    @ViewBuilder private func recent(_ tracker: Tracker) -> some View {
        let recent = recentEntries(tracker)
        if !recent.isEmpty {
            VStack(alignment: .leading, spacing: 2) {
                Text("Recent")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
                ForEach(recent) { entry in
                    Button {
                        Task {
                            do {
                                try await tracker.continueEntry(entry)
                            } catch {
                                model.notices.show(error, fallback: "Couldn't start the timer.")
                            }
                        }
                    } label: {
                        HStack(spacing: 8) {
                            Image(systemName: "play.fill")
                                .font(.caption)
                                .foregroundStyle(Color.brandText)
                            VStack(alignment: .leading, spacing: 1) {
                                Text(entry.description.isEmpty ? "No description" : entry.description)
                                    .foregroundStyle(entry.description.isEmpty ? .secondary : .primary)
                                    .lineLimit(1)
                                if entry.isSplit {
                                    SplitLabel(catalog: tracker.catalog, parts: entry.parts)
                                        .font(.caption)
                                } else if let project = tracker.catalog.project(entry.projectId) {
                                    ProjectLabel(project: project, task: tracker.catalog.task(entry.taskId))
                                        .font(.caption)
                                }
                            }
                            Spacer()
                        }
                        .padding(.vertical, 4)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help("Continue")
                }
            }
        }
    }

    /// The last few different things worked on, newest first.
    private func recentEntries(_ tracker: Tracker) -> [TimeEntry] {
        var seen = Set<EntryDetails>()
        return tracker.entries
            .filter { !$0.isRunning }
            .sorted { $0.start > $1.start }
            .filter { entry in
                var details = entry.details
                details.isBillable = nil
                return seen.insert(details).inserted
            }
            .prefix(5)
            .map { $0 }
    }

    private func start() {
        let details = EntryDetails(description: description)
        Task {
            if await model.start(details) {
                description = ""
            }
        }
    }

    private func openMainWindow() {
        openWindow(id: "main")
        NSApp.activate()
    }
}
