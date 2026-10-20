#if DEBUG
import AppKit
import SwiftUI
import SpindleKit

/// A launch that draws the window into a PNG and quits, to check the interface without a person at the screen.
///
/// Set by environment variables:
/// - `SPINDLE_SNAPSHOT_DIR`: where to write the picture; a snapshot run only when set.
/// - `SPINDLE_SNAPSHOT_NAME`: the file name, without `.png` (default `snapshot`).
/// - `SPINDLE_SNAPSHOT_EMAIL`, `SPINDLE_SNAPSHOT_PASSWORD`: an account to sign in to first.
/// - `SPINDLE_SNAPSHOT_AUTH`: `signIn`, `register`, `forgotPassword` or `server`, the form to show when signed out.
/// - `SPINDLE_SNAPSHOT_SUBMIT`: `1` to submit that form as it opens, to see its errors.
/// - `SPINDLE_SNAPSHOT_SECTION`: the sidebar section to show, e.g. `timer`.
/// - `SPINDLE_SNAPSHOT_APPEARANCE`: `light` or `dark`.
/// - `SPINDLE_SNAPSHOT_SIZE`: the window's content size, e.g. `1100x720`.
/// - `SPINDLE_SNAPSHOT_AGENT_LOGS`: synthetic log root (`claude/projects` and `codex/sessions`); forces activity on.
/// - `SPINDLE_SNAPSHOT_DAY`: calendar date, `YYYY-MM-DD`.
/// - `SPINDLE_SNAPSHOT_CALENDAR_MODE`: `day` or `week`.
/// - `SPINDLE_SNAPSHOT_DELAY`: seconds to wait before drawing, for data to load (default 1.5).
/// - `SPINDLE_SNAPSHOT_EDIT`: `running`, `stopping` or `new`, to open the entry editor on the running entry or a
///   new one; or `entry:` and a description, to open this week's entry with that description; or `suggested:` and a
///   description, to open it with the split agent activity suggests already taken.
/// - `SPINDLE_SNAPSHOT_OFFER`: a description, to offer a split for this week's entry with it, as after a stop.
/// - `SPINDLE_SNAPSHOT_QUICK_ENTRY`: `open` to draw the quick entry panel as ⌃⌥⌘K opens it, `split` to open its split
///   page on the running entry, or a line to type into it, e.g. `Review PR @boo` or `Standup yesterday 9-9:15`.
/// - `SPINDLE_SNAPSHOT_PICKER`: `project` or `tags`, to open that picker in the timer bar.
/// - `SPINDLE_SNAPSHOT_DESCRIPTION`: text to type in the timer's description field, to see its suggestions.
/// - `SPINDLE_SNAPSHOT_WINDOW`: `menubar`, `projectPicker`, `tagPicker`, `runningStart`, `settingsGeneral`,
///   `settingsPomodoro`, `settingsAgents`, `settingsAccount` or `branchRules`, to draw that panel on its own instead of the window. (Popovers are glass, which snapshots
///   can't draw, and the settings are a window of their own.)
/// - `SPINDLE_SNAPSHOT_PROPOSALS`: `review` to open the review of what agents proposed on the server, or
///   `addAll` to add all that can be added first, as its Add All button does.
/// - `SPINDLE_SNAPSHOT_REVIEW`: `today`, `yesterday`, `week`, `lastWeek` or a date (`YYYY-MM-DD`), to open the review of
///   that day or those days.
/// - `SPINDLE_SNAPSHOT_REVIEW_TAKE`: `all` to take everything in that review as its Take All button does, or
///   `allThenUndo` to undo that again, as the notice's Undo does.
/// - `SPINDLE_SNAPSHOT_ONBOARDING`: `found`, `link` or `review`, to open what turning agent logs on shows, at that step.
/// - `SPINDLE_SNAPSHOT_SPLIT_PREVIEW`: an entry's description, to draw its suggestion on its calendar block, as when the
///   pointer is on its ✦.
/// - `SPINDLE_SNAPSHOT_REPOSITORY`, `SPINDLE_SNAPSHOT_BRANCH_RULES`: for the `branchRules` window, the repository and its
///   rules, as `feature/*=Copper/Support;fix-*=Beacon` (project, then task), shown without being saved.
/// - `SPINDLE_SNAPSHOT_SCROLL`: how far down to scroll that panel, from `0` to `1`, or `end`; a window can't be taller
///   than the screen, so long settings are seen part by part.
///
/// Sheets and popovers are windows of their own; they are drawn over the window where they sit.
///
/// Its sign-in is kept in memory only, so it never touches the one in the Keychain.
struct SnapshotRun {
    let directory: URL
    let name: String
    let email: String?
    let password: String?
    let authMode: AuthView.Mode?
    let submitsForms: Bool
    let section: AppSection?
    let appearance: NSAppearance.Name?
    let size: CGSize?
    let delay: Duration
    let edit: String?
    let picker: String?
    let description: String?
    let panel: String?
    let quickEntry: String?

