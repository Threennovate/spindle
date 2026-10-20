import Foundation
import Testing
@testable import SpindleKit

/// The cases the Windows app runs too (`tests/vectors/agent-logs.json`), so both read each agent's logs the same way.
struct AgentLogVectorTests {
    struct Vectors: Decodable {
        let secret: String
        let cases: [Case]
        let discovery: [Path]
    }

    struct Path: Decodable, CustomTestStringConvertible {
        let source: AgentSource
        let path: String
        let log: Bool
        let session: String?
        var testDescription: String { source.rawValue + " " + path }
    }

    struct Case: Decodable, CustomTestStringConvertible {
        let name: String
        let source: AgentSource
        let lines: [String]
        let sessions: [Expected]
        var testDescription: String { name }
    }

    struct Expected: Decodable {
        let id: String
        let parent: String?
        let cwd: String
        let title: String
        let branches: [String]
        let touches: [String]
        let events: Int
    }

    static let vectors: Vectors = {
        let url = URL(filePath: #filePath).deletingLastPathComponent()
            .appending(path: "../../../../tests/vectors/agent-logs.json").standardized
        return try! JSONDecoder().decode(Vectors.self, from: Data(contentsOf: url))
    }()

    @Test(arguments: vectors.cases, [true, false])
    func readsTheLogs(_ vector: Case, quickly: Bool) throws {
        var parser = AgentLogParser(source: vector.source)
        parser.readsQuickly = quickly
        for line in vector.lines { parser.consume(Data(line.utf8)) }
        let sessions = parser.finish()
        #expect(sessions.map(\.id) == vector.sessions.map(\.id))
        for (session, expected) in zip(sessions, vector.sessions) {
            #expect(session.source == vector.source)
            #expect(session.parentID == expected.parent)
            #expect(session.cwd == expected.cwd)
            #expect(session.title == expected.title)
            #expect(session.branches.sorted() == expected.branches)
            #expect(session.events.filter(\.isTouch).map(\.date) == expected.touches.map { try! Date($0, strategy: .iso8601) })
            #expect(session.events.count == expected.events)
        }
        // What the ledger keeps, too: the parser's whole state.
        let kept = String(decoding: try JSONEncoder().encode(parser), as: UTF8.self)
            + String(decoding: try JSONEncoder().encode(sessions), as: UTF8.self)
        #expect(!kept.contains(Self.vectors.secret))
    }

    @Test(arguments: vectors.discovery)
    func findsTheLogs(_ vector: Path) {
        let format = vector.source.format
        #expect(format.isLog(Substring(vector.path)) == vector.log)
        if let session = vector.session { #expect(format.session(Substring(vector.path)) == session) }
    }
}
