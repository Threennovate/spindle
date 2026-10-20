import Foundation

/// Whether an agent has the server's MCP, read from the agent's own settings so nothing has to run: Claude Code keeps
/// servers for the user in `~/.claude.json`, Codex in `~/.codex/config.toml` (or `$CODEX_HOME`). Only a server called
/// `spindle` at this app's `<server>/mcp` counts; anything else by that name is replaced when the person connects.
/// Whether the agent finished signing in isn't in those files: the server lists signed-in agents under Account.
public enum AgentConnections {
    /// The name agents know the server by.
    public static let name = "spindle"

    /// The MCP server on `server`, the address the app signs in to: `/mcp` at its root.
    public static func mcpURL(server: URL) -> URL {
        var text = server.absoluteString
        while text.hasSuffix("/") { text.removeLast() }
        return URL(string: text + "/mcp") ?? server.appending(path: "mcp")
    }

    /// Whether Claude Code has `spindle` for the user as an HTTP server at `url`. Servers added for a single project
    /// don't count: the app adds it for the user.
    public static func claude(home: URL, url: URL) -> Bool {
        guard let data = try? Data(contentsOf: home.appending(path: ".claude.json")),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let servers = json["mcpServers"] as? [String: Any],
              let server = servers[name] as? [String: Any],
              (server["type"] as? String ?? "http") == "http"
        else { return false }
        return same(server["url"] as? String, url)
    }

    /// Whether Codex has `spindle` at `url`, from `config.toml` in `codexHome`.
    public static func codex(codexHome: URL, url: URL) -> Bool {
        guard let text = try? String(contentsOf: codexHome.appending(path: "config.toml"), encoding: .utf8) else { return false }
        return same(codexURL(in: text), url)
    }

    /// Where Codex keeps its settings: `$CODEX_HOME`, or `.codex` in the home folder.
    public static func codexHome(home: URL, environment: [String: String] = ProcessInfo.processInfo.environment) -> URL {
        environment["CODEX_HOME"].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0) } ?? home.appending(path: ".codex")
    }

    /// The same address, give or take a trailing slash and the case of the scheme and host.
    static func same(_ text: String?, _ url: URL) -> Bool {
        func normalized(_ text: String) -> String? {
            guard var components = URLComponents(string: text.trimmingCharacters(in: .whitespaces)) else { return nil }
            components.scheme = components.scheme?.lowercased()
            components.host = components.host?.lowercased()
            while components.path.hasSuffix("/") { components.path.removeLast() }
            return components.string
        }
        guard let text, let left = normalized(text) else { return false }
        return left == normalized(url.absoluteString)
    }

    /// The `url` of the `[mcp_servers.spindle]` table, as `codex mcp add --url` writes it, or nil when there is no
    /// such table or it has no readable `url`. Only what that needs of TOML.
    static func codexURL(in toml: String) -> String? {
        let headers = ["[mcp_servers.\(name)]", "[mcp_servers.\"\(name)\"]", "[mcp_servers.'\(name)']"]
        var inTable = false
        for raw in toml.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("[") {
                let header = line.split(separator: "#", maxSplits: 1)[0].trimmingCharacters(in: .whitespaces)
                inTable = headers.contains(header)
                continue
            }
            let parts = line.split(separator: "=", maxSplits: 1)
            guard inTable, parts.count == 2, parts[0].trimmingCharacters(in: .whitespaces) == "url" else { continue }
            return string(parts[1].trimmingCharacters(in: .whitespaces))
        }
        return nil
    }

    /// A TOML basic ("…", with escapes) or literal ('…') string at the start of `value`.
    private static func string(_ value: String) -> String? {
        guard let quote = value.first, quote == "\"" || quote == "'" else { return nil }
        var result = ""
        var escaped = false
        for character in value.dropFirst() {
            if escaped {
                switch character {
                case "n": result.append("\n")
                case "t": result.append("\t")
                default: result.append(character)
                }
                escaped = false
            } else if character == "\\" && quote == "\"" {
                escaped = true
            } else if character == quote {
                return result
            } else {
                result.append(character)
            }
        }
        return nil
    }

    /// What a command printed to a terminal, as plain lines: without colours, links, cursor moves and carriage returns.
    public static func plainText(_ output: String) -> String {
        var text = output.replacingOccurrences(of: "\u{1B}\\][^\u{07}\u{1B}]*(\u{07}|\u{1B}\\\\)", with: "", options: .regularExpression)
        text = text.replacingOccurrences(of: "\u{1B}\\[[0-9;?]*[ -/]*[@-~]", with: "", options: .regularExpression)
        text = text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        text = String(text.unicodeScalars.filter { $0 == "\n" || $0 == "\t" || $0.value >= 0x20 }.map(Character.init))
        return text.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }.joined(separator: "\n")
    }
}
