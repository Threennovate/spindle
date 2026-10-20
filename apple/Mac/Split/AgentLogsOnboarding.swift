import SwiftUI
import SpindleKit

/// Right after agent logs are turned on: what the last week's logs found in the time already tracked, the
/// repositories to link to projects, and the way into that week's review. So the point shows in the first minutes,
/// not when the first parallel entry stops.
struct AgentLogsOnboarding: View {
    @Environment(Desktop.self) private var desktop
    let tracker: Tracker

    enum Step: String {
        case found, link, review
    }

    @State private var step: Step = Self.firstStep
    @State private var findings: Findings?
    /// The project chosen for each repository, by its identity; nil for none.
    @State private var choices: [String: String?] = [:]
    @State private var branchRules: BranchRulesTarget?

    /// The days the finding is about, and the review opens on.
    static let findingDays = 7
    /// The days whose repositories are offered for linking.
    static let repositoryDays = 14

    private var time: UserTime { tracker.time }
    private var today: DayKey { time.day(of: Date()) }
    private var week: [DayKey] { (0..<Self.findingDays).reversed().map { time.adding(-$0, to: today) } }
    private var fortnight: [DayKey] { (0..<Self.repositoryDays).reversed().map { time.adding(-$0, to: today) } }

    /// What the logs found, worked out once they are read.
    struct Findings {
        var elsewhere: ActivityFindings.Elsewhere
        /// All the time tracked in those days, finished entries only.
        var tracked: TimeInterval
        var untracked: TimeInterval
        var repositories: [ActivityFindings.Repository]
    }

    var body: some View {
        VStack(spacing: 0) {
            Group {
                if let findings {
                    switch step {
                    case .found: found(findings)
                    case .link: link(findings)
                    case .review: review
                    }
                } else {
                    VStack(spacing: 12) {
                        ProgressView()
                        Text("Reading the last \(Self.findingDays) days of \(AgentSource.names) logs…")
                            .foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity, minHeight: 280)
                }
            }
            .padding(.horizontal, 32)
            .padding(.top, 28)
            .padding(.bottom, 24)
            .frame(maxWidth: .infinity, alignment: .topLeading)
            Divider()
            footer
                .padding(.horizontal, 20)
                .padding(.vertical, 14)
        }
        .frame(width: 600)
        .fixedSize(horizontal: false, vertical: true)
        .sheet(item: $branchRules, onDismiss: {
            // Rules are saved with the repository's link, so its project here is the link's.
            for (key, projectId) in desktop.splits.chosen where choices[key] != nil { choices[key] = .some(projectId) }
        }) { target in
            BranchRulesEditor(repository: target.repository, tracker: tracker, splits: desktop.splits)
        }
        .task {
            await desktop.loadReview(week, tracker: tracker)
            await desktop.splits.load(fortnight, tracker: tracker)
            work()
        }
    }

    // MARK: - Steps

