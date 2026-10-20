import AppKit
import SpindleKit

/// The rows the quick entry panel offers on its current page, as the web palette does: the entry a typed line saves,
/// what to do with the running timer, projects and earlier entries to start again, and the pages' own rows.
struct QuickEntryRows {
    let quick: QuickEntry
    let tracker: Tracker
    let now: Date

    private var time: UserTime { tracker.time }
    private var catalog: Catalog { tracker.catalog }
    /// A timer running this long, or an entry this long, was most likely given the wrong time.
    private static let longSeconds: TimeInterval = 12 * 3600

    var sections: [QuickSection] {
        switch quick.page {
        case nil:
            return quick.mention != nil ? mentionSections : quick.query.trimmingCharacters(in: .whitespaces).isEmpty && quick.chips.isEmpty ? nowSections : typedSections
        case let .project(_, pick):
            return projectSections(pick)
        case .tags:
            return tagSections
        case .startTime:
            return startTimeSections
        case let .split(draft):
            return splitSections(draft)
        case let .actions(row):
            return [QuickSection(id: "actions", title: nil, rows: actionRows(row))]
        }
    }

    // MARK: - Nothing typed yet

    /// The split agent activity suggests for the running entry or the one that just stopped, the timer, and what to
    /// continue.
    private var nowSections: [QuickSection] {
        var sections: [QuickSection] = []
        if let done = quick.undoable {
            sections.append(QuickSection(id: "undo", title: nil, rows: [QuickRow(
                id: "undo", title: "Undo: \(done.summary)", detail: "⌘Z", symbol: "arrow.uturn.backward",
                primary: QuickAction(label: "Undo") { quick.undo() })]))
        }
        if let offer = splitOffer {
            sections.append(QuickSection(id: "suggestion", title: "From agent activity", rows: [suggestionRow(offer)]))
        }
        sections.append(QuickSection(id: "timer", title: tracker.running == nil ? "Timer" : nil, rows: timerRows))
        let recent = recentEntries.prefix(tracker.running == nil ? 4 : 3).map(continueRow)
        if !recent.isEmpty {
            sections.append(QuickSection(id: "continue", title: "Continue", rows: Array(recent)))
        }
        return sections
    }

    /// A split from agent activity for the running entry, or the one the timer just stopped; Enter takes it.
    private var splitOffer: SplitOffer? {
        if let running = tracker.running, let suggestion = quick.desktop.splits.suggestion(for: running, tracker: tracker),
           suggestion.splitParts(billable: { _ in false }) != nil || suggestion.projectStretches != nil {
            return SplitOffer(entry: running, suggestion: suggestion)
        }
        return quick.desktop.splits.offer
    }

    private func suggestionRow(_ offer: SplitOffer) -> QuickRow {
        let parts = offer.suggestion.splitParts { catalog.project($0)?.isBillable ?? false }
        let shares = parts.map { $0.map { "\($0.percent)% \(catalog.name(of: $0))" }.joined(separator: " · ") }
        let summary = shares ?? SplitApplier.summary(offer.suggestion, entry: offer.entry, catalog: catalog, time: time)
        let name = offer.entry.description.isEmpty ? "the timer" : "“\(offer.entry.description)”"
        return QuickRow(
            id: "suggestion", title: "Suggested split from agent activity: \(summary)",
            detail: offer.entry.isRunning ? "For \(name), running" : "For \(name), ended \(time.clock(offer.entry.stop ?? now))",
            symbol: "sparkles", parts: parts, stacked: true,
            primary: QuickAction(label: offer.move.map { "Switch at \(time.clock($0.at))" }
                                 ?? SplitSuggestionChip.separateTitle(offer.suggestion, time: time)) { try await quick.apply(offer) },
            secondary: QuickAction(label: "Review in the window") { quick.desktop.review(offer) },
            more: [QuickAction(label: "Keep as is") { quick.desktop.keep(offer) }])
    }

