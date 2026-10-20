import { formatMoney } from '@/lib/money'
import { formatHoursMinutes } from '@/lib/time/duration'
import type { Agreement, AgreementProfitability, RetainerPeriod } from './queries'

const dayMonth = new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', timeZone: 'UTC' })
const dayMonthYear = new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' })
const monthYear = new Intl.DateTimeFormat('en-GB', { month: 'long', year: 'numeric', timeZone: 'UTC' })

const asDate = (day: string) => new Date(`${day}T00:00:00Z`)

/** A day such as 15 Sep, with the year only when it is not this year's. */
export function formatDay(day: string, today: string): string {
  return (day.slice(0, 4) === today.slice(0, 4) ? dayMonth : dayMonthYear).format(asDate(day))
}

/** A retainer's period: "October 2026" for a calendar month, "2027" for a calendar year, else its first and last day. */
export function formatPeriod(period: Pick<RetainerPeriod, 'start' | 'end'>, today: string): string {
  const start = asDate(period.start)
  const next = asDate(period.end)
  next.setUTCDate(next.getUTCDate() + 1)
  if (period.start.endsWith('-01') && next.getUTCDate() === 1) {
    const months = (next.getUTCFullYear() - start.getUTCFullYear()) * 12 + next.getUTCMonth() - start.getUTCMonth()
    if (months === 1) return monthYear.format(start)
    if (months === 12 && period.start.endsWith('-01-01')) return period.start.slice(0, 4)
  }
  return `${formatDay(period.start, today)} – ${formatDay(period.end, today)}`
}

/** Hours as a duration, e.g. 7.5 → 7h 30m. */
export function formatHours(hours: number): string {
  return formatHoursMinutes(Math.round(hours * 3600))
}

/** What was agreed, in a few words: "€2,400", "€300 a month", "5h 00m a month". */
export function formatTerms(agreement: Agreement, currency: string): string {
  const per = agreement.period === 'year' ? ' a year' : ' a month'
  if (agreement.kind === 'fixedPrice') {
    return agreement.amount == null ? '—' : formatMoney(agreement.amount, currency)
  }
  return agreement.amount != null ? `${formatMoney(agreement.amount, currency)}${per}` : agreement.hours != null ? `${formatHours(agreement.hours)}${per}` : '—'
}

/** Where an agreement stands: in progress, done on a day, running since a day, or ended. */
export function agreementStatus(agreement: Agreement, today: string): { label: string; tone: 'active' | 'done' | 'muted' } {
  if (agreement.kind === 'fixedPrice') {
    return agreement.endsOn ? { label: `Done ${formatDay(agreement.endsOn, today)}`, tone: 'done' } : { label: 'In progress', tone: 'active' }
  }
  if (agreement.isOver && agreement.endsOn) {
    return { label: `Ended ${formatDay(agreement.endsOn, today)}`, tone: 'muted' }
  }
  if (agreement.startsOn > today) {
    return { label: `Starts ${formatDay(agreement.startsOn, today)}`, tone: 'muted' }
  }
  return { label: agreement.endsOn ? `Until ${formatDay(agreement.endsOn, today)}` : `Since ${formatDay(agreement.startsOn, today)}`, tone: 'active' }
}

/** Profit as a share of revenue, e.g. 43%; a dash without revenue. */
export function margin(profit: number, revenue: number): string {
  return revenue > 0 ? `${Math.round((profit / revenue) * 100)}%` : '—'
}

/** What an hour of the time earned: revenue over the hours tracked. */
export function earnedPerHour(item: Pick<AgreementProfitability, 'revenue' | 'trackedSeconds'>, currency: string): string | null {
  return item.trackedSeconds > 0 && item.revenue > 0 ? `${formatMoney(item.revenue / (item.trackedSeconds / 3600), currency)} earned per hour` : null
}
