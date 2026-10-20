import Foundation

/// A repository whose branches belong to different projects, e.g. a monorepo, or maintenance and a feature billed
/// separately: work on branches matching `pattern` goes to `projectId`, and to `taskId` when there is one. Rules
/// belong to a repository's link, in the order the person put them, and sync with it. The server and Windows have
/// the same rules (tests/vectors/split-suggestions.json).
public struct BranchRule: Sendable, Equatable, Hashable, Codable {
    /// A branch name where `*` stands for any characters, `/` included: `acme/*`, `ACME-*`. Case doesn't matter.
    public var pattern: String
    public var projectId: String
    public var taskId: String?

    public init(pattern: String, projectId: String, taskId: String? = nil) {
        self.pattern = pattern
        self.projectId = projectId
        self.taskId = taskId
    }

    /// The longest pattern a rule may have, as long as a branch name.
    public static let patternMaxLength = 255
    /// The most rules one repository may have.
    public static let maxRules = 20

    /// Whether the rule covers work on these branches: there is at least one, and every one matches. A session that
    /// also ran on a branch outside the rule isn't the rule's, as the logs can't tell which branch a prompt was on.
    public func covers(_ branches: Set<String>) -> Bool {
        !branches.isEmpty && branches.allSatisfy(matches)
    }

    public func matches(_ branch: String) -> Bool {
        Self.matches(pattern: pattern, branch: branch)
    }

    /// `branch` against `pattern`, ignoring case, where `*` matches any run of characters, none included.
    public static func matches(pattern: String, branch: String) -> Bool {
        let pattern = Array(pattern.trimmingCharacters(in: .whitespaces).lowercased().utf16)
        let text = Array(branch.lowercased().utf16)
        let star = UInt16(UInt8(ascii: "*"))
        guard !pattern.isEmpty else { return false }
        var p = 0, t = 0, lastStar = -1, resume = 0
        while t < text.count {
            if p < pattern.count, pattern[p] == star {
                lastStar = p
                resume = t
                p += 1
            } else if p < pattern.count, pattern[p] == text[t] {
                p += 1
                t += 1
            } else if lastStar >= 0 {
                // Let the last `*` take one more character, and try again from there.
                p = lastStar + 1
                resume += 1
                t = resume
            } else {
                return false
            }
        }
        while p < pattern.count, pattern[p] == star { p += 1 }
        return p == pattern.count
    }
}
