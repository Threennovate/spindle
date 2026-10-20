import XCTest

extension XCTestCase {
    /// Signs up a new account through the API, then launches the app signed in to it, with the sign-in in memory.
    @MainActor
    func signedInApp() async throws -> XCUIApplication {
        let email = "ui-\(UUID().uuidString.prefix(8).lowercased())@example.com"
        let password = "correct horse battery"
        var request = URLRequest(url: URL(string: "http://127.0.0.1:5258/api/auth/register")!)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "email": email, "password": password, "fullName": "UI Test", "timeZone": TimeZone.current.identifier,
        ])
        let (_, response) = try await URLSession.shared.data(for: request)
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 200, "Signing up through the API failed")

        let app = XCUIApplication()
        app.launchEnvironment["SPINDLE_AUTOMATION_EMAIL"] = email
        app.launchEnvironment["SPINDLE_AUTOMATION_PASSWORD"] = password
        app.launch()
        XCTAssertTrue(app.tabBars.buttons["Calendar"].waitForExistence(timeout: 15), "The app didn't sign in")
        return app
    }
}
