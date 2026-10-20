import { TZDate } from '@date-fns/tz'
import { addDays, format, isSameYear, startOfDay, startOfWeek, subDays } from 'date-fns'
import { formatClockField, parseClock, type Clock } from './clock'
import { parseDuration } from './duration'

/**
 * Date handling in the user's time zone from their profile, not the browser's: a day, a week and a clock time
 * mean the same on every device the user signs in from.
 */

export type WeekStart = 0 | 1 | 2 | 3 | 4 | 5 | 6

/** The calendar day an instant falls on in the zone, as `yyyy-MM-dd`. */
export function dayKey(instant: Date | string, timeZone: string): string {
  return format(new TZDate(new Date(instant).getTime(), timeZone), 'yyyy-MM-dd')
}

/** Midnight at the start of the given `yyyy-MM-dd` day in the zone. */
export function startOfDayKey(key: string, timeZone: string): Date {
  const [year, month, day] = key.split('-').map(Number)
  return new Date(new TZDate(year, month - 1, day, timeZone).getTime())
}

/** The instant in the zone for a `yyyy-MM-dd` day and a clock time. */
export function atTime(key: string, hours: number, minutes: number, timeZone: string): Date {
  const [year, month, day] = key.split('-').map(Number)
  return new Date(new TZDate(year, month - 1, day, hours, minutes, timeZone).getTime())
}

export function addDaysToKey(key: string, days: number): string {
  const [year, month, day] = key.split('-').map(Number)
  return format(addDays(new Date(year, month - 1, day), days), 'yyyy-MM-dd')
}

/** The week that contains the instant: [from, to) in the zone. */
export function weekOf(instant: Date, timeZone: string, weekStartsOn: WeekStart): { from: Date; to: Date } {
  const start = startOfWeek(startOfDay(new TZDate(instant.getTime(), timeZone)), { weekStartsOn })
  return { from: new Date(start.getTime()), to: new Date(addDays(start, 7).getTime()) }
}

/** A clock time such as `09:05`, or `9:05 AM` with a 12-hour clock. */
export function formatClock(instant: Date | string, timeZone: string, hour12 = false): string {
  return new Intl.DateTimeFormat('en-GB', {
    timeZone,
    hour: hour12 ? 'numeric' : '2-digit',
    minute: '2-digit',
    hour12,
  }).format(new Date(instant))
}

/** A clock time for an input field: `09:05`, or `9:05 am` with a 12-hour clock. */
export function clockInputValue(instant: Date | string, timeZone: string, hour12 = false): string {
  return formatClockField(clockAt(instant, timeZone), hour12)
}

/** The hours and minutes on the clock in the zone at an instant. */
export function clockAt(instant: Date | string, timeZone: string): Clock {
  const zoned = new TZDate(new Date(instant).getTime(), timeZone)
  return { hours: zoned.getHours(), minutes: zoned.getMinutes() }
}

/**
 * Reads what a person typed in a clock field: a clock time such as `930` or `9:30` (see `parseClock`), `now`, or
 * how long ago after a minus: `-15` is 15 minutes ago, `-1:30` or `-1.5` an hour and a half ago.
 */
export function readClockField(text: string, now: Date, timeZone: string): Clock | null {
  const value = text.trim().toLowerCase()
  if (value === 'now') {
    return clockAt(now, timeZone)
  }
  if (value.startsWith('-')) {
    const seconds = parseDuration(value.slice(1))
    return seconds === null ? null : clockAt(new Date(now.getTime() - seconds * 1000), timeZone)
  }
  return parseClock(value)
}

/**
 * When something started, from what was typed in a clock field: `-15` is 15 minutes before `now`, and a clock time
 * is the latest instant at that time that isn't after `now`. Counted in whole minutes.
 */
export function readStartField(text: string, now: Date, timeZone: string): Date | null {
  const value = text.trim()
  if (value.startsWith('-')) {
    const seconds = parseDuration(value.slice(1))
    return seconds === null ? null : new Date(Math.floor((now.getTime() - seconds * 1000) / 60_000) * 60_000)
  }
  const clock = readClockField(value, now, timeZone)
  return clock && latestAt(clock, now, timeZone)
}

/** The latest instant at this clock time that isn't after `now`: today's, or yesterday's if today's is still to come. */
export function latestAt(clock: Clock, now: Date, timeZone: string): Date {
  const today = dayKey(now, timeZone)
  const start = atTime(today, clock.hours, clock.minutes, timeZone)
  return start > now ? atTime(addDaysToKey(today, -1), clock.hours, clock.minutes, timeZone) : start
}

/** `Today`, `Yesterday`, `Mon, 22 Sep`, or with the year when it is not this year. */
export function formatDayHeading(key: string, timeZone: string, now: Date): string {
  const today = dayKey(now, timeZone)
  if (key === today) {
    return 'Today'
  }
  if (key === dayKey(subDays(new TZDate(now.getTime(), timeZone), 1), timeZone)) {
    return 'Yesterday'
  }

  const [year, month, day] = key.split('-').map(Number)
  const date = new Date(year, month - 1, day)
  return format(date, isSameYear(date, new TZDate(now.getTime(), timeZone)) ? 'EEE, d MMM' : 'EEE, d MMM yyyy')
}

/** The time zone the browser runs in, e.g. to offer it when the profile's differs. */
export function browserTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone
}

/**
 * The week `weeksBack` weeks before the current one, as [from, to). Computed by calendar days, so a week is
 * always seven local days even when daylight saving time makes it 167 or 169 hours long.
 */
export function weekBefore(weeksBack: number, now: Date, timeZone: string, weekStartsOn: WeekStart): { from: Date; to: Date } {
  const thisWeek = dayKey(weekOf(now, timeZone, weekStartsOn).from, timeZone)
  const first = addDaysToKey(thisWeek, -7 * weeksBack)
  return { from: startOfDayKey(first, timeZone), to: startOfDayKey(addDaysToKey(first, 7), timeZone) }
}

/** A `yyyy-MM-dd` day in the person's chosen date format, e.g. `28.09.2026` for `dd.MM.yyyy`. */
export function formatDayAs(key: string, pattern: string): string {
  const [year, month, day] = key.split('-').map(Number)
  return format(new Date(year, month - 1, day), pattern)
}
