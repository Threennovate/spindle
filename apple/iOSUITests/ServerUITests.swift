import XCTest

/// Choosing your own server on the sign-in screen and going back to Spindle Cloud, against the API on this Mac.
final class ServerUITests: XCTestCase {
    override func setUp() {
        continueAfterFailure = false
    }

    @MainActor
    func testChoosingOwnServerAndGoingBackToCloud() {
        let app = XCUIApplication()
        app.launchEnvironment["SPINDLE_AUTOMATION_AUTH"] = "signIn"
        app.launch()
        // A run that stopped halfway may have left its own server chosen.
        if !app.staticTexts["Spindle Cloud"].waitForExistence(timeout: 10) {
            app.buttons["Change server"].tap()
            app.buttons["Use Spindle Cloud"].tap()
        }
        XCTAssertTrue(app.staticTexts["Spindle Cloud"].waitForExistence(timeout: 5), "Sign-in should start on Spindle Cloud")

        app.buttons["Change server"].tap()
        let address = app.textFields.firstMatch
        XCTAssertTrue(address.waitForExistence(timeout: 5))
        XCTAssertFalse(app.buttons["Use Spindle Cloud"].exists, "Spindle Cloud is already in use")

        // Plain http only on the local network.
        address.tap()
        address.typeText("http://spindle.example.com")
        app.buttons["Use this server"].tap()
        XCTAssertTrue(app.staticTexts["Error: Use an https address. Only a server on the local network can be plain http."].waitForExistence(timeout: 5))

        // The API on this Mac, under another address than the debug build's Spindle Cloud. The field's far end puts
        // the cursor after what was typed.
        address.coordinate(withNormalizedOffset: CGVector(dx: 0.95, dy: 0.5)).tap()
        address.typeText(String(repeating: XCUIKeyboardKey.delete.rawValue, count: "http://spindle.example.com".count))
        address.typeText("http://127.0.0.1:5258")
        app.buttons["Use this server"].tap()
        XCTAssertTrue(app.staticTexts["127.0.0.1:5258"].waitForExistence(timeout: 10), "Sign-in should show the chosen server")
        XCTAssertTrue(app.buttons["Sign in"].exists)
        XCTAssertFalse(app.buttons["Sign in with a passkey"].exists)

        app.buttons["Change server"].tap()
        XCTAssertEqual(app.textFields.firstMatch.value as? String, "127.0.0.1:5258")
        app.buttons["Use Spindle Cloud"].tap()
        XCTAssertTrue(app.staticTexts["Spindle Cloud"].waitForExistence(timeout: 5), "Sign-in should be back on Spindle Cloud")
    }
}
