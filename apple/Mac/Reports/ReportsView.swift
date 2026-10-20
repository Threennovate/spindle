import SwiftUI
import SpindleKit
import UniformTypeIdentifiers

/// Summary, detailed and weekly reports of the workspace's time, with the same settings as the web app, so a
/// report saved on one opens the same on the other.
struct ReportsView: View {
    @Environment(AppModel.self) private var model
    let tracker: Tracker
    let client: SpindleClient
    /// The settings and the last reports, kept by the model so going back to Reports shows them at once.
    @Bindable var state: ReportsState

    private var settings: ReportSettings {
        get { state.settings }
        nonmutating set { state.settings = newValue }
    }
    private var savedId: String? {
        get { state.savedId }
        nonmutating set { state.savedId = newValue }
    }
    private var savedReports: [SavedReport] {
        get { state.savedReports }
        nonmutating set { state.savedReports = newValue }
    }
    private var members: [Member] { state.members }
    private var groups: [WorkspaceGroup] { state.groups }
    private var summary: SummaryReport? { state.summary }
    private var detailed: DetailedReport? { state.detailed }
    private var weekly: WeeklyReport? { state.weekly }
    @State private var loading = false
    @State private var loadError: String?
    @State private var naming = false
    @State private var confirmingDelete = false
    @State private var choosingRange = false
    @State private var exporting: ExportFormat?

    private var workspaceId: String { tracker.workspace.id }
    private var time: UserTime { tracker.time }
    private var range: DayRange { settings.range(today: time.day(of: Date()), weekStart: time.weekStart) }
    private var current: SavedReport? { savedReports.first { $0.id == savedId } }

    /// What the report shown depends on; a change loads it again.
    struct Request: Equatable {
        let settings: ReportSettings
        let range: DayRange
        let revision: Int

        /// Whether this asks for the same report as `other`, perhaps newer.
        func isSameReport(as other: Request) -> Bool {
            settings == other.settings && range == other.range
        }

        /// Whether only the description filter changed since `other`, as it does while typing in it.
        func isTyping(after other: Request) -> Bool {
            var typed = other.settings
            typed.q = settings.q
            typed.page = settings.page
            return settings.q != other.settings.q && typed == settings && range == other.range
        }
    }

    private var request: Request {
        Request(settings: settings, range: range, revision: model.dataRevision)
    }

    /// A report being loaded for other settings shows faded; one being refreshed, e.g. on coming back, doesn't.
    private var refreshing: Bool {
        state.loaded[settings.currentTab].map { $0.isSameReport(as: request) } ?? false
    }

    var body: some View {
        VStack(spacing: 0) {
            header
                .padding(.horizontal, 20)
                .padding(.top, 14)
            ReportFilters(tracker: tracker, settings: $state.settings, members: members, groups: groups)
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
            Divider()
            content
                .opacity(loading && !refreshing ? 0.6 : 1)
                .animation(.default, value: loading)
        }
        .navigationSubtitle(tracker.isAdmin ? "Time tracked in \(tracker.workspace.name)" : "Your time in \(tracker.workspace.name)")
        .task { await start() }
        .task(id: request) {
            let request = request
            await Performance.measure("Report shown") {
                // Typing a description loads once it pauses; opening Reports, a period or a tab loads at once.
                if let previous = state.requested, request.isTyping(after: previous) {
                    try? await Task.sleep(for: .milliseconds(250))
                    guard !Task.isCancelled else { return }
                }
                state.requested = request
                await load()
            }
        }
        .onChange(of: settings) {
            UserDefaults.standard.set(settings.savedJSON, forKey: "reports:\(workspaceId)")
        }
        .sheet(isPresented: $naming) {
            NameSheet(title: "Save report", placeholder: "e.g. Monthly billing") { name in
                try await save(named: name, id: nil)
            }
        }
        .confirmationDialog("Delete “\(current?.name ?? "")”?", isPresented: $confirmingDelete) {
            Button("Delete", role: .destructive, action: deleteCurrent)
        } message: {
            Text("The saved report goes for everyone who uses it; the time in it stays.")
        }
    }

    // MARK: - Header

