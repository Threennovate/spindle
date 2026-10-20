import SwiftUI
import SpindleKit

/// The signed-in window: the sidebar and the section chosen in it.
struct MainView: View {
    @Environment(AppModel.self) private var model
    @Environment(Desktop.self) private var desktop

    var body: some View {
        @Bindable var model = model
        NavigationSplitView {
            Sidebar(selection: $model.section)
                .navigationSplitViewColumnWidth(min: 200, ideal: 220, max: 300)
        } detail: {
            // Keyed by workspace, so switching starts every section fresh instead of mixing two workspaces' data.
            SectionView(section: model.section)
                .id(model.workspace?.id)
        }
        .frame(minWidth: 900, minHeight: 560)
        .toolbar {
            ToolbarItem(placement: .status) {
                SyncStatus()
            }
            ToolbarItem(placement: .primaryAction) {
                AccountMenu()
            }
        }
        .sheet(item: $model.editing) { editing in
            if let tracker = model.tracker {
                EntryEditor(tracker: tracker, editing: editing)
                    .environment(model.notices)
            }
        }
        .sheet(isPresented: Binding(get: { desktop.proposals.reviewing && model.editing == nil },
                                    set: { if !$0 { desktop.proposals.endReview() } })) {
            if let tracker = model.tracker {
                ProposalReview(tracker: tracker)
                    .environment(model.notices)
            }
        }
        // The day's review steps aside while the editor is open from it, and comes back after.
        .sheet(isPresented: Binding(get: { desktop.reviewing != nil && model.editing == nil && !desktop.proposals.reviewing },
                                    set: { if !$0 { desktop.reviewing = nil } })) {
            if let tracker = model.tracker, let scope = desktop.reviewing {
                DayReviewView(tracker: tracker, scope: scope)
                    .environment(model.notices)
            }
        }
        .sheet(isPresented: Binding(get: { desktop.onboarding && desktop.reviewing == nil && model.editing == nil },
                                    set: { if !$0 { desktop.onboarding = false } })) {
            if let tracker = model.tracker {
                AgentLogsOnboarding(tracker: tracker)
            }
        }
        .overlay(alignment: .bottom) {
            VStack(spacing: 10) {
                if let offer = desktop.splits.offer, let tracker = model.tracker, model.editing == nil {
                    SplitOfferCard(offer: offer, tracker: tracker)
                        .padding(.horizontal, 20)
                        .transition(.move(edge: .bottom).combined(with: .opacity))
                } else if let offer = desktop.splits.driftOffer, let tracker = model.tracker, model.editing == nil {
                    DriftOfferCard(offer: offer, tracker: tracker)
                        .padding(.horizontal, 20)
                        .transition(.move(edge: .bottom).combined(with: .opacity))
                }
                if let notice = model.notices.current {
                    NoticeView(notice: notice, perform: { model.notices.perform() }) { model.notices.dismiss() }
                        .transition(.move(edge: .bottom).combined(with: .opacity))
                }
            }
            .padding(.bottom, 20)
        }
        .animation(.snappy, value: model.notices.current)
        .animation(.snappy, value: desktop.splits.offer?.id)
        .animation(.snappy, value: desktop.splits.driftOffer?.id)
        // A stop is offered a split at once, not at the next tick.
        .onChange(of: model.tracker?.running?.id) { desktop.runningChanged() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            Task { await model.refreshAfterActivation() }
        }
        #if DEBUG
        .task {
            if let snapshot = SnapshotRun.current?.section {
                model.section = snapshot
            }
        }
        #endif
    }
}

private struct SectionView: View {
    @Environment(AppModel.self) private var model
    let section: AppSection

    var body: some View {
        let _ = Performance.event("Section drawn", "\(section)")
        Group {
            switch section {
            case .timer:
                if let tracker = model.tracker {
                    TimerView(tracker: tracker)
                }
            case .calendar:
                if let tracker = model.tracker {
                    CalendarView(tracker: tracker, state: model.kept(for: tracker, CalendarState.init))
                }
            case .reports:
                if let tracker = model.tracker {
                    ReportsView(tracker: tracker, client: model.client, state: model.kept(for: tracker) { ReportsState(workspaceId: tracker.workspace.id) })
                }
            case .projects:
                if let tracker = model.tracker {
                    ProjectsView(tracker: tracker, client: model.client)
                }
            case .clients:
                if let tracker = model.tracker {
                    ClientsView(tracker: tracker, client: model.client)
                }
            case .tags:
                if let tracker = model.tracker {
                    TagsView(tracker: tracker, client: model.client)
                }
            }
        }
        .safeAreaInset(edge: .top, spacing: 0) {
            VStack(spacing: 0) {
                ReadOnlyBanner()
                ProposalBanner()
            }
        }
        .navigationTitle(section.title)
    }
}

