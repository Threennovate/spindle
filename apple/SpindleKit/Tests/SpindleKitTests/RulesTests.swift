import Foundation
import Testing
@testable import SpindleKit

/// The same cases as the web app's rules tests.
struct RulesTests {
    func settings(all required: Bool = false, lockedBefore: String? = nil) -> WorkspaceSettings {
        WorkspaceSettings(
            name: "Studio", currency: "EUR", defaultHourlyRate: nil, onlyAdminsCreateProjects: false, onlyAdminsCreateTags: false,
            lockedBefore: lockedBefore, requireDescription: required, requireProject: required, requireTask: required, requireTag: required)
    }

    func project(tasksDone: [Bool]) -> Project {
        Project(
            id: "p1", name: "Website", clientId: nil, color: "#000000", isPrivate: false, isBillable: false, separateBilledTime: false, estimateSeconds: nil,
            archivedAt: nil, trackedSeconds: 0,
            tasks: tasksDone.enumerated().map { index, done in
                ProjectTask(id: "t\(index)", projectId: "p1", name: "Task \(index)", estimateSeconds: nil, isDone: done, trackedSeconds: 0)
            },
            hourlyRate: nil, canManage: false, billingMode: .asTracked, dailyHours: nil)
    }

    @Test func asksForNothingByDefault() {
        let rules = EntryRules(settings: settings(), isAdmin: false)
        #expect(rules.missingDetails(EntryDetails(), project: nil).isEmpty)
    }

    @Test func listsEveryRequiredDetailThatIsMissingInTheOrderOfTheFields() {
        let rules = EntryRules(settings: settings(all: true), isAdmin: false)
        let missing = rules.missingDetails(EntryDetails(description: "  "), project: nil)
        #expect(missing.map(\.field) == [.description, .projectId, .tagIds])
        #expect(missing.first?.message == "A description is required in this workspace.")
    }

    @Test func isSatisfiedWhenTheDetailsAreThere() {
        let rules = EntryRules(settings: settings(all: true), isAdmin: false)
        let details = EntryDetails(description: "Design review", projectId: "p1", taskId: "t0", tagIds: ["tag"])
        #expect(rules.missingDetails(details, project: project(tasksDone: [false])).isEmpty)
    }

    @Test func requiresATaskOnlyOnProjectsWithOpenTasks() {
        let rules = EntryRules(settings: settings(all: true), isAdmin: false)
        let details = EntryDetails(description: "Design review", projectId: "p1", tagIds: ["tag"])
        #expect(rules.missingDetails(details, project: project(tasksDone: [])).isEmpty)
        #expect(rules.missingDetails(details, project: project(tasksDone: [true])).isEmpty)
        #expect(rules.missingDetails(details, project: project(tasksDone: [true, false])).map(\.message) == ["A task is required on this project."])
        #expect(!EntryRules(settings: settings(), isAdmin: false).requiresTask(on: project(tasksDone: [false])))
    }

    @Test func aRequiredDescriptionIsRequiredOfEachPartDescribedOnItsOwn() {
        let rules = EntryRules(settings: settings(all: true), isAdmin: false)
        let parts = [
            SplitPart(projectId: "p1", taskId: "t0", isBillable: false, percent: 60, description: "Design review"),
            SplitPart(projectId: "p2", isBillable: false, percent: 40, description: " "),
        ]
        let details = EntryDetails(description: "", tagIds: ["tag"]).splitting(parts)

        #expect(rules.missingDetails(details, project: nil).isEmpty)
        #expect(rules.missingInParts(parts) { _ in nil } == [1: "A description is required in this workspace."])
        // Parts sharing one description are not asked for their own; the entry's is.
        let shared = SplitPart.sharingOneDescription(parts).parts
        #expect(rules.missingInParts(shared) { _ in nil }.isEmpty)
        #expect(rules.missingDetails(details.splitting(shared), project: nil).map(\.field) == [.description])
    }

    @Test func locksDaysBeforeTheLockDateForMembersOnly() {
        let member = EntryRules(settings: settings(lockedBefore: "2026-09-01"), isAdmin: false)
        let admin = EntryRules(settings: settings(lockedBefore: "2026-09-01"), isAdmin: true)

        #expect(member.isLocked(DayKey("2026-08-31")!))
        #expect(!member.isLocked(DayKey("2026-09-01")!))
        #expect(!admin.isLocked(DayKey("2026-08-31")!))
        #expect(member.firstOpenDay == DayKey("2026-09-01"))
        #expect(admin.firstOpenDay == nil)
        #expect(member.lockMessage(UserTime(timeZone: .gmt)) == "Time before 1 September 2026 is locked. Ask an admin to change it.")
    }

    @Test func recognizesRefusalsAboutAnEntrysDetails() {
        #expect(APIError(status: 400, fieldErrors: ["projectId": ["A project is required in this workspace."]]).isAboutDetails)
        #expect(!APIError(status: 400, fieldErrors: ["start": ["Time before … is locked."]]).isAboutDetails)
        #expect(!APIError(status: 500).isAboutDetails)
    }
}

struct DayGroupTests {
    @Test func groupsByTheDayEntriesStartOnNewestFirst() {
        let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
        func entry(_ id: String, _ start: String, _ stop: String?) -> TimeEntry {
            TimeEntry(
                id: id, workspaceId: "w1", description: id, projectId: nil, taskId: nil, tagIds: [], isBillable: false,
                start: try! Date(start, strategy: .iso8601), stop: stop.map { try! Date($0, strategy: .iso8601) }, shares: [], version: "1")
        }
        let entries = [
            entry("a", "2026-09-28T08:00:00Z", "2026-09-28T09:00:00Z"),
            // 00:30 on 29 September in Zagreb, though still the 28th in UTC.
            entry("b", "2026-09-28T22:30:00Z", "2026-09-28T23:00:00Z"),
            entry("c", "2026-09-28T10:00:00Z", "2026-09-28T10:30:00Z"),
            entry("d", "2026-09-29T07:00:00Z", nil),
        ]

        let days = DayGroup.grouping(entries, in: time)

        #expect(days.map(\.day.description) == ["2026-09-29", "2026-09-28"])
        #expect(days[0].entries.map(\.id) == ["d", "b"])
        #expect(days[1].entries.map(\.id) == ["c", "a"])
        #expect(days[1].totalSeconds(at: .now) == 5400)
        // A running entry counts until now.
        #expect(days[0].totalSeconds(at: try! Date("2026-09-29T08:00:00Z", strategy: .iso8601)) == 1800 + 3600)
    }
}
