import Foundation
import HTTPTypes
import OpenAPIRuntime

/// A failed API call, with what the server said about it: RFC 9457 problem details, and for validation errors the
/// message for each field, keyed by the JSON property name, e.g. `["email": ["Enter a valid email address."]]`.
public struct APIError: Error, Sendable, Equatable, LocalizedError {
    public var status: Int
    public var title: String?
    public var detail: String?
    public var fieldErrors: [String: [String]]
    /// The problem's `type`, when the server names one.
    public var type: String?
    /// The entry as it is now, when a change was refused because the entry changed since this device saw it.
    public var current: TimeEntry?

    public init(
        status: Int, title: String? = nil, detail: String? = nil, fieldErrors: [String: [String]] = [:],
        type: String? = nil, current: TimeEntry? = nil
    ) {
        self.status = status
        self.title = title
        self.detail = detail
        self.fieldErrors = fieldErrors
        self.type = type
        self.current = current
    }

    public static let entryChangedType = "urn:spindle:problem:entry-changed"

    /// The entry changed elsewhere since this device saw it, so the change wasn't saved; `current` is how it is now.
    public var isEntryChanged: Bool {
        status == 412 && type == Self.entryChangedType && current != nil
    }

    /// The session is over, e.g. signed out on another device; the app should show sign-in.
    public static let sessionEnded = APIError(status: 401, title: "Your session has ended. Please sign in again.")

    public static let readOnlyMessage = "Nothing can be changed here: the trial or plan has ended."

    /// The change was refused because the organization is read-only: its trial or plan has ended.
    public var isReadOnly: Bool {
        status == 402
    }

    /// The first message for a field, if the server named one.
    public func message(for field: String) -> String? {
        fieldErrors[field]?.first
    }

    /// One sentence to show: a validation message, the problem the server described, or a general one.
    public var errorDescription: String? {
        if isReadOnly {
            return Self.readOnlyMessage
        }
        if let first = fieldErrors.values.first?.first {
            return first
        }
        if let title, let detail {
            return "\(title) \(detail)"
        }
        return title ?? detail ?? "Something went wrong (\(status)). Please try again."
    }
}

/// A request that never reached the server, or whose answer was lost.
public struct ConnectionError: Error, Sendable, LocalizedError {
    public var underlying: String

    public var errorDescription: String? {
        "Couldn't reach Spindle. Check your connection and try again."
    }
}

extension Error {
    /// The API's own error, or a connection error, whatever layer of the OpenAPI runtime wrapped it.
    public var asSpindleError: Error {
        if let error = self as? APIError { return error }
        if let error = self as? ConnectionError { return error }
        if let client = self as? ClientError {
            return client.underlyingError.asSpindleError
        }
        if self is URLError {
            return ConnectionError(underlying: String(describing: self))
        }
        return self
    }
}

/// Turns every error response into an ``APIError`` before the generated code sees it, so calls read the success
/// case directly and still surface the server's message.
struct ProblemMiddleware: ClientMiddleware {
    private struct Problem: Decodable {
        var type: String?
        var title: String?
        var detail: String?
        var status: Int?
        var errors: [String: [String]]?
    }

    private struct Changed: Decodable {
        var current: TimeEntry
    }

    /// Reads an entry as the generated client does, with the API's dates.
    private static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let text = try decoder.singleValueContainer().decode(String.self)
            return try LenientISO8601DateTranscoder().decode(text)
        }
        return decoder
    }()

    func intercept(
        _ request: HTTPRequest,
        body: HTTPBody?,
        baseURL: URL,
        operationID: String,
        next: @Sendable (HTTPRequest, HTTPBody?, URL) async throws -> (HTTPResponse, HTTPBody?)
    ) async throws -> (HTTPResponse, HTTPBody?) {
        let response: HTTPResponse
        let responseBody: HTTPBody?
        do {
            (response, responseBody) = try await next(request, body, baseURL)
        } catch let error as URLError {
            throw ConnectionError(underlying: String(describing: error))
        }

        guard response.status.code >= 400 else {
            return (response, responseBody)
        }

        var problem: Problem?
        var current: TimeEntry?
        if let responseBody, let data = try? await Data(collecting: responseBody, upTo: 1024 * 1024) {
            problem = try? JSONDecoder().decode(Problem.self, from: data)
            if problem?.type == APIError.entryChangedType {
                current = try? Self.decoder.decode(Changed.self, from: data).current
            }
        }
        throw APIError(
            status: response.status.code,
            title: problem?.title,
            detail: problem?.detail,
            fieldErrors: problem?.errors ?? [:],
            type: problem?.type,
            current: current)
    }
}
