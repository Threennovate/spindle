import { useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { unwrap } from '@/api/unwrap'
import { meQuery } from '@/auth/me'
import { settingsQuery } from './settings/queries'

/**
 * Shows the workspace the user just moved to. Its settings shape the whole interface, so they are loaded first:
 * the pages then switch over at once instead of waiting on them.
 */
export async function enterWorkspace(queryClient: QueryClient, workspaceId: string) {
  await queryClient.prefetchQuery(settingsQuery(workspaceId))
  await queryClient.invalidateQueries({ queryKey: meQuery.queryKey })
}

/** Switching workspaces changes the user's default on every device; the app then shows the new one. */
export function useSwitchWorkspace() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (workspaceId: string) => unwrap(api.PUT('/api/me/default-workspace', { body: { workspaceId } })),
    onSuccess: (_, workspaceId) => enterWorkspace(queryClient, workspaceId),
  })
}

export function useCreateWorkspace() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (name: string) => unwrap(api.POST('/api/workspaces', { body: { name } })),
    onSuccess: (workspace) => enterWorkspace(queryClient, workspace.id),
  })
}

/**
 * Renames the organization, and its workspace that still has the old name, as the server does. Both names show
 * across the app, so the user and the workspace's settings are read again.
 */
export function useRenameOrganization() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ organizationId, name }: { organizationId: string; name: string }) =>
      unwrap(api.PUT('/api/organizations/{organizationId}', { params: { path: { organizationId } }, body: { name } })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: meQuery.queryKey })
      await queryClient.invalidateQueries({ predicate: ({ queryKey }) => queryKey[0] === 'workspaces' && queryKey[2] === 'settings' })
    },
  })
}
