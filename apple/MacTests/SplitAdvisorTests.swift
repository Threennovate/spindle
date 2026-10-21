import Foundation
import Testing
@testable import SpindleKit
@testable import Spindle

@MainActor
struct SplitAdvisorTests {
    func defaults() -> UserDefaults {
        let suite = UUID().uuidString
        return UserDefaults(suiteName: suite)!
    }

    func advisor(_ defaults: UserDefaults) -> SplitAdvisor {
        SplitAdvisor(settings: DesktopSettings(defaults: defaults, clearActivityCache: {}), defaults: defaults)
    }

    @Test func linksKeptByPathMoveToTheRepositorysIdentityAndGoToTheAccount() throws {
        let defaults = defaults()
        let root = FileManager.default.temporaryDirectory.appending(path: "atlas-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root.appending(path: ".git"), withIntermediateDirectories: true)
        try "[remote \"origin\"]\n\turl = git@github.com:Acme/atlas.git\n".write(to: root.appending(path: ".git/config"), atomically: true, encoding: .utf8)
        defaults.set([root.path: "p1", "/no/longer/there": "p2"], forKey: "repositoryLinks")

        let splits = advisor(defaults)

        #expect(splits.chosen == ["origin:github.com/acme/atlas": "p1"])
        #expect(splits.unsentLinks["origin:github.com/acme/atlas"]?.projectId == "p1")
        #expect(defaults.object(forKey: "repositoryLinks") == nil)
        #expect(advisor(defaults).chosen == splits.chosen)
    }

    @Test func aLinkChosenHereStandsUntilTheAccountHasIt() {
        let splits = advisor(defaults())
        let atlas = ActivityRepository(key: "/code/atlas", name: "atlas", origin: "https://github.com/acme/atlas", isRepository: true)

        splits.link(atlas, to: "p2")
        splits.absorb(links: ["origin:github.com/acme/atlas": "p1", "origin:github.com/acme/beacon": "p3"])
        #expect(splits.chosen == ["origin:github.com/acme/atlas": "p2", "origin:github.com/acme/beacon": "p3"])

        splits.sent(link: "origin:github.com/acme/atlas", projectId: "p2")
        splits.absorb(links: ["origin:github.com/acme/atlas": "p4"])
        #expect(splits.chosen == ["origin:github.com/acme/atlas": "p4"])
        #expect(splits.unsentLinks.isEmpty)
    }

    @Test func anUnlinkedRepositoryGoesToNoProjectOnEveryDeviceUntilChosenAgain() throws {
        let defaults = defaults()
        let splits = advisor(defaults)
        let atlas = ActivityRepository(key: "/code/atlas", name: "atlas", origin: "https://github.com/acme/atlas", isRepository: true)
        let identity = try #require(atlas.identity)
        let projects = [Project(id: "p-atlas", name: "Atlas", clientId: nil, color: "#000000", isPrivate: false, isBillable: false,
                                separateBilledTime: false, estimateSeconds: nil, archivedAt: nil, trackedSeconds: 0, tasks: [],
                                hourlyRate: nil, canManage: true, billingMode: .asTracked, dailyHours: nil)]
        splits.link(atlas, to: "p-atlas", rules: [BranchRule(pattern: "acme/*", projectId: "p-atlas", taskId: nil)])
        splits.sent(link: identity, projectId: "p-atlas", rules: [BranchRule(pattern: "acme/*", projectId: "p-atlas", taskId: nil)])
        let before = splits.choice(of: atlas)

        splits.unlink(atlas)

        // Neither its link nor a project named like it takes its work; the account hears of it.
        #expect(splits.isNoProject(atlas))
        #expect(splits.chosen[identity] == nil && splits.rules[identity] == nil)
        #expect(splits.links.project(for: atlas, among: projects) == nil)
        #expect(splits.unsentLinks[identity] == SplitAdvisor.UnsentLink(name: "atlas", projectId: nil, noProject: true))
        // The account's old link doesn't bring it back while the change is unsent, and it is kept on this Mac.
        splits.absorb(links: [identity: "p-atlas"])
        #expect(splits.isNoProject(atlas))
        #expect(advisor(defaults).isNoProject(atlas))
        splits.sent(link: identity, projectId: nil, noProject: true)
        splits.absorb(links: [:], noProject: [identity])
        #expect(splits.isNoProject(atlas))

        // Undo puts back the project and its rules.
        splits.choose(before, for: atlas)
        #expect(!splits.isNoProject(atlas))
        #expect(splits.choice(of: atlas) == before)
        #expect(splits.links.project(for: atlas, among: projects) == "p-atlas")
    }

    @Test func anIgnoredRepositoryLosesItsLinkGoesToTheAccountAsAHashAndComesBack() throws {
        let defaults = defaults()
        let splits = advisor(defaults)
        let account = "0C000000-0000-4000-8000-000000000001"
        let atlas = ActivityRepository(key: "/code/atlas", name: "atlas", origin: "https://github.com/acme/atlas", isRepository: true)
        let identity = try #require(atlas.identity)
        splits.account = account
        splits.link(atlas, to: "p-atlas")
        splits.sent(link: identity, projectId: "p-atlas")
        let context = splits.context(time: UserTime(timeZone: .current))

        let hash = try #require(splits.ignore(atlas))

        #expect(hash == IgnoredRepositories.hash(identity, account: account.lowercased()))
        #expect(splits.isIgnored(atlas))
        #expect(splits.chosen.isEmpty && splits.names[identity] == nil && splits.unsentLinks.isEmpty)
        #expect(splits.unsentIgnores == [hash: true])
        #expect(splits.ignoredName(hash) == "atlas")
        // What was read so far had it, so it is read again.
        #expect(splits.context(time: UserTime(timeZone: .current)) != context)
        // The account's links, which may not have heard yet, don't bring its link back.
        splits.absorb([RepositoryLink(identity: identity, name: "atlas", projectId: "p-atlas", workspaceId: "w", branchRules: [])])
        #expect(splits.chosen.isEmpty)

        // Kept on this Mac, and the account's list doesn't undo what wasn't sent.
        let again = advisor(defaults)
        again.absorb(ignored: [])
        #expect(again.isIgnored(atlas) && again.ignoredName(hash) == "atlas")
        again.sent(ignore: hash, true)
        again.absorb(ignored: [])
        #expect(!again.isIgnored(atlas))

        splits.unignore(hash)
        #expect(!splits.isIgnored(atlas))
        #expect(splits.unsentIgnores == [hash: false])
    }

    @Test func aRepositoryIgnoredOnAnotherDeviceIsNamedFromWhatThisMacKnows() throws {
        let splits = advisor(defaults())
        let account = "0c000000-0000-4000-8000-000000000001"
        let beacon = ActivityRepository(key: "/code/beacon", name: "beacon", origin: "https://github.com/acme/beacon", isRepository: true)
        splits.account = account
        splits.unlink(beacon)
        let hash = IgnoredRepositories.hash(try #require(beacon.identity), account: account)

        splits.absorb(ignored: [hash, String(repeating: "0", count: 64)])

        #expect(splits.ignoredHashes.count == 2)
        #expect(splits.ignoredName(hash) == "beacon")
        #expect(splits.ignoredName(String(repeating: "0", count: 64)) == nil)
        #expect(splits.noProject.isEmpty)
        #expect(splits.unsentIgnores.isEmpty)
    }

    @Test func anotherAccountsListIsNotKept() throws {
        let splits = advisor(defaults())
        let atlas = ActivityRepository(key: "/code/atlas", name: "atlas", origin: "https://github.com/acme/atlas", isRepository: true)
        splits.account = "0c000000-0000-4000-8000-000000000001"
        splits.ignore(atlas)

        splits.account = "0c000000-0000-4000-8000-000000000002"

        #expect(splits.ignoredHashes.isEmpty && splits.unsentIgnores.isEmpty && !splits.isIgnored(atlas))
    }

    @Test func branchRulesSendSomeBranchesElsewhereGoWithTheLinkAndNameWhatAgentsDoNow() throws {
        let defaults = defaults()
        let settings = DesktopSettings(defaults: defaults, clearActivityCache: {})
        settings.useAgentLogs = true
        settings.setReads(.claude, true)
        let splits = SplitAdvisor(settings: settings, defaults: defaults)
        let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
        let day = DayKey("2026-09-29")!
        func at(_ minutes: Double) -> Date { time.start(of: day).addingTimeInterval(minutes * 60) }
        let atlas = ActivityRepository(key: "/code/atlas", name: "atlas", origin: "https://github.com/acme/atlas", isRepository: true)
        let identity = try #require(atlas.identity)
        let support = ProjectTask(id: "t-support", projectId: "p-copper", name: "Support", isDone: false, trackedSeconds: 0)
        func project(_ id: String, _ name: String, tasks: [ProjectTask] = []) -> Project {
            Project(id: id, name: name, clientId: nil, color: "#000000", isPrivate: false, isBillable: false, separateBilledTime: false,
                    estimateSeconds: nil, archivedAt: nil, trackedSeconds: 0, tasks: tasks, hourlyRate: nil, canManage: true,
                    billingMode: .asTracked, dailyHours: nil)
        }
        let projects = [project("p-atlas", "Atlas"), project("p-copper", "Copper", tasks: [support])]
        let entry = TimeEntry(id: "e", workspaceId: "w", description: "", projectId: "p-atlas", taskId: nil, tagIds: [], isBillable: false,
                              start: at(540), stop: at(720), shares: [], version: "1")
        // Two sessions in one repository, on different branches.
        let segments = [
            AttentionSegment(repository: atlas, range: DateInterval(start: at(540), end: at(600)), sessionIDs: ["a"], branches: ["main"]),
            AttentionSegment(repository: atlas, range: DateInterval(start: at(600), end: at(690)), sessionIDs: ["b"], branches: ["acme/login"]),
        ]
        splits.absorb([ActivityDay(day: day, attention: segments, blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)], entries: [], time: time)
        #expect(splits.suggestion(for: entry, time: time, projects: projects) == nil)
        #expect(splits.branches(of: atlas) == ["acme/login", "main"])

        splits.link(atlas, to: "p-atlas", rules: [BranchRule(pattern: "acme/*", projectId: "p-copper", taskId: "t-support")])
        let suggestion = try #require(splits.suggestion(for: entry, time: time, projects: projects))
        #expect(suggestion.parts.map(\.target) == [.project("p-atlas"), .project("p-copper")])
        #expect(suggestion.parts.map(\.taskId) == [nil, "t-support"])
        #expect(SplitApplier.parts(suggestion, catalog: Catalog(projects: projects))?.map(\.taskId) == [nil, "t-support"])
        // A finished task is no longer given.
        let done = [projects[0], project("p-copper", "Copper", tasks: [ProjectTask(id: "t-support", projectId: "p-copper", name: "Support", isDone: true, trackedSeconds: 0)])]
        #expect(SplitApplier.parts(suggestion, catalog: Catalog(projects: done))?.map(\.taskId) == [nil, nil])
        #expect(splits.agentsNow(time: time, projects: projects, now: at(680)) == [RepositoryLinks.Placement(projectId: "p-copper", taskId: "t-support")])

        // The rules wait to be sent with the link; once the account has them, the account's word counts.
        splits.absorb(links: [identity: "p-atlas"])
        #expect(splits.rules[identity]?.map(\.pattern) == ["acme/*"])
        let unsent = try #require(splits.unsentLinks[identity])
        splits.sent(link: identity, projectId: unsent.projectId, rules: unsent.rules)
        #expect(splits.unsentLinks.isEmpty)
        splits.absorb(links: [identity: "p-atlas"], rules: [:])
        #expect(splits.rules.isEmpty)
        #expect(splits.suggestion(for: entry, time: time, projects: projects) == nil)
    }

    @Test func entriesKeptAnywhereAreKeptHere() {
        let splits = advisor(defaults())

        splits.dismiss("e1", workspaceId: "w1")
        splits.absorb(dismissals: ["e2"])

        #expect(splits.dismissed == ["e1", "e2"])
        #expect(splits.unsentDismissals == ["e1": "w1"])
        splits.sent(dismissal: "e1")
        #expect(splits.unsentDismissals.isEmpty)
    }

    @Test func aKeptSuggestionChangesWithWhatItDependsOn() throws {
        let defaults = defaults()
        let settings = DesktopSettings(defaults: defaults, clearActivityCache: {})
        settings.useAgentLogs = true
        settings.setReads(.claude, true)
        let splits = SplitAdvisor(settings: settings, defaults: defaults)
        let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
        let day = DayKey("2026-09-29")!
        func at(_ minutes: Double) -> Date { time.start(of: day).addingTimeInterval(minutes * 60) }
        let atlas = ActivityRepository(key: "/code/atlas", name: "atlas", origin: "https://github.com/acme/atlas", isRepository: true)
        let beacon = ActivityRepository(key: "/code/beacon", name: "beacon", origin: "https://github.com/acme/beacon", isRepository: true)
        func project(_ id: String, _ name: String) -> Project {
            Project(id: id, name: name, clientId: nil, color: "#000000", isPrivate: false, isBillable: false, separateBilledTime: false,
                    estimateSeconds: nil, archivedAt: nil, trackedSeconds: 0, tasks: [], hourlyRate: nil, canManage: true,
                    billingMode: .asTracked, dailyHours: nil)
        }
        let projects = [project("p-atlas", "Atlas"), project("p-beacon", "Beacon")]
        let entry = TimeEntry(id: "e", workspaceId: "w", description: "", projectId: "p-atlas", taskId: nil, tagIds: [], isBillable: false,
                              start: at(540), stop: at(720), shares: [], version: "1")
        func targets(_ suggestion: SplitSuggestion?) -> [SplitSuggestion.Target]? { suggestion?.parts.map(\.target) }

        #expect(splits.suggestion(for: entry, time: time, projects: projects) == nil)

        // Activity arrives: half the time went to Beacon.
        let segments = [(atlas, 540.0, 600.0), (beacon, 600, 690), (atlas, 690, 700)].map {
            AttentionSegment(repository: $0.0, range: DateInterval(start: at($0.1), end: at($0.2)), sessionIDs: [])
        }
        splits.absorb([ActivityDay(day: day, attention: segments, blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)], entries: [], time: time)
        #expect(targets(splits.suggestion(for: entry, time: time, projects: projects)) == [.project("p-atlas"), .project("p-beacon")])

        // Disabling a source drops the activity the suggestion was worked out from immediately.
        settings.setReads(.codex, false)
        #expect(splits.suggestion(for: entry, time: time, projects: projects) == nil)
        #expect(splits.days.isEmpty)
        settings.setReads(.codex, true)
        splits.absorb([ActivityDay(day: day, attention: segments, blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)], entries: [], time: time)
        #expect(targets(splits.suggestion(for: entry, time: time, projects: projects)) == [.project("p-atlas"), .project("p-beacon")])

        // The project named like the repository is renamed, so the repository has no project any more.
        let renamed = [project("p-atlas", "Atlas"), project("p-beacon", "Lighthouse")]
        #expect(targets(splits.suggestion(for: entry, time: time, projects: renamed)) == [.project("p-atlas"), .repository(beacon)])

        // The person links it.
        splits.link(beacon, to: "p-beacon")
        #expect(targets(splits.suggestion(for: entry, time: time, projects: renamed)) == [.project("p-atlas"), .project("p-beacon")])

        // The entry moves to where only Beacon had attention.
        var moved = entry
        moved.start = at(610)
        moved.stop = at(680)
        #expect(splits.suggestion(for: moved, time: time, projects: renamed) == nil)

        // Kept as it is.
        splits.dismiss(entry.id, workspaceId: "w")
        #expect(splits.suggestion(for: entry, time: time, projects: renamed) == nil)
    }

    @Test func changingSourcesConsentOrTimeZoneDropsDaysAndLearnedLinks() {
        let defaults = defaults(), settings = DesktopSettings(defaults: defaults, clearActivityCache: {})
        settings.useAgentLogs = true
        let splits = SplitAdvisor(settings: settings, defaults: defaults)
        let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
        let day = DayKey("2026-09-29")!
        let repository = ActivityRepository(key: "/repo", name: "Atlas", origin: "https://github.com/acme/atlas", isRepository: true)
        let start = time.start(of: day).addingTimeInterval(9 * 3600), stop = start.addingTimeInterval(3600)
        let entry = TimeEntry(id: "e", workspaceId: "w", description: "", projectId: "p-atlas", taskId: nil,
                              tagIds: [], isBillable: false, start: start, stop: stop, shares: [], version: "1")
        let activity = ActivityDay(day: day, attention: [AttentionSegment(repository: repository,
            range: DateInterval(start: start, end: stop), sessionIDs: [])], blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)
        splits.absorb([activity], entries: [entry], time: time)
        #expect(splits.days.count == 1)
        #expect(splits.learned[repository.identity!] == "p-atlas")
        settings.setReads(.codex, false)
        #expect(splits.days.isEmpty)
        #expect(splits.learned.isEmpty)
        splits.absorb([activity], entries: [], time: time)
        settings.setReads(.codex, true)
        #expect(splits.days.isEmpty)
        splits.absorb([activity], entries: [], time: time)
        settings.useAgentLogs = false
        settings.useAgentLogs = true
        #expect(splits.days.isEmpty)
        splits.absorb([activity], entries: [], time: time)
        splits.absorb([], entries: [], time: UserTime(timeZone: TimeZone(identifier: "America/New_York")!))
        #expect(splits.days.isEmpty)
        #expect(splits.learned.isEmpty)
    }

    @Test func daysAreBoundedAndRecentlyUsedDaysSurvive() {
        let defaults = defaults(), settings = DesktopSettings(defaults: defaults, clearActivityCache: {})
        settings.useAgentLogs = true
        let splits = SplitAdvisor(settings: settings, defaults: defaults)
        let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
        let first = DayKey("2026-01-01")!
        func activity(_ index: Int) -> ActivityDay {
            ActivityDay(day: time.adding(index, to: first), attention: [], blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)
        }
        splits.absorb((0..<SplitAdvisor.dayLimit).map(activity), entries: [], time: time)
        // A stop or switch offer reads its days first, making them recently used even if no new logs arrived.
        splits.absorb([activity(0)], entries: [], time: time)
        splits.absorb([activity(SplitAdvisor.dayLimit)], entries: [], time: time)
        #expect(splits.days.count == SplitAdvisor.dayLimit)
        #expect(splits.days[first] != nil)
        #expect(splits.days[time.adding(1, to: first)] == nil)
        for index in SplitAdvisor.dayLimit..<(SplitAdvisor.dayLimit * 3) {
            splits.absorb([activity(index)], entries: [], time: time)
            #expect(splits.days.count <= SplitAdvisor.dayLimit)
        }
    }

    @Test func aRunningEntrysSuggestionMovesOnOnceAMinute() throws {
        let defaults = defaults()
        let settings = DesktopSettings(defaults: defaults, clearActivityCache: {})
        settings.useAgentLogs = true
        settings.setReads(.claude, true)
        let splits = SplitAdvisor(settings: settings, defaults: defaults)
        let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
        let day = DayKey("2026-09-29")!
        func at(_ minutes: Double) -> Date { time.start(of: day).addingTimeInterval(minutes * 60) }
        let atlas = ActivityRepository(key: "/code/atlas", name: "atlas", origin: nil, isRepository: true)
        let beacon = ActivityRepository(key: "/code/beacon", name: "beacon", origin: nil, isRepository: true)
        let segments = [(atlas, 540.0, 580.0), (beacon, 580, 640)].map {
            AttentionSegment(repository: $0.0, range: DateInterval(start: at($0.1), end: at($0.2)), sessionIDs: [])
        }
        splits.absorb([ActivityDay(day: day, attention: segments, blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)], entries: [], time: time)
        let running = TimeEntry(id: "r", workspaceId: "w", description: "", projectId: "p-atlas", taskId: nil, tagIds: [], isBillable: false,
                                start: at(540), stop: nil, shares: [], version: "1")
        // Time after the last prompt stays on the timer's project, so it grows as the timer runs.
        func onAtlas(_ now: Date) throws -> TimeInterval {
            let suggestion = try #require(splits.suggestion(for: running, time: time, projects: [], now: now))
            return try #require(suggestion.parts.first { $0.target == .project("p-atlas") }).unattributed
        }

        let first = try onAtlas(at(700))
        #expect(try onAtlas(at(700.5)) == first)
        #expect(try onAtlas(at(701)) == first + 60)
    }

    @Test func aSplitIsJudgedOnlyFromWhenItWasSetAndOfferedOnce() throws {
        let defaults = defaults()
        let settings = DesktopSettings(defaults: defaults, clearActivityCache: {})
        settings.useAgentLogs = true
        settings.setReads(.claude, true)
        let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
        let day = DayKey("2026-09-29")!
        func at(_ minutes: Double) -> Date { time.start(of: day).addingTimeInterval(minutes * 60) }
        let splits = SplitAdvisor(settings: settings, defaults: defaults, launched: at(500))
        let projects = [("atlas", "Atlas"), ("beacon", "Beacon")].map { id, name in
            Project(id: id, name: name, clientId: nil, color: "#000000", isPrivate: false, isBillable: false, separateBilledTime: false,
                    estimateSeconds: nil, archivedAt: nil, trackedSeconds: 0, tasks: [], hourlyRate: nil, canManage: true,
                    billingMode: .asTracked, dailyHours: nil)
        }
        let atlas = ActivityRepository(key: "/code/atlas", name: "atlas", origin: nil, isRepository: true)
        let beacon = ActivityRepository(key: "/code/beacon", name: "beacon", origin: nil, isRepository: true)
        let segments = [(atlas, 540.0, 560.0), (beacon, 560, 640)].map {
            AttentionSegment(repository: $0.0, range: DateInterval(start: at($0.1), end: at($0.2)), sessionIDs: [])
        }
        splits.absorb([ActivityDay(day: day, attention: segments, blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)], entries: [], time: time)
        let shares = [SplitPart(projectId: "atlas", isBillable: false, percent: 50).response, SplitPart(projectId: "beacon", isBillable: false, percent: 50).response]
        var running = TimeEntry(id: "r", workspaceId: "w", description: "", projectId: "atlas", taskId: nil, tagIds: [], isBillable: false,
                                start: at(540), stop: nil, shares: shares, version: "1")
        func drift(_ minutes: Double) -> SplitDrift.Drift? { splits.drift(for: running, time: time, projects: projects, now: at(minutes))?.drift }

        // Split when the timer started, seen running since: Atlas went quiet at 9:20.
        #expect(drift(600) == .quiet(["atlas"], at: at(560)))
        #expect(splits.statement(for: running)?.at == at(540))

        // The split changes at 10:00: judged from then, so nothing yet.
        running.shares = [SplitPart(projectId: "atlas", isBillable: false, percent: 30).response, SplitPart(projectId: "beacon", isBillable: false, percent: 70).response]
        #expect(drift(600) == nil)
        #expect(splits.statement(for: running, now: at(700))?.at == at(600))
        #expect(drift(630) == .quiet(["atlas"], at: at(600)))

        // The notification comes once; the chip stays until "Not Now".
        let offer = try #require(splits.changeOffer(for: running, time: time, projects: projects, now: at(630)))
        splits.markOffered(offer)
        #expect(splits.changeOffer(for: running, time: time, projects: projects, now: at(631)) == nil)
        #expect(drift(631) != nil)
        splits.answer(try #require(splits.drift(for: running, time: time, projects: projects, now: at(631))))
        #expect(drift(632) == nil)

        // Remembered across launches.
        let again = SplitAdvisor(settings: settings, defaults: defaults, launched: at(700))
        #expect(again.statement(for: running, now: at(700))?.answered == true)
    }

    @Test func untrackedTimeIsOfferedAtTheStartInTheReminderAndInTheCalendar() throws {
        let defaults = defaults()
        let settings = DesktopSettings(defaults: defaults, clearActivityCache: {})
        settings.useAgentLogs = true
        settings.setReads(.claude, true)
        let splits = SplitAdvisor(settings: settings, defaults: defaults)
        let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
        let day = DayKey("2026-09-29")!
        func at(_ minutes: Double) -> Date { time.start(of: day).addingTimeInterval(minutes * 60) }
        let projects = [("atlas", "Atlas"), ("beacon", "Beacon")].map { id, name in
            Project(id: id, name: name, clientId: nil, color: "#000000", isPrivate: false, isBillable: false, separateBilledTime: false,
                    estimateSeconds: nil, archivedAt: nil, trackedSeconds: 0, tasks: [], hourlyRate: nil, canManage: true,
                    billingMode: .asTracked, dailyHours: nil)
        }
        let atlas = ActivityRepository(key: "/code/atlas", name: "atlas", origin: nil, isRepository: true)
        let other = ActivityRepository(key: "/code/other", name: "other", origin: nil, isRepository: true)
        // Agents in Atlas 9:12–9:40, in a repository without a project 13:00–13:30.
        let segments = [(atlas, 552.0, 580.0), (other, 780, 810)].map {
            AttentionSegment(repository: $0.0, range: DateInterval(start: at($0.1), end: at($0.2)), sessionIDs: [], prompts: [at($0.1)])
        }
        splits.absorb([ActivityDay(day: day, attention: segments, blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)], entries: [], time: time)
        func entry(_ id: String, _ projectId: String?, _ start: Double, _ stop: Double?) -> TimeEntry {
            TimeEntry(id: id, workspaceId: "w", description: "", projectId: projectId, taskId: nil, tagIds: [], isBillable: false,
                      start: at(start), stop: stop.map(at), shares: [], version: "1")
        }

        // B1: a timer on Beacon started at 9:43 can start at 9:12; the chip names Atlas, and goes after 10 minutes.
        let running = entry("r", "beacon", 583, nil)
        let earlier = try #require(splits.earlierStart(for: running, entries: [running], time: time, projects: projects, now: at(584)))
        #expect(earlier.start == at(552))
        #expect(earlier.otherProjectId == "atlas")
        #expect(earlier.fillsProjectId == nil)
        // A timer without a project gets Atlas along with the start.
        let unnamed = entry("u", nil, 583, nil)
        let filling = try #require(splits.earlierStart(for: unnamed, entries: [unnamed], time: time, projects: projects, now: at(584)))
        #expect(filling.fillsProjectId == "atlas")
        #expect(filling.otherProjectId == nil)
        #expect(splits.earlierStart(for: running, entries: [running], time: time, projects: projects, now: at(594)) == nil)
        // Not before the previous entry's stop, and not into a locked day.
        let before = entry("b", "atlas", 500, 560)
        #expect(splits.earlierStart(for: running, entries: [before, running], time: time, projects: projects, now: at(584))?.start == at(560))
        #expect(splits.earlierStart(for: running, entries: [running], time: time, projects: projects, isLocked: { _ in true }, now: at(584)) == nil)

        // B2: with no timer, the work in Atlas is named after 15 minutes; the repository without a project never.
        #expect(splits.ongoingUntracked(entries: [], time: time, projects: projects, now: at(570))?.projects == ["atlas"])
        #expect(splits.ongoingUntracked(entries: [], time: time, projects: projects, now: at(562)) == nil)
        #expect(splits.ongoingUntracked(entries: [], time: time, projects: projects, now: at(810)) == nil)
        #expect(splits.ongoingUntracked(entries: [running], time: time, projects: projects, now: at(570)) == nil)

        // B3: both stretches show in the calendar until dismissed; an entry covers its time.
        let blocks = splits.untracked(on: [day], entries: [], time: time, projects: projects, now: at(900))
        #expect(blocks.map(\.range) == [DateInterval(start: at(552), end: at(580)), DateInterval(start: at(780), end: at(810))])
        #expect(blocks.map(\.stretch.projects) == [["atlas"], []])
        #expect(splits.untracked(on: [day], entries: [entry("e", "atlas", 540, 600)], time: time, projects: projects, now: at(900)).count == 1)
        #expect(splits.untracked(on: [day], entries: [], time: time, projects: projects, isDayLocked: { _ in true }, now: at(900)).isEmpty)
        splits.dismiss(blocks[0])
        #expect(splits.untracked(on: [day], entries: [], time: time, projects: projects, now: at(900)).map(\.id) == ["\(day)|780"])
        // Remembered across launches.
        #expect(SplitAdvisor(settings: settings, defaults: defaults).dismissedUntracked == ["\(day)|552"])
        // What the logs assume after a prompt inside an entry is not untracked work: 13:00–13:10 tracked, the rest
        // has no prompt of its own.
        #expect(splits.untracked(on: [day], entries: [entry("t", nil, 780, 790)], time: time, projects: projects, now: at(900)).isEmpty)
    }

    @Test func theDaysReviewHasEverySuggestionInOrder() throws {
        let defaults = defaults()
        let settings = DesktopSettings(defaults: defaults, clearActivityCache: {})
        settings.useAgentLogs = true
        settings.setReads(.claude, true)
        let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
        let day = DayKey("2026-09-29")!
        func at(_ minutes: Double) -> Date { time.start(of: day).addingTimeInterval(minutes * 60) }
        let splits = SplitAdvisor(settings: settings, defaults: defaults, launched: at(400))
        let projects = [("atlas", "Atlas"), ("beacon", "Beacon")].map { id, name in
            Project(id: id, name: name, clientId: nil, color: "#000000", isPrivate: false, isBillable: false, separateBilledTime: false,
                    estimateSeconds: nil, archivedAt: nil, trackedSeconds: 0, tasks: [], hourlyRate: nil, canManage: true,
                    billingMode: .asTracked, dailyHours: nil)
        }
        let atlas = ActivityRepository(key: "/code/atlas", name: "atlas", origin: nil, isRepository: true)
        let beacon = ActivityRepository(key: "/code/beacon", name: "beacon", origin: nil, isRepository: true)
        let other = ActivityRepository(key: "/code/other", name: "other", origin: nil, isRepository: true)
        // 9:00–10:40 on Atlas, though agents moved to Beacon at 9:40. A split 11:00–12:30 whose Atlas went quiet at
        // 11:20. Untracked: Atlas 13:00–13:40, a repository without a project 15:00–15:30.
        let spans: [(ActivityRepository, Double, Double)] = [
            (atlas, 540, 580), (beacon, 580, 640), (atlas, 660, 680), (beacon, 680, 750), (atlas, 780, 820), (other, 900, 930),
        ]
        let segments = spans.map {
            AttentionSegment(repository: $0.0, range: DateInterval(start: at($0.1), end: at($0.2)), sessionIDs: [], prompts: [at($0.1), at($0.1 + 5)])
        }
        splits.absorb([ActivityDay(day: day, attention: segments, blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)], entries: [], time: time)
        func entry(_ id: String, _ start: Double, _ stop: Double?, shares: [SplitPart] = []) -> TimeEntry {
            TimeEntry(id: id, workspaceId: "w", description: "", projectId: "atlas", taskId: nil, tagIds: [], isBillable: false,
                      start: at(start), stop: stop.map(at), shares: shares.map(\.response), version: "1")
        }
        let split = [SplitPart(projectId: "atlas", isBillable: false, percent: 50), SplitPart(projectId: "beacon", isBillable: false, percent: 50)]
        // This Mac saw the split entry run from its start.
        _ = splits.statement(for: entry("s", 660, nil, shares: split), now: at(661))
        let entries = [entry("a", 540, 640), entry("s", 660, 750, shares: split), entry("r", 960, nil)]
        func review(locked: Bool = false) -> [ReviewItem] {
            splits.review(on: [day], entries: entries, time: time, projects: projects, isDayLocked: { _ in locked }, now: at(1000))
        }

        let items = review()
        #expect(items.map(\.id) == ["split|a", "drift|s|\(SplitAdvisor.partsKey(split))", "untracked|\(day)|780", "untracked|\(day)|900"])
        #expect(items.map(\.isTakeable) == [true, true, true, false])
        #expect(ReviewItem.breakdown(items) == "1 split, 1 split change, 2 untracked stretches")
        #expect(items.map(\.range.start) == [at(540), at(660), at(780), at(900)])
        // A locked day has nothing to review.
        #expect(review(locked: true).isEmpty)

        // "Not Now" leaves the review, and is remembered; the chip's suggestion stays.
        splits.putOff(items[0])
        #expect(review().count == 3)
        #expect(SplitAdvisor(settings: settings, defaults: defaults).putOff == [items[0].id])
        #expect(splits.suggestion(for: entries[0], time: time, projects: projects) != nil)
        // Its Undo brings it back, on this Mac too.
        splits.bringBack(items[0])
        #expect(review().count == 4)
        #expect(SplitAdvisor(settings: settings, defaults: defaults).putOff.isEmpty)
    }

    @Test func eachItemOfTheReviewSaysWhatTheTimeBecomes() throws {
        let defaults = defaults()
        let settings = DesktopSettings(defaults: defaults, clearActivityCache: {})
        settings.useAgentLogs = true
        settings.setReads(.claude, true)
        let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
        let day = DayKey("2026-09-29")!
        func at(_ minutes: Double) -> Date { time.start(of: day).addingTimeInterval(minutes * 60) }
        let splits = SplitAdvisor(settings: settings, defaults: defaults, launched: at(400))
        let projects = [("atlas", "Atlas"), ("beacon", "Beacon")].map { id, name in
            Project(id: id, name: name, clientId: nil, color: "#000000", isPrivate: false, isBillable: false, separateBilledTime: false,
                    estimateSeconds: nil, archivedAt: nil, trackedSeconds: 0, tasks: [], hourlyRate: nil, canManage: true,
                    billingMode: .asTracked, dailyHours: nil)
        }
        let atlas = ActivityRepository(key: "/code/atlas", name: "atlas", origin: nil, isRepository: true)
        let beacon = ActivityRepository(key: "/code/beacon", name: "beacon", origin: nil, isRepository: true)
        let other = ActivityRepository(key: "/code/other", name: "other", origin: nil, isRepository: true)
        let spans: [(ActivityRepository, Double, Double)] = [
            (atlas, 540, 580), (beacon, 580, 640), (atlas, 660, 680), (beacon, 680, 750), (atlas, 780, 820), (other, 900, 930),
        ]
        let segments = spans.map {
            AttentionSegment(repository: $0.0, range: DateInterval(start: at($0.1), end: at($0.2)), sessionIDs: [], prompts: [at($0.1), at($0.1 + 5)])
        }
        splits.absorb([ActivityDay(day: day, attention: segments, blocks: [], runs: [], sessions: [], totals: [:], parallel: 0)], entries: [], time: time)
        func entry(_ id: String, _ start: Double, _ stop: Double?, shares: [SplitPart] = []) -> TimeEntry {
            TimeEntry(id: id, workspaceId: "w", description: "", projectId: "atlas", taskId: nil, tagIds: [], isBillable: false,
                      start: at(start), stop: stop.map(at), shares: shares.map(\.response), version: "1")
        }
        let split = [SplitPart(projectId: "atlas", isBillable: false, percent: 50), SplitPart(projectId: "beacon", isBillable: false, percent: 50)]
        _ = splits.statement(for: entry("s", 660, nil, shares: split), now: at(661))
        let entries = [entry("a", 540, 640), entry("s", 660, 750, shares: split)]
        let items = splits.review(on: [day], entries: entries, time: time, projects: projects, now: at(1000))
        func read(_ results: [ReviewResult]) -> [String] {
            results.map { result in
                let pieces = result.pieces.map { piece -> String in
                    let name = switch piece.target {
                    case .project(let id)?: id
                    case .repository(let repository)?: repository.name
                    case nil: "none"
                    }
                    return "\(name) \(Int(piece.seconds / 60))"
                }
                return "\(time.clock(result.range.start))–\(time.clock(result.range.end)) " + pieces.joined(separator: " · ")
            }
        }

        // Separate entries where the work moved: Atlas until 9:40, then Beacon.
        #expect(read(items[0].results()) == ["09:00–09:40 atlas 40", "09:40–10:40 beacon 60"])
        // The split ends where Atlas went quiet, and Beacon carries on.
        #expect(read(items[1].results()) == ["11:00–11:20 atlas 10 · beacon 10", "11:20–12:30 beacon 70"])
        // Untracked time becomes a new entry on its project, or with none while only in repositories without one.
        #expect(read(items[2].results()) == ["13:00–13:40 atlas 40"])
        #expect(read(items[3].results()) == ["15:00–15:30 none 30"])
    }

    @Test func aReviewCoversADayAWeekOrTheLastSevenDays() {
        let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)
        let thursday = DayKey("2026-10-15")!
        #expect(ReviewScope.day(thursday).days == [thursday])
        #expect(!ReviewScope.day(thursday).isWeek)
        #expect(ReviewScope.week(of: thursday, time: time).days.count == 7)
        #expect(ReviewScope.lastSevenDays(to: thursday, time: time).days == (0..<7).reversed().map { time.adding(-$0, to: thursday) })
    }
}
