import { toApiError } from '@/api/problem'
import { publicApi } from '@/api/public'
import { browserTimeZone } from '@/lib/time/zone'

interface StartOptions {
  /** The page to come back to afterwards, a path in this app. */
  returnTo?: string
  /** An invitation to the person's address, for a new account to join its workspace. */
  invitation?: string
  /** That the person saw the terms next to the button. They count only for a new account. */
  acceptTerms: boolean
}

/**
 * Leaves for Google or Apple. The server sets a cookie only this browser has, so only this browser can finish the
 * sign-in, then sends the page to the provider, which comes back to `/external-sign-in`.
 */
export function startExternalSignIn(provider: string, { returnTo, invitation, acceptTerms }: StartOptions) {
  const query = new URLSearchParams({ timeZone: browserTimeZone(), acceptTerms: String(acceptTerms) })
  if (returnTo) query.set('returnTo', returnTo)
  if (invitation) query.set('invitation', invitation)
  window.location.assign(`/api/auth/external/${encodeURIComponent(provider)}/start?${query}`)
}

/** Trades the code from the redirect for a session, with the cookie from the start. */
export async function finishExternalSignIn(code: string) {
  const { data, error, response } = await publicApi.POST('/api/auth/web/external/exchange', { body: { code } })
  if (!data) {
    throw toApiError(response, error)
  }
  return data
}
