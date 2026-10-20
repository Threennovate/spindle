import Foundation
import Testing
@testable import Spindle

struct LaunchOptionsTests {
    @Test func readsTheAutomationSettings() {
        let options = LaunchOptions(environment: [
            "SPINDLE_AUTOMATION_EMAIL": "ana@example.com",
            "SPINDLE_AUTOMATION_PASSWORD": "correct horse",
            "SPINDLE_AUTOMATION_TAB": "reports",
            "SPINDLE_AUTOMATION_AUTH": "register",
            "SPINDLE_AUTOMATION_SUBMIT": "1",
        ])

        #expect(options.email == "ana@example.com")
        #expect(options.password == "correct horse")
        #expect(options.tab == .reports)
        #expect(options.authMode == .register)
        #expect(options.submitsForms)
        #expect(options.isAutomated)
    }

    @Test func canOpenTheServerChoice() {
        #expect(LaunchOptions(environment: ["SPINDLE_AUTOMATION_AUTH": "server"]).authMode == .server)
    }

    @Test func anOrdinaryLaunchIsNotAutomated() {
        let options = LaunchOptions(environment: ["SPINDLE_AUTOMATION_TAB": "", "HOME": "/"])

        #expect(options.tab == nil)
        #expect(!options.isAutomated)
    }
}

struct IOSBuildTests {
    @Test func debugBuildsTalkToTheAPIOnThisMac() {
        // The simulator reaches the Mac it runs on as localhost.
        #expect(ServerAddress.cloud.absoluteString == "http://localhost:5258")
        #expect(ServerAddress.cloudWebApp.absoluteString == "http://localhost:5173")
    }
}

struct AuthViewTests {
    @Test func namesTheServerAsPeopleTypeIt() {
        #expect(AuthView.name(of: URL(string: "https://spindle.example.com")!) == "spindle.example.com")
        #expect(AuthView.name(of: URL(string: "http://192.168.1.5:8080")!) == "192.168.1.5:8080")
    }
}
