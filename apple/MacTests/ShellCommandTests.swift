import Foundation
import Testing
@testable import Spindle

struct ShellCommandTests {
    @Test func aShellThatIgnoresTerminationStillStopsAtItsTimeLimit() async {
        let started = Date()
        let result = await ShellCommand("trap '' TERM; echo started; sleep 20").run(timeout: 1)
        #expect(result.timedOut)
        #expect(result.output.contains("started"))
        #expect(Date().timeIntervalSince(started) < 8)
    }

    @Test func aCommandThatEndsOnItsOwnSaysWhatItPrinted() async {
        let result = await ShellCommand("exec echo hello").run(timeout: 10)
        #expect(result.status == 0)
        #expect(!result.timedOut)
        #expect(result.output.contains("hello"))
    }
}
