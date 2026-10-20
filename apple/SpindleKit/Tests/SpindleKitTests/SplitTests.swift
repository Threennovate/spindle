import Foundation
import Testing
@testable import SpindleKit

struct SplitMathTests {
    @Test func dividesSecondsToAddUpExactlyAsTheServerDoes() {
        #expect(SplitMath.allocate(3600, [60, 40]) == [2160, 1440])
        #expect(SplitMath.allocate(10, [33, 33, 34]) == [3, 3, 4])
        #expect(SplitMath.allocate(1, [50, 50]) == [1, 0])
    }

    @Test func splitsEvenlyTheFirstPartsTakingTheRest() {
        #expect(SplitMath.equal(2) == [50, 50])
        #expect(SplitMath.equal(3) == [34, 33, 33])
    }

    @Test func movingADividerChangesOnlyTheTwoPartsBesideIt() {
        #expect(SplitMath.moveBoundary([34, 33, 33], after: 1, to: 80) == [34, 46, 20])
        #expect(SplitMath.moveBoundary([50, 50], after: 0, to: 62, step: 5) == [60, 40])
        #expect(SplitMath.moveBoundary([50, 50], after: 0, to: 120) == [99, 1])
    }

    @Test func aTypedShareMakesRoomInTheOthersInProportion() {
        #expect(SplitMath.setting([50, 30, 20], at: 0, to: 60) == [60, 24, 16])
        #expect(SplitMath.setting([50, 30, 20], at: 2, to: 100) == [1, 1, 98])
    }

    @Test func addingAndRemovingPartsKeepsTheTotal() {
        #expect(SplitMath.adding(to: [70, 30]) == [47, 20, 33])
        #expect(SplitMath.removing([50, 30, 20], at: 0) == [60, 40])
        #expect(SplitMath.removing([60, 40], at: 1) == [100])
    }

    @Test func theLargestPartIsTheEntrysProjectTheFirstOnATie() {
        let parts = [
            SplitPart(projectId: "a", isBillable: false, percent: 50),
            SplitPart(projectId: "b", isBillable: true, percent: 50),
        ]
        let details = EntryDetails().splitting(parts)

        #expect(details.projectId == "a")
        #expect(details.shares == parts)
        #expect(EntryDetails().splitting([parts[1]]).shares.isEmpty)
        #expect(EntryDetails().splitting([parts[1]]).projectId == "b")
    }

    @Test func describingEachPartGivesTheDescriptionToTheLargestAndStartsTheOthersEmpty() {
        let parts = [
            SplitPart(projectId: "a", isBillable: false, percent: 30),
            SplitPart(projectId: "b", isBillable: false, percent: 40),
            SplitPart(projectId: "c", isBillable: false, percent: 30),
        ]
        let described = SplitPart.describingEach(parts, from: " Fix login ")

        #expect(described.map(\.description) == ["", "Fix login", ""])
        #expect(SplitPart.describesEach(described))
        #expect(!SplitPart.describesEach(parts))
        #expect(!SplitPart.describesEach([described[1]]))
        // On a tie the first of the largest gets it, as the server and the web choose the entry's project.
        #expect(SplitPart.describingEach(Array(parts[..<2].map { var part = $0; part.percent = 50; return part }), from: "x").map(\.description) == ["x", ""])
    }

    @Test func oneDescriptionForAllJoinsThePartsOwn() {
        var parts = SplitPart.describingEach(
            [SplitPart(projectId: "a", isBillable: false, percent: 60), SplitPart(projectId: "b", isBillable: false, percent: 40)], from: "Fix login")
        parts[1].description = " Review PR "
        let shared = SplitPart.sharingOneDescription(parts)

        #expect(shared.description == "Fix login; Review PR")
        #expect(shared.parts.allSatisfy { $0.description == nil })
        #expect(SplitPart.sharingOneDescription([parts[0], SplitPart(projectId: "c", isBillable: false, percent: 0, description: "")]).description == "Fix login")
    }

