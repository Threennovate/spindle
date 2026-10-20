import Foundation

/// What a person uses in the quick entry panel, so often and recently used rows rank higher, the same as the web
/// palette (`features/command/usage.ts`): an item's weight halves every two weeks it goes unused.
public struct PaletteUsage: Codable, Equatable, Sendable {
    public struct Use: Codable, Equatable, Sendable {
        public var count: Int
        public var last: Date
    }

    public private(set) var uses: [String: Use]

    static let halfLifeDays = 14.0
    static let kept = 120

    public init(uses: [String: Use] = [:]) {
        self.uses = uses
    }

    /// Records one more use of `id` at `now`. Only the most recently used items are kept.
    public mutating func record(_ id: String, now: Date = Date()) {
        uses[id] = Use(count: (uses[id]?.count ?? 0) + 1, last: now)
        if uses.count > Self.kept {
            let keep = Set(uses.sorted { $0.value.last > $1.value.last }.prefix(Self.kept).map(\.key))
            uses = uses.filter { keep.contains($0.key) }
        }
    }

    /// Between 0 and 1: often and recently used items score high, and old habits fade.
    public func score(_ id: String, now: Date = Date()) -> Double {
        guard let use = uses[id] else { return 0 }
        let days = max(0, now.timeIntervalSince(use.last)) / 86_400
        let weight = Double(use.count) * pow(0.5, days / Self.halfLifeDays)
        return weight / (weight + 2)
    }

    /// The ids used most, by score, leaving out what was barely used.
    public func mostUsed(now: Date = Date(), limit: Int) -> [String] {
        let scored: [(id: String, score: Double)] = uses.keys.map { ($0, score($0, now: now)) }.filter { $0.score > 0.15 }
        let sorted = scored.sorted { $0.score != $1.score ? $0.score > $1.score : $0.id < $1.id }
        return sorted.prefix(limit).map(\.id)
    }

    // MARK: - Kept in UserDefaults, per workspace

    private static func key(_ workspaceID: String) -> String {
        "paletteUsage.\(workspaceID)"
    }

    public static func load(workspaceID: String, from defaults: UserDefaults = .standard) -> PaletteUsage {
        defaults.data(forKey: key(workspaceID)).flatMap { try? JSONDecoder().decode(PaletteUsage.self, from: $0) } ?? PaletteUsage()
    }

    public func save(workspaceID: String, to defaults: UserDefaults = .standard) {
        if let data = try? JSONEncoder().encode(self) {
            defaults.set(data, forKey: Self.key(workspaceID))
        }
    }
}
