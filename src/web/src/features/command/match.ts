/** A run of matched characters in a label, as [start, end) indexes. */
export type MatchRange = readonly [number, number]

export interface Match {
  score: number
  /** What to highlight in the label; empty when only a keyword matched. */
  ranges: MatchRange[]
}

// How well one typed word fits, best first. Predictable tiers instead of fuzzy scoring: a person can tell why a
// result is where it is.
const WHOLE_LABEL = 100
const FIRST_WORD_START = 80
const WORD_START = 70
const INITIALS = 55
const INSIDE = 35
// A keyword match counts for a little less than the same match in the label people read.
const KEYWORD_FACTOR = 0.8

// Letters with a stroke rather than an accent, which do not come apart in Unicode normalisation.
const STROKED: Record<string, string> = { đ: 'd', ł: 'l', ø: 'o', ħ: 'h' }

/** Lower case without accents, so `cakovec` finds `Čakovec`. Keeps the length, so indexes still fit the label. */
export function fold(text: string): string {
  return text
    .split('')
    .map((unit) => {
      const lower = unit.toLowerCase()
      const bare = lower.normalize('NFD').replace(/\p{M}/gu, '')
      return STROKED[lower] ?? (bare.length === 1 ? bare : lower.length === 1 ? lower : unit)
    })
    .join('')
}

/** Where words start: the first letter, and every letter after a space or punctuation, or a lower-to-upper step. */
function wordStarts(text: string): number[] {
  const starts: number[] = []
  for (let i = 0; i < text.length; i++) {
    const char = text[i]
    if (!/[\p{L}\p{N}]/u.test(char)) continue
    const before = text[i - 1]
    if (i === 0 || !/[\p{L}\p{N}]/u.test(before) || (/\p{Ll}/u.test(before) && /\p{Lu}/u.test(char))) {
      starts.push(i)
    }
  }
  return starts
}

function matchWord(label: string, folded: string, starts: number[], word: string): Match | null {
  if (folded === word) {
    return { score: WHOLE_LABEL, ranges: [[0, label.length]] }
  }
  for (const start of starts) {
    if (folded.startsWith(word, start)) {
      return { score: start === starts[0] ? FIRST_WORD_START : WORD_START, ranges: [[start, start + word.length]] }
    }
  }
  // `gtr` for "Go to Reports": each letter starts a word, in order.
  if (word.length >= 2 && word.length <= starts.length) {
    const ranges: MatchRange[] = []
    let next = 0
    for (const letter of word) {
      while (next < starts.length && folded[starts[next]] !== letter) next++
      if (next === starts.length) break
      ranges.push([starts[next], starts[next] + 1])
      next++
    }
    if (ranges.length === word.length) {
      return { score: INITIALS, ranges }
    }
  }
  const inside = folded.indexOf(word)
  if (inside >= 0) {
    return { score: INSIDE, ranges: [[inside, inside + word.length]] }
  }
  return null
}

/** How well `query` fits `label`: every typed word has to be found. Null when it does not fit. */
export function matchLabel(label: string, query: string): Match | null {
  const words = fold(query).split(/\s+/).filter(Boolean)
  if (words.length === 0) {
    return { score: 0, ranges: [] }
  }
  // "Tags…" is the whole label when "tags" is typed: the ellipsis only says a page follows.
  const folded = fold(label).replace(/…$/, '')
  const starts = wordStarts(label)
  // The whole query as one phrase reads better than its words found apart.
  const phrase = words.join(' ')
  if (words.length > 1) {
    const whole = matchWord(label, folded, starts, phrase)
    if (whole && whole.score > INITIALS) {
      return whole
    }
  }

  let score = 0
  const ranges: MatchRange[] = []
  for (const word of words) {
    const match = matchWord(label, folded, starts, word)
    if (!match) {
      return null
    }
    score += match.score
    ranges.push(...match.ranges)
  }
  return { score: score / words.length, ranges: merge(ranges) }
}

/** The best fit of `query` with a label or its keywords, e.g. "timer" for "Start the clock". */
export function matchItem(label: string, keywords: readonly string[], query: string): Match | null {
  let best = matchLabel(label, query)
  for (const keyword of keywords) {
    const match = matchLabel(keyword, query)
    if (match && (!best || match.score * KEYWORD_FACTOR > best.score)) {
      best = { score: match.score * KEYWORD_FACTOR, ranges: [] }
    }
  }
  // All words have to be found, but they may be spread over the label and the keywords: "start acme".
  if (!best) {
    const words = fold(query).split(/\s+/).filter(Boolean)
    if (words.length > 1) {
      const texts = [label, ...keywords]
      const found = words.map((word) => Math.max(...texts.map((text) => matchLabel(text, word)?.score ?? -1)))
      if (found.every((score) => score >= 0)) {
        const ranges = merge(words.flatMap((word) => matchLabel(label, word)?.ranges ?? []))
        best = { score: (found.reduce((sum, score) => sum + score, 0) / words.length) * KEYWORD_FACTOR, ranges }
      }
    }
  }
  return best
}

function merge(ranges: MatchRange[]): MatchRange[] {
  const sorted = [...ranges].sort((a, b) => a[0] - b[0])
  const merged: [number, number][] = []
  for (const [start, end] of sorted) {
    const last = merged[merged.length - 1]
    if (last && start <= last[1]) {
      last[1] = Math.max(last[1], end)
    } else {
      merged.push([start, end])
    }
  }
  return merged
}
