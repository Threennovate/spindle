import SwiftUI
import SpindleKit

/// Summary, detailed and weekly reports of the workspace's time, with the same settings as the web and Mac apps,
/// so a report saved on one opens the same on the others.
struct ReportsTab: View {
    @Environment(AppModel.self) private var model
    let tracker: Tracker
    let client: SpindleClient

    @State private var settings = ReportSettings()
    @State private var savedId: String?
    @State private var savedReports: [SavedReport] = []
    @State private var members: [Member] = []
    @State private var groups: [WorkspaceGroup] = []
    @State private var summary: SummaryReport?
    @State private var detailed: DetailedReport?
    @State private var weekly: WeeklyReport?
    @State private var loading = false
    @State private var loadError: String?
    @State private var filtering = false
    @State private var choosingRange = false
    @State private var naming = false
    @State private var newName = ""
    @State private var confirmingDelete = false
    @State private var exporting = false
    @State private var exported: ExportedFile?

    private var workspaceId: String { tracker.workspace.id }
    private var time: UserTime { tracker.time }
    private var range: DayRange { settings.range(today: time.day(of: AppClock.now), weekStart: time.weekStart) }
    private var current: SavedReport? { savedReports.first { $0.id == savedId } }

    /// What the report shown depends on; a change loads it again.
    private struct Request: Equatable {
        let settings: ReportSettings
        let range: DayRange
        let revision: Int
    }

