import type { TimeEntry } from '@/api/types'
import { dayKey } from '@/lib/time/zone'

export interface DayGroup {
  /** `yyyy-MM-dd` in the user's time zone. */
  key: string
  entries: TimeEntry[]
  totalSeconds: number
}

/** How long an entry lasted, or has been running until `now`. */
export function entrySeconds(entry: Pick<TimeEntry, 'start' | 'stop'>, now: number): number {
  const end = entry.stop ? Date.parse(entry.stop) : now
  return Math.max(0, Math.floor((end - Date.parse(entry.start)) / 1000))
}

/** Groups entries by the day they start on in the zone, newest day and entry first. */
export function groupByDay(entries: readonly TimeEntry[], timeZone: string, now: number): DayGroup[] {
  const days = new Map<string, TimeEntry[]>()
  for (const entry of entries) {
    const key = dayKey(entry.start, timeZone)
    days.set(key, [...(days.get(key) ?? []), entry])
  }

  return [...days.entries()]
    .sort(([a], [b]) => b.localeCompare(a))
    .map(([key, dayEntries]) => {
      const sorted = [...dayEntries].sort((a, b) => Date.parse(b.start) - Date.parse(a.start))
      return { key, entries: sorted, totalSeconds: sorted.reduce((sum, entry) => sum + entrySeconds(entry, now), 0) }
    })
}
