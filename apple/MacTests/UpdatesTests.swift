import Foundation
import Testing
@testable import Spindle

struct UpdatesTests {
    /// Whichever server someone signs in to, updates come from Spindle Cloud.
    @Test func theFeedIsInSpindleCloudsDownloads() {
        let feed = Bundle.main.object(forInfoDictionaryKey: "SUFeedURL") as? String
        #expect(feed == ServerAddress.cloud.appending(path: "downloads/spindle-mac/appcast.xml").absoluteString)
    }

    @Test func withNoOneAtTheMacThereAreNoUpdates() {
        let updates = Updates(active: false)

        #expect(!updates.isAvailable)
        #expect(!updates.canCheck)
    }
}
