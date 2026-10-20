import Foundation

/// Conservative accounting of derived data, not a claim about the process's allocator footprint.
/// An oversized value is usable by the caller but is never admitted to the cache.
struct ActivityCache<Key: Hashable, Value> {
    private struct Entry { let value: Value; let cost: Int; var used: UInt64 }
    let byteLimit: Int
    let countLimit: Int
    private var entries: [Key: Entry] = [:]
    private var clock: UInt64 = 0
    private(set) var bytes = 0
    var count: Int { entries.count }

    init(byteLimit: Int, countLimit: Int) {
        self.byteLimit = max(0, byteLimit)
        self.countLimit = max(0, countLimit)
    }

    mutating func value(for key: Key) -> Value? {
        guard let value = entries[key]?.value else { return nil }
        clock &+= 1
        entries[key]?.used = clock
        return value
    }

    mutating func insert(_ value: Value, for key: Key, cost: Int) {
        remove(key)
        let cost = max(1, cost)
        guard cost <= byteLimit, countLimit > 0 else { return }
        while bytes + cost > byteLimit || entries.count >= countLimit {
            guard let oldest = entries.min(by: { $0.value.used < $1.value.used })?.key else { break }
            remove(oldest)
        }
        clock &+= 1
        entries[key] = Entry(value: value, cost: cost, used: clock)
        bytes += cost
    }

    mutating func remove(_ key: Key) {
        if let entry = entries.removeValue(forKey: key) { bytes -= entry.cost }
    }

    mutating func remove(where predicate: (Key) -> Bool) {
        for key in Array(entries.keys) where predicate(key) { remove(key) }
    }

    mutating func removeAll() {
        entries.removeAll()
        bytes = 0
        clock = 0
    }
}

extension AgentSession {
    var activityCacheCost: Int {
        // Allow for array capacity, both parser/result ownership, strings and dictionary/set entries.
        512 + events.count * 32 + (id.utf8.count + cwd.utf8.count + title.utf8.count + (parentID?.utf8.count ?? 0)) * 2
            + branches.reduce(0) { $0 + 64 + $1.utf8.count * 2 }
    }
}


extension ActivityRepository {
    var activityCacheCost: Int {
        128 + (key.utf8.count + name.utf8.count + (origin?.utf8.count ?? 0) + (identity?.utf8.count ?? 0)) * 2
    }
}
