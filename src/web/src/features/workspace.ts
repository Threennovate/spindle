import { useSuspenseQuery } from '@tanstack/react-query'
import { meQuery } from '@/auth/me'
import { formatDurationAs, type DurationFormat } from '@/lib/time/duration'
import type { WeekStart } from '@/lib/time/zone'

/** The signed-in user's current workspace and the preferences that shape how time is shown. */
export function useWorkspace() {
  const { data: me } = useSuspenseQuery(meQuery)
  const workspace = me.workspaces.find((w) => w.id === me.defaultWorkspaceId) ?? me.workspaces[0]

  return {
    me,
    workspace,
    workspaceId: workspace.id,
    isAdmin: workspace.role === 'admin',
    /** Owners and admins of the current workspace's organization can create more workspaces. */
    canCreateWorkspaces: me.organizations.some((o) => o.id === workspace.organizationId && o.role !== 'member'),
    timeZone: me.timeZone,
    weekStartsOn: me.preferences.weekStart as WeekStart,
    hour12: me.preferences.timeFormat === '12h',
    durationFormat: me.preferences.durationFormat as DurationFormat,
    /** A date-fns pattern, e.g. `dd.MM.yyyy`. */
    dateFormat: me.preferences.dateFormat,
  }
}

/** Writes finished durations the way the signed-in person chose: 1:30:00, 1:30 or 1.50 h. */
export function useDurationFormat(): (seconds: number) => string {
  const { durationFormat } = useWorkspace()
  return (seconds) => formatDurationAs(seconds, durationFormat)
}