    @Test func changesSavedBeforeSplitsStillDecode() throws {
        let old = #"{"description":"Old","projectId":"p1","taskId":null,"tagIds":[],"isBillable":true}"#
        let details = try JSONDecoder().decode(EntryDetails.self, from: Data(old.utf8))

        #expect(details.projectId == "p1")
        #expect(details.shares.isEmpty)
    }

    @Test func waitingChangesShowTheirSplitAtOnce() {
        let start = Date(timeIntervalSince1970: 1_790_000_000)
        let parts = [SplitPart(projectId: "a", isBillable: true, percent: 70), SplitPart(projectId: "b", isBillable: false, percent: 30)]
        let request = EntryRequest(details: EntryDetails(description: "Both").splitting(parts), start: start, stop: start.addingTimeInterval(3600))

        let shown = EntryChange.apply([.create(request, id: "e1", workspaceId: "w1")], to: [], running: nil, workspaceId: "w1") { _ in false }

        #expect(shown.entries.first?.parts == parts)
        #expect(shown.entries.first?.projectId == "a")
    }
}

struct SplitSuggestionTests {
    let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
    let day = DayKey("2026-09-28")!

    func at(_ minutes: Double) -> Date { time.start(of: day).addingTimeInterval(minutes * 60) }

    func repo(_ name: String, isRepository: Bool = true) -> ActivityRepository {
        ActivityRepository(key: "/code/\(name)", name: name, origin: nil, isRepository: isRepository)
    }

    /// A day whose attention moves between repositories: (repository, from minute, to minute).
    func activity(_ spans: [(ActivityRepository, Double, Double)]) -> [ActivityDay] {
        let attention = spans.map { AttentionSegment(repository: $0.0, range: DateInterval(start: at($0.1), end: at($0.2)), sessionIDs: []) }
        return [ActivityDay(day: day, attention: attention, blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)]
    }

    func project(_ id: String, _ name: String, archived: Bool = false) -> Project {
        Project(
            id: id, name: name, clientId: nil, color: "#000000", isPrivate: false, isBillable: id == "acme", separateBilledTime: false,
            estimateSeconds: nil, archivedAt: archived ? Date() : nil, trackedSeconds: 0, tasks: [], hourlyRate: nil, canManage: true,
            billingMode: .asTracked, dailyHours: nil)
    }

    @Test func attentionInTwoProjectsSuggestsSplittingByIt() throws {
        let track = repo("spindle"), site = repo("acme-site")
        let days = activity([(track, 540, 600), (site, 600, 640), (track, 640, 660)])
        let links: [String: String] = [track.key: "tt", site.key: "acme"]

        let suggestion = try #require(SplitSuggestion.suggest(from: at(540), to: at(720), activity: days) { links[$0.key] })

        #expect(suggestion.parts.map(\.target) == [.project("tt"), .project("acme")])
        #expect(suggestion.parts.map(\.percent) == [67, 33])
        #expect(abs(suggestion.coverage - 120.0 / 180) < 0.001)
        #expect(suggestion.splitParts { $0 == "acme" }?.map(\.isBillable) == [false, true])
    }

    @Test func onlyAttentionInsideTheEntryCounts() throws {
        let track = repo("spindle"), site = repo("acme-site")
        let days = activity([(track, 400, 600), (site, 600, 630)])

        let suggestion = try #require(SplitSuggestion.suggest(from: at(570), to: at(630), activity: days) { $0 == track ? "tt" : "acme" })

        #expect(suggestion.parts.map(\.percent) == [50, 50])
    }

    @Test func nothingIsSuggestedForOneProjectOrTooLittleAttention() {
        let track = repo("spindle"), site = repo("acme-site")

        #expect(SplitSuggestion.suggest(from: at(540), to: at(720), activity: activity([(track, 540, 600)])) { _ in "tt" } == nil)
        #expect(SplitSuggestion.suggest(from: at(540), to: at(720), activity: activity([(track, 540, 545), (site, 545, 549)])) { $0.name } == nil)
    }

