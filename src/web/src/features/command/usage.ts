import { useCallback, useState } from 'react'

/** How often and how lately a palette item was used. */
export interface Use {
  count: number
  /** Milliseconds since the epoch. */
  last: number
}

export type Usage = Readonly<Record<string, Use>>

const HALF_LIFE_DAYS = 14
const KEPT = 120
const DAY = 86_400_000

/** Usage after `id` was used once more at `now`. Only the most recent items are kept. */
export function recordUse(usage: Usage, id: string, now: number): Usage {
  const previous = usage[id]
  const next = { ...usage, [id]: { count: (previous?.count ?? 0) + 1, last: now } }
  const ids = Object.keys(next)
  if (ids.length <= KEPT) {
    return next
  }
  const kept = ids.sort((a, b) => next[b].last - next[a].last).slice(0, KEPT)
  return Object.fromEntries(kept.map((key) => [key, next[key]]))
}

/**
 * Between 0 and 1: often and recently used items score high, and an item's score halves every two weeks it goes
 * unused, so old habits fade.
 */
export function usageScore(use: Use | undefined, now: number): number {
  if (!use) {
    return 0
  }
  const weight = use.count * 0.5 ** (Math.max(0, now - use.last) / DAY / HALF_LIFE_DAYS)
  return weight / (weight + 2)
}

/** The ids used most, by score. */
export function mostUsed(usage: Usage, now: number, limit: number): string[] {
  return Object.keys(usage)
    .map((id) => ({ id, score: usageScore(usage[id], now) }))
    .filter(({ score }) => score > 0.15)
    .sort((a, b) => b.score - a.score)
    .slice(0, limit)
    .map(({ id }) => id)
}

function read(key: string): Usage {
  try {
    const stored = window.localStorage.getItem(key)
    const parsed: unknown = stored ? JSON.parse(stored) : {}
    return parsed && typeof parsed === 'object' ? (parsed as Usage) : {}
  } catch {
    return {}
  }
}

/** What this person used in the palette, remembered in this browser per workspace. */
export function useUsage(workspaceId: string) {
  const key = `spindle:palette-usage:${workspaceId}`
  const [usage, setUsage] = useState<Usage>(() => read(key))

  const record = useCallback(
    (id: string) => {
      const next = recordUse(read(key), id, Date.now())
      setUsage(next)
      try {
        window.localStorage.setItem(key, JSON.stringify(next))
      } catch {
        // Not remembered; the palette still works, only without learning.
      }
    },
    [key],
  )

  return { usage, record }
}
