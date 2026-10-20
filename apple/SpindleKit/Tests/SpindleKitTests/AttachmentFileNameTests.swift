import Testing
@testable import SpindleKit

/// A server names the files it sends; the apps save them under that name, so it must stay a bare file name.
struct AttachmentFileNameTests {
    @Test func the_servers_name_is_kept() {
        #expect(SpindleClient.attachmentFileName(#"attachment; filename="spindle-data-2026-10-18.json""#) == "spindle-data-2026-10-18.json")
        #expect(SpindleClient.attachmentFileName("attachment; filename=spindle.json") == "spindle.json")
    }

    @Test func folders_in_the_name_are_dropped() {
        #expect(SpindleClient.attachmentFileName(#"attachment; filename="../../Library/Preferences/target.plist""#) == "target.plist")
        #expect(SpindleClient.attachmentFileName(#"attachment; filename="..\..\evil.json""#) == "evil.json")
        #expect(SpindleClient.attachmentFileName(#"attachment; filename="/etc/passwd""#) == "passwd")
    }

    @Test func a_name_that_is_only_a_folder_gives_none() {
        #expect(SpindleClient.attachmentFileName(#"attachment; filename="..""#) == nil)
        #expect(SpindleClient.attachmentFileName(#"attachment; filename="../""#) == nil)
        #expect(SpindleClient.attachmentFileName(nil) == nil)
        #expect(SpindleClient.attachmentFileName("attachment") == nil)
    }
}