    private var timerRows: [QuickRow] {
        guard let running = tracker.running else {
            var rows = [QuickRow(
                id: "timer.start", title: "Start timer", symbol: "play.fill", keywords: ["begin", "track"],
                primary: QuickAction(label: "Start") { try await quick.start(.empty, summary: "Timer started") })]
            if let last = lastEndedToday { rows.append(splitRow(last, id: "timer.split-last")) }
            return rows
        }
        var rows = [
            QuickRow(id: "timer.stop", title: "Stop timer", symbol: "stop.fill", keywords: ["end", "finish", "pause"],
                     primary: QuickAction(label: "Stop") { try await quick.stop(summary: "Stopped at \(Durations.clock(running.seconds(at: Date())))") }),
            splitRow(running, id: "timer.split"),
            QuickRow(id: "timer.start-time", title: "Change start time…", detail: "started \(time.clock(running.start))", symbol: "clock",
                     keywords: ["started", "earlier", "forgot"], primary: QuickAction(label: "Change", keepOpen: true) { quick.push(.startTime) }),
            QuickRow(id: "timer.tags", title: "Tags…", detail: running.tagIds.compactMap { catalog.tag($0)?.name }.joined(separator: ", ").nilIfEmpty,
                     symbol: "tag", keywords: ["label"], primary: QuickAction(label: "Choose", keepOpen: true) { quick.push(.tags) }),
        ]
        if let last = lastEndedToday { rows.append(splitRow(last, id: "timer.split-last")) }
        return rows
    }

    /// Splits an entry's time from the keyboard: a project to split with first, unless it is split already.
    private func splitRow(_ entry: TimeEntry, id: String) -> QuickRow {
        let title: String
        if entry.isRunning {
            title = entry.isSplit ? "Edit the split…" : "Split with…"
        } else {
            let name = entry.description.isEmpty ? "No description" : entry.description
            title = "\(entry.isSplit ? "Edit the split of" : "Split") “\(name)” (ended \(time.clock(entry.stop ?? now)))…"
        }
        return QuickRow(
            id: id, title: title, detail: entry.isSplit ? catalog.describe(entry.parts) : nil,
            symbol: "arrow.triangle.branch", keywords: ["parallel", "agents", "share", "percent", "split"],
            boost: entry.isRunning ? 0 : -4,
            primary: QuickAction(label: entry.isSplit ? "Edit the split" : "Choose a project", keepOpen: true) {
                if entry.isSplit {
                    quick.push(.split(SplitDraft(entry: entry, parts: entry.parts)))
                } else {
                    quick.push(.project(title: "Split with") { projectId, taskId in
                        let percents = SplitMath.equal(2)
                        var whole = entry.parts[0]
                        whole.percent = percents[0]
                        let other = SplitPart(projectId: projectId, taskId: taskId, isBillable: catalog.project(projectId)?.isBillable ?? false, percent: percents[1])
                        quick.back()
                        quick.push(.split(SplitDraft(entry: entry, parts: [whole, other])))
                    })
                }
            },
            secondary: QuickAction(label: "Open the full editor") { openEditor(entry, split: entry.isSplit ? nil : entry.parts, startSplit: !entry.isSplit) })
    }

    private func openEditor(_ entry: TimeEntry, split: [SplitPart]?, startSplit: Bool = false) {
        quick.desktop.showMainWindow()
        quick.model.editing = EntryEditing(entry: entry, split: split, startSplit: startSplit)
    }

    /// The entry that ended last today, often time that ran on two things at once.
    private var lastEndedToday: TimeEntry? {
        let today = time.day(of: now)
        return tracker.entries.filter { $0.stop.map { time.day(of: $0) == today } ?? false }.max { $0.stop! < $1.stop! }
    }

    /// The last different things worked on, newest first.
    private var recentEntries: [TimeEntry] {
        var seen = Set<EntryDetails>()
        return tracker.entries
            .filter { !$0.isRunning }
            .sorted { $0.start > $1.start }
            .filter { entry in
                var details = entry.details
                details.isBillable = nil
                return seen.insert(details).inserted
            }
    }

    private func continueRow(_ entry: TimeEntry) -> QuickRow {
        var details = entry.details
        details.isBillable = entry.isBillable
        let name = entry.description.isEmpty ? "No description" : entry.description
        return QuickRow(
            id: "entry:\(entry.id)", title: name,
            detail: entry.isSplit ? catalog.describe(entry.parts) : catalog.project(entry.projectId).map { project in
                catalog.task(entry.taskId).map { "\(project.name) · \($0.name)" } ?? project.name
            },
            color: entry.isSplit ? nil : catalog.project(entry.projectId)?.color, parts: entry.isSplit ? entry.parts : nil,
            keywords: [catalog.project(entry.projectId)?.name ?? ""],
            primary: QuickAction(label: tracker.running == nil ? "Continue" : "Switch to this") {
                try await quick.start(details, summary: "Continuing “\(name)”")
            })
    }

