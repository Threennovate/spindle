import { TZDate } from '@date-fns/tz'
import type { TimeEntry } from '@/api/types'
import { addDaysToKey, startOfDayKey } from '@/lib/time/zone'

export const MINUTES_PER_DAY = 24 * 60

/** The part of an entry that falls on one day, in minutes of that day's wall clock. */
export interface DaySegment {
  entry: TimeEntry
  day: string
  startMinute: number
  endMinute: number
  /** The entry continues from the previous day or into the next one. */
  continuesBefore: boolean
  continuesAfter: boolean
  /** Side-by-side placement among overlapping segments: this segment's lane out of `lanes`. */
  lane: number
  lanes: number
}

/** The wall-clock minute of the day (0–1440) of an instant in the zone. */
export function minuteOfDay(instant: Date, timeZone: string): number {
  const local = new TZDate(instant.getTime(), timeZone)
  return local.getHours() * 60 + local.getMinutes() + local.getSeconds() / 60
}

/**
 * Splits entries into per-day segments for the given days (an entry past midnight appears on both days) and
 * places overlapping segments in lanes, the way calendars show double bookings.
 */
export function layoutDays(entries: readonly TimeEntry[], days: readonly string[], timeZone: string, now: number): DaySegment[] {
  return days.flatMap((day) => {
    const dayStart = startOfDayKey(day, timeZone).getTime()
    const dayEnd = startOfDayKey(addDaysToKey(day, 1), timeZone).getTime()

    const segments = entries
      .map((entry) => ({ entry, start: Date.parse(entry.start), end: entry.stop ? Date.parse(entry.stop) : now }))
      .filter(({ start, end }) => start < dayEnd && end > dayStart)
      .map(({ entry, start, end }) => ({
        entry,
        day,
        startMinute: start <= dayStart ? 0 : minuteOfDay(new Date(start), timeZone),
        endMinute: end >= dayEnd ? MINUTES_PER_DAY : minuteOfDay(new Date(end), timeZone),
        continuesBefore: start < dayStart,
        continuesAfter: end > dayEnd,
      }))

    return assignLanes(segments)
  })
}

function assignLanes<T extends { startMinute: number; endMinute: number }>(segments: T[]): (T & { lane: number; lanes: number })[] {
  const sorted = [...segments].sort((a, b) => a.startMinute - b.startMinute || b.endMinute - a.endMinute)
  const placed: (T & { lane: number; lanes: number })[] = []

  // Segments that overlap, directly or through each other, form a cluster that shares the column's width.
  let cluster: (T & { lane: number; lanes: number })[] = []
  let laneEnds: number[] = []
  let clusterEnd = -1

  const closeCluster = () => {
    for (const segment of cluster) {
      segment.lanes = laneEnds.length
    }
    placed.push(...cluster)
    cluster = []
    laneEnds = []
  }

  for (const segment of sorted) {
    if (segment.startMinute >= clusterEnd && cluster.length > 0) {
      closeCluster()
    }
    // A zero-length segment still needs a sliver of room.
    const end = Math.max(segment.endMinute, segment.startMinute + 1)
    let lane = laneEnds.findIndex((laneEnd) => laneEnd <= segment.startMinute)
    if (lane === -1) {
      lane = laneEnds.length
      laneEnds.push(end)
    } else {
      laneEnds[lane] = end
    }
    cluster.push({ ...segment, lane, lanes: 0 })
    clusterEnd = Math.max(clusterEnd, end)
  }
  closeCluster()

  return placed
}

/** Rounds minutes to the nearest step, e.g. 15 minutes, within the day. */
export function snapMinutes(minutes: number, step: number): number {
  return Math.min(MINUTES_PER_DAY, Math.max(0, Math.round(minutes / step) * step))
}
