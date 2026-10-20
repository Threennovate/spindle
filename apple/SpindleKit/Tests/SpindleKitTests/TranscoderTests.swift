import Foundation
import Testing
@testable import SpindleKit

@Suite struct TranscoderTests {
    @Test(arguments: [
        "2026-09-29T07:00:00Z",
        "2026-09-29T07:00:00+00:00",
        "2026-09-29T09:00:00+02:00",
        "2026-09-29T07:00:00.123Z",
        "2026-09-29T07:00:00.1234567+00:00",
    ])
    func readsDatesTheWayDotNetWritesThem(text: String) throws {
        let date = try LenientISO8601DateTranscoder().decode(text)
        #expect(abs(date.timeIntervalSince1970 - 1_790_665_200) < 1)
    }

    @Test func writesUTCWithMilliseconds() throws {
        let text = try LenientISO8601DateTranscoder().encode(Date(timeIntervalSince1970: 1_790_665_200.25))
        #expect(text == "2026-09-29T07:00:00.250Z")
    }
}
