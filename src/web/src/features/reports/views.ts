import type { SavedReport } from './queries'
import { NONE_ID, parseSavedParameters, sameReport, type ReportSearch, type ViewId } from './search'

/**
 * A report built into the web app: a set of report settings with a name, defined here and never stored, so it can't
 * be hidden, changed or deleted. Changing one gives an edited copy, which can be saved as the person's own.
 */
export interface BuiltInView {
  id: ViewId
  group: 'you' | 'team'
  label: string
  description: string
  search: ReportSearch
}

/**
 * The views offered to this person. "Me" is their own user ID, never a token: the other apps drop IDs and keys they
 * don't know, so a token would quietly turn a saved "my time" report into everyone's.
 */
export function builtInViews(userId: string, isAdmin: boolean): BuiltInView[] {
  const me = [userId]
  const views: BuiltInView[] = [
    { id: 'my-week', group: 'you', label: 'My week', description: 'Your time this week, day by day.', search: { tab: 'weekly', period: 'thisWeek', users: me } },
    {
      id: 'my-month',
      group: 'you',
      label: 'My month',
      description: 'Your time this month, by project, then description.',
      search: { tab: 'summary', period: 'thisMonth', users: me, groupBy: 'project', subGroupBy: 'description' },
    },
    { id: 'yesterday', group: 'you', label: 'Yesterday', description: 'Every entry of yours from yesterday.', search: { tab: 'detailed', period: 'yesterday', users: me } },
    {
      id: 'billable-last-month',
      group: 'you',
      label: 'Billable last month',
      description: 'Your billable time last month, by client.',
      search: { tab: 'summary', period: 'lastMonth', users: me, billable: 'yes', groupBy: 'client' },
    },
  ]
  if (!isAdmin) {
    return views
  }
  return [
    ...views,
    { id: 'team-week', group: 'team', label: 'Team this week', description: "Everyone's time this week, a row per member.", search: { tab: 'weekly', period: 'thisWeek', groupBy: 'user' } },
    {
      id: 'who-worked',
      group: 'team',
      label: 'Who worked on what',
      description: "Everyone's time this month, by member, then project.",
      search: { tab: 'summary', period: 'thisMonth', groupBy: 'user', subGroupBy: 'project' },
    },
    {
      id: 'billable-by-client',
      group: 'team',
      label: 'Billable by client',
      description: 'Billable time last month, by client.',
      search: { tab: 'summary', period: 'lastMonth', billable: 'yes', groupBy: 'client' },
    },
    { id: 'no-project', group: 'team', label: 'Time without a project', description: 'Entries this month that have no project yet.', search: { tab: 'detailed', period: 'thisMonth', projects: [NONE_ID] } },
  ]
}

export const FIRST_VIEW: ViewId = 'my-week'

/** Where a view is: a built-in view's ID, or `saved:` and a saved report's ID. */
export type ViewRef = ViewId | `saved:${string}`

/** The settings a view opens with, keeping in the address bar which view it is. */
export function viewSearch(view: BuiltInView): ReportSearch {
  return { ...view.search, view: view.id }
}

export function savedReportSearch(report: SavedReport): ReportSearch {
  return { ...parseSavedParameters(report.parameters), saved: report.id }
}

/** The view to open: the one asked for if it is still there, otherwise the first. */
export function openingSearch(ref: string | null, views: BuiltInView[], savedReports: SavedReport[]): ReportSearch {
  const saved = ref?.startsWith('saved:') ? savedReports.find((report) => `saved:${report.id}` === ref) : undefined
  if (saved) {
    return savedReportSearch(saved)
  }
  const view = views.find((candidate) => candidate.id === ref) ?? views.find((candidate) => candidate.id === FIRST_VIEW)!
  return viewSearch(view)
}

/** Which view the address bar shows, if any. */
export function viewRef(search: ReportSearch): ViewRef | undefined {
  return search.saved ? `saved:${search.saved}` : search.view
}

/** Whether the address bar holds no report at all, as when Reports is opened from the menu. */
export function isBlank(search: ReportSearch): boolean {
  return Object.values(search).every((value) => value === undefined)
}

/** Whether the report differs from the view it was opened from. */
export function isEdited(base: ReportSearch, search: ReportSearch): boolean {
  return !sameReport(base, search)
}

export type WhoseTime = 'me' | 'everyone' | 'some'

/** Whose time the report counts, as the "Whose time" filter shows it. */
export function whoseTime(search: Pick<ReportSearch, 'users' | 'groups'>, userId: string): WhoseTime {
  if (!search.users?.length && !search.groups?.length) {
    return 'everyone'
  }
  return !search.groups?.length && search.users?.length === 1 && search.users[0] === userId ? 'me' : 'some'
}

const lastViewKey = (userId: string, workspaceId: string) => `spindle.reports.lastView.${userId}.${workspaceId}`

/** The view this person last opened in this workspace in this browser, if it is remembered. */
export function lastView(userId: string, workspaceId: string): string | null {
  try {
    return window.localStorage.getItem(lastViewKey(userId, workspaceId))
  } catch {
    return null
  }
}

export function rememberView(userId: string, workspaceId: string, ref: ViewRef) {
  try {
    window.localStorage.setItem(lastViewKey(userId, workspaceId), ref)
  } catch {
    // Opened now, but not remembered for next time.
  }
}
