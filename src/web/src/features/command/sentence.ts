import { parseClock, type Clock } from '@/lib/time/clock'
import { parseDuration } from '@/lib/time/duration'
import { addDaysToKey, atTime, dayKey, latestAt } from '@/lib/time/zone'

/**
 * One line that says what someone works on: "Review PR @Acme #review $ since 9:40".
 *
 * Free text is the description. Projects and tags come as chips the person picked from a list, never from the text
 * itself, so "Fix #482" stays as typed. `$` on its own makes the entry billable. A time is read only at the end of
 * the line and only in explicit forms, so a description that merely contains numbers stays text.
 *
 * Pure and deterministic: the Mac app reads the same lines the same way, proven by the shared vectors in
 * tests/vectors/entry-sentence.json.
 */

/** What was picked from the lists, shown as chips before the text. */
export type SentenceChip =
  | { kind: 'project'; projectId: string; taskId: string | null }
  | { kind: 'client'; clientId: string }
  | { kind: 'tag'; tagId: string }
  | { kind: 'billable' }

export interface SentenceOptions {
  /** With a 12-hour clock, a bare `3` since the morning reads as 15:00 when that is the more recent. */
  hour12: boolean
  /** The person's date format, e.g. `MM/dd/yyyy`: it decides whether `10/3` is 10 March or 3 October. */
  dateFormat: string
}

/** A day named before the time: `yesterday`, `mon`, `3.10.`, `2026-10-03`. */
export type DayRef =
  | { kind: 'today' }
  | { kind: 'yesterday' }
  /** 0 is Sunday, as in `Date.getDay`. */
  | { kind: 'weekday'; weekday: number }
  | { kind: 'date'; year: number | null; month: number; day: number }

/** A clock time as typed, and whether it said am or pm. */
export interface TypedClock extends Clock {
  meridiem: boolean
}

export type SentenceTime =
  /** `25m ago`, `-25`: running since then. */
  | { kind: 'ago'; seconds: number }
  /** `since 9:40`, `from 9:40`: running since then. */
  | { kind: 'since'; day: DayRef | null; at: TypedClock }
  /** `9:00-10:30`: a finished entry. */
  | { kind: 'range'; day: DayRef | null; from: TypedClock; to: TypedClock }
  /** `for 1h30`, `45m`: finished, ending now (or on another day, after its last entry). */
  | { kind: 'for'; day: DayRef | null; seconds: number }

export interface Sentence {
  description: string
  projectId: string | null
  taskId: string | null
  clientId: string | null
  tagIds: string[]
  billable: boolean
  time: SentenceTime | null
  /** The words read as the time, e.g. `yesterday 9:00-10:30`, so the preview can show what was understood. */
  timeText: string
}

const WEEKDAYS: Record<string, number> = {
  sun: 0, sunday: 0,
  mon: 1, monday: 1,
  tue: 2, tues: 2, tuesday: 2,
  wed: 3, wednesday: 3,
  thu: 4, thur: 4, thurs: 4, thursday: 4,
  fri: 5, friday: 5,
  sat: 6, saturday: 6,
}

/** Reads a line and the chips before it. */
export function readSentence(text: string, chips: readonly SentenceChip[], options: SentenceOptions): Sentence {
  const tokens = text.split(/\s+/).filter(Boolean)
  const found = readTime(tokens, options)
  const rest = found ? tokens.slice(0, found.index) : tokens

  const project = chips.find((chip) => chip.kind === 'project')
  const client = chips.find((chip) => chip.kind === 'client')
  return {
    description: rest.filter((token) => token !== '$').join(' '),
    projectId: project?.projectId ?? null,
    taskId: project?.taskId ?? null,
    clientId: client?.clientId ?? null,
    tagIds: [...new Set(chips.flatMap((chip) => (chip.kind === 'tag' ? [chip.tagId] : [])))],
    billable: rest.includes('$') || chips.some((chip) => chip.kind === 'billable'),
    time: found?.time ?? null,
    timeText: found ? tokens.slice(found.index).join(' ') : '',
  }
}

