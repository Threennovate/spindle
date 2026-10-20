import Foundation
import Testing
@testable import SpindleKit

struct AgentConnectionsTests {
    let url = URL(string: "https://app.spindletrack.com/mcp")!

    func folder(_ files: [String: String]) throws -> URL {
        let url = FileManager.default.temporaryDirectory.appending(path: "agent-connections-\(UUID().uuidString)")
        for (path, text) in files {
            let file = url.appending(path: path)
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try text.write(to: file, atomically: true, encoding: .utf8)
        }
        return url
    }

    @Test func mcpURL() {
        #expect(AgentConnections.mcpURL(server: URL(string: "https://app.spindletrack.com")!) == url)
        #expect(AgentConnections.mcpURL(server: URL(string: "https://app.spindletrack.com/")!) == url)
        #expect(AgentConnections.mcpURL(server: URL(string: "http://localhost:5258")!).absoluteString == "http://localhost:5258/mcp")
    }

    @Test func claudeForTheUser() throws {
        let home = try folder([".claude.json": """
            {"projects":{"/x":{"mcpServers":{"spindle":{"type":"http","url":"https://elsewhere.example/mcp"}}}},
             "mcpServers":{"spindle":{"type":"http","url":"https://app.spindletrack.com/mcp/"}}}
            """])
        #expect(AgentConnections.claude(home: home, url: url))
        #expect(AgentConnections.claude(home: home, url: URL(string: "HTTPS://App.Spindletrack.com/mcp")!))
        #expect(!AgentConnections.claude(home: home, url: URL(string: "https://spindle.example.com/mcp")!))
    }

    @Test func claudeWithAnotherSpindle() throws {
        let local = try folder([".claude.json": #"{"mcpServers":{"spindle":{"type":"stdio","command":"/Applications/Spindle.app/Contents/MacOS/spindle-mcp","args":[]}}}"#])
        #expect(!AgentConnections.claude(home: local, url: url))
        let sse = try folder([".claude.json": #"{"mcpServers":{"spindle":{"type":"sse","url":"https://app.spindletrack.com/mcp"}}}"#])
        #expect(!AgentConnections.claude(home: sse, url: url))
        let other = try folder([".claude.json": #"{"mcpServers":{"other":{"type":"http","url":"https://app.spindletrack.com/mcp"}}}"#])
        #expect(!AgentConnections.claude(home: other, url: url))
    }

    @Test func claudeWithoutSettings() throws {
        #expect(!AgentConnections.claude(home: try folder([:]), url: url))
        #expect(!AgentConnections.claude(home: try folder([".claude.json": "{not json"]), url: url))
    }

    @Test func codexAsItsCommandWritesIt() throws {
        let home = try folder(["config.toml": """
            model = "gpt-5"

            [mcp_servers.other]
            url = "https://elsewhere.example/mcp"

            [mcp_servers.spindle]
            url = "https://app.spindletrack.com/mcp"

            [features]
            url = "https://not.a.server"
            """])
        #expect(AgentConnections.codex(codexHome: home, url: url))
        #expect(!AgentConnections.codex(codexHome: home, url: URL(string: "https://elsewhere.example/mcp")!))
        #expect(!AgentConnections.codex(codexHome: try folder([:]), url: url))
        let local = try folder(["config.toml": "[mcp_servers.spindle]\ncommand = \"/Applications/Spindle.app/Contents/MacOS/spindle-mcp\"\n"])
        #expect(!AgentConnections.codex(codexHome: local, url: url))
    }

    @Test func codexStrings() {
        #expect(AgentConnections.codexURL(in: "[mcp_servers.\"spindle\"] # ours\nurl='https://a/mcp'") == "https://a/mcp")
        #expect(AgentConnections.codexURL(in: "[mcp_servers.'spindle']\nurl = \"https://a/\\\"b\" # x") == "https://a/\"b")
        #expect(AgentConnections.codexURL(in: "[mcp_servers.spindle]\nurls = \"https://a\"") == nil)
        #expect(AgentConnections.codexURL(in: "[mcp_servers.spindle.env]\nurl = \"https://a\"") == nil)
        #expect(AgentConnections.codexURL(in: "[mcp_servers.spindle]\nurl = https") == nil)
    }

    @Test func codexHome() {
        let home = URL(fileURLWithPath: "/Users/me")
        #expect(AgentConnections.codexHome(home: home, environment: [:]).path == "/Users/me/.codex")
        #expect(AgentConnections.codexHome(home: home, environment: ["CODEX_HOME": "/opt/codex"]).path == "/opt/codex")
    }

    @Test func plainText() {
        let output = "\u{04}\u{08}\u{08}Starting authentication…\r\nIf the browser didn't open, visit:\r\n  \u{1B}]8;;https://a/b\u{07}https://a/b\u{1B}]8;;\u{07}\r\n\r\n\u{1B}[1G\u{1B}[0JOr paste the redirect URL here: \u{1B}[33G"
        #expect(AgentConnections.plainText(output) == "Starting authentication…\nIf the browser didn't open, visit:\nhttps://a/b\nOr paste the redirect URL here:")
    }
}
