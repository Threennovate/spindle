import { addDaysToKey, type WeekStart } from '@/lib/time/zone'

/** Ready-made periods, as in Toggl, plus any range the person picks. */
export const PERIODS = ['today', 'yesterday', 'thisWeek', 'lastWeek', 'thisMonth', 'lastMonth', 'thisYear', 'lastYear', 'custom'] as const
export type PeriodKey = (typeof PERIODS)[number]

export const PERIOD_LABELS: Record<PeriodKey, string> = {
  today: 'Today',
  yesterday: 'Yesterday',
  thisWeek: 'This week',
  lastWeek: 'Last week',
  thisMonth: 'This month',
  lastMonth: 'Last month',
  thisYear: 'This year',
  lastYear: 'Last year',
  custom: 'Custom',
}

/** A range of whole days, both included, as `yyyy-MM-dd`. */
export interface DayRange {
  from: string
  to: string
}

/** The longest period a report covers, as the server allows. */
export const MAX_DAYS = 366

/** The days a period covers, counted from today in the user's calendar. */
export function resolvePeriod(period: PeriodKey, custom: Partial<DayRange>, today: string, weekStartsOn: WeekStart): DayRange {
  switch (period) {
    case 'today':
      return { from: today, to: today }
    case 'yesterday': {
      const yesterday = addDaysToKey(today, -1)
      return { from: yesterday, to: yesterday }
    }
    case 'thisWeek':
      return weekContaining(today, weekStartsOn)
    case 'lastWeek':
      return weekContaining(addDaysToKey(today, -7), weekStartsOn)
    case 'thisMonth':
      return monthContaining(today)
    case 'lastMonth':
      return monthContaining(addDaysToKey(`${today.slice(0, 7)}-01`, -1))
    case 'thisYear':
      return { from: `${today.slice(0, 4)}-01-01`, to: `${today.slice(0, 4)}-12-31` }
    case 'lastYear': {
      const year = Number(today.slice(0, 4)) - 1
      return { from: `${year}-01-01`, to: `${year}-12-31` }
    }
    case 'custom':
      return custom.from && custom.to && custom.from <= custom.to ? { from: custom.from, to: custom.to } : weekContaining(today, weekStartsOn)
  }
}

/**
 * The period before or after this one: a month moves by a month and a year by a year, so February follows January
 * whatever their lengths; anything else moves by its own number of days.
 */
export function shiftRange(range: DayRange, direction: -1 | 1): DayRange {
  if (isWholeMonths(range)) {
    const months = monthsBetween(range) * direction
    const from = addMonths(range.from, months)
    return { from, to: addDaysToKey(addMonths(from, monthsBetween(range)), -1) }
  }
  const days = dayCount(range) * direction
  return { from: addDaysToKey(range.from, days), to: addDaysToKey(range.to, days) }
}

export function weekContaining(day: string, weekStartsOn: WeekStart): DayRange {
  const weekday = new Date(`${day}T00:00:00Z`).getUTCDay()
  const from = addDaysToKey(day, -((weekday - weekStartsOn + 7) % 7))
  return { from, to: addDaysToKey(from, 6) }
}

export function dayCount({ from, to }: DayRange): number {
  return Math.round((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / 86_400_000) + 1
}

/** "14 – 20 Sep 2026", "28 Sep – 4 Oct 2026", "Mon, 14 Sep 2026". */
export function formatDayRange({ from, to }: DayRange): string {
  const format = (key: string, options: Intl.DateTimeFormatOptions) =>
    new Intl.DateTimeFormat('en-GB', { ...options, timeZone: 'UTC' }).format(new Date(`${key}T00:00:00Z`))
  if (from === to) {
    return format(from, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' })
  }
  if (from.slice(0, 4) !== to.slice(0, 4)) {
    return `${format(from, { day: 'numeric', month: 'short', year: 'numeric' })} – ${format(to, { day: 'numeric', month: 'short', year: 'numeric' })}`
  }
  const sameMonth = from.slice(0, 7) === to.slice(0, 7)
  return `${format(from, sameMonth ? { day: 'numeric' } : { day: 'numeric', month: 'short' })} – ${format(to, { day: 'numeric', month: 'short', year: 'numeric' })}`
}

function monthContaining(day: string): DayRange {
  const from = `${day.slice(0, 7)}-01`
  return { from, to: addDaysToKey(addMonths(from, 1), -1) }
}

function isWholeMonths({ from, to }: DayRange): boolean {
  return from.endsWith('-01') && addDaysToKey(to, 1).endsWith('-01')
}

function monthsBetween({ from, to }: DayRange): number {
  const next = addDaysToKey(to, 1)
  return (Number(next.slice(0, 4)) - Number(from.slice(0, 4))) * 12 + Number(next.slice(5, 7)) - Number(from.slice(5, 7))
}

/** Moves the first of a month by whole months. */
function addMonths(firstOfMonth: string, months: number): string {
  const index = Number(firstOfMonth.slice(0, 4)) * 12 + Number(firstOfMonth.slice(5, 7)) - 1 + months
  return `${Math.floor(index / 12)}-${String((index % 12) + 1).padStart(2, '0')}-01`
}