    static let current: SnapshotRun? = {
        let environment = ProcessInfo.processInfo.environment
        guard let directory = environment["SPINDLE_SNAPSHOT_DIR"] else { return nil }
        let size = environment["SPINDLE_SNAPSHOT_SIZE"]?.split(separator: "x").compactMap { Double($0) } ?? []
        let authMode: AuthView.Mode? = switch environment["SPINDLE_SNAPSHOT_AUTH"] {
            case "register": .register
            case "forgotPassword": .forgotPassword
            case "signIn": .signIn
            case "server": .server
            default: nil
        }
        let appearance: NSAppearance.Name? = switch environment["SPINDLE_SNAPSHOT_APPEARANCE"] {
            case "dark": .darkAqua
            case "light": .aqua
            default: nil
        }
        let delay = Double(environment["SPINDLE_SNAPSHOT_DELAY"] ?? "") ?? 1.5
        return SnapshotRun(
            directory: URL(filePath: directory, directoryHint: .isDirectory),
            name: environment["SPINDLE_SNAPSHOT_NAME"] ?? "snapshot",
            email: environment["SPINDLE_SNAPSHOT_EMAIL"],
            password: environment["SPINDLE_SNAPSHOT_PASSWORD"],
            authMode: authMode,
            submitsForms: environment["SPINDLE_SNAPSHOT_SUBMIT"] == "1",
            section: environment["SPINDLE_SNAPSHOT_SECTION"].flatMap(AppSection.init(rawValue:)),
            appearance: appearance,
            size: size.count == 2 ? CGSize(width: size[0], height: size[1]) : nil,
            delay: .milliseconds(Int(delay * 1000)),
            edit: environment["SPINDLE_SNAPSHOT_EDIT"],
            picker: environment["SPINDLE_SNAPSHOT_PICKER"],
            description: environment["SPINDLE_SNAPSHOT_DESCRIPTION"],
            panel: environment["SPINDLE_SNAPSHOT_WINDOW"],
            quickEntry: environment["SPINDLE_SNAPSHOT_QUICK_ENTRY"])
    }()

