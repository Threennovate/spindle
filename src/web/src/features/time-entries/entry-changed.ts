import type { TimeEntry } from '@/api/types'
import { dayKey, formatClock, formatDayHeading } from '@/lib/time/zone'

/** What a write found changed elsewhere, and what the person was doing: saving their version or deleting the entry. */
export interface EntryChange {
  current: TimeEntry
  action: 'save' | 'delete'
}

/** One line for the entry as another device left it: its description, project and time. */
export function changedEntrySummary(entry: TimeEntry, projectName: string | undefined, timeZone: string, hour12: boolean, now: Date): string {
  const description = entry.description ? `“${entry.description}”` : 'No description'
  const day = formatDayHeading(dayKey(new Date(entry.start), timeZone), timeZone, now)
  const start = formatClock(entry.start, timeZone, hour12)
  const time = entry.stop ? `${day}, ${start}–${formatClock(entry.stop, timeZone, hour12)}` : `${day}, running since ${start}`
  return [description, projectName, time].filter(Boolean).join(' · ')
}
