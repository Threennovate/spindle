import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '@/api/client'
import { unwrap } from '@/api/unwrap'
import { parseAmount } from '@/lib/money'
import { dayKey } from '@/lib/time/zone'
import { useWorkspace } from '../workspace'

/** Whose rate: the workspace's default (no ids), a member's, a project's, or a member's on a project. */
export interface RateScope {
  kind?: 'billable' | 'laborCost' | 'dailyHours'
  projectId?: string
  userId?: string
}

export const ratePeriodsQuery = (workspaceId: string, scope: RateScope) =>
  queryOptions({
    queryKey: ['workspaces', workspaceId, 'rates', scope],
    queryFn: () => unwrap(api.GET('/api/workspaces/{workspaceId}/rates', { params: { path: { workspaceId }, query: scope } })),
  })

/** Removing a period changes the rate everywhere it shows, and what reports add up to. */
export function useDeleteRatePeriod(workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (rateId: string) => unwrap(api.DELETE('/api/workspaces/{workspaceId}/rates/{rateId}', { params: { path: { workspaceId, rateId } } })),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId] }),
  })
}

/**
 * Whether a rate field was changed from a rate that is already set. Only then does it matter from when the change
 * applies; a first rate covers all the time before it.
 */
export function rateChanged(saved: number | null | undefined, typed: string): boolean {
  const amount = parseAmount(typed)
  return saved != null && amount !== undefined && amount !== saved
}

/** Today, and the first day of this month, where a changed rate starts unless chosen otherwise. */
export function useRateChangeStart() {
  const { timeZone } = useWorkspace()
  const today = dayKey(new Date(), timeZone)
  return { today, firstOfMonth: `${today.slice(0, 8)}01` }
}

/** State for a rate field: what is typed, and from when a change applies. */
export function useRateInput(saved: number | null | undefined) {
  const { firstOfMonth } = useRateChangeStart()
  const [value, setValue] = useState(saved == null ? '' : String(saved))
  const [from, setFrom] = useState<string | null>(firstOfMonth)
  return {
    value,
    setValue,
    from,
    setFrom,
    /** The day to send with the rate: only for a change to a rate already set, and none to correct it. */
    fromToSend: rateChanged(saved, value) ? from : null,
  }
}
