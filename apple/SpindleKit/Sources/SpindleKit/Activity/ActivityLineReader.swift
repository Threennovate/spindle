import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// The bound applies to one record, including incomplete/malformed records, not just to read chunks.
/// Oversized records are discarded through their newline. The state can resume discarding after an append.
enum ActivityLineReader {
    static let recordLimit = 32 * 1024 * 1024
    static let metadataLimit = 1024 * 1024
    struct State: Codable, Sendable { var offset: UInt64 = 0; var discarding = false }
    struct Result {
        let state: State
        let tail: Data
        let bytes: UInt64
        let peakBufferedBytes: Int
    }
    enum Failure: Error { case oversizedMetadata }

    static func read(_ file: FileHandle, from initial: State, through limit: UInt64? = nil,
                     recordLimit: Int = recordLimit, consume: (Data) -> Void) throws -> Result {
        precondition(recordLimit > 0)
        var state = initial, position = initial.offset, peak = 0, pendingCount = 0
        // Reuse one bounded allocation for records spanning chunks. Growing Data by slices can
        // repeatedly allocate/copy a large record and leave hundreds of MiB with the allocator.
        var buffer: UnsafeMutableRawBufferPointer?
        defer { buffer?.deallocate() }
        while limit == nil || position < limit! {
            // FileHandle's temporary Foundation buffers must be created inside this pool. Pooling only
            // individual decoder calls leaves the read buffers alive until the actor yields.
            let didRead: Bool = try autoreleasepool {
                try Task.checkCancellation()
                let count = Int(min(1024 * 1024, limit.map { $0 - position } ?? 1024 * 1024))
                guard let chunk = try file.read(upToCount: count), !chunk.isEmpty else { return false }
                let baseOffset = position
                position += UInt64(chunk.count)
                let ends = chunk.withUnsafeBytes { bytes -> [Int] in
                    guard let base = bytes.baseAddress else { return [] }
                    var ends: [Int] = [], start = 0
                    while start < bytes.count, let newline = memchr(base + start, 10, bytes.count - start) {
                        let end = base.distance(to: newline)
                        ends.append(end)
                        start = end + 1
                    }
                    return ends
                }
                var start = 0
                // Search each chunk once, rather than rescanning a growing incomplete record.
                for end in ends + [chunk.count] {
                    let newline = end < chunk.count
                    let length = end - start
                    if !state.discarding {
                        if length > recordLimit - pendingCount {
                            pendingCount = 0
                            state.discarding = true
                        } else if newline && pendingCount == 0 {
                            peak = max(peak, length)
                            consume(chunk[(chunk.startIndex + start)..<(chunk.startIndex + end)])
                        } else {
                            if length > 0 {
                                if buffer == nil {
                                    buffer = .allocate(byteCount: recordLimit, alignment: 16)
                                }
                                chunk.withUnsafeBytes { bytes in
                                    buffer!.baseAddress!.advanced(by: pendingCount)
                                        .copyMemory(from: bytes.baseAddress!.advanced(by: start), byteCount: length)
                                }
                                pendingCount += length
                            }
                            peak = max(peak, pendingCount)
                            if newline {
                                // The decoder consumes this borrowed record synchronously; it never retains
                                // its bytes. Only the incomplete tail returned below needs an owned copy.
                                consume(Data(bytesNoCopy: buffer!.baseAddress!, count: pendingCount, deallocator: .none))
                                pendingCount = 0
                            }
                        }
                    }
                    if newline {
                        state.discarding = false
                        state.offset = baseOffset + UInt64(end + 1)
                    } else if state.discarding {
                        // All these bytes have already been rejected; don't reread a giant tail next time.
                        state.offset = position
                    }
                    start = end + 1
                }
                return true
            }
            if !didRead { break }
        }
        let tail = pendingCount == 0 ? Data() : Data(bytes: buffer!.baseAddress!, count: pendingCount)
        return Result(state: state, tail: tail, bytes: position - initial.offset, peakBufferedBytes: peak)
    }

    static func firstLine(_ url: URL, limit: Int = metadataLimit) throws -> Data {
        let file = try FileHandle(forReadingFrom: url)
        defer { try? file.close() }
        var line = Data()
        while true {
            let done: Bool = try autoreleasepool {
                try Task.checkCancellation()
                guard let chunk = try file.read(upToCount: min(64 * 1024, limit - line.count + 1)), !chunk.isEmpty else { return true }
                let end = chunk.withUnsafeBytes { bytes -> Int? in
                    guard let base = bytes.baseAddress, let newline = memchr(base, 10, bytes.count) else { return nil }
                    return base.distance(to: newline)
                }
                let length = end ?? chunk.count
                guard length <= limit - line.count else { throw Failure.oversizedMetadata }
                line.append(chunk.prefix(length))
                return end != nil
            }
            if done { return line }
        }
    }
}