/** The time at the end of the tokens, and where it begins. The longest reading wins, e.g. a day before a range. */
function readTime(tokens: readonly string[], options: SentenceOptions): { index: number; time: SentenceTime } | null {
  const lower = tokens.map((token) => token.toLowerCase())
  const n = lower.length
  const last = lower[n - 1]
  if (!last) return null

  // ——— Times without a day: "25m ago", "-25" ———

  if (last === 'ago') {
    for (const width of [2, 1]) {
      const seconds = n - 1 - width >= 0 ? readDuration(lower.slice(n - 1 - width, n - 1), false) : null
      if (seconds) return { index: n - 1 - width, time: { kind: 'ago', seconds } }
    }
    return null
  }
  if (/^[-−]\d/.test(last)) {
    const seconds = readDuration([last.slice(1)], false)
    return seconds ? { index: n - 1, time: { kind: 'ago', seconds } } : null
  }

  // ——— Times that may have a day before them ———

  const timed = readDayTime(lower, (token) => readDayRef(token, options) !== null)
  if (!timed) return null
  const day = timed.index > 0 ? readDayRef(lower[timed.index - 1], options) : null
  if (day) {
    return { index: timed.index - 1, time: { ...timed.time, day } }
  }
  return timed
}

function readDayTime(
  lower: readonly string[],
  isDay: (token: string) => boolean,
): { index: number; time: Exclude<SentenceTime, { kind: 'ago' }> } | null {
  const n = lower.length
  const last = lower[n - 1]
  // A day or `from` before a range says it is a time, so "yesterday 9-10" is one while "Sprint 1-2" stays text.
  const saysTime = (index: number) => index > 0 && (lower[index - 1] === 'from' || isDay(lower[index - 1]))

  // "9:00-10:30", "9-10:30pm", "9 - 10:30", "from 9 to 10:30", "yesterday 9-10"
  let range: { index: number; from: TypedClock; to: TypedClock } | null = null
  const dash = /^([^-–]+)[-–]([^-–]+)$/.exec(last)
  if (dash) {
    range = withIndex(readRange(dash[1], dash[2], saysTime(n - 1)), n - 1)
  } else if (n >= 3 && (lower[n - 2] === '-' || lower[n - 2] === '–')) {
    range = withIndex(readRange(lower[n - 3], last, saysTime(n - 3)), n - 3)
  } else if (n >= 4 && lower[n - 2] === 'to' && lower[n - 4] === 'from') {
    range = withIndex(readRange(lower[n - 3], last, true), n - 4)
  }
  if (range) {
    const index = lower[range.index] !== 'from' && lower[range.index - 1] === 'from' ? range.index - 1 : range.index
    return { index, time: { kind: 'range', day: null, from: range.from, to: range.to } }
  }

  // "since 9:40", "from 9:40"
  if (n >= 2 && (lower[n - 2] === 'since' || lower[n - 2] === 'from')) {
    const at = readClock(last)
    if (at) return { index: n - 2, time: { kind: 'since', day: null, at } }
  }

  // "for 1h30", "for 1h 30m", "for 90"
  for (const width of [2, 1]) {
    if (n - 1 - width >= 0 && lower[n - 1 - width] === 'for') {
      const seconds = readDuration(lower.slice(n - width), false)
      if (seconds) return { index: n - 1 - width, time: { kind: 'for', day: null, seconds } }
    }
  }
  // "45m", "1h30", "1h 30m": only with a unit, as a bare number is too often part of the description.
  for (const width of [2, 1]) {
    if (n - width >= 0) {
      const seconds = readDuration(lower.slice(n - width), true)
      if (seconds) return { index: n - width, time: { kind: 'for', day: null, seconds } }
    }
  }
  return null
}