    private func found(_ findings: Findings) -> some View {
        let elsewhere = findings.elsewhere
        return VStack(alignment: .leading, spacing: 0) {
            Eyebrow(text: "Last \(Self.findingDays) days")
            if elsewhere.otherProjects > 0 {
                Text(Durations.hoursMinutes(elsewhere.otherProjects))
                    .font(.system(size: 44, weight: .semibold, design: .rounded))
                    .foregroundStyle(Color.brandText)
                    .monospacedDigit()
                    .padding(.top, 10)
                Text("of tracked time went to other projects than the one on the timer.")
                    .font(.title3.weight(.medium))
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Text(elsewhere.entries > 0 || elsewhere.unlinked > 0
                     ? "No tracked time went to another project."
                     : findings.repositories.isEmpty ? "No agent work in the logs in the last \(Self.findingDays) days."
                     : "No tracked time overlaps agent work in the last \(Self.findingDays) days.")
                    .font(.title2.weight(.semibold))
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 10)
            }
            if findings.tracked > 0 {
                ShareBar(tracked: findings.tracked, elsewhere: elsewhere.otherProjects, unlinked: elsewhere.unlinked)
                    .padding(.top, 18)
                Text(shareCaption(findings))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
                    .padding(.top, 6)
            }
            HStack(alignment: .top, spacing: 10) {
                Tile(symbol: "rectangle.split.2x1", value: "\(elsewhere.entries)",
                     caption: elsewhere.entries == 1 ? "entry with a split to offer" : "entries with a split to offer")
                Tile(symbol: "link", value: Durations.hoursMinutes(elsewhere.unlinked), caption: "in repositories without a project yet")
                Tile(symbol: "clock.badge.questionmark", value: Durations.hoursMinutes(findings.untracked), caption: "of agent work with no timer running")
            }
            .fixedSize(horizontal: false, vertical: true)
            .padding(.top, 22)
            Label("Counted by where your prompts went. Time without prompts stays on the timer's project. Nothing changes until you take a suggestion.",
                  systemImage: "info.circle")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 20)
        }
    }

    private func shareCaption(_ findings: Findings) -> String {
        let share = Int((findings.elsewhere.otherProjects / findings.tracked * 100).rounded())
        return "\(share)% of \(Durations.hoursMinutes(findings.tracked)) tracked" + (findings.elsewhere.unlinked > 0 ? " · hatched: repositories without a project" : "")
    }

    private func link(_ findings: Findings) -> some View {
        let most = findings.repositories.map(\.attention).max() ?? 1
        return VStack(alignment: .leading, spacing: 0) {
            Text("Link repositories to projects")
                .font(.title2.weight(.semibold))
            Text("Agent work in the last \(Self.repositoryDays) days, most first. Guesses come from repository names and past entries. Links sync to your account.")
                .foregroundStyle(.secondary)
                .padding(.top, 4)
            ScrollView {
                VStack(spacing: 0) {
                    ForEach(findings.repositories, id: \.repository.key) { found in
                        repositoryRow(found, most: most)
                        if found.repository != findings.repositories.last?.repository { Divider().padding(.leading, 34) }
                    }
                }
                .padding(.horizontal, 12)
            }
            .frame(maxHeight: 300)
            .fixedSize(horizontal: false, vertical: true)
            .background(Color.primary.opacity(0.03), in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Color.primary.opacity(0.1)))
            .padding(.top, 16)
        }
    }

    private func repositoryRow(_ found: ActivityFindings.Repository, most: TimeInterval) -> some View {
        let key = found.repository.identity ?? found.repository.key
        let projects = tracker.catalog.projects.filter { !$0.isArchived }
        let choice = choices[key] ?? nil
        let color = choice.flatMap { tracker.catalog.project($0) }.map { Color(hex: $0.color) } ?? ActivityStyle.color(found.repository.key)
        return HStack(spacing: 12) {
            Circle()
                .fill(color)
                .frame(width: 10, height: 10)
            VStack(alignment: .leading, spacing: 3) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(found.repository.name)
                        .fontWeight(.semibold)
                    if let origin = found.repository.origin {
                        Text(origin)
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                            .lineLimit(1)
                            .truncationMode(.middle)
                    }
                }
                HStack(spacing: 8) {
                    GeometryReader { geometry in
                        Capsule()
                            .fill(color.opacity(0.7))
                            .frame(width: max(4, geometry.size.width * found.attention / most))
                    }
                    .frame(width: 120, height: 4)
                    Text(Durations.hoursMinutes(found.attention))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                }
            }
            Spacer(minLength: 12)
            Picker("Project for \(found.repository.name)", selection: Binding(
                get: { choices[key] ?? nil },
                set: { choices[key] = .some($0) })) {
                Text("No Project").tag(String?.none)
                Divider()
                ForEach(projects) { project in
                    Text(project.name).tag(String?.some(project.id))
                }
            }
            .labelsHidden()
            .frame(width: 180)
            BranchRulesButton(repository: found.repository, short: true, editing: $branchRules)
                .labelStyle(.iconOnly)
                .buttonStyle(.borderless)
        }
        .padding(.vertical, 10)
    }

    private var review: some View {
        let count = desktop.review(on: week, tracker: tracker).count
        return VStack(alignment: .leading, spacing: 0) {
            let items = desktop.review(on: week, tracker: tracker)
            Text(count == 0 ? "Nothing to review from the last \(Self.findingDays) days"
                 : "\(count) \(count == 1 ? "suggestion" : "suggestions") from the last \(Self.findingDays) days")
                .font(.title2.weight(.semibold))
            if count > 0 {
                Text(ReviewItem.breakdown(items))
                    .foregroundStyle(.secondary)
                    .padding(.top, 4)
            }
            VStack(alignment: .leading, spacing: 14) {
                Way(symbol: "tray.full", title: "Where suggestions wait",
                    caption: "In the day's review. Open it from the menu bar, a day's header in the list or calendar, or the card above the timer the next morning.")
                Way(symbol: "bell", title: "When the app interrupts",
                    caption: "Only when the timer stops, and only for splits where the logs cover at least 60% of the time with two or more prompts per project.")
                Way(symbol: "waveform.path", title: "Reading a suggestion",
                    caption: "Ticks are prompts, bars are agent attention, hatched is time the logs don't cover.")
            }
            .padding(.top, 20)
        }
    }

    // MARK: - Footer

    private var footer: some View {
        HStack(spacing: 10) {
            HStack(spacing: 6) {
                ForEach([Step.found, .link, .review], id: \.self) { each in
                    Capsule()
                        .fill(each == step ? Color.brand : Color.primary.opacity(0.15))
                        .frame(width: each == step ? 18 : 6, height: 6)
                }
            }
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(stepLabel)
            .animation(.snappy, value: step)
            Spacer()
            switch step {
            case .found:
                Button("Close") { desktop.onboarding = false }
                    .keyboardShortcut(.cancelAction)
                Button(findings?.repositories.isEmpty == false ? "Next" : "Done") {
                    if findings?.repositories.isEmpty == false { step = .link } else { desktop.onboarding = false }
                }
                .buttonStyle(.brand)
                .keyboardShortcut(.defaultAction)
                .disabled(findings == nil)
            case .link:
                Button("Back") { step = .found }
                Button(linkTitle) {
                    saveLinks()
                    step = .review
                }
                .buttonStyle(.brand)
                .keyboardShortcut(.defaultAction)
            case .review:
                let count = desktop.review(on: week, tracker: tracker).count
                Button("Done") { desktop.onboarding = false }
                    .keyboardShortcut(.cancelAction)
                if count > 0 {
                    Button("Review Last Week (\(count))") {
                        desktop.onboarding = false
                        desktop.openReview(.lastSevenDays(to: today, time: time))
                    }
                    .buttonStyle(.brand)
                    .keyboardShortcut(.defaultAction)
                }
            }
        }
        .controlSize(.large)
    }

    private var stepLabel: String {
        switch step {
        case .found: "Step 1 of 3"
        case .link: "Step 2 of 3"
        case .review: "Step 3 of 3"
        }
    }

    private var linkTitle: String {
        let count = changedLinks.count
        return count == 0 ? "Next" : count == 1 ? "Link 1 Repository" : "Link \(count) Repositories"
    }

    // MARK: - Working it out

    /// The repositories whose project was chosen here, and differs from the person's own link.
    private var changedLinks: [(ActivityRepository, String)] {
        guard let findings else { return [] }
        return findings.repositories.compactMap { found in
            let repository = found.repository
            guard let identity = repository.identity, let projectId = choices[identity] ?? nil,
                  desktop.splits.chosen[identity] != projectId else { return nil }
            return (repository, projectId)
        }
    }

    private func saveLinks() {
        for (repository, projectId) in changedLinks {
            desktop.splits.link(repository, to: projectId)
        }
    }

    /// Reads what the logs say about the days loaded, by the same rules as the suggestions.
    private func work() {
        let splits = desktop.splits
        let days = splits.days
        let matcher = splits.links.matcher(among: tracker.catalog.projects)
        let entries = tracker.entries(overlapping: week).filter { $0.start >= time.start(of: week[0]) }
        let elsewhere = ActivityFindings.elsewhere(entries: entries, activity: week.compactMap { days[$0] }, placing: matcher.place)
        let untracked = splits.untracked(on: week, tracker: tracker).reduce(0) { $0 + $1.range.duration }
        let repositories = ActivityFindings.repositories(in: fortnight.compactMap { days[$0] })
        for found in repositories {
            let key = found.repository.identity ?? found.repository.key
            if choices[key] == nil { choices[key] = .some(matcher.project(for: found.repository)) }
        }
        let tracked = entries.reduce(0) { total, entry in total + (entry.stop.map { $0.timeIntervalSince(entry.start) } ?? 0) }
        findings = Findings(elsewhere: elsewhere, tracked: tracked, untracked: untracked, repositories: repositories)
    }

    private static var firstStep: Step {
        #if DEBUG
        if let step = ProcessInfo.processInfo.environment["SPINDLE_SNAPSHOT_ONBOARDING"].flatMap(Step.init(rawValue:)) { return step }
        #endif
        return .found
    }
}

