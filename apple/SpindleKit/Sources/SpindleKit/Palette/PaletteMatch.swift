import Foundation

/// How well what is typed fits a row of the quick entry panel, the same as the web palette (`features/command/match.ts`):
/// predictable tiers instead of fuzzy scoring, so a person can tell why a result is where it is.
public enum PaletteMatch {
    /// A run of matched characters in a label, as [start, end) character offsets.
    public typealias Range = (start: Int, end: Int)

    public struct Match {
        public var score: Double
        /// What to highlight in the label; empty when only a keyword matched.
        public var ranges: [Range]
    }

    // How well one typed word fits, best first.
    static let wholeLabel = 100.0
    static let firstWordStart = 80.0
    static let wordStart = 70.0
    static let initials = 55.0
    static let inside = 35.0
    // A keyword match counts for a little less than the same match in the label people read.
    static let keywordFactor = 0.8

    // Letters with a stroke rather than an accent, which do not come apart in Unicode normalisation.
    private static let stroked: [String: String] = ["đ": "d", "ł": "l", "ø": "o", "ħ": "h"]

    /// Lower case without accents, so `cakovec` finds `Čakovec`. One character for each of the label's.
    public static func fold(_ text: String) -> [Character] {
        text.map { character in
            let lower = String(character).lowercased()
            if let plain = stroked[lower] { return Character(plain) }
            let bare = String(String.UnicodeScalarView(lower.decomposedStringWithCanonicalMapping.unicodeScalars.filter { !$0.properties.isDiacritic || $0.properties.isAlphabetic }))
            if bare.count == 1 { return Character(bare) }
            return lower.count == 1 ? Character(lower) : character
        }
    }

    /// Where words start: the first letter, and every letter after a space or punctuation, or a lower-to-upper step.
    private static func wordStarts(_ text: [Character]) -> [Int] {
        text.indices.filter { i in
            let character = text[i]
            guard character.isLetter || character.isNumber else { return false }
            guard i > 0 else { return true }
            let before = text[i - 1]
            return !(before.isLetter || before.isNumber) || (before.isLowercase && character.isUppercase)
        }
    }

    private static func hasPrefix(_ folded: [Character], _ word: [Character], at start: Int) -> Bool {
        start + word.count <= folded.count && Array(folded[start..<(start + word.count)]) == word
    }

    private static func matchWord(_ label: [Character], _ folded: [Character], _ starts: [Int], _ word: [Character]) -> Match? {
        if folded == word {
            return Match(score: wholeLabel, ranges: [(0, label.count)])
        }
        for start in starts where hasPrefix(folded, word, at: start) {
            return Match(score: start == starts.first ? firstWordStart : wordStart, ranges: [(start, start + word.count)])
        }
        // `gtr` for "Go to Reports": each letter starts a word, in order.
        if word.count >= 2, word.count <= starts.count {
            var ranges: [Range] = []
            var next = 0
            for letter in word {
                while next < starts.count, folded[starts[next]] != letter { next += 1 }
                if next == starts.count { break }
                ranges.append((starts[next], starts[next] + 1))
                next += 1
            }
            if ranges.count == word.count {
                return Match(score: initials, ranges: ranges)
            }
        }
        if let inside = folded.indices.first(where: { hasPrefix(folded, word, at: $0) }) {
            return Match(score: Self.inside, ranges: [(inside, inside + word.count)])
        }
        return nil
    }

    private static func words(_ query: String) -> [[Character]] {
        fold(query).split(whereSeparator: \.isWhitespace).map(Array.init)
    }

    /// How well `query` fits `label`: every typed word has to be found. Nil when it does not fit.
    public static func label(_ label: String, query: String) -> Match? {
        let typed = words(query)
        guard !typed.isEmpty else { return Match(score: 0, ranges: []) }
        let characters = Array(label)
        // "Tags…" is the whole label when "tags" is typed: the ellipsis only says a page follows.
        var folded = fold(label)
        if folded.last == "…" { folded.removeLast() }
        let starts = wordStarts(characters)
        // The whole query as one phrase reads better than its words found apart.
        if typed.count > 1, let whole = matchWord(characters, folded, starts, Array(typed.joined(separator: " "))), whole.score > initials {
            return whole
        }

        var score = 0.0
        var ranges: [Range] = []
        for word in typed {
            guard let match = matchWord(characters, folded, starts, word) else { return nil }
            score += match.score
            ranges += match.ranges
        }
        return Match(score: score / Double(typed.count), ranges: merge(ranges))
    }

    /// The best fit of `query` with a label or its keywords, e.g. "timer" for "Start the clock".
    public static func item(_ label: String, keywords: [String] = [], query: String) -> Match? {
        var best = Self.label(label, query: query)
        for keyword in keywords {
            if let match = Self.label(keyword, query: query), best == nil || match.score * keywordFactor > best!.score {
                best = Match(score: match.score * keywordFactor, ranges: [])
            }
        }
        // All words have to be found, but they may be spread over the label and the keywords: "start acme".
        let typed = words(query)
        if best == nil, typed.count > 1 {
            let texts = [label] + keywords
            let found = typed.map { word in texts.map { Self.label($0, query: String(word))?.score ?? -1 }.max()! }
            if found.allSatisfy({ $0 >= 0 }) {
                let ranges = merge(typed.flatMap { Self.label(label, query: String($0))?.ranges ?? [] })
                best = Match(score: found.reduce(0, +) / Double(typed.count) * keywordFactor, ranges: ranges)
            }
        }
        return best
    }

    private static func merge(_ ranges: [Range]) -> [Range] {
        var merged: [Range] = []
        for range in ranges.sorted(by: { $0.start < $1.start }) {
            if let last = merged.last, range.start <= last.end {
                merged[merged.count - 1].end = max(last.end, range.end)
            } else {
                merged.append(range)
            }
        }
        return merged
    }

    /// Ranks rows for a query: the best match first, then rows used more often, as the web palette does.
    public static func rank<Row>(
        _ rows: [Row], query: String, label: (Row) -> String, keywords: (Row) -> [String] = { _ in [] },
        boost: (Row) -> Double = { _ in 0 }, usage: (Row) -> Double = { _ in 0 }
    ) -> [(row: Row, score: Double, match: Match)] {
        var scored: [(row: Row, score: Double, match: Match)] = []
        for row in rows {
            guard let match = item(label(row), keywords: keywords(row), query: query) else { continue }
            let score: Double = match.score + 15 * usage(row) + boost(row)
            scored.append((row, score, match))
        }
        // Sorted stably, so rows that score the same keep their order.
        let order = scored.indices.sorted { a, b in scored[a].score != scored[b].score ? scored[a].score > scored[b].score : a < b }
        return order.map { scored[$0] }
    }
}
