import { queryOptions } from '@tanstack/react-query'
import { api } from '@/api/client'
import { unwrap } from '@/api/unwrap'
import { addDaysToKey, dayKey, startOfDayKey, weekOf, type WeekStart } from '@/lib/time/zone'
import { entryKeys } from '../time-entries/queries'

/** The seven `yyyy-MM-dd` days of the week that contains `day`. */
export function weekDays(day: string, timeZone: string, weekStartsOn: WeekStart): string[] {
  const first = dayKey(weekOf(startOfDayKey(day, timeZone), timeZone, weekStartsOn).from, timeZone)
  return Array.from({ length: 7 }, (_, index) => addDaysToKey(first, index))
}

/** "22 – 28 Sep 2026", "29 Sep – 5 Oct 2026", or one day. */
export function formatRange(days: readonly string[]): string {
  const format = (key: string, options: Intl.DateTimeFormatOptions) => {
    const [year, month, day] = key.split('-').map(Number)
    return new Intl.DateTimeFormat('en-GB', options).format(new Date(Date.UTC(year, month - 1, day)))
  }
  const first = days[0]
  const last = days[days.length - 1]
  if (first === last) {
    return format(first, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' })
  }
  const sameMonth = first.slice(0, 7) === last.slice(0, 7)
  return `${format(first, sameMonth ? { day: 'numeric', timeZone: 'UTC' } : { day: 'numeric', month: 'short', timeZone: 'UTC' })} – ${format(last, { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' })}`
}

/**
 * The user's entries that overlap the given days. Entries are listed by start, so the range starts a day early to
 * include an entry that began the evening before and ran past midnight.
 */
export const rangeEntriesQuery = (workspaceId: string, days: readonly string[], timeZone: string) => {
  const from = startOfDayKey(addDaysToKey(days[0], -1), timeZone)
  const to = startOfDayKey(addDaysToKey(days[days.length - 1], 1), timeZone)
  return queryOptions({
    queryKey: [...entryKeys.all(workspaceId), 'range', from.toISOString(), to.toISOString()],
    queryFn: () =>
      unwrap(
        api.GET('/api/workspaces/{workspaceId}/time-entries', {
          params: { path: { workspaceId }, query: { from: from.toISOString(), to: to.toISOString() } },
        }),
      ),
  })
}
