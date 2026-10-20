#if DEBUG
import Foundation
import SpindleKit
import SwiftUI

/// How an automated run in the simulator starts, from its environment (`SIMCTL_CHILD_` plus the name, with simctl):
///
/// - `SPINDLE_AUTOMATION_EMAIL`, `SPINDLE_AUTOMATION_PASSWORD`: sign in with this account. The sign-in stays in
///   memory, away from the Keychain, as in any automated run.
/// - `SPINDLE_AUTOMATION_TAB`: `timer`, `calendar`, `reports`, `projects` or `settings`.
/// - `SPINDLE_AUTOMATION_AUTH`: `signIn`, `register`, `forgotPassword` or `server`, the form to show when signed out.
/// - `SPINDLE_AUTOMATION_SUBMIT`: `1` submits the sign-in form at once, to show what it says.
/// - `SPINDLE_AUTOMATION_DESCRIPTION`: text typed into the timer's description, which shows its suggestions.
/// - `SPINDLE_AUTOMATION_EDIT`: `first`, `running` or `new` opens that entry, or a new one, in the editor.
/// - `SPINDLE_AUTOMATION_PICKER`: `project` or `tags` opens that picker from the timer.
/// - `SPINDLE_AUTOMATION_REPORT`: `summary`, `detailed` or `weekly`, the report to show.
/// - `SPINDLE_AUTOMATION_SCROLL`: `breakdown` scrolls the summary report to its donut.
/// - `SPINDLE_AUTOMATION_FILTERS`: `1` opens the report's filters.
/// - `SPINDLE_AUTOMATION_EXPORT`: `csv`, `xlsx` or `pdf` exports the report and opens the share sheet.
/// - `SPINDLE_AUTOMATION_MANAGE`: `projects`, `clients` or `tags`, the list the Projects tab shows.
/// - `SPINDLE_AUTOMATION_NOW`: an ISO 8601 time the app takes as now (`AppClock`), for screenshots of a made-up moment.
/// - `SPINDLE_AUTOMATION_PROJECT`: a project's name, to open it; `new` opens the form for a new one.
struct LaunchOptions {
    static let current = LaunchOptions(environment: ProcessInfo.processInfo.environment)

    let email: String?
    let password: String?
    let tab: MainTab?
    let authMode: AuthView.Mode?
    let submitsForms: Bool
    let description: String?
    let edit: String?
    let picker: String?
    let reportTab: ReportTab?
    let scroll: String?
    let showsFilters: Bool
    let export: ExportFormat?
    let manage: String?
    let project: String?
    let now: Date?

    init(environment: [String: String]) {
        func value(_ name: String) -> String? {
            environment["SPINDLE_AUTOMATION_\(name)"].flatMap { $0.isEmpty ? nil : $0 }
        }
        email = value("EMAIL")
        password = value("PASSWORD")
        tab = value("TAB").flatMap(MainTab.init(rawValue:))
        authMode = switch value("AUTH") {
        case "signIn": .signIn
        case "register": .register
        case "forgotPassword": .forgotPassword
        case "server": .server
        default: nil
        }
        submitsForms = value("SUBMIT") == "1"
        description = value("DESCRIPTION")
        edit = value("EDIT")
        picker = value("PICKER")
        reportTab = value("REPORT").flatMap(ReportTab.init(rawValue:))
        scroll = value("SCROLL")
        showsFilters = value("FILTERS") == "1"
        export = value("EXPORT").flatMap(ExportFormat.init(rawValue:))
        manage = value("MANAGE")
        project = value("PROJECT")
        now = value("NOW").flatMap { try? Date($0, strategy: .iso8601) }
        if let now { AppClock.run(from: now) }
    }

    /// Whether any of these is set: then the run keeps its sign-in in memory.
    var isAutomated: Bool {
        email != nil || authMode != nil || tab != nil || submitsForms || description != nil || edit != nil || picker != nil || reportTab != nil || scroll != nil || showsFilters || export != nil || manage != nil || project != nil || now != nil
    }

    /// Signs in, if asked to.
    func run(_ model: AppModel) async {
        guard let email, let password, model.phase == .signedOut else { return }
        do {
            try await model.signIn(email: email, password: password)
        } catch {
            model.notices.show(error, fallback: "Couldn't sign in.")
        }
    }
}

/// With `SPINDLE_AUTOMATION_SCROLL=breakdown`, scrolls the report to its donut once the summary has loaded.
struct ScrollsToBreakdown: ViewModifier {
    let trigger: Int64?

    func body(content: Content) -> some View {
        ScrollViewReader { scroller in
            content.onChange(of: trigger) {
                guard LaunchOptions.current.scroll == "breakdown" else { return }
                Task {
                    try? await Task.sleep(for: .milliseconds(400))
                    withAnimation(nil) { scroller.scrollTo(SummaryContent.breakdownId, anchor: .top) }
                }
            }
        }
    }
}
#endif
