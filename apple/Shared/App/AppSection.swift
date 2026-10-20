import Foundation

/// The parts of the app, in the sidebar's order.
enum AppSection: String, CaseIterable, Identifiable {
    case timer, calendar, reports, projects, clients, tags

    var id: Self { self }

    var title: String {
        switch self {
        case .timer: "Timer"
        case .calendar: "Calendar"
        case .reports: "Reports"
        case .projects: "Projects"
        case .clients: "Clients"
        case .tags: "Tags"
        }
    }

    var systemImage: String {
        switch self {
        case .timer: "timer"
        case .calendar: "calendar"
        case .reports: "chart.bar.xaxis"
        case .projects: "folder"
        case .clients: "building.2"
        case .tags: "tag"
        }
    }

    static let groups: [(heading: String, sections: [AppSection])] = [
        ("Track", [.timer, .calendar]),
        ("Analyze", [.reports]),
        ("Manage", [.projects, .clients, .tags]),
    ]
}