    // MARK: - A typed line

    private var typedSections: [QuickSection] {
        let typed = quick.query.trimmingCharacters(in: .whitespaces)
        let reading = EntrySentence.read(quick.query, chips: quick.chips, options: quick.options)
        let explicit = !quick.chips.isEmpty || reading.time != nil || reading.billable
        var sections: [QuickSection] = []

        let commands = commandRows(typed, reading: reading)
        if !commands.isEmpty {
            sections.append(QuickSection(id: "commands", title: nil, rows: commands))
        }

        let preview = commands.contains { $0.id == "sentence.split" || $0.id == "sentence.report" } ? nil : entryRow(reading, explicit: explicit)
        // With chips, the text describes the entry; searching it would only get in the way.
        var results: [(QuickSection, Double)] = []
        if quick.chips.isEmpty, !typed.isEmpty {
            let projects = PaletteMatch.rank(projectRows, query: typed, label: \.title, keywords: \.keywords, boost: \.boost, usage: usageScore)
            if !projects.isEmpty {
                results.append((QuickSection(id: "projects", title: "Projects", rows: projects.prefix(5).map(\.row)), projects[0].score))
            }
            let entries = PaletteMatch.rank(recentEntries.prefix(60).map(continueRow), query: typed, label: \.title, keywords: \.keywords, usage: usageScore)
            if !entries.isEmpty {
                results.append((QuickSection(id: "entries", title: "Earlier entries", rows: entries.prefix(4).map(\.row)), entries[0].score))
            }
            let timer = PaletteMatch.rank(timerRows, query: typed, label: \.title, keywords: \.keywords, boost: \.boost, usage: usageScore)
            if !timer.isEmpty {
                results.append((QuickSection(id: "timer", title: "Timer", rows: timer.map(\.row)), timer[0].score + 5))
            }
        }
        results.sort { $0.1 > $1.1 }
        // What was typed reads as a description when nothing fits it well, or when it names a project, a tag or a
        // time; then saving it comes first.
        let previewFirst = explicit || (results.first?.1 ?? 0) < 60
        if let preview, previewFirst { sections.append(QuickSection(id: "preview", title: commands.isEmpty ? nil : "Start", rows: [preview])) }
        sections += results.map(\.0)
        if let preview, !previewFirst { sections.append(QuickSection(id: "preview", title: "Start", rows: [preview])) }
        if preview != nil, reading.time != nil {
            // The time read from the line may have been meant as text, as in "Sprint 9:00-10:30"; offered below.
            var written = reading
            written.description = typed.split(whereSeparator: \.isWhitespace).joined(separator: " ")
            written.time = nil
            written.timeText = ""
            var row = entryRow(written, explicit: false, id: "sentence.as-written")
            row.detail = "as written, without reading a time"
            row.secondary = nil
            sections.append(QuickSection(id: "as-written", title: nil, rows: [row]))
        }
        return sections
    }

    private func usageScore(_ row: QuickRow) -> Double {
        quick.usage.score(row.id, now: now)
    }

    private var projectRows: [QuickRow] {
        catalog.projects.filter { $0.archivedAt == nil }.flatMap { project in
            let client = catalog.client(project.clientId)
            let projectRow = QuickRow(
                id: "project:\(project.id)", title: project.name, detail: client?.name, color: project.color,
                keywords: [client?.name ?? ""],
                primary: QuickAction(label: tracker.running == nil ? "Start timer" : "Start timer on it") {
                    try await quick.start(EntryDetails(projectId: project.id), summary: "Timer started on \(project.name)")
                })
            let tasks = project.tasks.filter { $0.isDone != true }.map { task in
                QuickRow(
                    id: "task:\(task.id)", title: task.name, detail: project.name, color: project.color,
                    keywords: [project.name, client?.name ?? ""], boost: -3,
                    primary: QuickAction(label: "Start timer") {
                        try await quick.start(EntryDetails(projectId: project.id, taskId: task.id), summary: "Timer started on \(task.name)")
                    })
            }
            return [projectRow] + tasks
        }
    }

