import { queryOptions, useMutation, useQueryClient, useSuspenseQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { unwrap } from '@/api/unwrap'
import { meQuery } from '@/auth/me'
import { dayKey } from '@/lib/time/zone'
import { useWorkspace } from '../workspace'

type WorkspaceSettingsRequest = components['schemas']['WorkspaceSettingsRequest']

export const settingsQuery = (workspaceId: string) =>
  queryOptions({
    queryKey: ['workspaces', workspaceId, 'settings'],
    queryFn: () => unwrap(api.GET('/api/workspaces/{workspaceId}/settings', { params: { path: { workspaceId } } })),
    staleTime: 60_000,
  })

export function useUpdateSettings(workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: WorkspaceSettingsRequest) =>
      unwrap(api.PUT('/api/workspaces/{workspaceId}/settings', { params: { path: { workspaceId } }, body })),
    onSuccess: (settings) => {
      queryClient.setQueryData(settingsQuery(workspaceId).queryKey, settings)
      // The workspace name also appears in the switcher.
      return queryClient.invalidateQueries({ queryKey: meQuery.queryKey })
    },
  })
}

/**
 * What the signed-in user may do in the current workspace, mirroring the server's rules so the interface only offers
 * what will work. The server checks everything again.
 */
export function usePermissions() {
  const { workspaceId, isAdmin, timeZone } = useWorkspace()
  const { data: settings } = useSuspenseQuery(settingsQuery(workspaceId))

  return {
    settings,
    isAdmin,
    canCreateProjects: isAdmin || !settings.onlyAdminsCreateProjects,
    canCreateTags: isAdmin || !settings.onlyAdminsCreateTags,
    canManageClients: isAdmin,
    canManageTags: isAdmin,
    /** The first day the user can still change, if earlier days are locked for them. */
    firstOpenDay: !isAdmin ? settings.lockedBefore : null,
    /** Whether time starting at this instant is on a day the user can no longer change. */
    isLocked: (start: string | Date) => !isAdmin && !!settings.lockedBefore && dayKey(start, timeZone) < settings.lockedBefore,
    isDayLocked: (day: string) => !isAdmin && !!settings.lockedBefore && day < settings.lockedBefore,
    lockMessage: settings.lockedBefore ? lockMessage(settings.lockedBefore) : '',
  }
}

const lockDateFormat = new Intl.DateTimeFormat('en-GB', { dateStyle: 'long', timeZone: 'UTC' })

/** The server's words for a locked day, e.g. "Time before 1 September 2026 is locked. Ask an admin to change it." */
export function lockMessage(lockedBefore: string) {
  return `Time before ${lockDateFormat.format(new Date(`${lockedBefore}T00:00:00Z`))} is locked. Ask an admin to change it.`
}