const withIndex = <T extends object>(value: T | null, index: number) => value && { ...value, index }

/** A clock time as typed, e.g. `9`, `9:40`, `940` or `9:40pm`. */
function readClock(token: string): TypedClock | null {
  const clock = parseClock(token)
  return clock && { ...clock, meridiem: /[ap]\.?m?\.?$/.test(token) }
}

/**
 * Both ends of a range. Unless the words before it say it is a time, at least one end has to look like a clock time
 * (`9:00`, `9.30`, `9h`, `9pm`), so "2025-2026" or "Sprint 1-2" stay text. A bare end takes the other end's am or pm
 * when that keeps the range in order.
 */
function readRange(fromText: string, toText: string, saysTime: boolean): { from: TypedClock; to: TypedClock } | null {
  const looksLikeClock = (text: string) => /[:.h]|[ap]\.?m?\.?$/.test(text)
  if (!saysTime && !looksLikeClock(fromText) && !looksLikeClock(toText)) return null
  let from = readClock(fromText)
  let to = readClock(toText)
  if (!from || !to) return null

  // "9-10:30pm" is 21:00 to 22:30, and "11-1pm" 11:00 to 13:00.
  if (to.meridiem && !from.meridiem && from.hours <= 12) {
    const sameHalf = { hours: (from.hours % 12) + (to.hours >= 12 ? 12 : 0), minutes: from.minutes, meridiem: true }
    from = minutesOf(sameHalf) < minutesOf(to) ? sameHalf : { ...from, meridiem: true }
  } else if (from.meridiem && !to.meridiem && to.hours <= 12) {
    const sameHalf = { hours: (to.hours % 12) + (from.hours >= 12 ? 12 : 0), minutes: to.minutes, meridiem: true }
    to = minutesOf(sameHalf) > minutesOf(from) ? sameHalf : { ...to, meridiem: true }
  }
  if (minutesOf(from) === minutesOf(to)) return null
  return { from, to }
}

const minutesOf = (clock: Clock) => clock.hours * 60 + clock.minutes

const UNITS: Record<string, 'h' | 'm'> = { h: 'h', hr: 'h', hrs: 'h', hour: 'h', hours: 'h', m: 'm', min: 'm', mins: 'm', minute: 'm', minutes: 'm' }

/**
 * A duration in one or two tokens: `45m`, `25 min`, `1h30`, `1h 30m`, `1.5h`, `1:30`, `90`. With `needsUnit`, only
 * forms with hours or minutes written out count. Returns seconds, or null when it is not a duration or is nothing.
 */
function readDuration(tokens: readonly string[], needsUnit: boolean): number | null {
  let text: string
  if (tokens.length === 2) {
    const [first, second] = tokens
    if (Object.hasOwn(UNITS, second) && /^\d+(?:[.,]\d+)?$/.test(first)) {
      text = first + UNITS[second]
    } else if (/^\d+(?:[.,]\d+)?h$/.test(first) && /^\d{1,2}(?:m|min)?$/.test(second)) {
      text = first + second
    } else {
      return null
    }
  } else {
    text = tokens[0].replace(/^(\d+(?:[.,]\d+)?)([a-z]+)$/, (whole, digits: string, unit: string) => (Object.hasOwn(UNITS, unit) ? digits + UNITS[unit] : whole))
  }

  let seconds: number | null
  const hours = /^(\d+(?:[.,]\d+)?)h(?:(\d{1,2})(?:m|min)?)?$/.exec(text)
  const minutes = /^(\d+)(?:m|min)$/.exec(text)
  if (hours) {
    seconds = Math.round(Number(hours[1].replace(',', '.')) * 3600) + Number(hours[2] ?? 0) * 60
  } else if (minutes) {
    seconds = Number(minutes[1]) * 60
  } else {
    seconds = needsUnit ? null : parseDuration(text)
  }
  return seconds && seconds > 0 ? seconds : null
}

