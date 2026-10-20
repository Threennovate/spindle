import { queryOptions } from '@tanstack/react-query'
import { api } from '@/api/client'
import { publicApi } from '@/api/public'
import type { BillingInterval } from '@/api/types'
import { unwrap } from '@/api/unwrap'

/** What this server offers, read before signing in too. It doesn't change while the app is open. */
export const serverQuery = queryOptions({
  queryKey: ['server'],
  queryFn: () => unwrap(publicApi.GET('/api/server')),
  staleTime: Infinity,
})

export const subscriptionQuery = (organizationId: string) =>
  queryOptions({
    queryKey: ['organizations', organizationId, 'subscription'],
    queryFn: () => unwrap(api.GET('/api/organizations/{organizationId}/subscription', { params: { path: { organizationId } } })),
  })

export function startCheckout(organizationId: string, interval: BillingInterval) {
  return unwrap(api.POST('/api/organizations/{organizationId}/subscription/checkout', { params: { path: { organizationId } }, body: { interval } }))
}

export function manageSubscription(organizationId: string) {
  return unwrap(api.POST('/api/organizations/{organizationId}/subscription/manage', { params: { path: { organizationId } } }))
}
