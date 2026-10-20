import Foundation
import Testing
@testable import SpindleKit

/// The cases the web app's vitest runs too (`tests/vectors/entry-sentence.json`), so a line means the same on both.
@Suite struct EntrySentenceTests {
    struct Vectors: Decodable {
        let now: String
        let timeZone: String
        let cases: [Vector]
    }

    struct Vector: Decodable, CustomTestStringConvertible {
        struct Chip: Decodable {
            let kind: String
            let projectId: String?
            let taskId: String?
            let clientId: String?
            let tagId: String?
        }

        struct Options: Decodable {
            let hour12: Bool?
            let dateFormat: String?
        }

        let text: String
        let chips: [Chip]?
        let options: Options?
        let dayEnds: [String: [String]]?
        let description: String
        let projectId: String?
        let taskId: String?
        let clientId: String?
        let tagIds: [String]?
        let billable: Bool?
        let start: String?
        let stop: String?
        let timeText: String

        var testDescription: String { options.map { "\(text) (\($0.hour12 == true ? "12h" : "24h") \($0.dateFormat ?? ""))" } ?? text }
    }

    static let vectors: Vectors = {
        // tests/vectors at the repository's root, from apple/SpindleKit/Tests/SpindleKitTests.
        let url = URL(filePath: #filePath).deletingLastPathComponent()
            .appending(path: "../../../../tests/vectors/entry-sentence.json").standardized
        return try! JSONDecoder().decode(Vectors.self, from: Data(contentsOf: url))
    }()

    static let time = UserTime(timeZone: TimeZone(identifier: vectors.timeZone)!)

    /// A wall-clock time in the vectors' time zone.
    static func local(_ text: String) -> Date {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = time.timeZone
        formatter.dateFormat = "yyyy-MM-dd'T'HH:mm:ss"
        return formatter.date(from: text)!
    }

    static func wallClock(_ instant: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = time.timeZone
        formatter.dateFormat = "yyyy-MM-dd'T'HH:mm:ss"
        return formatter.string(from: instant)
    }

    @Test(arguments: vectors.cases)
    func readsTheSharedVectors(_ vector: Vector) {
        let chips: [EntrySentence.Chip] = (vector.chips ?? []).map { chip in
            switch chip.kind {
            case "project": .project(projectID: chip.projectId!, taskID: chip.taskId)
            case "client": .client(chip.clientId!)
            case "tag": .tag(chip.tagId!)
            default: .billable
            }
        }
        let options = EntrySentence.Options(hour12: vector.options?.hour12 ?? false, dateFormat: vector.options?.dateFormat ?? "dd.MM.yyyy")
        let reading = EntrySentence.read(vector.text, chips: chips, options: options)

        #expect(reading.description == vector.description)
        #expect(reading.projectID == vector.projectId)
        #expect(reading.taskID == vector.taskId)
        #expect(reading.clientID == vector.clientId)
        #expect(reading.tagIDs == (vector.tagIds ?? []))
        #expect(reading.billable == (vector.billable ?? false))
        #expect(reading.timeText == vector.timeText)

        let now = Self.local(Self.vectors.now)
        let time = UserTime(timeZone: Self.time.timeZone, hour12: options.hour12)
        let span = EntrySentence.resolve(reading.time, now: now, in: time) { day in
            (vector.dayEnds?[day.description] ?? []).map(Self.local)
        }
        #expect(span.start == (vector.start.map(Self.local) ?? now))
        #expect(span.stop.map(Self.wallClock) == vector.stop)
    }

    /// A duration too long to count is no duration, and the line still reads.
    @Test(arguments: ["Review for 99999999999999999999m", "Review for 99999999999999999999h", "Review 99999999999999999999m ago"])
    func readsALineWithADurationTooLongToCount(text: String) {
        let reading = EntrySentence.read(text, chips: [], options: EntrySentence.Options())
        #expect(reading.timeText.isEmpty)
    }

    @Test func findsTheMentionAtTheCaret() {
        #expect(EntrySentence.mention(in: "Review @ac", caret: 10) == .init(sign: "@", query: "ac", start: 7, end: 10))
        #expect(EntrySentence.mention(in: "Review #rev now", caret: 11) == .init(sign: "#", query: "rev", start: 7, end: 11))
        #expect(EntrySentence.mention(in: "Review @", caret: 8) == .init(sign: "@", query: "", start: 7, end: 8))
        #expect(EntrySentence.mention(in: "mail ana@x.com", caret: 14) == nil)
        #expect(EntrySentence.mention(in: "Fix #482 later", caret: 14) == nil)
    }

    @Test func takesTheMentionOutLeavingOneSpace() {
        let mention = EntrySentence.Mention(sign: "@", query: "ac", start: 7, end: 10)
        #expect(EntrySentence.removing(mention, from: "Review @ac") == ("Review ", 7))
        #expect(EntrySentence.removing(mention, from: "Review @ac since 9") == ("Review since 9", 7))
        #expect(EntrySentence.removing(.init(sign: "@", query: "ac", start: 0, end: 3), from: "@ac Review") == ("Review", 0))
    }
}
