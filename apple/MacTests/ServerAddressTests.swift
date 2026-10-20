import Foundation
import Testing
@testable import Spindle

struct ServerAddressTests {
    @Test func debugBuildsKnowTheAPIOnThisMacAsSpindleCloud() {
        // The tests run in a debug build.
        #expect(ServerAddress.cloud.absoluteString == "http://localhost:5258")
        #expect(ServerAddress.cloudWebApp.absoluteString == "http://localhost:5173")
    }

    @Test func readsTheBuildSetting() {
        #expect(ServerAddress.address("https://spindle.example.com")?.host() == "spindle.example.com")
    }

    @Test(arguments: [nil, "", "  ", "localhost:5258", "ftp://example.com", "https://"])
    func anEmptyOrBrokenSettingIsNoAddress(value: String?) {
        #expect(ServerAddress.address(value) == nil)
    }

    @Test func formerAddressesAreTheHTTPSOnesInTheSetting() {
        #expect(ServerAddress.formerAddresses("https://a.example.com  http://b.example.com https://c.example.com")
            == [URL(string: "https://a.example.com")!, URL(string: "https://c.example.com")!])
        #expect(ServerAddress.formerAddresses("").isEmpty)
        #expect(ServerAddress.formerAddresses(nil).isEmpty)
    }

    @Test func ownServerAddressesAreTidiedUp() throws {
        #expect(try ServerAddress.ownServer(" Spindle.Example.com/login ") == URL(string: "https://spindle.example.com"))
        #expect(try ServerAddress.ownServer("http://192.168.1.5:8080") == URL(string: "http://192.168.1.5:8080"))
        #expect(try ServerAddress.ownServer("http://localhost:5258") == URL(string: "http://localhost:5258"))
    }

    @Test func ownServersOnTheInternetNeedHTTPS() {
        #expect(throws: ServerAddress.OwnServerError.needsHTTPS) { try ServerAddress.ownServer("http://spindle.example.com") }
        #expect(throws: ServerAddress.OwnServerError.notAnAddress) { try ServerAddress.ownServer("  ") }
    }
}

struct AvatarTests {
    @Test(arguments: [
        ("Ana Kovač", "AK"),
        ("ana", "A"),
        ("Ivan Marko Horvat", "IH"),
        ("  ", "?"),
    ])
    func usesTheFirstAndLastInitials(name: String, initials: String) {
        #expect(Avatar.initials(of: name) == initials)
    }
}
