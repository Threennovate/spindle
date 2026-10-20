import Foundation
import Testing
@testable import SpindleKit

struct UntrackedActivityTests {
    let atlas = ActivityRepository(key: "/repos/atlas", name: "atlas", identity: "atlas")
    let beacon = ActivityRepository(key: "/repos/beacon", name: "beacon", identity: "beacon")
    let day = Date(timeIntervalSince1970: 1_800_000_000)

    func at(_ hours: Double) -> Date { day.addingTimeInterval(hours * 3600) }
    func span(_ from: Double, _ to: Double) -> DateInterval { DateInterval(start: at(from), end: at(to)) }
    func segment(_ repository: ActivityRepository, _ from: Double, _ to: Double) -> AttentionSegment {
        AttentionSegment(repository: repository, range: span(from, to), sessionIDs: ["s"])
    }

    @Test func leavesWhatEntriesCoverAndJoinsCloseStretches() {
        let attention = [segment(atlas, 9, 10), segment(atlas, 10.1, 11), segment(beacon, 13, 14), segment(beacon, 15, 15.05)]
        // An entry 9:30–10:30 and a waiting proposal 13:30–14:30.
        let found = UntrackedActivity.stretches(attention: attention, covered: [span(9.5, 10.5), span(13.5, 14.5)],
                                                within: span(0, 24), now: at(24))
        // 9:00–9:30 and 10:30–11:00 are 60 minutes apart: two stretches. 13:00–13:30 stays; the 3 minutes at 15:00
        // are too short.
        #expect(found == [span(9, 9.5), span(10.5, 11), span(13, 13.5)])
    }

    @Test func joinsStretchesWithinTwentyMinutes() {
        let found = UntrackedActivity.stretches(attention: [segment(atlas, 9, 9.5), segment(beacon, 9.75, 10)], covered: [],
                                                within: span(0, 24), now: at(24))
        #expect(found == [span(9, 10)])
    }

    @Test func stopsAtNowAndAtTheDaysAskedFor() {
        let found = UntrackedActivity.stretches(attention: [segment(atlas, -1, 1), segment(atlas, 10, 12)], covered: [],
                                                within: span(0, 24), now: at(11))
        #expect(found == [span(0, 1), span(10, 11)])
    }

    @Test func nothingWhenEverythingIsCovered() {
        let found = UntrackedActivity.stretches(attention: [segment(atlas, 9, 11)], covered: [span(8, 12)], within: span(0, 24), now: at(24))
        #expect(found.isEmpty)
    }

    @Test func runsShowWhereAStretchWentInOrder() {
        let attention = [segment(atlas, 9, 9.5), segment(beacon, 9.25, 10), segment(atlas, 9.55, 10.5), segment(atlas, 12, 13)]
        let runs = UntrackedActivity.runs(attention: attention, covered: [span(10.25, 11)], within: span(9, 10.5))
        #expect(runs.map(\.repository.name) == ["atlas", "beacon"])
        // Atlas's 3-minute pause doesn't split it; the entry from 10:15 cuts it.
        #expect(runs.map(\.range) == [span(9, 10.25), span(9.25, 10)])
    }

    func project(_ repository: ActivityRepository) -> String? { repository == atlas ? "p-atlas" : nil }

    @Test func stretchesNameTheirProjectsMostAttentionFirst() {
        let attention = [segment(atlas, 9, 9.25), segment(beacon, 9.25, 9.75), segment(atlas, 9.75, 10)]
        let found = UntrackedActivity.stretches(attention: attention, covered: [], within: span(0, 24), now: at(24), project: project)
        #expect(found == [UntrackedActivity.Stretch(range: span(9, 10), projects: ["p-atlas"], unlinked: 1800)])
    }

    @Test func aTimerCanStartWhereTheAgentsDidUpToThePreviousStop() throws {
        // Untracked attention 9:12–9:40, the timer started at 9:43.
        let attention = [segment(atlas, 9.2, 9 + 40 / 60.0)]
        let start = at(9 + 43 / 60.0)
        let earlier = try #require(UntrackedActivity.earlierStart(before: start, attention: attention, covered: [], notBefore: at(0), project: project))
        #expect(earlier.range.start == at(9.2))
        #expect(earlier.projects == ["p-atlas"])
        // Not before the previous entry, which stopped at 9:30.
        let after = try #require(UntrackedActivity.earlierStart(before: start, attention: attention, covered: [span(8, 9.5)], notBefore: at(9.5),
                                                                 project: project))
        #expect(after.range.start == at(9.5))
    }

    @Test func aTimerStartedLongAfterTheAgentsStoppedKeepsItsStart() {
        // The attention ended at 9:40; the timer started at 9:50.
        let attention = [segment(atlas, 9.2, 9 + 40 / 60.0)]
        #expect(UntrackedActivity.earlierStart(before: at(9 + 50 / 60.0), attention: attention, covered: [], notBefore: at(0), project: project) == nil)
        // Nothing before the floor, e.g. midnight.
        #expect(UntrackedActivity.earlierStart(before: at(0.05), attention: [segment(atlas, -1, 0.02)], covered: [], notBefore: at(0),
                                                project: project) == nil)
    }

    @Test func ongoingWorkNamesItsProjectAfterFifteenMinutes() throws {
        let attention = [segment(beacon, 9, 9.2), segment(atlas, 9.2, 9.4)]
        // Atlas has had 12 minutes by 9:24: not yet.
        #expect(UntrackedActivity.ongoing(attention: attention, covered: [], notBefore: at(0), now: at(9.4), project: project) == nil)
        let later = attention + [segment(atlas, 9.4, 9.5)]
        let ongoing = try #require(UntrackedActivity.ongoing(attention: later, covered: [], notBefore: at(0), now: at(9.5), project: project))
        #expect(ongoing.projects == ["p-atlas"])
        #expect(ongoing.range.start == at(9))
        // Over once the agents stopped a while ago.
        #expect(UntrackedActivity.ongoing(attention: later, covered: [], notBefore: at(0), now: at(9.7), project: project) == nil)
        // Repositories without a project never count.
        #expect(UntrackedActivity.ongoing(attention: [segment(beacon, 9, 10)], covered: [], notBefore: at(0), now: at(10), project: project) == nil)
    }
}