    /// Commands the whole line stands for: `+15`, `-10` and `started 9:40` on a running timer, `split @Beacon 40`, and
    /// a report on a project or client.
    private func commandRows(_ typed: String, reading: EntrySentence.Reading) -> [QuickRow] {
        var rows: [QuickRow] = []
        if let running = tracker.running, quick.chips.isEmpty {
            if let shift = typed.wholeMatch(of: /([+\-−])\s*(\S+)/), let seconds = Durations.parse(String(shift.2)), seconds > 0 {
                let earlier = shift.1 == "+"
                let amount = seconds >= 3600 ? Durations.format(Double(seconds), as: .hm) : "\(seconds / 60) min"
                rows.append(startChange(running, to: running.start.addingTimeInterval(Double(earlier ? -seconds : seconds)),
                                        id: "timer.shift", title: earlier ? "Started \(amount) earlier" : "Started \(amount) later"))
            }
            if let started = typed.wholeMatch(of: /(?i)started\s+(?:at\s+)?(.+)/), let at = time.readStartField(String(started.1), now: now) {
                rows.append(startChange(running, to: at, id: "timer.started", title: "Started at \(time.clock(at))\(dayNote(at))"))
            }
        }

        // "split @Beacon 40": the running timer's time shared with another project.
        if let running = tracker.running, let projectId = reading.projectID, reading.time == nil,
           let split = reading.description.wholeMatch(of: /(?i)split(?:\s+(\d{1,2}))?/),
           running.parts.count < SplitDraft.maxParts, !running.parts.contains(where: { $0.projectId == projectId && $0.taskId == reading.taskID }) {
            let base = running.parts
            var percents = SplitMath.adding(to: base.map(\.percent))
            if let share = split.1.flatMap({ Int($0) }) { percents = SplitMath.setting(percents, at: base.count, to: share) }
            let parts = SplitDraft.with(base + [SplitPart(projectId: projectId, taskId: reading.taskID, isBillable: catalog.project(projectId)?.isBillable ?? false, percent: 0)], percents)
            let problem = tracker.isLocked(running.start) ? tracker.rules?.lockMessage(time) : tracker.rules?.missingInParts(parts, project: catalog.project).values.first
            let adjust = QuickAction(label: "Adjust the split…", keepOpen: true) { quick.push(.split(SplitDraft(entry: running, parts: parts))) }
            rows.append(QuickRow(
                id: "sentence.split", title: "Split with \(catalog.project(projectId)?.name ?? "the project")",
                detail: problem ?? parts.map { "\(catalog.name(of: $0)) \($0.percent)%" }.joined(separator: " · "),
                tone: problem == nil ? .normal : .blocked, parts: parts, stacked: true,
                primary: problem != nil ? adjust : QuickAction(label: "Save split") {
                    try await quick.change(running, to: EntryRequest(details: running.details.splitting(parts), start: running.start, stop: nil),
                                           summary: "Split: " + parts.map { "\(catalog.name(of: $0)) \($0.percent)%" }.joined(separator: " · "))
                },
                secondary: problem == nil ? adjust : nil))
        }

        // "@Acme last month": that report, in the window.
        let period = ReportPeriodWords.period(of: reading.description)
        if let clientId = reading.clientID ?? (period != nil ? reading.projectID : nil) {
            let isClient = reading.clientID != nil
            let name = isClient ? catalog.client(clientId)?.name : catalog.project(clientId)?.name
            rows.append(QuickRow(
                id: "sentence.report", title: "Report: \(name ?? "") · \(period?.title ?? "This month")", symbol: "chart.bar.xaxis",
                primary: QuickAction(label: "Open report") {
                    // The web app's reports read their filters from the address, as JSON lists.
                    var address = URLComponents(url: quick.model.webURL("/reports"), resolvingAgainstBaseURL: false)!
                    address.queryItems = [
                        URLQueryItem(name: "period", value: period?.key ?? "thisMonth"),
                        URLQueryItem(name: isClient ? "clients" : "projects", value: "[\"\(clientId)\"]"),
                    ]
                    if let url = address.url { NSWorkspace.shared.open(url) }
                }))
        }
        return rows
    }

    private func dayNote(_ instant: Date) -> String {
        let day = time.day(of: instant)
        return day == time.day(of: now) ? "" : " " + time.dayHeading(day, now: now).lowercased()
    }

