import { keepPreviousData, queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { unwrap } from '@/api/unwrap'
import { meQuery } from '@/auth/me'

/** Which organizations the list shows. The server reads the same names. */
export type OrganizationFilter = 'all' | 'trialing' | 'trialEndingSoon' | 'readOnly' | 'paying' | 'comped'

const operatorKeys = {
  all: ['operator'] as const,
  overview: ['operator', 'overview'] as const,
  organizations: (filter: OrganizationFilter, search: string) => ['operator', 'organizations', filter, search] as const,
  changes: (organizationId: string) => ['operator', 'changes', organizationId] as const,
  priceCohorts: ['operator', 'priceCohorts'] as const,
}

export const operatorOverviewQuery = queryOptions({
  queryKey: operatorKeys.overview,
  queryFn: () => unwrap(api.GET('/api/operator/overview')),
})

export const operatorOrganizationsQuery = (filter: OrganizationFilter, search: string) =>
  queryOptions({
    queryKey: operatorKeys.organizations(filter, search),
    queryFn: () =>
      unwrap(
        api.GET('/api/operator/organizations', {
          params: { query: { filter: filter === 'all' ? undefined : filter, search: search || undefined } },
        }),
      ),
    // Typing in the search keeps the last list on screen instead of flashing an empty table.
    placeholderData: keepPreviousData,
  })

export const priceCohortsQuery = queryOptions({
  queryKey: operatorKeys.priceCohorts,
  queryFn: () => unwrap(api.GET('/api/operator/price-cohorts')),
})

export const subscriptionChangesQuery = (organizationId: string) =>
  queryOptions({
    queryKey: operatorKeys.changes(organizationId),
    queryFn: () => unwrap(api.GET('/api/operator/organizations/{organizationId}/changes', { params: { path: { organizationId } } })),
  })

/** After a change, everything the operator sees, and the operator's own organizations in case one of them changed. */
function useOperatorMutation<TVariables>(mutationFn: (variables: TVariables) => Promise<unknown>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn,
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: operatorKeys.all }),
        queryClient.invalidateQueries({ queryKey: meQuery.queryKey }),
      ]),
  })
}

export function usePutOnTrial() {
  return useOperatorMutation(({ organizationId, trialEndsAt }: { organizationId: string; trialEndsAt: string }) =>
    unwrap(api.PUT('/api/operator/organizations/{organizationId}/trial', { params: { path: { organizationId } }, body: { trialEndsAt } })),
  )
}

export function useComp() {
  return useOperatorMutation((organizationId: string) =>
    unwrap(api.POST('/api/operator/organizations/{organizationId}/comp', { params: { path: { organizationId } } })),
  )
}