    func run(_ model: AppModel, desktop: Desktop) async {
        if let appearance {
            NSApp.appearance = NSAppearance(named: appearance)
        }
        if let email, let password, model.phase == .signedOut {
            do {
                try await model.signIn(email: email, password: password)
            } catch {
                print("Snapshot sign-in failed: \(error.localizedDescription)")
            }
        }
        // The account's links and branch rules, as `ActivitySync` reads them; it doesn't run with synthetic logs.
        if ActivitySnapshot.root != nil, model.phase == .signedIn, let links = try? await model.client.repositoryLinks() {
            desktop.splits.absorb(links)
        }
        if let size, let window = mainWindow {
            window.setContentSize(size)
            window.center()
        }
        // Drawn as the front window, as people see it while using it.
        NSApp.activate()
        mainWindow?.makeKeyAndOrderFront(nil)
        try? await Task.sleep(for: delay / 2)
        switch edit {
        case "running", "stopping":
            if let running = model.tracker?.running {
                model.editing = EntryEditing(entry: running, stopping: edit == "stopping")
            }
        case "new":
            let end = Date()
            model.editing = EntryEditing(newRange: DateInterval(start: end.addingTimeInterval(-5400), end: end))
        case let edit? where edit.hasPrefix("entry:"):
            if let entry = model.tracker?.entries.first(where: { $0.description == edit.dropFirst("entry:".count) }) {
                model.editing = EntryEditing(entry: entry)
            }
        case let edit? where edit.hasPrefix("suggested:"):
            // As the entry's split chip opens it, with repositories linked to projects by name.
            if let tracker = model.tracker, let root = ActivitySnapshot.root,
               let entry = tracker.entries.first(where: { $0.description == edit.dropFirst("suggested:".count) }) {
                let days = Array(Set([tracker.time.day(of: entry.start), tracker.time.day(of: entry.stop ?? Date())]))
                let activity = (try? await AgentActivityLoader.shared.load(
                    days: days, time: tracker.time, sources: Set(AgentSource.allCases), fixtureRoot: root)) ?? []
                let suggestion = SplitSuggestion.suggest(
                    from: entry.start, to: entry.stop ?? Date(), activity: activity, fallback: entry.projectId) {
                    RepositoryLinks().project(for: $0, among: tracker.catalog.projects)
                }
                model.editing = SplitAdvisor.editing(entry, as: suggestion, catalog: tracker.catalog)
            }
        default:
            break
        }
        // As if the timer had just stopped on this entry: the split offered for it.
        if let text = ProcessInfo.processInfo.environment["SPINDLE_SNAPSHOT_OFFER"], let tracker = model.tracker,
           let entry = tracker.entries.first(where: { $0.description == text }) {
            await desktop.splits.offerAfterStop(entry.id, tracker: tracker)
            try? await Task.sleep(for: .seconds(1))
        }
        if let review = ProcessInfo.processInfo.environment["SPINDLE_SNAPSHOT_REVIEW"], let tracker = model.tracker {
            let time = tracker.time, today = time.day(of: Date())
            let scope: ReviewScope = switch review {
            case "yesterday": .day(time.adding(-1, to: today))
            case "week": .week(of: today, time: time)
            case "lastWeek": .lastSevenDays(to: today, time: time)
            default: DayKey(review).map(ReviewScope.day) ?? .day(today)
            }
            await desktop.loadReview(scope.days, tracker: tracker)
            desktop.reviewing = scope
            try? await Task.sleep(for: .seconds(1))
            if let take = ProcessInfo.processInfo.environment["SPINDLE_SNAPSHOT_REVIEW_TAKE"], take == "some" {
                // Mid-review: the first item one click can take is taken, and the next put off.
                let items = desktop.review(on: scope.days, tracker: tracker)
                if let first = items.first(where: { desktop.splits.blocker($0, tracker: tracker) == nil }) {
                    await desktop.take(first)
                    if let other = items.first(where: { $0.id != first.id }) { desktop.notNow(other) }
                }
                try? await Task.sleep(for: .seconds(1))
            } else if let take = ProcessInfo.processInfo.environment["SPINDLE_SNAPSHOT_REVIEW_TAKE"] {
                let before = desktop.review(on: scope.days, tracker: tracker).count
                let taken = await desktop.takeAll(desktop.review(on: scope.days, tracker: tracker))
                try? await Task.sleep(for: .seconds(1))
                print("Snapshot review: \(before) items, took \(taken), \(desktop.review(on: scope.days, tracker: tracker).count) left")
                if take == "allDone" {
                    // What one click can't take is put off, so nothing waits.
                    for item in desktop.review(on: scope.days, tracker: tracker) where desktop.reviewed[item.id] == nil { desktop.notNow(item) }
                    try? await Task.sleep(for: .seconds(1))
                }
                if take == "allThenUndo" {
                    desktop.undoTake()
                    try? await Task.sleep(for: .seconds(2))
                    print("Snapshot review after Undo: \(desktop.review(on: scope.days, tracker: tracker).count) items")
                }
            }
        }
        if ProcessInfo.processInfo.environment["SPINDLE_SNAPSHOT_ONBOARDING"] != nil {
            desktop.onboarding = true
            try? await Task.sleep(for: .seconds(2))
        }
        if let proposals = ProcessInfo.processInfo.environment["SPINDLE_SNAPSHOT_PROPOSALS"] {
            if proposals == "addAll", let tracker = model.tracker {
                let result = await desktop.proposals.acceptAll(in: tracker)
                print("Snapshot added \(result.added) proposals, \(result.failed) failed")
            }
            desktop.proposals.reviewing = true
            try? await Task.sleep(for: .seconds(1))
        }
        var window: NSWindow?
        if let quickEntry, let tracker = model.tracker {
            window = await quickEntryWindow(quickEntry, desktop: desktop, tracker: tracker)
        } else if let panel, let root = await panelView(panel, model: model, desktop: desktop) {
            window = NSWindow(contentRect: NSRect(x: 200, y: 200, width: 320, height: 420), styleMask: [.borderless], backing: .buffered, defer: false)
            // Menus and popovers have a material behind them; a plain background stands in for it.
            window?.contentView = NSHostingView(rootView: root
                .environment(model).environment(model.notices)
                .background(Color(nsColor: .windowBackgroundColor)))
            window?.setContentSize(window?.contentView?.fittingSize ?? .zero)
            window?.orderFront(nil)
            if let scroll = ProcessInfo.processInfo.environment["SPINDLE_SNAPSHOT_SCROLL"], let content = window?.contentView {
                try? await Task.sleep(for: .milliseconds(300))
                Self.scroll(content, to: scroll == "end" ? 1 : Double(scroll) ?? 0)
            }
        }
        try? await Task.sleep(for: delay / 2)

        do {
            if let window {
                try write(Self.render(window.contentView!))
            } else {
                try capture()
            }
        } catch {
            print("Snapshot failed: \(error)")
        }
        // Each run signs in afresh, so end its session rather than leave one behind on the server.
        if email != nil, model.phase == .signedIn {
            await model.signOut()
        }
        NSApp.terminate(nil)
    }