    private func startChange(_ entry: TimeEntry, to start: Date, id: String, title: String) -> QuickRow {
        let problem = start > now ? "That is after now." : tracker.isLocked(start) ? tracker.rules?.lockMessage(time) : nil
        let detail = "\(Durations.clock(now.timeIntervalSince(entry.start))) → \(Durations.clock(now.timeIntervalSince(start)))"
        return QuickRow(
            id: id, title: title, detail: problem ?? detail, tone: problem == nil ? .normal : .blocked, symbol: "clock",
            primary: QuickAction(label: problem == nil ? "Change start" : "Can’t change", keepOpen: problem != nil) {
                guard problem == nil else { return }
                var request = EntryRequest(entry)
                request.start = start
                try await quick.change(entry, to: request, summary: "Started at \(time.clock(start))\(dayNote(start))")
            })
    }

    /// The entry a line saves, and what stands in its way, before anything is saved.
    private func entryRow(_ reading: EntrySentence.Reading, explicit: Bool, id: String = "sentence.entry") -> QuickRow {
        let span = EntrySentence.resolve(reading.time, now: now, in: time) { day in
            tracker.entries(overlapping: [day]).compactMap(\.stop)
        }
        let adding = span.stop != nil
        let project = catalog.project(reading.projectID)
        let details = EntryDetails(
            description: reading.description, projectId: reading.projectID, taskId: reading.taskID, tagIds: reading.tagIDs,
            isBillable: reading.billable ? true : nil)
        let seconds = (span.stop ?? now).timeIntervalSince(span.start)
        let when = adding ? spanText(span) : span.start < now.addingTimeInterval(-60) ? "since \(time.clock(span.start))\(dayNote(span.start))" : nil

        var problem: String?
        if !adding, span.start > now.addingTimeInterval(60) {
            problem = "A timer can’t start after now."
        } else if adding, span.start > now {
            problem = "That starts after now."
        } else if tracker.isLocked(span.start) {
            problem = tracker.rules?.lockMessage(time)
        }
        let missing = adding ? tracker.rules?.missingDetails(details, project: project) ?? [] : []
        let needs = missing.map { need(for: $0.field) }

        let verb = adding ? "Add" : "Start"
        let name = reading.description.isEmpty ? nil : "“\(reading.description)”"
        let summary = adding ? "Added \(name ?? "time") · \(spanText(span))" : "Timer started \(name ?? "")" + (when.map { " \($0)" } ?? "")
        let primary: QuickAction
        if problem != nil {
            primary = QuickAction(label: "Can’t save", keepOpen: true) {}
        } else if missing.contains(where: { $0.field == .projectId || $0.field == .taskId }) {
            primary = QuickAction(label: missing.contains { $0.field == .taskId } ? "Choose a task" : "Choose a project", keepOpen: true) {
                quick.push(.project(title: "Project") { projectId, taskId in
                    quick.back()
                    if let projectId { quick.addChip(.project(projectID: projectId, taskID: taskId)) }
                })
            }
        } else if missing.contains(where: { $0.field == .tagIds }) {
            primary = QuickAction(label: "Add a tag", keepOpen: true) { quick.append("#") }
        } else if !missing.isEmpty {
            primary = QuickAction(label: "Describe it", keepOpen: true) {}
        } else if adding {
            primary = QuickAction(label: "Add entry") {
                try await quick.add(EntryRequest(details: details, start: span.start, stop: span.stop), summary: summary)
            }
        } else {
            primary = QuickAction(label: "Start timer") {
                try await quick.start(details, at: reading.time == nil ? Date() : span.start, summary: summary)
            }
        }

        var facts: [String] = []
        if let project { facts.append(catalog.task(reading.taskID).map { "\(project.name) · \($0.name)" } ?? project.name) }
        facts += reading.tagIDs.compactMap { catalog.tag($0).map { "#\($0.name)" } }
        if reading.billable || project?.isBillable == true { facts.append("Billable") }
        if let when { facts.append("\(when) (\(Durations.format(seconds, as: .hm)))") } else if problem == nil && needs.isEmpty { facts.append("now") }
        var tone = QuickRow.Tone.normal
        if let running = tracker.running, !adding {
            // The server stops the running timer where the new one starts, but not before its own start.
            let stops = max(running.start, min(span.start, now))
            let runningName = "“\(running.description.isEmpty ? "No description" : running.description)”"
            if span.start <= running.start {
                facts.append("stops \(runningName), which then has no time")
                tone = .warning
            } else {
                facts.append("stops \(runningName)" + (stops < now.addingTimeInterval(-60) ? " at \(time.clock(stops))" : ""))
            }
        }
        if let problem {
            facts.append(problem)
            tone = .blocked
        } else if !needs.isEmpty {
            facts.append(needs.joined(separator: " · "))
            tone = .blocked
        } else if seconds >= Self.longSeconds {
            facts.append("More than 12 hours. Check the times.")
            tone = .warning
        }

        return QuickRow(
            id: id,
            title: name.map { "\(verb) \($0)" } ?? (adding ? "Add time" : "Start timer"),
            detail: facts.joined(separator: " · "), tone: tone,
            symbol: tone != .normal ? "exclamationmark.triangle.fill" : adding ? "text.badge.plus" : "play.fill",
            color: tone == .normal ? project?.color : nil, stacked: explicit,
            primary: primary,
            secondary: problem == nil && reading.projectID == nil ? QuickAction(label: "\(verb) on a project…", keepOpen: true) {
                quick.push(.project(title: name.map { "\(verb) \($0)" } ?? verb) { projectId, taskId in
                    var chosen = details
                    chosen.projectId = projectId
                    chosen.taskId = taskId
                    if !reading.billable { chosen.isBillable = catalog.project(projectId)?.isBillable }
                    Task {
                        if adding {
                            try? await quick.add(EntryRequest(details: chosen, start: span.start, stop: span.stop), summary: summary)
                        } else {
                            try? await quick.start(chosen, at: reading.time == nil ? Date() : span.start, summary: summary)
                        }
                        quick.close()
                    }
                })
            } : nil)
    }

