import SwiftUI
import SpindleKit

/// The tabs of the signed-in app.
enum MainTab: String, CaseIterable {
    case timer, calendar, reports, projects, settings
}

struct MainTabView: View {
    @Environment(AppModel.self) private var model
    @State private var tab = Self.initialTab

    var body: some View {
        @Bindable var model = model
        TabView(selection: $tab) {
            Group {
                if let tracker = model.tracker {
                    TimerTab(tracker: tracker)
                } else {
                    ProgressView()
                }
            }
            .tabItem { Label("Timer", systemImage: "timer") }
            .tag(MainTab.timer)
            Group {
                if let tracker = model.tracker {
                    CalendarTab(tracker: tracker)
                } else {
                    ProgressView()
                }
            }
            .tabItem { Label("Calendar", systemImage: "calendar") }
            .tag(MainTab.calendar)
            Group {
                if let tracker = model.tracker {
                    ReportsTab(tracker: tracker, client: model.client)
                } else {
                    ProgressView()
                }
            }
            .tabItem { Label("Reports", systemImage: "chart.bar.xaxis") }
            .tag(MainTab.reports)
            Group {
                if let tracker = model.tracker {
                    ManageTab(tracker: tracker, client: model.client)
                } else {
                    ProgressView()
                }
            }
            .tabItem { Label("Projects", systemImage: "folder") }
            .tag(MainTab.projects)
            SettingsView()
                .tabItem { Label("Settings", systemImage: "gearshape") }
                .tag(MainTab.settings)
        }
        // Keyed by workspace, so switching starts every tab fresh instead of mixing two workspaces' data.
        .id(model.workspace?.id)
        // Any entry the app opens, e.g. one that needs details before the timer can stop.
        .sheet(item: $model.editing) { editing in
            if let tracker = model.tracker {
                EntryEditorSheet(tracker: tracker, editing: editing)
                    .environment(model.notices)
            }
        }
    }

    private static var initialTab: MainTab {
        #if DEBUG
        if let tab = LaunchOptions.current.tab {
            return tab
        }
        #endif
        return .timer
    }
}
