import { matchItem } from './match'
import type { PaletteItem } from './types'

/** Ranks items for a query: the best match first, then items used more often. */
export function rank(items: readonly PaletteItem[], query: string, usageScore: (id: string) => number) {
  return items
    .map((item) => ({ item, match: matchItem(item.label, item.keywords ?? [], query) }))
    .filter((scored): scored is { item: PaletteItem; match: NonNullable<typeof scored.match> } => scored.match !== null)
    // What can be done with the running timer comes first among equals: it is what is going on now.
    .map(({ item, match }) => ({ item, score: match.score + 15 * usageScore(item.id) + (item.boost ?? 0) + (item.section === 'running' ? 5 : 0) }))
    .sort((a, b) => b.score - a.score)
}
