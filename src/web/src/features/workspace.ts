import { useSuspenseQuery } from '@tanstack/react-query'
import type { Me } from '@/api/types'
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
    canCreateWorkspaces: newWorkspaceAllowed(me, workspace),
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

type Workspace = Me['workspaces'][number]

/**
 * Whether the person may create a workspace, as the server says. A server from before it said nothing let owners and
 * admins of the current workspace's organization create them.
 */
function newWorkspaceAllowed(me: Me, workspace: Workspace): boolean {
  return me.newWorkspace?.allowed ?? me.organizations.some((o) => o.id === workspace.organizationId && o.role !== 'member')
}

/** Says what a new workspace is and where it goes, before the person creates it. */
export function describeNewWorkspace(me: Me, workspace: Workspace): string {
  const space = 'its own projects, clients, tags and team'
  const target = me.newWorkspace
  if (target?.allowed && target.organizationId == null) {
    const current = me.organizations.find((o) => o.id === workspace.organizationId)?.name
    const apart = current ? `A space of your own, apart from ${current}, with ${space}.` : `A space of your own, with ${space}.`
    if (target.trialDays == null) return `${apart} You own it and can invite people to it.`
    return `${apart} ${current ? `It's not on ${current}'s plan: it` : 'It'} starts its own ${target.trialDays}-day free trial.`
  }
  const elsewhere =
    target?.organizationId && target.organizationId !== workspace.organizationId
      ? me.organizations.find((o) => o.id === target.organizationId)?.name
      : undefined
  return elsewhere ? `A separate space in ${elsewhere}, with ${space}. You become its admin.` : `A separate space with ${space}. You become its admin.`
}