    /// The quick entry panel's content in a window of its own, as it opens over another app. Its vibrancy is drawn by
    /// the window server, so a plain background stands in for it.
    private func quickEntryWindow(_ setup: String, desktop: Desktop, tracker: Tracker) async -> NSWindow {
        await desktop.splits.load([tracker.time.day(of: Date())], tracker: tracker)
        let quick = desktop.quickEntry.quick
        quick.reset()
        switch setup {
        case "open":
            break
        case "split":
            if let running = tracker.running {
                let percents = SplitMath.equal(2)
                var whole = running.parts[0]
                whole.percent = percents[0]
                let other = tracker.catalog.projects.first { $0.id != running.projectId && $0.archivedAt == nil }
                quick.push(.split(SplitDraft(entry: running, parts: [whole, SplitPart(projectId: other?.id, isBillable: other?.isBillable ?? false, percent: percents[1])])))
                if let draft = { if case let .split(draft) = quick.page { draft } else { nil } }() {
                    draft.grow(0, by: 10, typed: "")
                }
            }
        default:
            quick.text = setup
            quick.caretMoved(to: setup.utf16.count)
            quick.caretRequest = setup.utf16.count
        }
        let window = NSWindow(contentRect: NSRect(x: 200, y: 200, width: 640, height: 400), styleMask: [.borderless], backing: .buffered, defer: false)
        window.contentView = NSHostingView(rootView: QuickEntryView(quick: quick)
            .environment(quick.model).environment(desktop)
            .background(Color(nsColor: .windowBackgroundColor)))
        window.setContentSize(window.contentView?.fittingSize ?? .zero)
        window.orderFront(nil)
        return window
    }

