import XCTest

/// Creating a project and a task on the phone, on a new account on the local API.
final class ManageUITests: XCTestCase {
    override func setUp() {
        continueAfterFailure = false
    }

    @MainActor
    func testCreatingAProjectAndATask() async throws {
        let app = try await signedInApp()

        app.tabBars.buttons["Projects"].tap()
        app.buttons["New project"].tap()
        app.textFields["Name"].tap()
        app.typeText("Launch site")
        // Out from under the keyboard, then the switch itself, at the row's trailing end; the middle of the row
        // doesn't flip it.
        app.typeText("\n")
        let billable = app.switches["Billable"]
        for _ in 0..<3 where !billable.isHittable {
            app.swipeUp()
        }
        billable.coordinate(withNormalizedOffset: CGVector(dx: 0.93, dy: 0.5)).tap()
        XCTAssertEqual(billable.value as? String, "1", "Billable should be on")
        app.navigationBars.buttons["Create"].tap()

        let row = app.buttons.matching(NSPredicate(format: "label BEGINSWITH 'Launch site'")).firstMatch
        XCTAssertTrue(row.waitForExistence(timeout: 5), "The new project isn't in the list")
        XCTAssertTrue(row.label.contains("Billable"), "The project should be billable: “\(row.label)”")
        row.tap()

        app.textFields["Add a task"].tap()
        app.typeText("Copywriting")
        app.buttons["Add"].tap()
        XCTAssertTrue(app.staticTexts["Copywriting"].waitForExistence(timeout: 5), "The new task isn't on the project")
    }
}
