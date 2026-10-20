import Foundation

/// Where the app finds its server. Each build knows Spindle Cloud: https://app.spindletrack.com for release
/// builds, the API on this Mac for debug builds (SPINDLE_SERVER_URL in project.yml). People who run their own
/// server can choose it when they sign in instead.
enum ServerAddress {
    /// Spindle Cloud, as this build knows it. Updates always come from here, whichever server someone uses.
    static let cloud: URL = {
        guard let url = address(Bundle.main.object(forInfoDictionaryKey: "SpindleServerURL")) else {
            preconditionFailure("Set SPINDLE_SERVER_URL for this build configuration in project.yml.")
        }
        return url
    }()

    /// Where Spindle Cloud's web app is: on the server itself, except while developing, when it runs on its own dev
    /// server. Someone's own server serves its web app itself.
    static let cloudWebApp: URL = address(Bundle.main.object(forInfoDictionaryKey: "SpindleWebURL")) ?? cloud

    /// The addresses Spindle Cloud had before, for apps whose sign-in is still kept under one of them. A list in the
    /// build rather than what the app saw last, so someone who skipped the versions in between moves along too.
    static let formerClouds: [URL] = formerAddresses(Bundle.main.object(forInfoDictionaryKey: "SpindleFormerServerURLs"))

    /// The https addresses in a setting that lists them separated by spaces.
    static func formerAddresses(_ value: Any?) -> [URL] {
        ((value as? String) ?? "").split(separator: " ").compactMap { address(String($0)) }.filter { $0.scheme == "https" }
    }

    /// An http(s) address from Info.plist, or nil if the setting is empty.
    static func address(_ value: Any?) -> URL? {
        guard let text = (value as? String)?.trimmingCharacters(in: .whitespaces),
              let url = URL(string: text), url.scheme == "https" || url.scheme == "http", url.host() != nil
        else { return nil }
        return url
    }

    enum OwnServerError: LocalizedError, Equatable {
        case notAnAddress
        case needsHTTPS

        var errorDescription: String? {
            switch self {
            case .notAnAddress: "Enter the server's address, e.g. spindle.example.com."
            #if os(macOS)
            case .needsHTTPS: "Use an https address. Only a server on this Mac or the local network can be plain http."
            #else
            case .needsHTTPS: "Use an https address. Only a server on the local network can be plain http."
            #endif
            }
        }
    }

    /// The address someone typed for their own server, as the app uses it: https unless they wrote http, and only the
    /// scheme, host and port, since the API and the web app live at the server's root.
    static func ownServer(_ text: String) throws(OwnServerError) -> URL {
        var text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if !text.contains("://") {
            text = "https://" + text
        }
        guard let typed = URLComponents(string: text), let host = typed.host?.lowercased(), !host.isEmpty,
              typed.scheme == "https" || typed.scheme == "http"
        else { throw .notAnAddress }
        var components = URLComponents()
        components.scheme = typed.scheme
        components.host = host
        components.port = typed.port
        guard let url = components.url else { throw .notAnAddress }
        // The app may reach only these over plain http (NSAllowsLocalNetworking in Info.plist).
        if url.scheme == "http" && !isLocal(host) {
            throw .needsHTTPS
        }
        return url
    }

    /// This Mac, or a name or address on the local network.
    private static func isLocal(_ host: String) -> Bool {
        if host == "localhost" || host.hasSuffix(".local") || !host.contains(".") {
            return true
        }
        let parts = host.split(separator: ".").compactMap { Int($0) }
        guard parts.count == 4 else { return false }
        return parts[0] == 127 || parts[0] == 10 || (parts[0] == 192 && parts[1] == 168)
            || (parts[0] == 172 && (16...31).contains(parts[1]))
    }
}
