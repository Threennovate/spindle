import Foundation
import Testing
@testable import SpindleKit

struct ActivityFindingsTests {
    let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
    let day = DayKey("2026-10-13")!
    let atlas = ActivityRepository(key: "/code/atlas", name: "atlas", identity: "atlas")
    let beacon = ActivityRepository(key: "/code/beacon", name: "beacon", identity: "beacon")
    let copper = ActivityRepository(key: "/code/copper", name: "copper", identity: "copper")
    let notes = ActivityRepository(key: "/notes", name: "notes", identity: nil)

    func at(_ minutes: Double) -> Date { time.start(of: day).addingTimeInterval(minutes * 60) }

    func activity(_ spans: [(ActivityRepository, Double, Double)]) -> [ActivityDay] {
        let attention = spans.map {
            AttentionSegment(repository: $0.0, range: DateInterval(start: at($0.1), end: at($0.2)), sessionIDs: ["s"],
                             prompts: [at($0.1), at($0.1 + 5)])
        }
        return [ActivityDay(day: day, attention: attention, blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)]
    }

    func entry(_ id: String, _ from: Double, _ to: Double?, project: String?, shares: [SplitPart] = []) -> TimeEntry {
        TimeEntry(id: id, workspaceId: "w", description: "", projectId: project, taskId: nil, tagIds: [], isBillable: false,
                  start: at(from), stop: to.map(at), shares: shares.map(\.response), version: "1")
    }

    func project(_ repository: ActivityRepository) -> String? {
        ["atlas": "p-atlas", "beacon": "p-beacon"][repository.name]
    }

    @Test func countsTheTimeSuggestionsMoveToOtherProjects() {
        // 9:00–11:00 on Atlas: Atlas 9:00–9:40, Beacon 9:40–10:30, Atlas again 10:30–11:00.
        let days = activity([(atlas, 540, 580), (beacon, 580, 630), (atlas, 630, 660)])
        let found = ActivityFindings.elsewhere(entries: [entry("e", 540, 660, project: "p-atlas")], activity: days, project: project)
        #expect(found == ActivityFindings.Elsewhere(otherProjects: 50 * 60, unlinked: 0, entries: 1))
    }

    @Test func repositoriesWithoutAProjectAreCountedApart() {
        let days = activity([(atlas, 540, 580), (copper, 580, 640)])
        let found = ActivityFindings.elsewhere(entries: [entry("e", 540, 640, project: "p-atlas")], activity: days, project: project)
        #expect(found.otherProjects == 0)
        #expect(found.unlinked == 60 * 60)
    }

    @Test func leavesOutSplitRunningAndProjectlessEntries() {
        let days = activity([(atlas, 540, 580), (beacon, 580, 640)])
        let split = [SplitPart(projectId: "p-atlas", taskId: nil, isBillable: false, percent: 50),
                     SplitPart(projectId: "p-beacon", taskId: nil, isBillable: false, percent: 50)]
        let entries = [
            entry("split", 540, 640, project: "p-atlas", shares: split),
            entry("running", 540, nil, project: "p-atlas"),
            entry("none", 540, 640, project: nil),
        ]
        #expect(ActivityFindings.elsewhere(entries: entries, activity: days, project: project) == ActivityFindings.Elsewhere())
    }

    @Test func listsRepositoriesMostTimeFirstWithoutFolders() {
        let days = activity([(atlas, 540, 560), (beacon, 560, 620), (notes, 620, 700), (atlas, 700, 720)])
        let found = ActivityFindings.repositories(in: days)
        #expect(found.map(\.repository.name) == ["beacon", "atlas"])
        #expect(found.map(\.attention) == [3600, 2400])
    }
}
