import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { publicApi } from '@/api/public'
import type { components } from '@/api/schema'
import type { WorkspaceRole } from '@/api/types'
import { unwrap } from '@/api/unwrap'
import { meQuery } from '@/auth/me'
import { enterWorkspace } from '../workspace-queries'

type GroupRequest = components['schemas']['GroupRequest']

const teamKeys = {
  members: (workspaceId: string) => ['workspaces', workspaceId, 'members'] as const,
  invitations: (workspaceId: string) => ['workspaces', workspaceId, 'invitations'] as const,
  groups: (workspaceId: string) => ['workspaces', workspaceId, 'groups'] as const,
}

export const membersQuery = (workspaceId: string) =>
  queryOptions({
    queryKey: teamKeys.members(workspaceId),
    queryFn: () => unwrap(api.GET('/api/workspaces/{workspaceId}/members', { params: { path: { workspaceId } } })),
  })

export const invitationsQuery = (workspaceId: string) =>
  queryOptions({
    queryKey: teamKeys.invitations(workspaceId),
    queryFn: () => unwrap(api.GET('/api/workspaces/{workspaceId}/invitations', { params: { path: { workspaceId } } })),
  })

export const groupsQuery = (workspaceId: string) =>
  queryOptions({
    queryKey: teamKeys.groups(workspaceId),
    queryFn: () => unwrap(api.GET('/api/workspaces/{workspaceId}/groups', { params: { path: { workspaceId } } })),
  })

/** What an invitation link leads to. Public: the visitor may not have an account yet. */
export const invitationPreviewQuery = (token: string) =>
  queryOptions({
    queryKey: ['invitations', token],
    queryFn: () => unwrap(publicApi.GET('/api/invitations/{token}', { params: { path: { token } } })),
    retry: false,
  })

function useTeamMutation<TVariables, TResult>(workspaceId: string, mutationFn: (variables: TVariables) => Promise<TResult>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn,
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: teamKeys.members(workspaceId) }),
        queryClient.invalidateQueries({ queryKey: teamKeys.invitations(workspaceId) }),
      ]),
  })
}

export function useInvite(workspaceId: string) {
  return useTeamMutation(workspaceId, (body: { email: string; role: WorkspaceRole }) =>
    unwrap(api.POST('/api/workspaces/{workspaceId}/invitations', { params: { path: { workspaceId } }, body })),
  )
}

export function useResendInvitation(workspaceId: string) {
  return useTeamMutation(workspaceId, (invitationId: string) =>
    unwrap(api.POST('/api/workspaces/{workspaceId}/invitations/{invitationId}/resend', { params: { path: { workspaceId, invitationId } } })),
  )
}

export function useRevokeInvitation(workspaceId: string) {
  return useTeamMutation(workspaceId, (invitationId: string) =>
    unwrap(api.DELETE('/api/workspaces/{workspaceId}/invitations/{invitationId}', { params: { path: { workspaceId, invitationId } } })),
  )
}

/**
 * Sets a member's role and rates together: rates left out are cleared. A role decides what someone may see and do
 * everywhere in the workspace, including an admin's own when they step down, so all of it is refreshed.
 */
export function useUpdateMember(workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({
      userId,
      ...body
    }: {
      userId: string
      role: WorkspaceRole
      hourlyRate: number | null
      laborCost: number | null
      /** From when changed rates apply; without a day the current rate is corrected. */
      hourlyRateFrom?: string | null
      laborCostFrom?: string | null
    }) =>
      unwrap(api.PUT('/api/workspaces/{workspaceId}/members/{userId}', { params: { path: { workspaceId, userId } }, body })),
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId] }),
        queryClient.invalidateQueries({ queryKey: meQuery.queryKey }),
      ]),
  })
}

/** Removing someone also takes them out of every group, so groups are refreshed too. */
export function useRemoveMember(workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (userId: string) => unwrap(api.DELETE('/api/workspaces/{workspaceId}/members/{userId}', { params: { path: { workspaceId, userId } } })),
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: teamKeys.members(workspaceId) }),
        queryClient.invalidateQueries({ queryKey: teamKeys.groups(workspaceId) }),
      ]),
  })
}

function useGroupMutation<TVariables, TResult>(workspaceId: string, mutationFn: (variables: TVariables) => Promise<TResult>) {
  const queryClient = useQueryClient()
  return useMutation({ mutationFn, onSettled: () => queryClient.invalidateQueries({ queryKey: teamKeys.groups(workspaceId) }) })
}

export function useSaveGroup(workspaceId: string) {
  return useGroupMutation(workspaceId, ({ id, body }: { id?: string; body: GroupRequest }) =>
    id
      ? unwrap(api.PUT('/api/workspaces/{workspaceId}/groups/{groupId}', { params: { path: { workspaceId, groupId: id } }, body }))
      : unwrap(api.POST('/api/workspaces/{workspaceId}/groups', { params: { path: { workspaceId } }, body })),
  )
}

export function useDeleteGroup(workspaceId: string) {
  return useGroupMutation(workspaceId, (groupId: string) =>
    unwrap(api.DELETE('/api/workspaces/{workspaceId}/groups/{groupId}', { params: { path: { workspaceId, groupId } } })),
  )
}

/** Accepting switches the user to the new workspace, so everything is reloaded for it. */
export function useAcceptInvitation() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (token: string) => unwrap(api.POST('/api/invitations/{token}/accept', { params: { path: { token } } })),
    onSuccess: ({ workspaceId }) => enterWorkspace(queryClient, workspaceId),
  })
}