    @Test func sliversAndFoldersOutsideRepositoriesAreLeftOut() {
        let track = repo("spindle"), site = repo("acme-site"), scratch = repo("scratch", isRepository: false)
        let days = activity([(track, 540, 640), (site, 640, 645), (scratch, 645, 700)])

        #expect(SplitSuggestion.suggest(from: at(540), to: at(720), activity: days) { $0.name } == nil)
    }

    @Test func repositoriesWithoutAProjectAreNamedSoTheyCanBeLinked() throws {
        let track = repo("spindle"), unknown = repo("side-thing")
        let days = activity([(track, 540, 600), (unknown, 600, 660)])

        let suggestion = try #require(SplitSuggestion.suggest(from: at(540), to: at(720), activity: days) { $0 == track ? "tt" : nil })

        #expect(suggestion.unlinked == [unknown])
        #expect(suggestion.splitParts { _ in false } == nil)
    }

    @Test func anEntryAlreadySplitThatWayNeedsNoSuggestion() throws {
        let track = repo("spindle"), site = repo("acme-site")
        let suggestion = try #require(
            SplitSuggestion.suggest(from: at(540), to: at(720), activity: activity([(track, 540, 600), (site, 600, 640)])) { $0 == track ? "tt" : "acme" })
        func entry(_ shares: [(String, Int)]) -> TimeEntry {
            TimeEntry(
                id: "e", workspaceId: "w", description: "", projectId: shares[0].0, taskId: nil, tagIds: [], isBillable: false,
                start: at(540), stop: at(720),
                shares: shares.map { .init(projectId: $0.0, taskId: nil, isBillable: false, percent: Int32($0.1)) }, version: "1")
        }

