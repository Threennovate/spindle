import Foundation
import SpindleAPI

public typealias TimeEntry = Components.Schemas.TimeEntryResponse
public typealias TimeEntrySuggestion = Components.Schemas.TimeEntrySuggestion
public typealias Project = Components.Schemas.ProjectResponse
public typealias ProjectTask = Components.Schemas.TaskResponse
/// A client of the workspace, whom projects are for. (Not `Client`, which is the generated API client.)
public typealias WorkspaceClient = Components.Schemas.ClientResponse
public typealias Tag = Components.Schemas.TagResponse
public typealias WorkspaceSettings = Components.Schemas.WorkspaceSettingsResponse

extension TimeEntry: Identifiable {
    public var isRunning: Bool { stop == nil }

    /// How long the entry lasted, or has been running until `now`.
    public func seconds(at now: Date) -> TimeInterval {
        max(0, (stop ?? now).timeIntervalSince(start)).rounded(.down)
    }

    /// What the entry was spent on, without when.
    public var details: EntryDetails {
        EntryDetails(
            description: description, projectId: projectId, taskId: taskId, tagIds: tagIds, isBillable: isBillable,
            shares: isSplit ? parts : [])
    }
}

extension Project: Identifiable {
    public var isArchived: Bool { archivedAt != nil }
}

extension ProjectTask: Identifiable {}
extension WorkspaceClient: Identifiable {}
extension Tag: Identifiable {}

extension TimeEntrySuggestion: Identifiable {
    /// Suggestions are told apart by what they would start.
    public var id: String {
        "\(description)|\(projectId ?? "")|\(taskId ?? "")|" + shares.map { "\($0.projectId ?? "")/\($0.taskId ?? "")/\($0.percent)" }.joined(separator: ",")
    }

    public var details: EntryDetails {
        EntryDetails(
            description: description, projectId: projectId, taskId: taskId, tagIds: tagIds, isBillable: isBillable,
            shares: shares.count > 1
                ? shares.map { SplitPart(projectId: $0.projectId, taskId: $0.taskId, isBillable: $0.isBillable, percent: Int($0.percent)) }
                : [])
    }
}

/// What time is spent on: the parts of an entry that a timer starts with and a person edits.
public struct EntryDetails: Hashable, Sendable, Codable {
    public var description: String
    public var projectId: String?
    public var taskId: String?
    public var tagIds: [String]
    /// Nil to follow the project's billable setting.
    public var isBillable: Bool?
    /// Two or more parts when the time is split between projects; then the project, task and billable flag above are
    /// those of the largest part. Empty when it all goes to one project.
    public var shares: [SplitPart]

    public init(
        description: String = "", projectId: String? = nil, taskId: String? = nil, tagIds: [String] = [], isBillable: Bool? = nil,
        shares: [SplitPart] = []
    ) {
        self.description = description
        self.projectId = projectId
        self.taskId = taskId
        self.tagIds = tagIds
        self.isBillable = isBillable
        self.shares = shares
    }

    public static let empty = EntryDetails()

    private enum CodingKeys: String, CodingKey {
        case description, projectId, taskId, tagIds, isBillable, shares
    }

    // Changes waiting to be sent were saved before entries could be split, without shares.
    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            description: try container.decode(String.self, forKey: .description),
            projectId: try container.decodeIfPresent(String.self, forKey: .projectId),
            taskId: try container.decodeIfPresent(String.self, forKey: .taskId),
            tagIds: try container.decode([String].self, forKey: .tagIds),
            isBillable: try container.decodeIfPresent(Bool.self, forKey: .isBillable),
            shares: try container.decodeIfPresent([SplitPart].self, forKey: .shares) ?? [])
    }
}

/// A new ID for something created on this device, in the lowercase form the API writes.
public func newID() -> String {
    UUID().uuidString.lowercased()
}
