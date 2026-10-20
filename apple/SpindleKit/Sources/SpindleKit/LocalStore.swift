import Foundation
import SQLite3

/// A small SQLite database on the device for one account: what was last loaded, so the app opens without the
/// network, and the outbox of changes still to send. It is a cache: the server's database is the one that counts.
public actor LocalStore {
    private let connection: Connection
    private var db: OpaquePointer { connection.handle }
    private let encoder = JSONEncoder()
    private let decoder = JSONDecoder()

    /// Opens the database at `url`, creating it and its folder if needed.
    public init(url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        var handle: OpaquePointer?
        guard sqlite3_open_v2(url.path(percentEncoded: false), &handle, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX, nil) == SQLITE_OK else {
            let message = handle.map { String(cString: sqlite3_errmsg($0)) } ?? "unknown error"
            sqlite3_close(handle)
            throw StoreError(message)
        }
        connection = Connection(handle!)
        try Self.migrate(handle!)
    }

    /// A database that lives only as long as the app runs, e.g. for tests.
    public init() throws {
        var handle: OpaquePointer?
        guard sqlite3_open(":memory:", &handle) == SQLITE_OK, let handle else { throw StoreError("Couldn't open an in-memory database.") }
        connection = Connection(handle)
        try Self.migrate(handle)
    }

    /// Where an account's database lives: one file per server and person, in Application Support.
    public static func url(server: URL, userId: String) -> URL {
        let host = (server.host() ?? "server") + (server.port.map { "-\($0)" } ?? "")
        let safe = "\(host)-\(userId)".map { $0.isLetter || $0.isNumber || $0 == "-" || $0 == "." ? $0 : "_" }
        return URL.applicationSupportDirectory
            .appending(path: "Spindle", directoryHint: .isDirectory)
            .appending(path: String(safe) + ".sqlite")
    }

    // MARK: - Cached documents

    public func read<Value: Decodable>(_ type: Value.Type, key: String) -> Value? {
        guard let data = try? query("SELECT value FROM document WHERE key = ?", [.text(key)]).first?.first,
              case .blob(let bytes) = data
        else { return nil }
        return try? decoder.decode(type, from: bytes)
    }

    public func write(_ value: some Encodable, key: String) {
        guard let data = try? encoder.encode(value) else { return }
        _ = try? query("INSERT INTO document (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", [.text(key), .blob(data)])
    }

    public func remove(key: String) {
        _ = try? query("DELETE FROM document WHERE key = ?", [.text(key)])
    }

    // MARK: - The outbox

    /// The changes waiting to be sent, oldest first.
    public func changes() -> [(seq: Int64, change: EntryChange)] {
        let rows = (try? query("SELECT seq, change FROM outbox ORDER BY seq")) ?? []
        return rows.compactMap { row in
            guard case .integer(let seq) = row[0], case .blob(let data) = row[1],
                  let change = try? decoder.decode(EntryChange.self, from: data)
            else { return nil }
            return (seq, change)
        }
    }

    @discardableResult
    public func append(_ change: EntryChange) throws -> Int64 {
        _ = try query("INSERT INTO outbox (change, created_at) VALUES (?, ?)", [.blob(try encoder.encode(change)), .real(Date().timeIntervalSince1970)])
        return sqlite3_last_insert_rowid(db)
    }

    public func replace(_ seq: Int64, with change: EntryChange) throws {
        _ = try query("UPDATE outbox SET change = ? WHERE seq = ?", [.blob(try encoder.encode(change)), .integer(seq)])
    }

    public func removeChange(_ seq: Int64) {
        _ = try? query("DELETE FROM outbox WHERE seq = ?", [.integer(seq)])
    }

    // MARK: - SQLite

    private enum Value {
        case integer(Int64), real(Double), text(String), blob(Data), null
    }

    private static func migrate(_ db: OpaquePointer) throws {
        var version: Int32 = 0
        var statement: OpaquePointer?
        if sqlite3_prepare_v2(db, "PRAGMA user_version", -1, &statement, nil) == SQLITE_OK, sqlite3_step(statement) == SQLITE_ROW {
            version = sqlite3_column_int(statement, 0)
        }
        sqlite3_finalize(statement)
        if version < 1 {
            let schema = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS document (key TEXT PRIMARY KEY NOT NULL, value BLOB NOT NULL);
                CREATE TABLE IF NOT EXISTS outbox (seq INTEGER PRIMARY KEY AUTOINCREMENT, change BLOB NOT NULL, created_at REAL NOT NULL);
                PRAGMA user_version = 1;
                """
            guard sqlite3_exec(db, schema, nil, nil, nil) == SQLITE_OK else {
                throw StoreError(String(cString: sqlite3_errmsg(db)))
            }
        }
    }

    private func query(_ sql: String, _ arguments: [Value] = []) throws -> [[Value]] {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &statement, nil) == SQLITE_OK else {
            throw StoreError(String(cString: sqlite3_errmsg(db)))
        }
        defer { sqlite3_finalize(statement) }
        // SQLite copies the values, since the Swift buffers don't outlive the call.
        let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
        for (index, argument) in arguments.enumerated() {
            let position = Int32(index + 1)
            switch argument {
            case .integer(let value): sqlite3_bind_int64(statement, position, value)
            case .real(let value): sqlite3_bind_double(statement, position, value)
            case .text(let value): sqlite3_bind_text(statement, position, value, -1, transient)
            case .blob(let value): _ = value.withUnsafeBytes { sqlite3_bind_blob(statement, position, $0.baseAddress, Int32($0.count), transient) }
            case .null: sqlite3_bind_null(statement, position)
            }
        }
        var rows: [[Value]] = []
        while true {
            switch sqlite3_step(statement) {
            case SQLITE_ROW:
                rows.append((0..<sqlite3_column_count(statement)).map { column in
                    switch sqlite3_column_type(statement, column) {
                    case SQLITE_INTEGER: .integer(sqlite3_column_int64(statement, column))
                    case SQLITE_FLOAT: .real(sqlite3_column_double(statement, column))
                    case SQLITE_TEXT: .text(sqlite3_column_text(statement, column).map { String(cString: $0) } ?? "")
                    case SQLITE_BLOB:
                        // An empty blob comes back as no pointer at all.
                        .blob(sqlite3_column_blob(statement, column).map { Data(bytes: $0, count: Int(sqlite3_column_bytes(statement, column))) } ?? Data())
                    default: .null
                    }
                })
            case SQLITE_DONE:
                return rows
            default:
                throw StoreError(String(cString: sqlite3_errmsg(db)))
            }
        }
    }
}

/// The open database, closed when the store goes. Only the store's actor uses it.
private final class Connection: @unchecked Sendable {
    let handle: OpaquePointer

    init(_ handle: OpaquePointer) {
        self.handle = handle
    }

    deinit {
        sqlite3_close(handle)
    }
}

public struct StoreError: Error, CustomStringConvertible {
    public let description: String
    init(_ description: String) { self.description = description }
}