        #expect(suggestion.matches(entry([("tt", 58), ("acme", 42)])))
        #expect(!suggestion.matches(entry([("tt", 80), ("acme", 20)])))
    }

    @Test func timeBetweenTwoPromptsForOneProjectGoesToItHoweverLong() throws {
        let track = repo("spindle"), site = repo("acme-site")
        // Spindle's prompts at 9:00 and 9:50 hold the 35 minutes between their attention.
        let days = activity([(track, 540, 555), (track, 590, 605), (site, 605, 620)])

        let suggestion = try #require(SplitSuggestion.suggest(from: at(540), to: at(620), activity: days) { $0 == track ? "tt" : "acme" })

        #expect(suggestion.parts.map(\.attention) == [65 * 60, 15 * 60])
        #expect(suggestion.parts.map(\.percent) == [81, 19])
    }

    @Test func timeBetweenPromptsForDifferentProjectsIsNotFilled() throws {
        let track = repo("spindle"), site = repo("acme-site")
        let days = activity([(track, 540, 555), (site, 600, 615)])

        let suggestion = try #require(SplitSuggestion.suggest(from: at(540), to: at(615), activity: days) { $0 == track ? "tt" : "acme" })

        #expect(suggestion.parts.map(\.attention) == [15 * 60, 15 * 60])
        #expect(suggestion.parts.map(\.unattributed) == [0, 0])
    }

    @Test func timeTheLogsLeaveOpenStaysOnTheEntrysProject() throws {
        let track = repo("spindle"), site = repo("acme-site")
        // Two hours tracked on spindle, with twenty minutes in each repository.
        let days = activity([(track, 540, 560), (site, 560, 580)])

        let suggestion = try #require(
            SplitSuggestion.suggest(from: at(540), to: at(660), activity: days, fallback: "tt") { $0 == track ? "tt" : "acme" })

        #expect(suggestion.parts.map(\.target) == [.project("tt"), .project("acme")])
        #expect(suggestion.parts.map(\.percent) == [83, 17])
        #expect(suggestion.parts[0].unattributed == 80 * 60)
        #expect(suggestion.parts[0].duration == 100 * 60)
    }

    @Test func workOnlyElsewhereStillLeavesTheEntrysProjectItsOpenTime() throws {
        let site = repo("acme-site")
        let days = activity([(site, 560, 600)])

        let suggestion = try #require(
            SplitSuggestion.suggest(from: at(540), to: at(660), activity: days, fallback: "tt") { _ in "acme" })

        #expect(suggestion.parts.map(\.target) == [.project("tt"), .project("acme")])
        #expect(suggestion.parts.map(\.percent) == [67, 33])
    }

    @Test func workThatMovedOnceIsOfferedAsTwoEntriesAtTheMove() throws {
        let anchor = repo("harbor"), site = repo("acme-site")
        // Timer on Harbor from 9:00; prompts there until 9:40, then in acme-site from 10:42 until 11:20.
        let days = activity([(anchor, 545, 560), (anchor, 580, 595), (site, 642, 657), (site, 680, 695)])

        let suggestion = try #require(
            SplitSuggestion.suggest(from: at(540), to: at(720), activity: days, fallback: "anchor") { $0 == anchor ? "anchor" : "acme" })

        let stretches = try #require(suggestion.projectStretches)
        #expect(stretches.map(\.projectId) == ["anchor", "acme"])
        #expect(stretches.map(\.range) == [DateInterval(start: at(540), end: at(642)), DateInterval(start: at(642), end: at(720))])
        // Shared by percent, the open time after the last prompt stays on Harbor.
        #expect(suggestion.parts.map(\.percent) == [71, 29])
    }

    @Test func shortDetoursDoNotBreakAStretch() throws {
        let anchor = repo("harbor"), site = repo("acme-site"), other = repo("other")
        let days = activity([(anchor, 540, 600), (other, 600, 606), (anchor, 606, 640), (site, 640, 720)])

        let suggestion = try #require(
            SplitSuggestion.suggest(from: at(540), to: at(720), activity: days) { $0.name })

        #expect(suggestion.projectStretches?.map(\.projectId) == ["harbor", "acme-site"])
        #expect(suggestion.projectStretches?.last?.range.start == at(640))
    }

    @Test func workThatAlternatesIsOnlySharedByPercent() throws {
        let anchor = repo("harbor"), site = repo("acme-site")
        let days = activity([(anchor, 540, 570), (site, 570, 600), (anchor, 600, 630), (site, 630, 660)])

        let suggestion = try #require(
            SplitSuggestion.suggest(from: at(540), to: at(660), activity: days) { $0.name })

        #expect(suggestion.stretches == nil)
        #expect(suggestion.parts.map(\.percent) == [50, 50])
    }

    @Test func stretchesNeedEveryRepositoryLinked() throws {
        let anchor = repo("harbor"), unknown = repo("side-thing")
        let days = activity([(anchor, 540, 600), (unknown, 600, 660)])

        let suggestion = try #require(
            SplitSuggestion.suggest(from: at(540), to: at(660), activity: days) { $0 == anchor ? "anchor" : nil })

        #expect(suggestion.stretches?.count == 2)
        #expect(suggestion.projectStretches == nil)
    }

    @Test func linksPreferTheChosenThenTheLearnedThenTheName() {
        let track = repo("time-sheet")
        let projects = [project("tt", "TimeSheet"), project("other", "Other"), project("old", "Old", archived: true)]

        #expect(RepositoryLinks().project(for: track, among: projects) == "tt")
        #expect(RepositoryLinks(learned: [track.identity!: "other"]).project(for: track, among: projects) == "other")
        #expect(RepositoryLinks(chosen: [track.identity!: "old"], learned: [track.identity!: "other"]).project(for: track, among: projects) == "other")
        #expect(RepositoryLinks(chosen: [track.identity!: "tt"], learned: [track.identity!: "other"]).project(for: track, among: projects) == "tt")
        #expect(RepositoryLinks().project(for: repo("x"), among: projects) == nil)
    }

    @Test func theMatcherFindsTheProjectsLinksFoundBefore() {
        // How a repository's project was found before projects' names were read once per set of projects.
        func before(_ links: RepositoryLinks, _ repository: ActivityRepository, _ projects: [Project]) -> String? {
            func normalized(_ text: String) -> String { text.lowercased().filter { $0.isLetter || $0.isNumber } }
            let active = projects.filter { !$0.isArchived }
            if let identity = repository.identity {
                if let id = links.chosen[identity], active.contains(where: { $0.id == id }) { return id }
                if let id = links.learned[identity], active.contains(where: { $0.id == id }) { return id }
            }
            let name = normalized(repository.name)
            guard name.count >= 3 else { return nil }
            if let exact = active.first(where: { normalized($0.name) == name }) { return exact.id }
            let close = active.filter {
                let project = normalized($0.name)
                return project.count >= 4 && (project.contains(name) || name.contains(project))
            }
            return close.count == 1 ? close[0].id : nil
        }
        let projects = [
            project("tt", "TimeSheet"), project("site", "Acme Site"), project("site2", "Acme Site Admin"),
            project("old", "Spindle", archived: true), project("spin", "Spindle Mac"), project("cafe", "Café-App"),
            project("short", "Ops"), project("atlas", "ATLAS"), project("atlas2", "atlas (old)", archived: true),
            project("cro", "Čarobnjak 2"), project("api", "API"),
        ]
        let names = ["time-sheet", "timesheet", "TIME_SHEET", "acme-site", "acme", "site", "spindle", "spindle-mac",
                     "mac", "cafe-app", "café-app", "ops", "ops-tools", "atlas", "atlas-old", "carobnjak-2", "čarobnjak2",
                     "api", "api-server", "x", "", "--", "unrelated"]
        let repositories = names.map { repo($0) } + [
            ActivityRepository(key: "/tmp/scratch", name: "scratch", origin: nil, isRepository: false),
        ]
        let identities = repositories.compactMap(\.identity)
        let choices: [RepositoryLinks] = [
            RepositoryLinks(),
            RepositoryLinks(chosen: [identities[0]: "site", identities[6]: "old"], learned: [identities[6]: "tt", identities[1]: "gone"]),
            RepositoryLinks(learned: Dictionary(uniqueKeysWithValues: identities.map { ($0, "spin") })),
        ]

        for links in choices {
            let matcher = links.matcher(among: projects)
            for repository in repositories {
                #expect(matcher.project(for: repository) == before(links, repository, projects), "\(repository.name)")
                #expect(links.project(for: repository, among: projects) == before(links, repository, projects))
            }
        }
    }

    @Test func eachRepositorysProjectIsLookedUpOnce() {
        let track = repo("spindle"), site = repo("acme-site")
        let days = activity([(track, 540, 545), (site, 545, 560), (track, 560, 600), (site, 600, 640), (track, 640, 700)])
        var asked: [String] = []

        let suggestion = SplitSuggestion.suggest(from: at(540), to: at(720), activity: days) { repository in
            asked.append(repository.name)
            return repository == track ? "tt" : "acme"
        }

        #expect(suggestion != nil)
        #expect(asked.sorted() == ["acme-site", "spindle"])
    }

    @Test func linksAreLearnedFromWhereTimeWasTrackedWhileWorkingInARepository() {
        let track = repo("spindle"), site = repo("acme-site")
        let days = activity([(track, 540, 600), (site, 600, 610)])
        let entry = TimeEntry(
            id: "e", workspaceId: "w", description: "", projectId: "tt", taskId: nil, tagIds: [], isBillable: false,
            start: at(530), stop: at(610), shares: [], version: "1")

        let links = RepositoryLinks.learn(from: [entry], activity: days)

        // Mostly spindle during spindle time; acme-site was only a sliver of that entry.
        #expect(links == [track.identity!: "tt"])
    }

    @Test func entriesSpreadBetweenRepositoriesTeachNothing() {
        let track = repo("spindle"), site = repo("acme-site")
        let days = activity([(track, 540, 580), (site, 580, 620)])
        let entry = TimeEntry(
            id: "e", workspaceId: "w", description: "", projectId: "tt", taskId: nil, tagIds: [], isBillable: false,
            start: at(540), stop: at(620), shares: [], version: "1")

        #expect(RepositoryLinks.learn(from: [entry], activity: days).isEmpty)
    }
}