private struct Sidebar: View {
    @Binding var selection: AppSection

    var body: some View {
        List(selection: Binding($selection)) {
            ForEach(AppSection.groups, id: \.heading) { group in
                Section(group.heading) {
                    ForEach(group.sections) { section in
                        Label(section.title, systemImage: section.systemImage)
                            .tag(section)
                    }
                }
            }
        }
        .safeAreaInset(edge: .top, spacing: 0) {
            WorkspaceSwitcher()
                .padding(.horizontal, 10)
                .padding(.bottom, 6)
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            if selection != .timer {
                SidebarTimer()
                    .padding(10)
            }
        }
    }
}

/// The signed-in person, with account settings and signing out.
private struct AccountMenu: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openURL) private var openURL

    var body: some View {
        if let me = model.me {
            Menu {
                Section {
                    Text(me.fullName)
                    Text(me.email)
                }
                Divider()
                Button("Account Settings…") {
                    open("account")
                }
                if model.isWorkspaceAdmin {
                    Button("Workspace Settings…") {
                        open("settings")
                    }
                    Button("Team…") {
                        open("team")
                    }
                }
                Button("Open Spindle on the Web") {
                    open("timer")
                }
                Divider()
                Button("Sign Out") {
                    model.requestSignOut()
                }
            } label: {
                Label(me.fullName, systemImage: "person.crop.circle")
            }
            .help("Account")
        }
    }

    private func open(_ path: String) {
        openURL(model.webURL(path))
    }
}

/// The running timer at the foot of the sidebar, so it can be seen and stopped from any section.
private struct SidebarTimer: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if let tracker = model.tracker, let running = tracker.running {
            HStack(spacing: 8) {
                Button {
                    model.section = .timer
                } label: {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(running.description.isEmpty ? "No description" : running.description)
                            .font(.callout.weight(.medium))
                            .lineLimit(1)
                        HStack(spacing: 5) {
                            if running.isSplit {
                                SplitDots(catalog: tracker.catalog, parts: running.parts, size: 7)
                            } else if let project = tracker.catalog.project(running.projectId) {
                                ProjectDot(color: project.color, size: 7)
                            }
                            Text(model.menuBarClock ?? Durations.clock(running.seconds(at: Date())))
                                .font(.caption.monospacedDigit())
                                .foregroundStyle(.secondary)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Open the running timer")
                Button {
                    Task { await model.stop() }
                } label: {
                    Image(systemName: "stop.fill")
                        .font(.system(size: 9, weight: .bold))
                        .foregroundStyle(.white)
                        .frame(width: 24, height: 24)
                        .background(Color.red, in: Circle())
                }
                .buttonStyle(.plain)
                .help("Stop timer")
                .accessibilityLabel("Stop timer")
            }
            .padding(8)
            .background(.background.opacity(0.6), in: RoundedRectangle(cornerRadius: 9))
            .overlay(RoundedRectangle(cornerRadius: 9).strokeBorder(.separator))
        }
    }
}

/// Whether changes reach the server: nothing when all is well, otherwise that the app is offline or catching up, or
/// that changes wait for the person because their entries changed on another device.
private struct SyncStatus: View {
    @Environment(AppModel.self) private var model
    @State private var reviewing = false

    var body: some View {
        if let outbox = model.account?.outbox {
            let waiting = outbox.pending.count
            let conflicts = outbox.conflicts.count
            if conflicts > 0, let tracker = model.tracker {
                Button {
                    reviewing = true
                } label: {
                    Label(conflicts == 1 ? "1 change to review" : "\(conflicts) changes to review", systemImage: "exclamationmark.arrow.triangle.2.circlepath")
                        .labelStyle(.titleAndIcon)
                }
                .foregroundStyle(.orange)
                .help("These entries changed on another device after you changed them here. Choose which version to keep.")
                .popover(isPresented: $reviewing) {
                    EntryConflicts(tracker: tracker) { reviewing = false }
                        .frame(width: 520, height: 380)
                }
            } else if !model.connectivity.isOnline || outbox.isOffline {
                Label(waiting > 0 ? "Offline · \(waiting) waiting" : "Offline", systemImage: "wifi.slash")
                    .labelStyle(.titleAndIcon)
                    .foregroundStyle(.secondary)
                    .help(waiting > 0
                        ? "Spindle can't reach the server. Your changes are kept on this Mac and sync when it's back."
                        : "Spindle can't reach the server. You can go on tracking; changes sync when it's back.")
            } else if waiting > 0 {
                Label("Syncing…", systemImage: "arrow.triangle.2.circlepath")
                    .labelStyle(.titleAndIcon)
                    .foregroundStyle(.secondary)
            }
        }
    }
}