    private func need(for field: DetailField) -> String {
        switch field {
        case .description: "Needs a description"
        case .projectId: "Needs a project"
        case .taskId: "Needs a task"
        case .tagIds: "Needs a tag"
        }
    }

    private func spanText(_ span: EntrySentence.Span) -> String {
        let day = time.day(of: span.start)
        let heading = day == time.day(of: now) ? "" : "\(time.dayHeading(day, now: now)) "
        return "\(heading)\(time.clock(span.start))–\(time.clock(span.stop ?? now))"
    }

    // MARK: - Completing @project and #tag

    private var mentionSections: [QuickSection] {
        guard let mention = quick.mention else { return [] }
        let query = mention.query.trimmingCharacters(in: .whitespaces)
        func order(_ rows: [QuickRow]) -> [QuickRow] {
            query.isEmpty
                ? rows.sorted { usageScore($0) > usageScore($1) }.prefix(8).map { $0 }
                : PaletteMatch.rank(rows, query: query, label: \.title, keywords: \.keywords, boost: \.boost, usage: usageScore).prefix(8).map(\.row)
        }
        var matches: [QuickRow]
        var create: QuickRow?
        if mention.sign == "@" {
            var rows = catalog.projects.filter { $0.archivedAt == nil }.flatMap { project in
                let client = catalog.client(project.clientId)
                return [QuickRow(id: "mention:project:\(project.id)", title: project.name, detail: client?.name, color: project.color,
                                 keywords: [client?.name ?? ""],
                                 primary: QuickAction(label: "Pick project", keepOpen: true) { quick.addChip(.project(projectID: project.id, taskID: nil)) })]
                    + project.tasks.filter { $0.isDone != true }.map { task in
                        QuickRow(id: "mention:task:\(task.id)", title: task.name, detail: project.name, color: project.color,
                                 keywords: [project.name, client?.name ?? ""], boost: -3,
                                 primary: QuickAction(label: "Pick task", keepOpen: true) { quick.addChip(.project(projectID: project.id, taskID: task.id)) })
                    }
            }
            // Clients only for reports, so only when asked for by name.
            if !query.isEmpty {
                rows += catalog.clients.filter { $0.archivedAt == nil }.map { client in
                    QuickRow(id: "mention:client:\(client.id)", title: client.name, detail: "Client, for a report", symbol: "building.2", boost: -5,
                             primary: QuickAction(label: "Pick client", keepOpen: true) { quick.addChip(.client(client.id)) })
                }
            }
            matches = order(rows)
        } else {
            let tags = catalog.sortedTags.filter { tag in !quick.chips.contains(.tag(tag.id)) }.map { tag in
                QuickRow(id: "mention:tag:\(tag.id)", title: tag.name, symbol: "tag",
                         primary: QuickAction(label: "Add tag", keepOpen: true) { quick.addChip(.tag(tag.id)) })
            }
            matches = order(tags)
            if !query.isEmpty, tracker.canCreateTags, !catalog.tags.contains(where: { $0.name.caseInsensitiveCompare(query) == .orderedSame }) {
                create = QuickRow(id: "mention:create-tag", title: "Create tag “\(query)”", symbol: "plus",
                                  primary: QuickAction(label: "Create and add", keepOpen: true) {
                                      let tag = try await tracker.createTag(named: query)
                                      quick.addChip(.tag(tag.id))
                                  })
            }
        }
        // What fits comes first, and Tab or Enter makes it a chip. When nothing fits, the line stays as typed, so
        // Enter still saves it, e.g. "Fix #482".
        let reading = EntrySentence.read(quick.query, chips: quick.chips, options: quick.options)
        let preview = entryRow(reading, explicit: !quick.chips.isEmpty || reading.time != nil)
        var sections: [QuickSection] = []
        if !matches.isEmpty { sections.append(QuickSection(id: "mention", title: mention.sign == "@" ? "Project" : "Tag", rows: matches)) }
        sections.append(QuickSection(id: "preview", title: matches.isEmpty ? nil : "Start", rows: [preview]))
        if let create { sections.append(QuickSection(id: "create", title: nil, rows: [create])) }
        return sections
    }