    var body: some View {
        NavigationStack {
            List {
                Section {
                    controls
                }
                .listRowBackground(Color.clear)
                .listRowInsets(EdgeInsets(top: 4, leading: 0, bottom: 4, trailing: 0))
                content
            }
            .listStyle(.insetGrouped)
            #if DEBUG
            .modifier(ScrollsToBreakdown(trigger: summary?.totals.seconds))
            #endif
            .opacity(loading ? 0.6 : 1)
            .animation(.default, value: loading)
            .navigationTitle(current?.name ?? "Reports")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button {
                        filtering = true
                    } label: {
                        Image(systemName: settings.hasFilters ? "line.3.horizontal.decrease.circle.fill" : "line.3.horizontal.decrease.circle")
                    }
                    .accessibilityLabel(settings.hasFilters ? "Filters, some on" : "Filters")
                }
                ToolbarItem(placement: .topBarTrailing) {
                    moreMenu
                }
            }
            .refreshable { await load() }
            .task { await start() }
            .task(id: Request(settings: settings, range: range, revision: model.dataRevision)) {
                // Typing a description in the filters loads once it pauses.
                try? await Task.sleep(for: .milliseconds(250))
                guard !Task.isCancelled else { return }
                await load()
            }
            .onChange(of: settings) {
                UserDefaults.standard.set(settings.savedJSON, forKey: "reports:\(workspaceId)")
            }
            .sheet(isPresented: $filtering) {
                ReportFiltersSheet(tracker: tracker, choices: ReportFilterChoices(catalog: tracker.catalog, members: members, groups: groups), settings: $settings)
            }
            .sheet(isPresented: $choosingRange) {
                RangeSheet(range: range, time: time, weekly: settings.currentTab == .weekly) { picked in
                    change {
                        $0.period = .custom
                        $0.from = picked.from.description
                        $0.to = picked.to.description
                    }
                }
            }
            .sheet(item: $exported) { file in
                ShareSheet(url: file.url)
            }
            .alert("Save report", isPresented: $naming) {
                TextField("e.g. Monthly billing", text: $newName)
                Button("Cancel", role: .cancel) {}
                Button("Save") {
                    let name = newName.trimmingCharacters(in: .whitespaces)
                    if !name.isEmpty { Task { await save(named: name, id: nil) } }
                }
            } message: {
                Text("Saved reports are shared with the workspace, on every device.")
            }
            .confirmationDialog("Delete “\(current?.name ?? "")”?", isPresented: $confirmingDelete, titleVisibility: .visible) {
                Button("Delete", role: .destructive, action: deleteCurrent)
            } message: {
                Text("The saved report goes for everyone who uses it; the time in it stays.")
            }
        }
    }

    // MARK: - Controls

    private var controls: some View {
        VStack(spacing: 12) {
            Picker("Report", selection: Binding(get: { settings.currentTab }, set: { tab in change { $0.tab = tab } })) {
                ForEach(ReportTab.allCases, id: \.self) { Text($0.label).tag($0) }
            }
            .pickerStyle(.segmented)

            HStack(spacing: 6) {
                Button { shift(-1) } label: {
                    Image(systemName: "chevron.left").frame(width: 36, height: 36)
                }
                .accessibilityLabel(settings.currentTab == .weekly ? "Previous week" : "Previous period")
                Menu {
                    let presets: [ReportPeriod] = settings.currentTab == .weekly ? [.thisWeek, .lastWeek] : ReportPeriod.allCases.filter { $0 != .custom }
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
                    Button(settings.currentTab == .weekly ? "Choose a Week…" : "Custom Range…") { choosingRange = true }
                } label: {
                    Label(range.label, systemImage: "calendar")
                        .font(.subheadline.weight(.semibold))
                        .frame(maxWidth: .infinity)
                        .frame(height: 36)
                }
                Button { shift(1) } label: {
                    Image(systemName: "chevron.right").frame(width: 36, height: 36)
                }
                .accessibilityLabel(settings.currentTab == .weekly ? "Next week" : "Next period")
            }
            // In a list row, buttons without a style all fire on any tap in it, so the arrows would cancel out.
            .buttonStyle(.borderless)
            .background(Color(.secondarySystemGroupedBackground), in: Capsule())

            if settings.hasFilters {
                HStack {
                    Label("Filtered", systemImage: "line.3.horizontal.decrease")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button("Clear Filters") { settings = settings.withoutFilters }
                        .font(.footnote)
                        .buttonStyle(.brandLink)
                }
                .padding(.horizontal, 4)
            }
        }
    }

    private var moreMenu: some View {
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
            }
            Section {
                if let current {
                    Button("Save Changes to “\(current.name)”") {
                        Task { await save(named: current.name, id: current.id) }
                    }
                }
                Button("Save as New Report…") {
                    newName = ""
                    naming = true
                }
                if current != nil {
                    Button("Delete Saved Report…", role: .destructive) { confirmingDelete = true }
                }
            }
            Section("Export") {
                ForEach(ExportFormat.allCases, id: \.self) { format in
                    Button(format.label) { export(format) }
                }
            }
            .disabled(exporting)
        } label: {
            if exporting {
                ProgressView()
            } else {
                Image(systemName: "ellipsis.circle")
            }
        }
        .accessibilityLabel("Saved reports and export")
    }

    // MARK: - The report

    @ViewBuilder private var content: some View {
        if let loadError, summary == nil && detailed == nil && weekly == nil {
            Section {
                VStack(spacing: 10) {
                    Label("Couldn't load the report", systemImage: "exclamationmark.triangle")
                        .font(.headline)
                    Text(loadError)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                    Button("Try Again") { Task { await load() } }
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 12)
            }
        } else {
            switch settings.currentTab {
            case .summary:
                if let summary {
                    SummaryContent(report: summary, tracker: tracker, settings: $settings, filtered: settings.hasFilters)
                } else {
                    loadingRow
                }
            case .detailed:
                if let detailed {
                    DetailedContent(report: detailed, tracker: tracker, settings: $settings)
                } else {
                    loadingRow
                }
            case .weekly:
                if let weekly {
                    WeeklyContent(report: weekly, tracker: tracker, settings: $settings)
                } else {
                    loadingRow
                }
            }
        }
    }

    private var loadingRow: some View {
        Section {
            ProgressView()
                .frame(maxWidth: .infinity)
                .padding(.vertical, 30)
        }
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
        if let saved = UserDefaults.standard.string(forKey: "reports:\(workspaceId)") {
            settings = ReportSettings(json: saved)
        }
        #if DEBUG
        if let tab = LaunchOptions.current.reportTab {
            settings.tab = tab
        }
        filtering = LaunchOptions.current.showsFilters
        if let format = LaunchOptions.current.export {
            export(format)
        }
        #endif
        async let saved = try? client.savedReports(in: workspaceId)
        async let groups = try? client.groups(in: workspaceId)
        async let members = tracker.isAdmin ? try? client.members(in: workspaceId) : nil
        savedReports = await saved ?? []
        self.groups = await groups ?? []
        self.members = await members ?? []
    }

    private func load() async {
        loading = true
        defer { loading = false }
        let settings = settings
        let range = range
        do {
            switch settings.currentTab {
            case .summary: summary = try await client.summaryReport(in: workspaceId, settings, range: range)
            case .detailed: detailed = try await client.detailedReport(in: workspaceId, settings, range: range)
            case .weekly: weekly = try await client.weeklyReport(in: workspaceId, settings, range: range)
            }
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

    private func save(named name: String, id: String?) async {
        do {
            let report = try await client.saveReport(settings, named: name, id: id, in: workspaceId)
            savedReports = (try? await client.savedReports(in: workspaceId)) ?? savedReports
            savedId = report.id
            model.notices.show("“\(report.name)” saved")
        } catch {
            model.notices.show(error, fallback: "Couldn't save the report.")
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

    /// Fetches the report as a file, then offers it to share, save to Files or open in another app.
    private func export(_ format: ExportFormat) {
        exporting = true
        Task {
            defer { exporting = false }
            do {
                let file = try await client.exportReport(in: workspaceId, settings, range: range, format: format)
                let folder = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString, directoryHint: .isDirectory)
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                let url = folder.appending(path: file.fileName)
                try file.data.write(to: url, options: .atomic)
                exported = ExportedFile(url: url)
            } catch {
                model.notices.show(error, fallback: "Couldn't export the report.")
            }
        }
    }
}

/// A file made to be shared.
struct ExportedFile: Identifiable {
    let url: URL
    var id: URL { url }
}

/// Picks a range of days, or for the weekly report a week by any of its days.
private struct RangeSheet: View {
    @Environment(\.dismiss) private var dismiss
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
        NavigationStack {
            Form {
                if weekly {
                    DatePicker("Week of", selection: $from, displayedComponents: .date)
                        .datePickerStyle(.graphical)
                } else {
                    Section("From") {
                        DatePicker("From", selection: $from, displayedComponents: .date)
                    }
                    Section("To") {
                        DatePicker("To", selection: $to, in: from..., displayedComponents: .date)
                    }
                }
            }
            .accountTime(time)
            .navigationTitle(weekly ? "Week" : "Custom Range")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Show") {
                        let start = time.day(of: from)
                        onPick(DayRange(from: start, to: weekly ? start : max(start, time.day(of: to))))
                        dismiss()
                    }
                }
            }
        }
        .presentationDetents(weekly ? [.large] : [.medium, .large])
    }
}

/// The system's share sheet, e.g. to save an exported report to Files or send it.
struct ShareSheet: UIViewControllerRepresentable {
    let url: URL

    func makeUIViewController(context: Context) -> UIActivityViewController {
        UIActivityViewController(activityItems: [url], applicationActivities: nil)
    }

    func updateUIViewController(_ controller: UIActivityViewController, context: Context) {}
}
