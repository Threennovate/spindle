import Foundation
import Testing
@testable import SpindleKit

/// What saving a proposal sends, which the review does for the proposals the server lists.
struct AgentProposalTests {
    let time = UserTime(timeZone: TimeZone(identifier: "Europe/Zagreb")!)

    func at(_ hours: Int) -> Date {
        time.date(DayKey(year: 2026, month: 10, day: 12), hours: hours, minutes: 0)
    }

    func entry(_ description: String = "", project: String? = "p1", split: Bool = false) -> TimeEntry {
        TimeEntry(
            id: "e", workspaceId: "w", description: description, projectId: project, taskId: nil, tagIds: ["tag"], isBillable: true,
            start: at(9), stop: at(10),
            shares: split ? [.init(projectId: "p1", taskId: nil, isBillable: true, percent: 60), .init(projectId: "p2", taskId: nil, isBillable: false, percent: 40)] : [], version: "1")
    }

    func proposal(entryId: String? = "e", _ fields: AgentProposal.Fields) -> AgentProposal {
        AgentProposal(workspaceId: "w", entryId: entryId, original: nil, fields: fields, reason: "", client: "Claude Code", proposedAt: at(12))
    }

    @Test func aChangeAppliesToTheEntryAsItIsNow() {
        let proposed = proposal(.init(work: .init(projectId: "p2", taskId: nil, isBillable: false)))
        var now = entry("Old words")
        now.description = "Written since"
        let request = proposed.request(current: now)
        #expect(request.details.description == "Written since")
        #expect(request.details.projectId == "p2")
        #expect(request.details.isBillable == false)
        #expect(request.details.tagIds == ["tag"])
        #expect(request.start == at(9))
        #expect(request.stop == at(10))
    }

    @Test func aNewEntryIsWhatWasProposed() {
        let proposed = proposal(entryId: nil, .init(start: at(9), stop: at(10), description: "Fix login",
                                                   work: .init(projectId: "p1", taskId: "t1", isBillable: true)))
        #expect(proposed.isNew)
        let request = proposed.request(current: nil)
        #expect(request.start == at(9))
        #expect(request.stop == at(10))
        #expect(request.details.description == "Fix login")
        #expect(request.details.projectId == "p1")
        #expect(request.details.taskId == "t1")
    }

    @Test func aProposedSplitCanDescribeEachProject() {
        let parts = [SplitPart(projectId: "p1", taskId: nil, isBillable: true, percent: 60, description: "Fix login"),
                     SplitPart(projectId: "p2", taskId: nil, isBillable: false, percent: 40)]
        let proposed = proposal(entryId: nil, .init(start: at(9), stop: at(10), work: .init(projectId: "p1", taskId: nil, isBillable: true, shares: parts)))
        let request = proposed.request(current: nil)
        #expect(request.describeParts == true)
        #expect(request.details.shares.map(\.description) == ["Fix login", ""])
        #expect(request.details.description == "Fix login")
    }

    @Test func aChangedSplitKeepsItsPartsDescriptionsUnlessOneDescriptionReplacesThem() {
        var described = entry("Fix login; Docs", split: true)
        described.shares[0].description = "Fix login"
        described.shares[1].description = "Docs"

        let reshared = proposal(.init(work: .init(projectId: "p1", taskId: nil, isBillable: true, shares: [
            SplitPart(projectId: "p1", taskId: nil, isBillable: true, percent: 70, description: "Fix login"),
            SplitPart(projectId: "p2", taskId: nil, isBillable: false, percent: 30, description: "Docs"),
        ]))).request(current: described)
        #expect(reshared.details.shares.map(\.percent) == [70, 30])
        #expect(reshared.details.shares.map(\.description) == ["Fix login", "Docs"])
        #expect(reshared.describeParts == true)

        let shared = proposal(.init(description: "Fix login; Docs")).request(current: described)
        #expect(shared.describeParts == false)
        #expect(shared.details.description == "Fix login; Docs")
        #expect(shared.details.shares.allSatisfy { $0.description == nil })

        let newWords = proposal(.init(work: .init(projectId: "p1", taskId: nil, isBillable: true, shares: [
            SplitPart(projectId: "p1", taskId: nil, isBillable: true, percent: 60, description: "Fix the login"),
            SplitPart(projectId: "p2", taskId: nil, isBillable: false, percent: 40, description: "Docs"),
        ]))).request(current: described)
        #expect(newWords.details.description == "Fix the login; Docs")
    }
}
