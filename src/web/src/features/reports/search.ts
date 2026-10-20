import type { components } from '@/api/schema'
import { PERIODS, type DayRange, type PeriodKey } from './period'

type Schemas = components['schemas']
export type ReportGrouping = Schemas['ReportGrouping']
export type ReportSort = Schemas['ReportSort']
export type RoundingMode = Schemas['RoundingMode']
export type ReportFilter = Omit<Schemas['SummaryReportRequest'], 'groupBy' | 'subGroupBy'>

export const TABS = ['summary', 'detailed', 'weekly'] as const
export type ReportTab = (typeof TABS)[number]

export const GROUPINGS = ['project', 'client', 'user', 'tag', 'task', 'description'] as const satisfies readonly ReportGrouping[]
export const GROUPING_LABELS: Record<ReportGrouping, string> = {
  project: 'Project',
  client: 'Client',
  user: 'Member',
  tag: 'Tag',
  task: 'Task',
  description: 'Description',
}

const SORTS = ['date', 'duration', 'description', 'user', 'project'] as const satisfies readonly ReportSort[]
export const ROUNDINGS = ['none', 'up', 'nearest', 'down'] as const satisfies readonly RoundingMode[]
export const ROUNDING_MINUTES = [1, 5, 6, 10, 12, 15, 30, 60] as const

/** In the id filters, the empty id stands for "without", e.g. time without a project. */
export const NONE_ID = '00000000-0000-0000-0000-000000000000'

/** The views built into the web app (see views.ts); they are defined here, not stored. */
export const VIEW_IDS = ['my-week', 'my-month', 'yesterday', 'billable-last-month', 'team-week', 'who-worked', 'billable-by-client', 'no-project'] as const
export type ViewId = (typeof VIEW_IDS)[number]

/**
 * A report as it is kept in the address bar, so it can be bookmarked, shared and saved. Everything is optional;
 * what is left out takes its default.
 */
export interface ReportSearch {
  tab?: ReportTab
  period?: PeriodKey
  from?: string
  to?: string
  users?: string[]
  groups?: string[]
  projects?: string[]
  clients?: string[]
  tags?: string[]
  billable?: 'yes' | 'no'
  /** Text the descriptions contain. */
  q?: string
  groupBy?: ReportGrouping
  subGroupBy?: ReportGrouping | 'none'
  sort?: ReportSort
  asc?: boolean
  page?: number
  rounding?: RoundingMode
  roundTo?: number
  /** The saved report being looked at, if any. */
  saved?: string
  /** The built-in view being looked at, if any. */
  view?: ViewId
}

export const DEFAULT_PERIOD: PeriodKey = 'thisWeek'
export const DEFAULT_SUB_GROUPING: ReportGrouping = 'description'

const DAY = /^\d{4}-\d{2}-\d{2}$/
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/** Reads a report from the address bar, dropping anything that is not valid. */
export function validateReportSearch(raw: Record<string, unknown>): ReportSearch {
  const oneOf = <T extends string>(value: unknown, options: readonly T[]) => (options.includes(value as T) ? (value as T) : undefined)
  const ids = (value: unknown) => {
    const valid = Array.isArray(value) ? value.filter((id): id is string => typeof id === 'string' && UUID.test(id)) : []
    return valid.length ? [...new Set(valid)] : undefined
  }
  const day = (value: unknown) => (typeof value === 'string' && DAY.test(value) ? value : undefined)
  const page = Number(raw.page)
  const roundTo = Number(raw.roundTo)
  const sub = raw.subGroupBy === 'none' ? 'none' : oneOf(raw.subGroupBy, GROUPINGS)

  return {
    tab: oneOf(raw.tab, TABS),
    period: oneOf(raw.period, PERIODS),
    from: day(raw.from),
    to: day(raw.to),
    users: ids(raw.users),
    groups: ids(raw.groups),
    projects: ids(raw.projects),
    clients: ids(raw.clients),
    tags: ids(raw.tags),
    billable: oneOf(raw.billable, ['yes', 'no'] as const),
    q: typeof raw.q === 'string' && raw.q.trim() ? raw.q.slice(0, 200) : undefined,
    groupBy: oneOf(raw.groupBy, GROUPINGS),
    subGroupBy: sub,
    sort: oneOf(raw.sort, SORTS),
    asc: raw.asc === true ? true : undefined,
    page: Number.isInteger(page) && page > 1 ? page : undefined,
    rounding: oneOf(raw.rounding, ROUNDINGS),
    roundTo: (ROUNDING_MINUTES as readonly number[]).includes(roundTo) ? roundTo : undefined,
    saved: typeof raw.saved === 'string' && UUID.test(raw.saved) ? raw.saved : undefined,
    view: oneOf(raw.view, VIEW_IDS),
  }
}

/** The filter a report request carries, for the given days. */
export function reportFilter(search: ReportSearch, range: DayRange): ReportFilter {
  return {
    from: range.from,
    to: range.to,
    userIds: search.users ?? [],
    groupIds: search.groups ?? [],
    projectIds: search.projects ?? [],
    clientIds: search.clients ?? [],
    tagIds: search.tags ?? [],
    billable: search.billable === 'yes' ? true : search.billable === 'no' ? false : null,
    description: search.q ?? null,
    rounding: search.rounding ?? 'none',
    roundingMinutes: search.roundTo ?? 15,
  }
}

/**
 * What is saved with a report: everything but the page shown and the view it was opened from. The other apps read the
 * same keys and drop any they don't know, so nothing else may go in here.
 */
export function savedParameters(search: ReportSearch): ReportSearch {
  const { page: _page, saved: _saved, view: _view, ...rest } = search
  return Object.fromEntries(Object.entries(rest).filter(([, value]) => value !== undefined))
}

/** A saved report's settings, read as carefully as the address bar. */
export function parseSavedParameters(json: string): ReportSearch {
  try {
    return savedParameters(validateReportSearch(JSON.parse(json) as Record<string, unknown>))
  } catch {
    return {}
  }
}

/**
 * Whether two reports show the same thing, whatever the order of their settings or of the IDs in a filter, and whether
 * a default is written out or left out.
 */
export function sameReport(a: ReportSearch, b: ReportSearch): boolean {
  return JSON.stringify(normalReport(a)) === JSON.stringify(normalReport(b))
}

function normalReport(search: ReportSearch) {
  const groupBy = search.groupBy ?? 'project'
  const period = search.period ?? DEFAULT_PERIOD
  const rounding = search.rounding ?? 'none'
  const ids = (list?: string[]) => (list?.length ? [...list].sort() : null)
  return [
    search.tab ?? 'summary',
    period,
    period === 'custom' ? [search.from ?? null, search.to ?? null] : null,
    ids(search.users),
    ids(search.groups),
    ids(search.projects),
    ids(search.clients),
    ids(search.tags),
    search.billable ?? null,
    search.q ?? null,
    groupBy,
    subGrouping(groupBy, search.subGroupBy),
    search.sort ?? 'date',
    search.asc ?? false,
    rounding,
    rounding === 'none' ? null : (search.roundTo ?? 15),
  ]
}

/** What the summary groups each group's time by, if anything. */
export function subGrouping(groupBy: ReportGrouping, subGroupBy: ReportSearch['subGroupBy']): ReportGrouping | null {
  if (subGroupBy === 'none') {
    return null
  }
  const sub = subGroupBy ?? DEFAULT_SUB_GROUPING
  return sub === groupBy ? null : sub
}
