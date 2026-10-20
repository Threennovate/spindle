import Foundation
import Testing
@testable import SpindleKit
@testable import Spindle

struct SplitApplierTests {
    let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
    let day = DayKey("2026-09-29")!
    func at(_ minutes: Double) -> Date { time.start(of: day).addingTimeInterval(minutes * 60) }

    func project(_ id: String, _ name: String, billable: Bool) -> Project {
        Project(
            id: id, name: name, clientId: nil, color: "#000000", isPrivate: false, isBillable: billable, separateBilledTime: false,
            estimateSeconds: nil, archivedAt: nil, trackedSeconds: 0, tasks: [], hourlyRate: nil, canManage: true,
            billingMode: .asTracked, dailyHours: nil)
    }

    var catalog: Catalog { Catalog(projects: [project("atlas", "Atlas", billable: true), project("beacon", "Beacon", billable: false)]) }

    @Test func stretchesBecomeConsecutiveEntriesAndTheLastRunsOn() throws {
        let base = EntryDetails(description: "Morning", projectId: "atlas", taskId: "t1", tagIds: ["x"], isBillable: false)
        let stretches = [TimeStretch(projectId: "atlas", range: DateInterval(start: at(540), end: at(642))),
                         TimeStretch(projectId: "beacon", range: DateInterval(start: at(642), end: at(700)))]

        let requests = try #require(SplitApplier.requests(for: stretches, base: base, start: at(540), stop: nil, catalog: catalog))

        #expect(requests.map(\.start) == [at(540), at(642)])
        #expect(requests.map(\.stop) == [at(642), nil])
        #expect(requests.map(\.details.projectId) == ["atlas", "beacon"])
        // The entry's own project keeps its task and billable setting; the other follows its project.
        #expect(requests.map(\.details.taskId) == ["t1", nil])
        #expect(requests.map(\.details.isBillable) == [false, false])
        #expect(requests.allSatisfy { $0.details.description == "Morning" && $0.details.tagIds == ["x"] })
    }

    @Test func stretchesPastTheEditedTimesAreRefused() {
        let base = EntryDetails(projectId: "atlas")
        let stretches = [TimeStretch(projectId: "atlas", range: DateInterval(start: at(540), end: at(642))),
                         TimeStretch(projectId: "beacon", range: DateInterval(start: at(642), end: at(700)))]

        #expect(SplitApplier.requests(for: stretches, base: base, start: at(650), stop: at(700), catalog: catalog) == nil)
        #expect(SplitApplier.requests(for: stretches, base: base, start: at(540), stop: at(600), catalog: catalog) == nil)
    }