/** A day as typed: `yesterday`, `mon`, `3.10.`, `10/3` (in the person's date order) or `2026-10-03`. */
export function readDayRef(token: string, options: SentenceOptions): DayRef | null {
  if (token === 'today') return { kind: 'today' }
  if (token === 'yesterday') return { kind: 'yesterday' }
  if (Object.hasOwn(WEEKDAYS, token)) return { kind: 'weekday', weekday: WEEKDAYS[token] }

  let year: number | null = null
  let month: number
  let day: number
  const iso = /^(\d{4})-(\d{1,2})-(\d{1,2})$/.exec(token)
  const dotted = /^(\d{1,2})\.(\d{1,2})\.(\d{2}|\d{4})?$/.exec(token)
  const slashed = /^(\d{1,2})\/(\d{1,2})(?:\/(\d{2}|\d{4}))?$/.exec(token)
  if (iso) {
    ;[year, month, day] = [Number(iso[1]), Number(iso[2]), Number(iso[3])]
  } else if (dotted) {
    // Dots are always day first: 3.10. is 3 October.
    ;[day, month] = [Number(dotted[1]), Number(dotted[2])]
    year = dotted[3] ? fullYear(dotted[3]) : null
  } else if (slashed) {
    const monthFirst = options.dateFormat.startsWith('M')
    ;[month, day] = monthFirst ? [Number(slashed[1]), Number(slashed[2])] : [Number(slashed[2]), Number(slashed[1])]
    year = slashed[3] ? fullYear(slashed[3]) : null
  } else {
    return null
  }
  if (month < 1 || month > 12 || day < 1 || day > daysIn(year ?? 2000, month)) return null
  return { kind: 'date', year, month, day }
}

const fullYear = (text: string) => (text.length === 2 ? 2000 + Number(text) : Number(text))
const daysIn = (year: number, month: number) => new Date(Date.UTC(year, month, 0)).getUTCDate()

// ——— Resolving to instants ———

export interface SentenceSpan {
  start: Date
  /** Null while it runs. */
  stop: Date | null
}

export interface ResolveContext {
  now: Date
  timeZone: string
  hour12: boolean
  /** When the entries of a day end, to place a duration on another day after them; 9:00 when there are none. */
  dayEnds?: (day: string) => readonly Date[]
}

const DEFAULT_START_HOUR = 9
const MINUTE = 60_000

/** The `yyyy-MM-dd` day a day reference points to: the latest such day that is not after today. */
export function resolveDay(ref: DayRef, today: string): string {
  switch (ref.kind) {
    case 'today':
      return today
    case 'yesterday':
      return addDaysToKey(today, -1)
    case 'weekday': {
      const [y, m, d] = today.split('-').map(Number)
      const weekday = new Date(Date.UTC(y, m - 1, d)).getUTCDay()
      return addDaysToKey(today, -((weekday - ref.weekday + 7) % 7))
    }
    case 'date': {
      const key = (year: number) => `${year}-${String(ref.month).padStart(2, '0')}-${String(ref.day).padStart(2, '0')}`
      if (ref.year !== null) return key(ref.year)
      const thisYear = Number(today.slice(0, 4))
      return key(thisYear) <= today ? key(thisYear) : key(thisYear - 1)
    }
  }
}

