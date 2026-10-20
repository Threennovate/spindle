import SwiftUI
import SpindleKit

/// Something a report can be narrowed to, e.g. a project.
struct FilterOption: Identifiable, Hashable {
    let id: String
    let label: String
    var detail: String?
    var color: String?
    /// More words to find it by, e.g. a member's email.
    var keywords: [String] = []
}

struct FilterSection: Identifiable {
    var heading: String?
    let options: [FilterOption]
    var id: String { heading ?? options.first?.id ?? "" }
}

/// What each of a report's filters offers: members and groups, projects by client, clients and tags, each with
/// "No …" for time without one.
struct ReportFilterChoices {
    let catalog: Catalog
    let members: [Member]
    let groups: [WorkspaceGroup]

    var memberSections: [FilterSection] {
        [
            FilterSection(heading: "Groups", options: groups.map {
                FilterOption(id: $0.id, label: $0.name, detail: $0.memberIds.count == 1 ? "1 member" : "\($0.memberIds.count) members")
            }),
            FilterSection(heading: "Members", options: members.map { FilterOption(id: $0.userId, label: $0.fullName, keywords: [$0.email]) }),
        ].filter { !$0.options.isEmpty }
    }

    var projectSections: [FilterSection] {
        [FilterSection(options: [FilterOption(id: ReportSettings.noneId, label: "No project")])]
            + catalog.projectGroups(keeping: nil).map { group in
                FilterSection(heading: group.client?.name ?? "No client", options: group.projects.map { project in
                    FilterOption(id: project.id, label: project.name, detail: project.isArchived ? "archived" : nil, color: project.color)
                })
            }
            + [FilterSection(heading: "Archived", options: catalog.projects.filter(\.isArchived).map {
                FilterOption(id: $0.id, label: $0.name, color: $0.color)
            })].filter { !$0.options.isEmpty }
    }

    var clientSections: [FilterSection] {
        [
            FilterSection(options: [FilterOption(id: ReportSettings.noneId, label: "No client")]),
            FilterSection(heading: "Clients", options: catalog.clients.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }.map {
                FilterOption(id: $0.id, label: $0.name, detail: $0.archivedAt != nil ? "archived" : nil)
            }),
        ].filter { !$0.options.isEmpty }
    }

    var tagSections: [FilterSection] {
        [
            FilterSection(options: [FilterOption(id: ReportSettings.noneId, label: "No tag")]),
            FilterSection(heading: "Tags", options: catalog.sortedTags.map { FilterOption(id: $0.id, label: $0.name) }),
        ].filter { !$0.options.isEmpty }
    }
}
