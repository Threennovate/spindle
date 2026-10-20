import Foundation
import SpindleAPI

/// Where part of an entry's time went: a project (and task), whether it is billable, and how much of the time.
public struct SplitPart: Hashable, Sendable, Codable {
    public var projectId: String?
    public var taskId: String?
    public var isBillable: Bool
    public var percent: Int
    /// What was done on this part, when each part of a split is described on its own, so its project's reports and
    /// timesheet links show only that; nil when the parts share the entry's description.
    public var description: String?

    public init(projectId: String?, taskId: String? = nil, isBillable: Bool, percent: Int, description: String? = nil) {
        self.projectId = projectId
        self.taskId = taskId
        self.isBillable = isBillable
        self.percent = percent
        self.description = description
    }
}

/// The arithmetic of splitting an entry's time, as the server and the web app do it, so all of them agree.
public enum SplitMath {
    /// The most parts the server allows.
    public static let maxParts = 6

    /// The smallest part, in percent.
    public static let minPercent = 1

    /// Whole numbers in proportion to `weights` that add up to `total`, each at least `minimum`: what rounding leaves
    /// over goes to the largest remainders, the earlier one on a tie.
    public static func apportion(_ weights: [Int], total: Int, minimum: Int = 0) -> [Int] {
        guard !weights.isEmpty else { return [] }
        let free = total - minimum * weights.count
        let positive = weights.map { max(0, $0) }
        let sum = positive.reduce(0, +)
        let shares = sum > 0 ? positive : positive.map { _ in 1 }
        let divisor = sum > 0 ? sum : weights.count
        var result = shares.map { $0 * free / divisor }
        let rests = shares.map { $0 * free % divisor }
        var left = free - result.reduce(0, +)
        for index in rests.indices.sorted(by: { rests[$0] != rests[$1] ? rests[$0] > rests[$1] : $0 < $1 }) where left > 0 {
            result[index] += 1
            left -= 1
        }
        return result.map { $0 + minimum }
    }

    /// Divides seconds between parts, as reports do.
    public static func allocate(_ seconds: Int, _ percents: [Int]) -> [Int] {
        apportion(percents, total: max(0, seconds))
    }

    /// `count` equal parts; the first ones get what does not divide evenly.
    public static func equal(_ count: Int) -> [Int] {
        apportion(Array(repeating: 1, count: count), total: 100, minimum: minPercent)
    }

    /// Moves the boundary after part `index` to `position` percent along the bar. Only the two parts beside it
    /// change, neither below the minimum. With `step`, the boundary snaps to multiples of it.
    public static func moveBoundary(_ percents: [Int], after index: Int, to position: Double, step: Int = 1) -> [Int] {
        let before = percents[..<index].reduce(0, +)
        let pair = percents[index] + percents[index + 1]
        let snapped = Int((position / Double(step)).rounded()) * step
        let at = min(before + pair - minPercent, max(before + minPercent, snapped))
        var next = percents
        next[index] = at - before
        next[index + 1] = pair - next[index]
        return next
    }

    /// Gives part `index` the percentage typed for it; the others make room in proportion to their size.
    public static func setting(_ percents: [Int], at index: Int, to value: Int) -> [Int] {
        let clamped = min(100 - (percents.count - 1) * minPercent, max(minPercent, value))
        var others = percents
        others.remove(at: index)
        var result = apportion(others, total: 100 - clamped, minimum: minPercent)
        result.insert(clamped, at: index)
        return result
    }

    /// Adds a part with an equal share; the others shrink in proportion.
    public static func adding(to percents: [Int]) -> [Int] {
        let share = Int((100.0 / Double(percents.count + 1)).rounded())
        return apportion(percents, total: 100 - share, minimum: minPercent) + [share]
    }

    /// Removes part `index`; its time goes to the others in proportion to their size.
    public static func removing(_ percents: [Int], at index: Int) -> [Int] {
        var rest = percents
        rest.remove(at: index)
        return apportion(rest, total: 100, minimum: rest.count > 1 ? minPercent : 0)
    }

    /// The percentage closest to `seconds` of an entry `total` seconds long.
    public static func percent(of seconds: Int, in total: Int) -> Int {
        total > 0 ? Int((Double(seconds) / Double(total) * 100).rounded()) : 0
    }
}

extension TimeEntry {
    /// Where the entry's time went: its parts when split, otherwise all of it on its one project.
    public var parts: [SplitPart] {
        isSplit
            ? shares.map {
                SplitPart(projectId: $0.projectId, taskId: $0.taskId, isBillable: $0.isBillable, percent: Int($0.percent),
                          description: $0.description)
            }
            : [SplitPart(projectId: projectId, taskId: taskId, isBillable: isBillable, percent: 100)]
    }

    public var isSplit: Bool { shares.count > 1 }
}

extension EntryDetails {
    public var isSplit: Bool { shares.count > 1 }

    /// The details with the time split between `parts`; the largest is the entry's project, for apps that show one.
    /// Fewer than two parts put the time on the one project left.
    public func splitting(_ parts: [SplitPart]) -> EntryDetails {
        var details = self
        if parts.count > 1, let largest = parts.max(by: { $0.percent < $1.percent }) {
            // max(by:) returns the last of equal maxima; the server keeps the first.
            let first = parts.first { $0.percent == largest.percent } ?? largest
            details.projectId = first.projectId
            details.taskId = first.taskId
            details.isBillable = first.isBillable
            details.shares = parts
        } else {
            if let only = parts.first {
                details.projectId = only.projectId
                details.taskId = only.taskId
                details.isBillable = only.isBillable
            }
            details.shares = []
        }
        return details
    }
}

extension SplitPart {
    var response: Components.Schemas.TimeShareResponse {
        .init(projectId: projectId, taskId: taskId, isBillable: isBillable, percent: Int32(percent), description: description)
    }

    var request: Components.Schemas.TimeShareRequest {
        .init(projectId: projectId, taskId: taskId, isBillable: isBillable, percent: Int32(percent), description: description)
    }

    /// The parts' own descriptions as one, as the server makes an entry's description of them.
    public static func joinedDescriptions(_ parts: [SplitPart]) -> String {
        parts.compactMap { $0.description?.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
            .joined(separator: "; ")
    }

    /// Whether each part has a description of its own, which its project's reports and timesheet links show.
    public static func describesEach(_ parts: [SplitPart]) -> Bool {
        parts.count > 1 && parts.contains { $0.description != nil }
    }

    /// Gives each part a description of its own. The one description so far goes to the largest part, as it most
    /// likely describes the project the timer was on; the others start empty rather than repeat it on other projects.
    public static func describingEach(_ parts: [SplitPart], from description: String) -> [SplitPart] {
        // The first of equal largest, as the server and the web choose it.
        let largest = parts.indices.reduce(0) { parts[$1].percent > parts[$0].percent ? $1 : $0 }
        return parts.enumerated().map { index, part in
            var part = part
            part.description = index == largest ? description.trimmingCharacters(in: .whitespacesAndNewlines) : ""
            return part
        }
    }

    /// One description for all parts again: theirs joined, and none of their own.
    public static func sharingOneDescription(_ parts: [SplitPart]) -> (parts: [SplitPart], description: String) {
        (parts.map { var part = $0; part.description = nil; return part }, joinedDescriptions(parts))
    }
}