/// "LAST 7 DAYS": the period a number is about, above it.
private struct Eyebrow: View {
    let text: String

    var body: some View {
        Text(text.uppercased())
            .font(.caption.weight(.semibold))
            .tracking(0.6)
            .foregroundStyle(Color.brandText)
    }
}

/// Tracked time as one bar: the part that went to other projects in marigold, the part in repositories without a
/// project hatched, the rest quiet.
private struct ShareBar: View {
    let tracked: TimeInterval
    let elsewhere: TimeInterval
    let unlinked: TimeInterval

    var body: some View {
        GeometryReader { geometry in
            let width = geometry.size.width
            let other = width * min(1, elsewhere / tracked)
            let open = min(width - other, width * min(1, unlinked / tracked))
            ZStack(alignment: .leading) {
                Capsule().fill(Color.primary.opacity(0.1))
                HStack(spacing: 2) {
                    Rectangle().fill(Color.brand).frame(width: other)
                    if open > 0 {
                        Rectangle()
                            .fill(Color.brand.opacity(0.18))
                            .overlay(Rectangle().strokeBorder(Color.brand.opacity(0.6), style: StrokeStyle(lineWidth: 1, dash: [2, 2])))
                            .frame(width: open)
                    }
                }
                .clipShape(Capsule())
            }
        }
        .frame(height: 10)
        .accessibilityHidden(true)
    }
}

/// One number and what it counts.
private struct Tile: View {
    let symbol: String
    let value: String
    let caption: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Image(systemName: symbol)
                .font(.callout)
                .foregroundStyle(.secondary)
            Text(value)
                .font(.title2.weight(.semibold))
                .monospacedDigit()
                .padding(.top, 2)
            Text(caption)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .padding(12)
        .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 10))
        .accessibilityElement(children: .combine)
    }
}

/// How the app behaves from now on, one way at a time.
private struct Way: View {
    let symbol: String
    let title: String
    let caption: String

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: symbol)
                .font(.callout.weight(.medium))
                .foregroundStyle(Color.brandText)
                .frame(width: 30, height: 30)
                .background(Color.brand.opacity(0.16), in: RoundedRectangle(cornerRadius: 8))
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .fontWeight(.semibold)
                Text(caption)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .accessibilityElement(children: .combine)
    }
}
