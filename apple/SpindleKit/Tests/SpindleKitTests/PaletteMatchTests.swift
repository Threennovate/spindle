import Foundation
import Testing
@testable import SpindleKit

/// The same cases as the web palette's `match.test.ts` and `usage.test.ts`.
@Suite struct PaletteMatchTests {
    private func ranges(_ match: PaletteMatch.Match?) -> [[Int]] {
        match?.ranges.map { [$0.start, $0.end] } ?? []
    }

    @Test func foldsAccentsAndKeepsTheLength() {
        #expect(String(PaletteMatch.fold("Čakovec Đurđa")) == "cakovec durda")
        #expect(PaletteMatch.fold("Čakovec Đurđa").count == "Čakovec Đurđa".count)
    }

    @Test func ranksTheTiersInOrder() {
        let scores = [
            PaletteMatch.label("Reports", query: "reports"),
            PaletteMatch.label("Reports overview", query: "rep"),
            PaletteMatch.label("Go to Reports", query: "rep"),
            PaletteMatch.label("Go to Reports", query: "gtr"),
            PaletteMatch.label("Preports", query: "rep"),
        ].map { $0!.score }
        #expect(scores == scores.sorted(by: >))
        #expect(Set(scores).count == 5)
    }

    @Test func findsEveryTypedWordInAnyOrder() {
        #expect(PaletteMatch.label("Website redesign", query: "red web") != nil)
        #expect(PaletteMatch.label("Website redesign", query: "red app") == nil)
    }

    @Test func ignoresAccentsAndCase() {
        #expect(ranges(PaletteMatch.label("Čakovec", query: "CAK")) == [[0, 3]])
    }

    @Test func readsALowerToUpperStepAsANewWord() {
        #expect(ranges(PaletteMatch.label("TigerTrail", query: "tr")) == [[5, 7]])
    }

    @Test func mergesTheRangesToHighlight() {
        #expect(ranges(PaletteMatch.label("Go to Reports", query: "gtr")) == [[0, 1], [3, 4], [6, 7]])
        #expect(ranges(PaletteMatch.label("Design review", query: "des rev")) == [[0, 3], [7, 10]])
    }

    @Test func readsALabelEndingInAnEllipsisAsWhole() {
        #expect(PaletteMatch.label("Tags…", query: "tags")?.score == PaletteMatch.label("Tags", query: "tags")?.score)
    }

    @Test func matchesEverythingForAnEmptyQuery() {
        let match = PaletteMatch.label("Anything", query: "  ")
        #expect(match?.score == 0 && match?.ranges.isEmpty == true)
    }

    @Test func findsAnItemByAKeywordALittleBelowItsLabel() {
        let byKeyword = PaletteMatch.item("Stop timer", keywords: ["end", "finish"], query: "finish")
        let byLabel = PaletteMatch.item("Finish", query: "finish")
        #expect(byKeyword != nil && byKeyword!.ranges.isEmpty)
        #expect(byKeyword!.score < byLabel!.score)
    }

    @Test func findsWordsSpreadOverTheLabelAndItsKeywords() {
        let match = PaletteMatch.item("Acme website", keywords: ["Start timer"], query: "start acme")
        #expect(ranges(match) == [[0, 4]])
    }

    @Test func ranksBetterMatchesThenMoreUsedRows() {
        let rows = ["Beacon", "Acme website", "Acme"]
        let ranked = PaletteMatch.rank(rows, query: "acme", label: { $0 }, usage: { $0 == "Acme website" ? 1 : 0 })
        #expect(ranked.map(\.row) == ["Acme", "Acme website"])
    }

    // MARK: - Usage

    private let now = Date(timeIntervalSince1970: 1_791_201_600)
    private let day: TimeInterval = 86_400

    @Test func countsUsesAndRemembersTheLast() {
        var usage = PaletteUsage()
        usage.record("a", now: now - day)
        usage.record("a", now: now)
        #expect(usage.uses["a"] == .init(count: 2, last: now))
    }

    @Test func scoresFrequentAndRecentUseHigherAndLetsOldUseFade() {
        let usage = PaletteUsage(uses: [
            "once": .init(count: 1, last: now),
            "often": .init(count: 8, last: now),
            "longAgo": .init(count: 8, last: now - 90 * day),
        ])
        #expect(usage.score("often", now: now) > usage.score("once", now: now))
        #expect(usage.score("longAgo", now: now) < usage.score("once", now: now))
        #expect(usage.score("often", now: now) < 1)
        #expect(usage.score("never", now: now) == 0)
    }

    @Test func keepsOnlyTheMostRecentlyUsedItems() {
        var usage = PaletteUsage()
        for i in 0..<130 {
            usage.record("item-\(i)", now: now + TimeInterval(i))
        }
        #expect(usage.uses.count == 120)
        #expect(usage.uses["item-0"] == nil)
        #expect(usage.uses["item-129"] != nil)
    }

    @Test func listsTheMostUsedLeavingOutWhatWasBarelyUsed() {
        let usage = PaletteUsage(uses: [
            "reports": .init(count: 6, last: now),
            "timer": .init(count: 3, last: now),
            "once": .init(count: 1, last: now - 60 * day),
        ])
        #expect(usage.mostUsed(now: now, limit: 5) == ["reports", "timer"])
    }

    @Test func keepsUsagePerWorkspace() {
        let defaults = UserDefaults(suiteName: "PaletteMatchTests.\(UUID())")!
        let workspace = UUID().uuidString
        var usage = PaletteUsage()
        usage.record("project:atlas", now: now)
        usage.save(workspaceID: workspace, to: defaults)
        #expect(PaletteUsage.load(workspaceID: workspace, from: defaults) == usage)
        #expect(PaletteUsage.load(workspaceID: "another", from: defaults).uses.isEmpty)
    }
}
