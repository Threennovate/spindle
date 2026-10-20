import Foundation

/// The details a finished entry can be required to have, keyed as the API names them in its errors.
public enum DetailField: String, CaseIterable, Sendable {
    case description, projectId, taskId, tagIds
}

/// The workspace's rules for entries, as the server applies them, so the apps only offer what will work. The server
/// checks everything again.
public struct EntryRules: Sendable {
    public let settings: WorkspaceSettings
    public let isAdmin: Bool

    /// The server's words for a missing description, of the entry or of a part described on its own.
    public static let descriptionRequired = "A description is required in this workspace."

    public init(settings: WorkspaceSettings, isAdmin: Bool) {
        self.settings = settings
        self.isAdmin = isAdmin
    }

    /// Whether a task must be chosen on this project: only when the workspace asks for one and the project has open
    /// tasks.
    public func requiresTask(on project: Project?) -> Bool {
        settings.requireTask && project?.tasks.contains { !$0.isDone } == true
    }

    /// The details the workspace requires that a finished entry is missing, with the server's messages, in the order
    /// the fields appear.
    public func missingDetails(_ details: EntryDetails, project: Project?) -> [(field: DetailField, message: String)] {
        var missing: [(DetailField, String)] = []
        // Parts described one by one are each checked for theirs; see missingInParts.
        if settings.requireDescription && !SplitPart.describesEach(details.shares)
            && details.description.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            missing.append((.description, Self.descriptionRequired))
        }
        // Each part of a split entry is checked on its own; see missingInParts.
        if !details.isSplit && settings.requireProject && details.projectId == nil {
            missing.append((.projectId, "A project is required in this workspace."))
        }
        if !details.isSplit, details.taskId == nil, details.projectId != nil, requiresTask(on: project) {
            missing.append((.taskId, "A task is required on this project."))
        }
        if settings.requireTag && details.tagIds.isEmpty {
            missing.append((.tagIds, "At least one tag is required in this workspace."))
        }
        return missing
    }

    /// What each part of a split entry is missing that the workspace requires, by the part's position. A part's own
    /// description is required when the parts are described one by one.
    public func missingInParts(_ parts: [SplitPart], project: (String?) -> Project?) -> [Int: String] {
        var missing: [Int: String] = [:]
        for (index, part) in parts.enumerated() {
            if settings.requireProject && part.projectId == nil {
                missing[index] = "A project is required in this workspace."
            } else if part.taskId == nil, part.projectId != nil, requiresTask(on: project(part.projectId)) {
                missing[index] = "A task is required on this project."
            } else if settings.requireDescription, let description = part.description,
                description.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                missing[index] = Self.descriptionRequired
            }
        }
        return missing
    }

    /// The first day this person can still change, when earlier days are locked for them.
    public var firstOpenDay: DayKey? {
        isAdmin ? nil : settings.lockedBefore.flatMap(DayKey.init)
    }

    /// Whether a day is one this person can no longer change. Admins can change any day.
    public func isLocked(_ day: DayKey) -> Bool {
        firstOpenDay.map { day < $0 } ?? false
    }

    /// The server's words for a locked day, e.g. "Time before 1 September 2026 is locked. Ask an admin to change it."
    public func lockMessage(_ time: UserTime) -> String {
        guard let first = settings.lockedBefore.flatMap(DayKey.init) else { return "" }
        return "Time before \(time.longDay(first)) is locked. Ask an admin to change it."
    }
}

extension APIError {
    /// Whether the server refused because of an entry's details, which the person can fix in the entry.
    public var isAboutDetails: Bool {
        status == 400 && DetailField.allCases.contains { fieldErrors[$0.rawValue] != nil }
    }
}