    private func panelView(_ name: String, model: AppModel, desktop: Desktop) async -> AnyView? {
        switch name {
        case "activityDetails":
            guard let root = ActivitySnapshot.root, let date = ActivitySnapshot.day, let tracker = model.tracker,
                  let day = try? await AgentActivityLoader.shared.load(days: [date], time: tracker.time, sources: Set(AgentSource.allCases), fixtureRoot: root).first,
                  let block = day.focusBlocks.first(where: { CalendarLayout.minuteOfDay($0.range.start, in: tracker.time) > 420 }) else { return nil }
            return AnyView(ActivityDetails(heading: "Focus", range: block.range, shares: block.shares, switches: block.switches,
                                           day: day, time: tracker.time))
        case "menubar":
            // With the app's own Desktop, having read today's agent activity for the running entry.
            if let tracker = model.tracker { await desktop.splits.load([tracker.time.day(of: Date())], tracker: tracker) }
            return AnyView(MenuBarPanel(model: model).environment(desktop))
        case "projectPicker":
            // With the app's own Desktop, having read today's agent activity for "Agents now".
            guard let tracker = model.tracker else { return nil }
            await desktop.splits.load([tracker.time.day(of: Date())], tracker: tracker)
            return AnyView(ProjectPicker(tracker: tracker, projectId: tracker.running?.projectId, taskId: nil) { _, _ in }.environment(desktop))
        case "branchRules":
            // SPINDLE_SNAPSHOT_REPOSITORY names the repository; SPINDLE_SNAPSHOT_BRANCH_RULES its rules, as
            // "feature/*=Copper/Support;fix-*=Beacon", without saving them.
            guard let tracker = model.tracker else { return nil }
            let days = [ActivitySnapshot.day ?? tracker.time.day(of: Date())]
            await desktop.splits.load(days, tracker: tracker)
            let wanted = ProcessInfo.processInfo.environment["SPINDLE_SNAPSHOT_REPOSITORY"]
            guard let repository = ActivityFindings.repositories(in: Array(desktop.splits.days.values))
                .map(\.repository).first(where: { wanted == nil || $0.name == wanted }) else { return nil }
            let rules = ProcessInfo.processInfo.environment["SPINDLE_SNAPSHOT_BRANCH_RULES"].map { text in
                text.split(separator: ";").compactMap { rule -> BranchRule? in
                    let sides = rule.split(separator: "=", maxSplits: 1).map(String.init)
                    guard sides.count == 2 else { return nil }
                    let names = sides[1].split(separator: "/", maxSplits: 1).map(String.init)
                    guard let project = tracker.catalog.projects.first(where: { $0.name == names[0] }) else { return nil }
                    let task = names.count > 1 ? project.tasks.first { $0.name == names[1] } : nil
                    return BranchRule(pattern: sides[0], projectId: project.id, taskId: task?.id)
                }
            }
            return AnyView(BranchRulesEditor(repository: repository, tracker: tracker, splits: desktop.splits, rules: rules).environment(desktop))
        case "tagPicker":
            guard let tracker = model.tracker else { return nil }
            return AnyView(TagPicker(tracker: tracker, tagIds: tracker.catalog.tags.prefix(1).map(\.id)) { _ in })
        case "runningStart":
            guard let tracker = model.tracker, let running = tracker.running else { return nil }
            return AnyView(RunningStart(entry: running, tracker: tracker, onSave: { _ in }, onDone: {}))
        case "settingsGeneral":
            return AnyView(GeneralSettings().frame(width: 560).environment(Desktop(model: model, active: false)).environment(Updates(active: false)))
        case "settingsPomodoro":
            return AnyView(PomodoroSettings().frame(width: 560).environment(Desktop(model: model, active: false)))
        case "settingsAgents":
            return AnyView(AgentSettings().frame(width: 560).environment(desktop))
        case "settingsAccount":
            return AnyView(AccountSettings().frame(width: 560))
        default:
            return nil
        }
    }