    @Test func oneClickSaysWhyItCantTakeASuggestion() throws {
        let anchor = ActivityRepository(key: "/a", name: "atlas", origin: nil, isRepository: true)
        let site = ActivityRepository(key: "/b", name: "beacon", origin: nil, isRepository: true)
        let copper = ActivityRepository(key: "/c", name: "copper", origin: nil, isRepository: true)
        func days(_ spans: [(ActivityRepository, Double, Double)]) -> [ActivityDay] {
            [ActivityDay(day: day, attention: spans.map { AttentionSegment(repository: $0.0, range: DateInterval(start: at($0.1), end: at($0.2)), sessionIDs: []) },
                         blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)]
        }
        let entry = TimeEntry(id: "e", workspaceId: "w", description: "Morning", projectId: "atlas", taskId: nil, tagIds: [], isBillable: true,
                              start: at(540), stop: at(720), shares: [], version: "1")
        let moved = try #require(SplitSuggestion.suggest(from: at(540), to: at(720), activity: days([(anchor, 540, 600), (site, 642, 720)]),
                                                         fallback: "atlas") { $0.name })
        let shared = try #require(SplitSuggestion.suggest(from: at(540), to: at(720),
                                                          activity: days([(anchor, 540, 570), (site, 570, 600), (anchor, 600, 630), (site, 630, 720)]),
                                                          fallback: "atlas") { $0.name })
        let unlinked = try #require(SplitSuggestion.suggest(from: at(540), to: at(720), activity: days([(anchor, 540, 600), (copper, 600, 720)]),
                                                            fallback: "atlas") { $0.name == "copper" ? nil : $0.name })
        var beacon = project("beacon", "Beacon", billable: false)
        beacon.tasks = [ProjectTask(id: "t", projectId: "beacon", name: "Support", estimateSeconds: nil, isDone: false, trackedSeconds: 0)]
        let strict = Catalog(projects: [project("atlas", "Atlas", billable: true), beacon])
        func rules(task: Bool = false, description: Bool = false) -> EntryRules {
            EntryRules(settings: WorkspaceSettings(
                name: "Studio", currency: "EUR", defaultHourlyRate: nil, onlyAdminsCreateProjects: false, onlyAdminsCreateTags: false,
                lockedBefore: nil, requireDescription: description, requireProject: false, requireTask: task, requireTag: false), isAdmin: false)
        }
        func blocker(_ suggestion: SplitSuggestion, _ entry: TimeEntry, _ rules: EntryRules?) -> SplitApplier.Blocker? {
            if case .failure(let blocker) = SplitApplier.prepare(suggestion, for: entry, rules: rules, catalog: strict) { return blocker }
            return nil
        }

        #expect(blocker(moved, entry, nil) == nil)
        #expect(blocker(shared, entry, rules()) == nil)
        #expect(blocker(unlinked, entry, nil) == .unlinked)
        // Beacon has open tasks the workspace requires, which a suggestion can't choose.
        #expect(blocker(moved, entry, rules(task: true)) == .details("Needs a task on Beacon"))
        #expect(blocker(shared, entry, rules(task: true)) == .details("Needs a task on Beacon"))
        var bare = entry
        bare.description = ""
        #expect(blocker(moved, bare, rules(description: true)) == .details("Needs a description"))
        // The entry's times changed since: the stretches no longer fit it.
        var shorter = entry
        shorter.stop = at(600)
        #expect(blocker(moved, shorter, nil) == .details(SplitApplier.timesChanged))
    }

    @Test func aSuggestionReadsAsMovesOrShares() throws {
        let anchor = ActivityRepository(key: "/a", name: "atlas", origin: nil, isRepository: true)
        let site = ActivityRepository(key: "/b", name: "beacon", origin: nil, isRepository: true)
        func days(_ spans: [(ActivityRepository, Double, Double)]) -> [ActivityDay] {
            [ActivityDay(day: day, attention: spans.map { AttentionSegment(repository: $0.0, range: DateInterval(start: at($0.1), end: at($0.2)), sessionIDs: []) },
                         blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)]
        }
        let entry = TimeEntry(id: "e", workspaceId: "w", description: "", projectId: "atlas", taskId: nil, tagIds: [], isBillable: true,
                              start: at(540), stop: at(720), shares: [], version: "1")
        let moved = try #require(SplitSuggestion.suggest(from: at(540), to: at(720), activity: days([(anchor, 540, 600), (site, 642, 720)]),
                                                         fallback: "atlas") { $0.name })
        #expect(SplitApplier.summary(moved, entry: entry, catalog: catalog, time: time) == "Atlas until 10:42, then Beacon")

        let shared = try #require(SplitSuggestion.suggest(from: at(540), to: at(720),
                                                          activity: days([(anchor, 540, 570), (site, 570, 600), (anchor, 600, 630), (site, 630, 720)]),
                                                          fallback: "atlas") { $0.name })
        #expect(shared.stretches == nil)
        #expect(SplitApplier.summary(shared, entry: entry, catalog: catalog, time: time) == "Atlas 59m · Beacon 2h 01m")
        // Chips show the same hours, in whole minutes that add up to the entry.
        #expect(SplitApplier.durations(shared, seconds: 3 * 3600).map { Durations.format($0, as: .hm) } == ["0:59", "2:01"])
    }

    @Test func theEvidenceStripReadsTheSplitInWords() throws {
        let anchor = ActivityRepository(key: "/a", name: "atlas", origin: nil, isRepository: true)
        let site = ActivityRepository(key: "/b", name: "beacon", origin: nil, isRepository: true)
        let activity = [ActivityDay(day: day, attention: [
            AttentionSegment(repository: anchor, range: DateInterval(start: at(540), end: at(600)), sessionIDs: [], prompts: [at(540), at(560)]),
            AttentionSegment(repository: site, range: DateInterval(start: at(642), end: at(700)), sessionIDs: [], prompts: [at(642), at(660), at(680)]),
        ], blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)]
        let suggestion = try #require(SplitSuggestion.suggest(from: at(540), to: at(720), activity: activity, fallback: "atlas") { $0.name })

        #expect(suggestion.touches == [.project("atlas"): 2, .project("beacon"): 3])
        #expect(suggestion.open.map(\.start) == [at(600), at(700)])
        #expect(suggestion.strength == .strong)
        #expect(EvidenceStrip.spoken(suggestion, catalog: catalog, time: time)
            == "Agent activity from 09:00 to 12:00: Atlas 2h 02m, 2 prompts, with 1h 02m the agent logs don't account for; Beacon 58m, 3 prompts. "
            + "The work moved to Beacon at 10:42.")
    }

    @Test func undoPutsTheEntryBackButLeavesATimerStoppedSinceStopped() {
        let running = TimeEntry(id: "e", workspaceId: "w", description: "Agents", projectId: "atlas", taskId: nil, tagIds: [], isBillable: true,
                                start: at(540), stop: nil, shares: [], version: "1")
        let change = SavedChange(before: running, added: ["a1"])

        let stillRunning = change.restoring(TimeEntry(id: "e", workspaceId: "w", description: "Agents", projectId: "beacon", taskId: nil, tagIds: [],
                                                      isBillable: false, start: at(642), stop: nil, shares: [], version: "1"))
        #expect(stillRunning?.start == at(540))
        #expect(stillRunning?.stop == nil)
        #expect(stillRunning?.details.projectId == "atlas")

        let stoppedSince = change.restoring(TimeEntry(id: "e", workspaceId: "w", description: "Agents", projectId: "beacon", taskId: nil, tagIds: [],
                                                      isBillable: false, start: at(642), stop: at(700), shares: [], version: "1"))
        #expect(stoppedSince?.start == at(540))
        #expect(stoppedSince?.stop == at(700))
        #expect(SavedChange(before: nil, added: ["a1"]).restoring(nil) == nil)
    }

    @Test func endingASplitContinuesInANewEntryWithWhatItKeeps() throws {
        let parts = [SplitPart(projectId: "atlas", taskId: "t1", isBillable: true, percent: 60, description: "Login"),
                     SplitPart(projectId: "beacon", isBillable: false, percent: 40, description: "Docs")]
        let running = TimeEntry(id: "e", workspaceId: "w", description: "Login; Docs", projectId: "atlas", taskId: "t1", tagIds: ["x"],
                                isBillable: true, start: at(540), stop: nil, shares: parts.map(\.response), version: "1")

        let (ended, next) = try #require(SplitApplier.continuing(running, at: at(600), on: [parts[1]].map { var part = $0; part.percent = 100; return part }))
        #expect(ended.start == at(540) && ended.stop == at(600))
        #expect(ended.details.shares.map(\.projectId) == ["atlas", "beacon"])
        #expect(next.start == at(600) && next.stop == nil)
        #expect(next.details.projectId == "beacon" && next.details.shares.isEmpty)
        // One project left: its own description is the entry's.
        #expect(next.details.description == "Docs" && next.details.tagIds == ["x"])

        let joined = SplitDrift.continuing(parts, after: .joined("copper", at: at(600))) { _ in false }
        let (_, three) = try #require(SplitApplier.continuing(running, at: at(600), on: joined))
        #expect(three.details.shares.map(\.description) == ["Login", "Docs", ""])
        #expect(three.describeParts == true)
        #expect(three.details.taskId == "t1")
        #expect(SplitApplier.continuing(running, at: at(540), on: joined) == nil)
    }

    @Test func undoAfterEndingASplitKeepsTheTimerRunningUnlessItStopped() {
        let running = TimeEntry(id: "e", workspaceId: "w", description: "", projectId: "atlas", taskId: nil, tagIds: [], isBillable: true,
                                start: at(540), stop: nil, shares: [], version: "1")
        let change = SavedChange(before: running, added: ["n"])
        var ended = running
        ended.stop = at(600)
        var next = TimeEntry(id: "n", workspaceId: "w", description: "", projectId: "beacon", taskId: nil, tagIds: [], isBillable: false,
                             start: at(600), stop: nil, shares: [], version: "1")

        #expect(change.restoring(ended, added: [next])?.stop == nil)
        next.stop = at(650)
        #expect(change.restoring(ended, added: [next])?.stop == at(650))
    }
}