    // MARK: - Pages

    private func projectSections(_ pick: @escaping (String?, String?) -> Void) -> [QuickSection] {
        let query = quick.query.trimmingCharacters(in: .whitespaces)
        let rows = catalog.projects.filter { $0.archivedAt == nil }.flatMap { project in
            let client = catalog.client(project.clientId)
            return [QuickRow(id: "pick:\(project.id)", title: project.name, detail: client?.name, color: project.color, keywords: [client?.name ?? ""],
                             primary: QuickAction(label: "Choose", keepOpen: true) { pick(project.id, nil) })]
                + project.tasks.filter { $0.isDone != true }.map { task in
                    QuickRow(id: "pick-task:\(task.id)", title: task.name, detail: project.name, color: project.color, keywords: [project.name], boost: -3,
                             primary: QuickAction(label: "Choose", keepOpen: true) { pick(project.id, task.id) })
                }
        }
        let shown = query.isEmpty ? rows : PaletteMatch.rank(rows, query: query, label: \.title, keywords: \.keywords, boost: \.boost).map(\.row)
        return [QuickSection(id: "projects", title: nil, rows: Array(shown.prefix(40)))]
    }

    private var tagSections: [QuickSection] {
        guard let running = tracker.running else { return [] }
        let query = quick.query.trimmingCharacters(in: .whitespaces)
        let rows = catalog.sortedTags.map { tag in
            let on = running.tagIds.contains(tag.id)
            return QuickRow(id: "tag:\(tag.id)", title: tag.name, symbol: "tag", checked: on,
                            primary: QuickAction(label: on ? "Remove" : "Add", keepOpen: true) {
                                var request = EntryRequest(running)
                                request.details.tagIds = on ? running.tagIds.filter { $0 != tag.id } : running.tagIds + [tag.id]
                                try await quick.change(running, to: request, summary: on ? "Removed “\(tag.name)”" : "Tagged “\(tag.name)”")
                            })
        }
        var shown = query.isEmpty ? rows.sorted { $0.checked && !$1.checked } : PaletteMatch.rank(rows, query: query, label: \.title).map(\.row)
        if !query.isEmpty, tracker.canCreateTags, !catalog.tags.contains(where: { $0.name.caseInsensitiveCompare(query) == .orderedSame }) {
            shown.append(QuickRow(id: "tag:create", title: "Create tag “\(query)”", symbol: "plus",
                                  primary: QuickAction(label: "Create and add", keepOpen: true) {
                                      let tag = try await tracker.createTag(named: query)
                                      var request = EntryRequest(running)
                                      request.details.tagIds = running.tagIds + [tag.id]
                                      try await quick.change(running, to: request, summary: "Tagged “\(tag.name)”")
                                      quick.query = ""
                                  }))
        }
        return [QuickSection(id: "tags", title: nil, rows: shown)]
    }

