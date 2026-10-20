import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { unwrap } from '@/api/unwrap'

export type Agreement = components['schemas']['AgreementResponse']
export type AgreementKind = components['schemas']['AgreementKind']
export type AgreementRequest = components['schemas']['AgreementRequest']
export type Profitability = components['schemas']['ProfitabilityResponse']
export type ProjectProfitability = components['schemas']['ProjectProfitability']
export type AgreementProfitability = components['schemas']['AgreementProfitability']
export type RetainerPeriod = components['schemas']['RetainerPeriodResponse']

export const profitabilityKeys = {
  all: (workspaceId: string) => ['workspaces', workspaceId, 'profitability'] as const,
}

/** Every project's profitability over all its time, or one project's. Admins only. */
export const profitabilityQuery = (workspaceId: string, projectId?: string) =>
  queryOptions({
    queryKey: [...profitabilityKeys.all(workspaceId), projectId ?? 'all'],
    queryFn: () =>
      unwrap(api.GET('/api/workspaces/{workspaceId}/profitability', { params: { path: { workspaceId }, query: projectId ? { projectId } : {} } })),
  })

/**
 * Agreements change what time counts towards, close or reopen tasks, and change what reports add up to, so all of
 * those are refreshed.
 */
function useAgreementMutation<TVariables, TResult>(workspaceId: string, mutationFn: (variables: TVariables) => Promise<TResult>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn,
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: profitabilityKeys.all(workspaceId) }),
        queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId, 'projects'] }),
        queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId, 'reports'] }),
      ]),
  })
}

export function useSaveAgreement(workspaceId: string, projectId: string) {
  return useAgreementMutation(workspaceId, ({ id, body }: { id?: string; body: AgreementRequest }) => {
    const path = { workspaceId, projectId }
    return id
      ? unwrap(api.PUT('/api/workspaces/{workspaceId}/projects/{projectId}/agreements/{agreementId}', { params: { path: { ...path, agreementId: id } }, body }))
      : unwrap(api.POST('/api/workspaces/{workspaceId}/projects/{projectId}/agreements', { params: { path }, body }))
  })
}

export function useDeleteAgreement(workspaceId: string, projectId: string) {
  return useAgreementMutation(workspaceId, (agreementId: string) =>
    unwrap(api.DELETE('/api/workspaces/{workspaceId}/projects/{projectId}/agreements/{agreementId}', { params: { path: { workspaceId, projectId, agreementId } } })),
  )
}

/** Moves the project's time without a task, on the days given, to the agreement. Entries change too. */
export function useMoveTimeToAgreement(workspaceId: string, projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ agreementId, from, to }: { agreementId: string; from: string; to: string }) =>
      unwrap(
        api.POST('/api/workspaces/{workspaceId}/projects/{projectId}/agreements/{agreementId}/time', {
          params: { path: { workspaceId, projectId, agreementId } },
          body: { from, to },
        }),
      ),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId] }),
  })
}

/** The request that saves an agreement as it is, with some of it changed, e.g. marked done. */
export function requestOf(agreement: Agreement, changes: Partial<AgreementRequest> = {}): AgreementRequest {
  return {
    name: agreement.name,
    kind: agreement.kind,
    amount: agreement.amount ?? null,
    hours: agreement.hours ?? null,
    period: agreement.period ?? null,
    startsOn: agreement.startsOn,
    endsOn: agreement.endsOn ?? null,
    billsOverage: agreement.billsOverage,
    ...changes,
  }
}