    /// One row when the window is wide enough, otherwise the period on a row of its own.
    private var header: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 10) {
                tabPicker
                periodPicker
                Spacer(minLength: 10)
                actions
            }
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 10) {
                    tabPicker
                    Spacer(minLength: 10)
                    actions
                }
                periodPicker
            }
        }
    }

    private var tabPicker: some View {
        Picker("Report", selection: Binding(get: { settings.currentTab }, set: { tab in change { $0.tab = tab } })) {
            ForEach(ReportTab.allCases, id: \.self) { tab in
                Text(tab.label).tag(tab)
            }
        }
        .pickerStyle(.segmented)
        .labelsHidden()
        .fixedSize()
    }

    private var actions: some View {
        HStack(spacing: 10) {
            savedMenu
            exportMenu
        }
    }

    private var exportMenu: some View {
        Menu {
            ForEach(ExportFormat.allCases, id: \.self) { format in
                Button(format.label) { export(format) }
            }
        } label: {
            Label(exporting == nil ? "Export" : "Exporting…", systemImage: "square.and.arrow.up")
        }
        .fixedSize()
        .disabled(exporting != nil)
    }

    private var periodPicker: some View {
        let weekly = settings.currentTab == .weekly
        let presets: [ReportPeriod] = weekly ? [.thisWeek, .lastWeek] : ReportPeriod.allCases.filter { $0 != .custom }
        return HStack(spacing: 2) {
            Button {
                shift(-1)
            } label: {
                Image(systemName: "chevron.left")
            }
            .help(weekly ? "Previous week" : "Previous period")
            Menu {
                ForEach(presets, id: \.self) { period in
                    Button {
                        change {
                            $0.period = period
                            $0.from = nil
                            $0.to = nil
                        }
                    } label: {
                        if settings.currentPeriod == period {
                            Label(period.label, systemImage: "checkmark")
                        } else {
                            Text(period.label)
                        }
                    }
                }
                Divider()
                Button(weekly ? "Choose a Week…" : "Custom Range…") { choosingRange = true }
            } label: {
                Label(range.label, systemImage: "calendar")
            }
            .fixedSize()
            .popover(isPresented: $choosingRange, arrowEdge: .bottom) {
                RangePicker(range: range, time: time, weekly: weekly) { picked in
                    change {
                        $0.period = .custom
                        $0.from = picked.from.description
                        $0.to = picked.to.description
                    }
                    choosingRange = false
                }
            }
            Button {
                shift(1)
            } label: {
                Image(systemName: "chevron.right")
            }
            .help(weekly ? "Next week" : "Next period")
        }
        .buttonStyle(.bordered)
    }

    private var savedMenu: some View {
        Menu {
            if !savedReports.isEmpty {
                Section("Saved reports") {
                    ForEach(savedReports) { report in
                        Button {
                            open(report)
                        } label: {
                            if report.id == savedId {
                                Label(report.name, systemImage: "checkmark")
                            } else {
                                Text(report.name)
                            }
                        }
                    }
                }
                Divider()
            }
            if let current {
                Button("Save Changes to “\(current.name)”") {
                    Task { try? await save(named: current.name, id: current.id) }
                }
            }
            Button("Save as New Report…") { naming = true }
            if current != nil {
                Divider()
                Button("Delete “\(current?.name ?? "")”…", role: .destructive) { confirmingDelete = true }
            }
        } label: {
            Label(current?.name ?? "Saved Reports", systemImage: current == nil ? "bookmark" : "bookmark.fill")
        }
        .fixedSize()
    }

    // MARK: - The report

    @ViewBuilder private var content: some View {
        if let loadError, summary == nil && detailed == nil && weekly == nil {
            ContentUnavailableView {
                Label("Couldn't load the report", systemImage: "exclamationmark.triangle")
            } description: {
                Text(loadError)
            } actions: {
                Button("Try Again") { Task { await load() } }
            }
        } else {
            switch settings.currentTab {
            case .summary:
                if let summary {
                    SummaryReportView(
                        report: summary, tracker: tracker, groupBy: settings.currentGroupBy, subGroupBy: settings.currentSubGroupBy,
                        filtered: settings.hasFilters
                    ) { groupBy, subGroupBy in
                        settings.groupBy = groupBy
                        settings.subGroupBy = subGroupBy?.rawValue ?? "none"
                    }
                } else {
                    loadingView
                }
            case .detailed:
                if let detailed {
                    DetailedReportView(report: detailed, tracker: tracker, settings: $state.settings)
                } else {
                    loadingView
                }
            case .weekly:
                if let weekly {
                    WeeklyReportView(report: weekly, tracker: tracker, settings: $state.settings)
                } else {
                    loadingView
                }
            }
        }
    }

    private var loadingView: some View {
        ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    // MARK: - Doing things

    /// Changes the settings; anything that changes what the report covers starts it again from its first page.
    private func change(_ edit: (inout ReportSettings) -> Void) {
        var next = settings
        edit(&next)
        next.page = nil
        settings = next
    }

    private func shift(_ direction: Int) {
        let next = range.shifted(direction)
        change {
            $0.period = .custom
            $0.from = next.from.description
            $0.to = next.to.description
        }
    }

    private func start() async {
        async let saved = try? client.savedReports(in: workspaceId)
        async let groups = try? client.groups(in: workspaceId)
        async let members = tracker.isAdmin ? try? client.members(in: workspaceId) : nil
        savedReports = await saved ?? savedReports
        state.groups = await groups ?? state.groups
        state.members = await members ?? state.members
    }

    private func load() async {
        loading = true
        defer { loading = false }
        let request = request
        let settings = request.settings
        let range = request.range
        do {
            try await Performance.measure("Report request") {
                switch settings.currentTab {
                case .summary: state.summary = try await client.summaryReport(in: workspaceId, settings, range: range)
                case .detailed: state.detailed = try await client.detailedReport(in: workspaceId, settings, range: range)
                case .weekly: state.weekly = try await client.weeklyReport(in: workspaceId, settings, range: range)
                }
            }
            state.loaded[settings.currentTab] = request
            loadError = nil
        } catch {
            if !Task.isCancelled {
                loadError = error.localizedDescription
            }
        }
    }

    private func open(_ report: SavedReport) {
        settings = ReportSettings(json: report.parameters)
        savedId = report.id
    }

    private func save(named name: String, id: String?) async throws {
        do {
            let report = try await client.saveReport(settings, named: name, id: id, in: workspaceId)
            savedReports = (try? await client.savedReports(in: workspaceId)) ?? savedReports
            savedId = report.id
            model.notices.show("“\(report.name)” saved")
        } catch {
            model.notices.show(error, fallback: "Couldn't save the report.")
            throw error
        }
    }

    private func deleteCurrent() {
        guard let current else { return }
        Task {
            do {
                try await client.deleteSavedReport(current.id, in: workspaceId)
                savedReports.removeAll { $0.id == current.id }
                savedId = nil
                model.notices.show("“\(current.name)” deleted")
            } catch {
                model.notices.show(error, fallback: "Couldn't delete the report.")
            }
        }
    }

    /// Fetches the report as a file and asks where to keep it.
    private func export(_ format: ExportFormat) {
        exporting = format
        Task {
            defer { exporting = nil }
            do {
                let file = try await client.exportReport(in: workspaceId, settings, range: range, format: format)
                let panel = NSSavePanel()
                panel.nameFieldStringValue = file.fileName
                panel.allowedContentTypes = UTType(filenameExtension: format.rawValue).map { [$0] } ?? []
                panel.canCreateDirectories = true
                guard panel.runModal() == .OK, let url = panel.url else { return }
                try file.data.write(to: url, options: .atomic)
                model.notices.show("Report exported")
            } catch {
                model.notices.show(error, fallback: "Couldn't export the report.")
            }
        }
    }
}

