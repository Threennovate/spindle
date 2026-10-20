import CryptoKit
import Foundation

/// Resolves worktrees without invoking Git. Lookups are injected so the rules need no disk in tests.
public enum ActivityRepositoryResolver {
    public static func resolve(cwd: String, home: String,
                               exists: (String) -> Bool, read: (String) -> String?) -> ActivityRepository {
        func path(_ value: String, relativeTo base: String) -> String {
            URL(fileURLWithPath: value, relativeTo: URL(fileURLWithPath: base + "/")).standardizedFileURL.path
        }
        let folder = path(cwd, relativeTo: home)
        let scratch = home + "/Library/Application Support/Claude/"
        if folder.hasPrefix(scratch), folder.lowercased().contains("scratch") {
            return ActivityRepository(key: scratch + "scratch", name: "Scratch", origin: nil, isRepository: false)
        }
        // A deleted cwd cannot reliably be associated with a surviving ancestor repository.
        if exists(folder) {
            var current = folder
            while current != "/" {
                let git = current + "/.git"
                if exists(git) {
                    var root = current, gitDirectory = git
                    if let pointer = read(git), pointer.hasPrefix("gitdir:") {
                        gitDirectory = path(String(pointer.dropFirst(7)).trimmingCharacters(in: .whitespacesAndNewlines), relativeTo: current)
                        if let common = read(gitDirectory + "/commondir") {
                            gitDirectory = path(common.trimmingCharacters(in: .whitespacesAndNewlines), relativeTo: gitDirectory)
                            if URL(fileURLWithPath: gitDirectory).lastPathComponent == ".git" {
                                root = URL(fileURLWithPath: gitDirectory).deletingLastPathComponent().path
                            } else { root = gitDirectory }
                        }
                    }
                    return ActivityRepository(key: root, name: URL(fileURLWithPath: root).lastPathComponent,
                                              origin: origin(read(gitDirectory + "/config") ?? ""), isRepository: true)
                }
                current = URL(fileURLWithPath: current).deletingLastPathComponent().path
            }
        }
        return ActivityRepository(key: folder, name: URL(fileURLWithPath: folder).lastPathComponent, origin: nil, isRepository: false)
    }

    private static func origin(_ config: String) -> String? {
        var inOrigin = false
        for line in config.split(separator: "\n") {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("[") { inOrigin = trimmed == "[remote \"origin\"]" }
            else if inOrigin, let equals = trimmed.firstIndex(of: "="),
                    trimmed[..<equals].trimmingCharacters(in: .whitespaces) == "url" {
                return trimmed[trimmed.index(after: equals)...].trimmingCharacters(in: .whitespaces)
            }
        }
        return nil
    }
}

/// Names a repository without its local path, so a link or uploaded activity says nothing about the Mac's disk: by
/// its git `origin`, normalised and without credentials, or by a hash of its path when it has none. The same
/// repository then has the same identity on two Macs, cloned over SSH on one and HTTPS on the other.
public enum RepositoryIdentity {
    public static func of(path: String, origin: String?) -> String {
        origin.flatMap(Self.origin) ?? Self.path(path)
    }

    /// `origin:host/owner/repo`, lowercased, without scheme, user, password, port or `.git`; nil for an origin that
    /// is itself a local path, whose name would give the disk away.
    public static func origin(_ url: String) -> String? {
        var rest = url.trimmingCharacters(in: .whitespacesAndNewlines)
        let host: Substring, path: Substring
        if let scheme = rest.range(of: "://") {
            guard rest[..<scheme.lowerBound].lowercased() != "file" else { return nil }
            rest = String(rest[scheme.upperBound...])
            let authority = rest.prefix { $0 != "/" }
            path = rest.dropFirst(authority.count)
            // user:password@host:port
            let server = authority.split(separator: "@", omittingEmptySubsequences: false).last ?? ""
            host = server.prefix { $0 != ":" }
        } else {
            // The scp-like form Git uses for SSH: user@host:owner/repo.
            let head = rest[..<(rest.firstIndex(of: "/") ?? rest.endIndex)]
            if let at = head.lastIndex(of: "@") { rest = String(rest[rest.index(after: at)...]) }
            guard let colon = rest.firstIndex(of: ":"), !rest[..<colon].contains("/"), rest[..<colon].count > 1 else { return nil }
            host = rest[..<colon]
            path = rest[rest.index(after: colon)...]
        }
        var trimmed = String(path.prefix { $0 != "?" && $0 != "#" })
        while trimmed.hasPrefix("/") { trimmed.removeFirst() }
        while trimmed.hasSuffix("/") { trimmed.removeLast() }
        if trimmed.lowercased().hasSuffix(".git") { trimmed.removeLast(4) }
        let identity = "origin:" + (host + "/" + trimmed).lowercased()
        // An origin the server couldn't take as a name goes by its path's hash instead.
        guard !host.isEmpty, !trimmed.isEmpty, identity.utf16.count <= 300, !identity.contains(where: \.isWhitespace) else { return nil }
        return identity
    }

    /// `path:` and the start of the path's SHA-256.
    public static func path(_ path: String) -> String {
        "path:" + SHA256.hash(data: Data(path.utf8)).prefix(16).map { String(format: "%02x", $0) }.joined()
    }
}
