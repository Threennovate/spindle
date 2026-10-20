import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { unwrap } from '@/api/unwrap'

export type ProjectBilling = components['schemas']['ProjectBillingResponse']
export type BillingRow = components['schemas']['BillingRow']

export const billingKeys = {
  all: (workspaceId: string) => ['workspaces', workspaceId, 'billing'] as const,
}

/** Everyone's tracked and billed time on a project, day by day. */
export const projectBillingQuery = (workspaceId: string, projectId: string, from: string, to: string) =>
  queryOptions({
    queryKey: [...billingKeys.all(workspaceId), projectId, from, to],
    queryFn: () =>
      unwrap(
        api.GET('/api/workspaces/{workspaceId}/projects/{projectId}/billing', {
          params: { path: { workspaceId, projectId }, query: { from, to } },
        }),
      ),
  })

/** Enters the time billed for someone on a day, or with `null` bills the day as tracked again. Reports change with it. */
export function useSetBilledTime(workspaceId: string, projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ userId, day, seconds }: { userId: string; day: string; seconds: number | null }) => {
      const params = { path: { workspaceId, projectId, userId, day } }
      return seconds === null
        ? unwrap(api.DELETE('/api/workspaces/{workspaceId}/projects/{projectId}/billing/{userId}/{day}', { params }))
        : unwrap(api.PUT('/api/workspaces/{workspaceId}/projects/{projectId}/billing/{userId}/{day}', { params, body: { seconds } }))
    },
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: billingKeys.all(workspaceId) }),
        queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId, 'reports'] }),
      ]),
  })
}

/** Describes what was done on someone's day for timesheet links, or with `null` describes it from the entries again. */
export function useDescribeBilledDay(workspaceId: string, projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ userId, day, description }: { userId: string; day: string; description: string | null }) => {
      const params = { path: { workspaceId, projectId, userId, day } }
      return description === null
        ? unwrap(api.DELETE('/api/workspaces/{workspaceId}/projects/{projectId}/billing/{userId}/{day}/description', { params }))
        : unwrap(api.PUT('/api/workspaces/{workspaceId}/projects/{projectId}/billing/{userId}/{day}/description', { params, body: { description } }))
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: billingKeys.all(workspaceId) }),
  })
}