    /// Scrolls the first scroll view inside `view` that far down, from 0 to 1.
    private static func scroll(_ view: NSView, to fraction: Double) {
        func first(_ view: NSView) -> NSScrollView? {
            if let scrollView = view as? NSScrollView { return scrollView }
            return view.subviews.lazy.compactMap(first).first
        }
        guard let scrollView = first(view), let document = scrollView.documentView else { return }
        let travel = max(0, document.bounds.height - scrollView.contentView.bounds.height)
        let y = document.isFlipped ? travel * fraction : travel * (1 - fraction)
        scrollView.contentView.scroll(to: NSPoint(x: 0, y: y))
        scrollView.reflectScrolledClipView(scrollView.contentView)
    }

    private var mainWindow: NSWindow? {
        NSApp.windows.first { $0.isVisible && $0.level == .normal && $0.contentView != nil }
    }

    /// Draws the whole window, title bar and toolbar included, and the sheets and popovers over it, into
    /// `<directory>/<name>.png`.
    private func capture() throws {
        guard let window = mainWindow, let content = window.contentView else {
            throw SnapshotError("No window to draw.")
        }
        let base = try Self.render(content.superview ?? content)
        // Popovers aren't among the app's ordered windows, so all visible ones are drawn, lowest level first.
        let overlays = NSApp.windows.filter { other in
            other !== window && other.isVisible && other.level != .statusBar && other.frame.intersects(window.frame)
        }
        .sorted { $0.level.rawValue < $1.level.rawValue }
        guard !overlays.isEmpty, let baseImage = base.cgImage else {
            try write(base)
            return
        }
        let scale = CGFloat(baseImage.width) / window.frame.width
        guard let context = CGContext(
            data: nil, width: baseImage.width, height: baseImage.height, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { throw SnapshotError("Couldn't make a bitmap.") }
        context.draw(baseImage, in: CGRect(x: 0, y: 0, width: baseImage.width, height: baseImage.height))
        for other in overlays {
            guard let view = other.contentView?.superview ?? other.contentView, let image = try Self.render(view).cgImage else { continue }
            let frame = other.frame.offsetBy(dx: -window.frame.minX, dy: -window.frame.minY)
            context.draw(image, in: CGRect(x: frame.minX * scale, y: frame.minY * scale, width: frame.width * scale, height: frame.height * scale))
        }
        guard let image = context.makeImage() else { throw SnapshotError("Couldn't finish the picture.") }
        try write(NSBitmapImageRep(cgImage: image))
    }

    private func write(_ bitmap: NSBitmapImageRep) throws {
        let output: NSBitmapImageRep
        if let size, let image = bitmap.cgImage,
           let context = CGContext(data: nil, width: Int(size.width), height: Int(size.height), bitsPerComponent: 8,
                                   bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                   bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) {
            NSApp.effectiveAppearance.performAsCurrentDrawingAppearance { context.setFillColor(NSColor.windowBackgroundColor.cgColor) }
            context.fill(CGRect(origin: .zero, size: size))
            let scale = min(1, size.width / bitmap.size.width, size.height / bitmap.size.height)
            let target = CGSize(width: bitmap.size.width * scale, height: bitmap.size.height * scale)
            context.draw(image, in: CGRect(x: (size.width - target.width) / 2, y: (size.height - target.height) / 2, width: target.width, height: target.height))
            output = context.makeImage().map(NSBitmapImageRep.init(cgImage:)) ?? bitmap
        } else { output = bitmap }
        guard let png = output.representation(using: .png, properties: [:]) else {
            throw SnapshotError("Couldn't encode the PNG.")
        }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let file = directory.appending(path: "\(name).png")
        try png.write(to: file)
        print("Snapshot written to \(file.path())")
    }
}

extension SnapshotRun {
    /// Draws a window's frame view with `cacheDisplay`. Liquid Glass, such as the sidebar and toolbar buttons, is
    /// drawn by the window server, out of reach of `cacheDisplay` (and reading the window's pixels from the window
    /// server needs the screen recording permission, even for the app's own windows). So each glass surface gets a
    /// plain stand-in, with its content drawn on top.
    private static func render(_ frameView: NSView) throws -> NSBitmapImageRep {
        frameView.layoutSubtreeIfNeeded()
        guard let base = draw(frameView),
              let context = CGContext(
                data: nil, width: base.width, height: base.height, bitsPerComponent: 8, bytesPerRow: 0,
                space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else {
            throw SnapshotError("Couldn't draw the window.")
        }
        let scale = CGFloat(base.width) / frameView.bounds.width
        context.draw(base, in: CGRect(x: 0, y: 0, width: base.width, height: base.height))
        context.scaleBy(x: scale, y: scale)
        if #available(macOS 26, *) {
            drawGlass(in: frameView, into: context, scale: scale)
        }
        guard let image = context.makeImage() else {
            throw SnapshotError("Couldn't finish the picture.")
        }
        return NSBitmapImageRep(cgImage: image)
    }

