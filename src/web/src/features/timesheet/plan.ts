import type { TimeEntry } from '@/api/types'
import { addDaysToKey, atTime, startOfDayKey } from '@/lib/time/zone'

/** One change to make so a timesheet cell shows the time the user typed. */
export type CellOperation =
  | { kind: 'create'; start: Date; stop: Date }
  | { kind: 'shorten'; entry: TimeEntry; stop: Date }
  | { kind: 'delete'; entry: TimeEntry }

interface CellChange {
  /** The row's finished entries on that day. */
  rowEntries: readonly TimeEntry[]
  /** All of the user's entries on that day, to place new time after them. */
  dayEntries: readonly TimeEntry[]
  day: string
  timeZone: string
  targetSeconds: number
}

const DEFAULT_START_HOUR = 9

/**
 * Plans how to make a cell's total equal the typed time.
 *
 * More time is added as one new entry, after the day's last entry (or at 9:00) so it does not overlap anything.
 * Less time is taken from the row's latest entries first: they are shortened, or deleted when used up.
 */
export function planCellChange({ rowEntries, dayEntries, day, timeZone, targetSeconds }: CellChange): CellOperation[] {
  const seconds = (entry: TimeEntry) => (Date.parse(entry.stop!) - Date.parse(entry.start)) / 1000
  const current = rowEntries.reduce((sum, entry) => sum + seconds(entry), 0)
  const delta = Math.round(targetSeconds - current)

  if (delta > 0) {
    const dayStart = startOfDayKey(day, timeZone).getTime()
    const dayEnd = startOfDayKey(addDaysToKey(day, 1), timeZone).getTime()
    const lastEnd = Math.max(
      atTime(day, DEFAULT_START_HOUR, 0, timeZone).getTime(),
      ...dayEntries.map((entry) => (entry.stop ? Date.parse(entry.stop) : Date.now())).filter((end) => end <= dayEnd),
    )
    // Keep the new entry on its day, even if that means it starts before the day's other entries end.
    const start = Math.max(dayStart, Math.min(lastEnd, dayEnd - delta * 1000))
    return [{ kind: 'create', start: new Date(start), stop: new Date(start + delta * 1000) }]
  }

  const operations: CellOperation[] = []
  let remaining = -delta
  const latestFirst = [...rowEntries].sort((a, b) => Date.parse(b.start) - Date.parse(a.start))
  for (const entry of latestFirst) {
    if (remaining <= 0) {
      break
    }
    const length = seconds(entry)
    if (length <= remaining) {
      operations.push({ kind: 'delete', entry })
      remaining -= length
    } else {
      operations.push({ kind: 'shorten', entry, stop: new Date(Date.parse(entry.stop!) - remaining * 1000) })
      remaining = 0
    }
  }
  return operations
}