    private var startTimeSections: [QuickSection] {
        guard let running = tracker.running else { return [] }
        let query = quick.query.trimmingCharacters(in: .whitespaces)
        if !query.isEmpty {
            guard let at = time.readStartField(query, now: now) else { return [] }
            return [QuickSection(id: "start", title: nil, rows: [startChange(running, to: at, id: "start:typed", title: "Started at \(time.clock(at))\(dayNote(at))")])]
        }
        let minute = (now.timeIntervalSince1970 / 60).rounded(.down) * 60
        let rows = [5, 15, 30, 60].map { minutes in
            startChange(running, to: Date(timeIntervalSince1970: minute - Double(minutes * 60)), id: "start:\(minutes)",
                        title: minutes == 60 ? "An hour ago" : "\(minutes) minutes ago")
        }
        return [QuickSection(id: "start", title: "Started at \(time.clock(running.start))\(dayNote(running.start)). Type another time, or pick one.", rows: rows)]
    }

    private func splitSections(_ draft: SplitDraft) -> [QuickSection] {
        let index = quick.splitIndex
        let parts = draft.shown(typed: quick.query, at: index)
        let entry = tracker.running?.id == draft.entry.id ? tracker.running! : draft.entry
        let seconds = Int((entry.stop ?? now).timeIntervalSince(entry.start).rounded())
        let allocated = SplitMath.allocate(seconds, parts.map(\.percent))
        let missing = tracker.rules?.missingInParts(parts, project: catalog.project) ?? [:]
        let problem = tracker.isLocked(entry.start) ? tracker.rules?.lockMessage(time) : missing.values.first
        let summary = "Split: " + parts.map { "\(catalog.name(of: $0)) \($0.percent)%" }.joined(separator: " · ")
        let full = QuickAction(label: "Open the full editor") { openEditor(entry, split: parts) }

        let rows = parts.indices.map { i in
            QuickRow(
                id: "part:\(i)", title: catalog.name(of: parts[i]), detail: missing[i], tone: missing[i] == nil ? .normal : .blocked,
                color: catalog.project(parts[i].projectId)?.color ?? "#8E8E93",
                meta: "\(Durations.format(Double(allocated[i]), as: .hm))   \(parts[i].percent)%",
                primary: problem != nil ? QuickAction(label: "Can’t save", keepOpen: true) {} : QuickAction(label: "Save split") {
                    try await quick.change(entry, to: EntryRequest(details: entry.details.splitting(parts), start: entry.start, stop: entry.stop), summary: summary)
                },
                secondary: full,
                more: parts.count > 2 ? [QuickAction(label: "Remove this part", keepOpen: true) {
                    draft.parts = parts
                    draft.remove(i)
                    quick.back()
                }] : [])
        }
        let extra = parts.count < SplitDraft.maxParts
            ? QuickRow(id: "split-add", title: "Add another project…", symbol: "plus", meta: "+",
                       primary: QuickAction(label: "Choose", keepOpen: true) { draft.addPart(quick) })
            : QuickRow(id: "split-full", title: "More parts in the full editor…", symbol: "arrow.up.left.and.arrow.down.right", primary: full)
        return [QuickSection(id: "parts", title: problem, rows: rows), QuickSection(id: "split-more", title: nil, rows: [extra])]
    }

    private func actionRows(_ row: QuickRow) -> [QuickRow] {
        let query = quick.query.trimmingCharacters(in: .whitespaces)
        let rows = row.actions.enumerated().map { index, action in
            QuickRow(id: "action:\(index)", title: action.label, meta: index == 0 ? "↩" : index == 1 && row.secondary != nil ? "⌘↩" : nil, primary: action)
        }
        return query.isEmpty ? rows : PaletteMatch.rank(rows, query: query, label: \.title).map(\.row)
    }
}

/// Report periods as typed after a project or client, e.g. "@Acme last month".
enum ReportPeriodWords {
    static let periods: [(key: String, title: String)] = [
        ("today", "Today"), ("yesterday", "Yesterday"), ("thisWeek", "This week"), ("lastWeek", "Last week"),
        ("thisMonth", "This month"), ("lastMonth", "Last month"), ("thisYear", "This year"), ("lastYear", "Last year"),
    ]

    static func period(of text: String) -> (key: String, title: String)? {
        periods.first { $0.title.caseInsensitiveCompare(text.trimmingCharacters(in: .whitespaces)) == .orderedSame }
    }
}

extension String {
    var nilIfEmpty: String? { isEmpty ? nil : self }
}