/** When the line's entry starts and stops. Without a time, it starts now. */
export function resolveSentence(time: SentenceTime | null, context: ResolveContext): SentenceSpan {
  const { now, timeZone } = context
  const today = dayKey(now, timeZone)
  const thisMinute = new Date(Math.floor(now.getTime() / MINUTE) * MINUTE)

  if (!time) {
    return { start: now, stop: null }
  }
  switch (time.kind) {
    case 'ago':
      return { start: new Date(Math.floor((now.getTime() - time.seconds * 1000) / MINUTE) * MINUTE), stop: null }
    case 'since': {
      const start = time.day ? atClock(resolveDay(time.day, today), time.at, context) : latestStart(time.at, context)
      return { start, stop: null }
    }
    case 'range': {
      const start = time.day ? atClock(resolveDay(time.day, today), time.from, context) : latestStart(time.from, context)
      const startDay = dayKey(start, timeZone)
      let stop = atTime(startDay, time.to.hours, time.to.minutes, timeZone)
      if (stop <= start) {
        // "9-5" on a 12-hour clock ends at 17:00; otherwise the range goes past midnight.
        const afternoon = context.hour12 && !time.to.meridiem && time.to.hours < 12 ? atTime(startDay, time.to.hours + 12, time.to.minutes, timeZone) : null
        stop = afternoon && afternoon > start ? afternoon : atTime(addDaysToKey(startDay, 1), time.to.hours, time.to.minutes, timeZone)
      }
      return { start, stop }
    }
    case 'for': {
      const day = time.day ? resolveDay(time.day, today) : null
      if (!day || day === today) {
        return { start: new Date(thisMinute.getTime() - time.seconds * 1000), stop: thisMinute }
      }
      // Another day: after that day's last entry, or at 9:00, as the timesheet adds time.
      const ends = (context.dayEnds?.(day) ?? []).map((end) => end.getTime())
      const dayEnd = atTime(addDaysToKey(day, 1), 0, 0, timeZone).getTime()
      const start = Math.min(Math.max(atTime(day, DEFAULT_START_HOUR, 0, timeZone).getTime(), ...ends), dayEnd - time.seconds * 1000)
      return { start: new Date(start), stop: new Date(start + time.seconds * 1000) }
    }
  }
}

/** A clock time on a day, on a 12-hour clock the afternoon's when only that has passed, e.g. `since 3` at 16:00. */
function atClock(day: string, at: TypedClock, { now, timeZone, hour12 }: ResolveContext): Date {
  const morning = atTime(day, at.hours, at.minutes, timeZone)
  if (!hour12 || at.meridiem || at.hours >= 12) return morning
  const afternoon = atTime(day, at.hours + 12, at.minutes, timeZone)
  return afternoon <= now ? afternoon : morning
}

/** The latest instant at this clock time that has passed; on a 12-hour clock, the morning's or the afternoon's. */
function latestStart(at: TypedClock, { now, timeZone, hour12 }: ResolveContext): Date {
  const latest = latestAt(at, now, timeZone)
  if (!hour12 || at.meridiem || at.hours >= 12) return latest
  const afternoon = latestAt({ hours: at.hours + 12, minutes: at.minutes }, now, timeZone)
  return afternoon > latest ? afternoon : latest
}

// ——— What is typed at the caret ———

/** A `@project` or `#tag` being typed at the caret, which the palette completes from a list. */
export interface Mention {
  kind: '@' | '#'
  /** What is typed after the sign. */
  query: string
  /** Where the mention starts and ends in the text, sign included. */
  start: number
  end: number
}

/** The mention the caret is in or right after, if any. */
export function mentionAt(text: string, caret: number): Mention | null {
  let start = caret
  while (start > 0 && !/\s/.test(text[start - 1])) start--
  let end = caret
  while (end < text.length && !/\s/.test(text[end])) end++
  const sign = text[start]
  if (sign !== '@' && sign !== '#') return null
  return { kind: sign, query: text.slice(start + 1, end), start, end }
}

/** The text without the mention, keeping one space where it was. */
export function withoutMention(text: string, mention: Mention): { text: string; caret: number } {
  const before = text.slice(0, mention.start).replace(/\s+$/, '')
  const after = text.slice(mention.end).replace(/^\s+/, '')
  const joined = before && after ? `${before} ${after}` : before || after
  return { text: before && !after ? `${joined} ` : joined, caret: before ? before.length + 1 : 0 }
}
