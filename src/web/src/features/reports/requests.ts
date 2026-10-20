import type { WeekStart } from '@/lib/time/zone'
import { resolvePeriod, weekContaining, type DayRange, type PeriodKey } from './period'
import type { DetailedRequest, SummaryRequest, WeeklyRequest } from './queries'
import { DEFAULT_PERIOD, reportFilter, subGrouping, type ReportFilter, type ReportSearch, type ReportTab } from './search'

export const PAGE_SIZE = 50

/** What a report in the address bar asks the server for, on each of its tabs. */
export interface ReportRequests {
  tab: ReportTab
  period: PeriodKey
  range: DayRange
  filter: ReportFilter
  summary: SummaryRequest
  detailed: DetailedRequest
  weekly: WeeklyRequest
}

export function reportRequests(search: ReportSearch, today: string, weekStartsOn: WeekStart): ReportRequests {
  const tab = search.tab ?? 'summary'
  const period = search.period ?? DEFAULT_PERIOD
  const range = reportRange(tab, period, search, today, weekStartsOn)
  const filter = reportFilter(search, range)
  const groupBy = search.groupBy ?? 'project'
  return {
    tab,
    period,
    range,
    filter,
    summary: { ...filter, groupBy, subGroupBy: subGrouping(groupBy, search.subGroupBy) },
    detailed: { ...filter, sort: search.sort ?? 'date', descending: !search.asc, page: search.page ?? 1, pageSize: PAGE_SIZE },
    weekly: { ...filter, groupBy: groupBy === 'description' ? 'project' : groupBy },
  }
}

/**
 * The days the report covers. The weekly report always shows a whole week: this or last week, the week of a day
 * picked for it, or otherwise the latest week of the chosen period that has begun.
 */
function reportRange(tab: ReportTab, period: PeriodKey, search: ReportSearch, today: string, weekStartsOn: WeekStart): DayRange {
  const range = resolvePeriod(period, search, today, weekStartsOn)
  if (tab !== 'weekly') {
    return range
  }
  if (period === 'custom') {
    return weekContaining(range.from, weekStartsOn)
  }
  return weekContaining(range.to < today ? range.to : today, weekStartsOn)
}