    @available(macOS 26, *)
    private static func drawGlass(in frameView: NSView, into context: CGContext, scale: CGFloat) {
        for case let glass as NSGlassEffectView in descendants(of: frameView, where: { $0 is NSGlassEffectView })
        where !glass.isHiddenOrHasHiddenAncestor {
            let rect = frameRect(of: glass, in: frameView)
            let radius = min(glass.cornerRadius, rect.height / 2, rect.width / 2)
            glass.effectiveAppearance.performAsCurrentDrawingAppearance {
                context.setFillColor(NSColor.windowBackgroundColor.cgColor)
                context.setStrokeColor(NSColor.separatorColor.cgColor)
            }
            context.addPath(CGPath(roundedRect: rect.insetBy(dx: 0.5, dy: 0.5), cornerWidth: radius, cornerHeight: radius, transform: nil))
            context.drawPath(using: .fillStroke)
            if let content = glass.contentView, let image = drawLayers(content, scale: scale) {
                context.draw(image, in: frameRect(of: content, in: frameView))
            }
        }
        // The window's buttons sit on the sidebar's glass; draw them again over its stand-in.
        for button in descendants(of: frameView, where: { String(describing: type(of: $0)).hasPrefix("_NSTheme") }) {
            if let image = draw(button) {
                context.draw(image, in: frameRect(of: button, in: frameView))
            }
        }
    }

    private static func draw(_ view: NSView) -> CGImage? {
        guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return nil }
        view.cacheDisplay(in: view.bounds, to: bitmap)
        return bitmap.cgImage
    }

    /// Draws a view's layers as they are. Vibrant content, such as the sidebar's rows, is drawn dark and lightened by
    /// a blend that only the window server does, so it comes out faint or missing.
    private static func drawLayers(_ view: NSView, scale: CGFloat) -> CGImage? {
        guard let layer = view.layer,
              let context = CGContext(
                data: nil, width: Int(view.bounds.width * scale), height: Int(view.bounds.height * scale),
                bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { return nil }
        context.scaleBy(x: scale, y: scale)
        layer.render(in: context)
        return context.makeImage()
    }

    /// A view's rectangle in the frame view, which, like Core Graphics, counts up from the bottom left.
    private static func frameRect(of view: NSView, in frameView: NSView) -> CGRect {
        view.convert(view.bounds, to: frameView)
    }

    private static func descendants(of view: NSView, where matches: (NSView) -> Bool) -> [NSView] {
        view.subviews.flatMap { subview in
            (matches(subview) ? [subview] : []) + descendants(of: subview, where: matches)
        }
    }
}

private struct SnapshotError: Error, CustomStringConvertible {
    let description: String
    init(_ description: String) { self.description = description }
}
#endif
