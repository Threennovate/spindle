import type { TimeEntry } from '@/api/types'
import { addDaysToKey, dayKey, startOfDayKey } from '@/lib/time/zone'
import { allocate, isSplit, partsOf } from '../time-entries/split'

/** A project's time in the week; `projectId` null is time on no project. */
export interface ProjectWeek {
  projectId: string | null
  seconds: number
  /** The part of it that came from entries split between projects. */
  splitSeconds: number
  /** Its seconds on each day of the week, in the week's order. */
  days: number[]
}

export interface WeekSummary {
  days: readonly string[]
  dayTotals: number[]
  seconds: number
  billableSeconds: number
  /** Time in entries split between projects, and how many of them. */
  splitSeconds: number
  splitEntries: number
  /** The projects with time in the week, the most time first. */
  projects: ProjectWeek[]
}

/**
 * The week's time as reports count it: each entry on the day it starts, its whole seconds divided between its parts,
 * each part on its own project. A running entry counts until `now`, so the week follows the timer.
 */
export function weekSummary(entries: readonly TimeEntry[], days: readonly string[], timeZone: string, now: number): WeekSummary {
  const index = new Map(days.map((day, i) => [day, i]))
  const dayTotals = days.map(() => 0)
  const projects = new Map<string | null, ProjectWeek>()
  let billableSeconds = 0
  let splitSeconds = 0
  let splitEntries = 0

  for (const entry of entries) {
    const day = index.get(dayKey(entry.start, timeZone))
    if (day === undefined) {
      continue
    }
    const seconds = entrySeconds(entry, now)
    const parts = partsOf(entry)
    const split = isSplit(entry)
    const divided = allocate(seconds, parts.map((part) => part.percent))
    dayTotals[day] += seconds
    if (split) {
      splitSeconds += seconds
      splitEntries += 1
    }
    parts.forEach((part, i) => {
      const project = projects.get(part.projectId) ?? { projectId: part.projectId, seconds: 0, splitSeconds: 0, days: days.map(() => 0) }
      project.seconds += divided[i]
      project.days[day] += divided[i]
      if (split) {
        project.splitSeconds += divided[i]
      }
      if (part.isBillable) {
        billableSeconds += divided[i]
      }
      projects.set(part.projectId, project)
    })
  }

  return {
    days,
    dayTotals,
    seconds: dayTotals.reduce((sum, seconds) => sum + seconds, 0),
    billableSeconds,
    splitSeconds,
    splitEntries,
    projects: [...projects.values()].filter((project) => project.seconds > 0).sort((a, b) => b.seconds - a.seconds),
  }
}

/**
 * A week as it stood at `cutoff`: its entries that started before it, each counted up to it. Last week "by this time"
 * is `weekBefore(lastWeek, lastWeekDays, timeZone, now - WEEK)`.
 */
export function weekBefore(entries: readonly TimeEntry[], days: readonly string[], timeZone: string, cutoff: number): WeekSummary {
  const started = entries
    .filter((entry) => Date.parse(entry.start) < cutoff)
    .map((entry) => (entry.stop && Date.parse(entry.stop) <= cutoff ? entry : { ...entry, stop: new Date(cutoff).toISOString() }))
  return weekSummary(started, days, timeZone, cutoff)
}

/** The time tracked by the end of each day of the week, for a running total. */
export function runningTotals(dayTotals: readonly number[]): number[] {
  let sum = 0
  return dayTotals.map((seconds) => (sum += seconds))
}

/** How far `now` is into the week, in days: 1.5 is midday on its second day, 0 before it begins, its length after. */
export function weekMoment(days: readonly string[], timeZone: string, now: number): number {
  const today = days.indexOf(dayKey(new Date(now), timeZone))
  if (today < 0) {
    return now < startOfDayKey(days[0], timeZone).getTime() ? 0 : days.length
  }
  const start = startOfDayKey(days[today], timeZone).getTime()
  const end = startOfDayKey(addDaysToKey(days[today], 1), timeZone).getTime()
  return today + (now - start) / (end - start)
}

/**
 * A running total as points of [days into the week, seconds]: nothing at its start, then the end of each day, and
 * `moment` where it falls inside a day. Drawing the week shown and the one before through the same moment lets the
 * line compare what the numbers compare: each as it stood by now.
 */
export function runningLine(dayTotals: readonly number[], moment?: { at: number; seconds: number }): [number, number][] {
  const points: [number, number][] = [[0, 0]]
  runningTotals(dayTotals).forEach((seconds, index) => {
    if (moment && moment.at > index && moment.at < index + 1) {
      points.push([moment.at, moment.seconds])
    }
    points.push([index + 1, seconds])
  })
  return points
}

/** Whole seconds, as the server counts them: a finished entry to its stop, a running one until `now`. */
function entrySeconds(entry: Pick<TimeEntry, 'start' | 'stop'>, now: number): number {
  return seconds(entry.start, entry.stop ? Date.parse(entry.stop) : now)
}

function seconds(start: string, end: number): number {
  return Math.max(0, Math.floor((end - Date.parse(start)) / 1000))
}

/**
 * Entries to pick up again, the latest first: one for each description and project (or split), finished ones only,
 * as the entry list continues them.
 */
export function recentEntries(entries: readonly TimeEntry[], limit: number): TimeEntry[] {
  const seen = new Set<string>()
  const recent: TimeEntry[] = []
  for (const entry of [...entries].filter((each) => each.stop).sort((a, b) => Date.parse(b.start) - Date.parse(a.start))) {
    const key = JSON.stringify([entry.description.trim().toLowerCase(), partsOf(entry).map((part) => [part.projectId, part.taskId, part.percent])])
    if (!seen.has(key)) {
      seen.add(key)
      recent.push(entry)
    }
    if (recent.length === limit) {
      break
    }
  }
  return recent
}
