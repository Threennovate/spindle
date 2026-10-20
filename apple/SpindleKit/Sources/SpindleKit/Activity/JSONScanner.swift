import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// Walks one JSON value's members or elements without decoding them, so a log line's few interesting fields can be
/// read while a large tool output beside them is only skipped over. It answers nil for anything it can't read
/// with certainty, and the caller then decodes the line in full.
struct JSONScanner {
    let bytes: UnsafeRawBufferPointer

    /// A string's contents, and whether it has escapes (whose text the scanner doesn't decode).
    struct StringValue { let contents: Range<Int>; let escaped: Bool }

    enum Kind { case string, object, array, bool(Bool), null, other }

    func kind(of value: Range<Int>) -> Kind {
        guard let first = value.first.map({ bytes[$0] }) else { return .other }
        switch first {
        case UInt8(ascii: "\""): return .string
        case UInt8(ascii: "{"): return .object
        case UInt8(ascii: "["): return .array
        case UInt8(ascii: "t") where equals(value, "true"): return .bool(true)
        case UInt8(ascii: "f") where equals(value, "false"): return .bool(false)
        case UInt8(ascii: "n") where equals(value, "null"): return .null
        default: return .other
        }
    }

    /// Calls `visit` with each member's key (without quotes) and value; false when the object isn't well formed.
    func members(of object: Range<Int>, _ visit: (_ key: StringValue, _ value: Range<Int>) -> Void) -> Bool {
        var index = skipSpace(object.lowerBound)
        guard index < object.upperBound, bytes[index] == UInt8(ascii: "{") else { return false }
        index = skipSpace(index + 1)
        if index < object.upperBound, bytes[index] == UInt8(ascii: "}") { return true }
        while index < object.upperBound {
            guard bytes[index] == UInt8(ascii: "\""), let key = string(at: index) else { return false }
            index = skipSpace(key.contents.upperBound + 1)
            guard index < object.upperBound, bytes[index] == UInt8(ascii: ":") else { return false }
            index = skipSpace(index + 1)
            guard let end = skipValue(at: index), end <= object.upperBound else { return false }
            visit(key, index..<end)
            index = skipSpace(end)
            guard index < object.upperBound else { return false }
            if bytes[index] == UInt8(ascii: "}") { return true }
            guard bytes[index] == UInt8(ascii: ",") else { return false }
            index = skipSpace(index + 1)
        }
        return false
    }

    /// Calls `visit` with each element; false when the array isn't well formed.
    func elements(of array: Range<Int>, _ visit: (_ value: Range<Int>) -> Void) -> Bool {
        var index = skipSpace(array.lowerBound)
        guard index < array.upperBound, bytes[index] == UInt8(ascii: "[") else { return false }
        index = skipSpace(index + 1)
        if index < array.upperBound, bytes[index] == UInt8(ascii: "]") { return true }
        while index < array.upperBound {
            guard let end = skipValue(at: index), end <= array.upperBound else { return false }
            visit(index..<end)
            index = skipSpace(end)
            guard index < array.upperBound else { return false }
            if bytes[index] == UInt8(ascii: "]") { return true }
            guard bytes[index] == UInt8(ascii: ",") else { return false }
            index = skipSpace(index + 1)
        }
        return false
    }

    /// A string value's text, when it has no escapes.
    func text(_ value: Range<Int>) -> String? {
        guard let string = string(at: value.lowerBound), !string.escaped,
              string.contents.upperBound + 1 == value.upperBound else { return nil }
        return String(decoding: UnsafeRawBufferPointer(rebasing: bytes[string.contents]), as: UTF8.self)
    }

    func equals(_ range: Range<Int>, _ ascii: StaticString) -> Bool {
        guard range.count == ascii.utf8CodeUnitCount, range.upperBound <= bytes.count else { return false }
        return memcmp(bytes.baseAddress! + range.lowerBound, ascii.utf8Start, range.count) == 0
    }

    func skipSpace(_ start: Int) -> Int {
        var index = start
        while index < bytes.count {
            switch bytes[index] {
            case UInt8(ascii: " "), UInt8(ascii: "\t"), UInt8(ascii: "\n"), UInt8(ascii: "\r"): index += 1
            default: return index
            }
        }
        return index
    }

    /// The string starting at `start` (its opening quote): where its contents lie, and whether it has escapes.
    func string(at start: Int) -> StringValue? {
        guard start < bytes.count, bytes[start] == UInt8(ascii: "\""), let base = bytes.baseAddress else { return nil }
        var from = start + 1, escaped = false
        while from < bytes.count {
            guard let found = memchr(base + from, Int32(UInt8(ascii: "\"")), bytes.count - from) else { return nil }
            let quote = base.distance(to: UnsafeRawPointer(found))
            var backslashes = 0
            while quote - backslashes - 1 > start, bytes[quote - backslashes - 1] == UInt8(ascii: "\\") { backslashes += 1 }
            if backslashes > 0 { escaped = true }
            if backslashes % 2 == 0 {
                if !escaped, memchr(base + start + 1, Int32(UInt8(ascii: "\\")), quote - start - 1) != nil { escaped = true }
                return StringValue(contents: (start + 1)..<quote, escaped: escaped)
            }
            from = quote + 1
        }
        return nil
    }

    /// The end of the value starting at `start`, skipping strings with `memchr` rather than byte by byte.
    func skipValue(at start: Int) -> Int? {
        guard start < bytes.count else { return nil }
        switch bytes[start] {
        case UInt8(ascii: "\""):
            return string(at: start).map { $0.contents.upperBound + 1 }
        case UInt8(ascii: "{"), UInt8(ascii: "["):
            var depth = 0, index = start
            while index < bytes.count {
                switch bytes[index] {
                case UInt8(ascii: "\""):
                    guard let string = string(at: index) else { return nil }
                    index = string.contents.upperBound + 1
                    continue
                case UInt8(ascii: "{"), UInt8(ascii: "["): depth += 1
                case UInt8(ascii: "}"), UInt8(ascii: "]"):
                    depth -= 1
                    if depth == 0 { return index + 1 }
                default: break
                }
                index += 1
            }
            return nil
        default:
            var index = start
            while index < bytes.count {
                switch bytes[index] {
                case UInt8(ascii: ","), UInt8(ascii: "}"), UInt8(ascii: "]"), UInt8(ascii: " "), UInt8(ascii: "\t"),
                     UInt8(ascii: "\n"), UInt8(ascii: "\r"):
                    return index > start ? index : nil
                default: index += 1
                }
            }
            return index > start ? index : nil
        }
    }
}