/// What Reports shows in one workspace: kept by the model rather than the view, so going back to Reports shows the
/// last report at once and loads it again behind it.
@Observable
final class ReportsState {
    var settings: ReportSettings
    var savedId: String?
    var savedReports: [SavedReport] = []
    var members: [Member] = []
    var groups: [WorkspaceGroup] = []
    var summary: SummaryReport?
    var detailed: DetailedReport?
    var weekly: WeeklyReport?
    /// What each tab's report was loaded for.
    var loaded: [ReportTab: ReportsView.Request] = [:]
    /// The report asked for last, to tell typing in the description filter from other changes.
    @ObservationIgnored var requested: ReportsView.Request?

    /// Opens with the settings used last in the workspace.
    init(workspaceId: String) {
        settings = UserDefaults.standard.string(forKey: "reports:\(workspaceId)").map(ReportSettings.init(json:)) ?? ReportSettings()
    }
}

/// Picks a range of days, or for the weekly report a week by any of its days.
private struct RangePicker: View {
    let time: UserTime
    let weekly: Bool
    let onPick: (DayRange) -> Void
    @State private var from: Date
    @State private var to: Date

    init(range: DayRange, time: UserTime, weekly: Bool, onPick: @escaping (DayRange) -> Void) {
        self.time = time
        self.weekly = weekly
        self.onPick = onPick
        _from = State(initialValue: time.date(range.from, hours: 12, minutes: 0))
        _to = State(initialValue: time.date(range.to, hours: 12, minutes: 0))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if weekly {
                DatePicker("Week of", selection: $from, displayedComponents: .date)
                    .datePickerStyle(.graphical)
            } else {
                HStack(alignment: .top, spacing: 16) {
                    VStack(alignment: .leading) {
                        Text("From").font(.callout.weight(.medium))
                        DatePicker("From", selection: $from, displayedComponents: .date).datePickerStyle(.graphical).labelsHidden()
                    }
                    VStack(alignment: .leading) {
                        Text("To").font(.callout.weight(.medium))
                        DatePicker("To", selection: $to, in: from..., displayedComponents: .date).datePickerStyle(.graphical).labelsHidden()
                    }
                }
            }
            HStack {
                Spacer()
                Button("Show") {
                    let start = time.day(of: from)
                    let end = weekly ? start : max(start, time.day(of: to))
                    onPick(DayRange(from: start, to: end))
                }
                .buttonStyle(.brand)
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(16)
        .environment(\.timeZone, time.timeZone)
        .environment(\.calendar, time.calendar)
    }
}
