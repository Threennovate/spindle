import XCTest

/// Drives the app in the simulator against the API on this Mac: signs up a new account each run, so it leaves the
/// other accounts' time alone.
final class CalendarUITests: XCTestCase {
    override func setUp() {
        continueAfterFailure = false
    }

    @MainActor
    func testDraggingAnEntryOnTheCalendarMovesIt() async throws {
        let app = try await signedInApp()

        // An hour-long entry at the quarter hour now, added from the calendar.
        app.tabBars.buttons["Calendar"].tap()
        app.buttons["Add a time entry"].tap()
        app.textFields["Description"].tap()
        app.typeText("Drag me")
        app.navigationBars.buttons["Add"].tap()

        let before = try entryStart(app)
        // Two hours down if the day leaves room below, otherwise two hours up.
        let hours = before.hour < 20 ? 2 : -2

        app.tabBars.buttons["Calendar"].tap()
        let block = app.descendants(matching: .any).matching(NSPredicate(format: "label BEGINSWITH 'Drag me'")).firstMatch
        XCTAssertTrue(block.waitForExistence(timeout: 5))
        let grip = block.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.3))
        // 56 points an hour.
        grip.press(forDuration: 0.8, thenDragTo: grip.withOffset(CGVector(dx: 0, dy: CGFloat(56 * hours))))

        let screen = app.debugDescription
        let after = try entryStart(app)
        XCTAssertEqual(after.hour * 60 + after.minute, (before.hour + hours) * 60 + before.minute, "The entry should move by \(hours) hours. After the drag:\n\(screen)")
    }

    @MainActor
    private func entryStart(_ app: XCUIApplication) throws -> (hour: Int, minute: Int) {
        app.tabBars.buttons["Timer"].tap()
        let row = app.descendants(matching: .any).matching(NSPredicate(format: "label BEGINSWITH 'Drag me'")).firstMatch
        XCTAssertTrue(row.waitForExistence(timeout: 5))
        let match = try XCTUnwrap(row.label.firstMatch(of: /(\d{1,2}):(\d{2}) –/), "No time in “\(row.label)”")
        return (Int(match.1)!, Int(match.2)!)
    }
}
