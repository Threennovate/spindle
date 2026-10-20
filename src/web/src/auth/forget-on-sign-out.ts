import type { QueryClient } from '@tanstack/react-query'
import type { Session } from './session-core'

/**
 * Forgets everything fetched for the account once the session ends, wherever that happens: in the app, on a public
 * page such as an invitation, in another tab, or because the server ended it. The next account to sign in in this
 * tab then never sees the last one's data. Requests still on their way are cancelled, so they can't fill it again.
 */
export function forgetOnSignOut(session: Pick<Session, 'subscribe' | 'isSignedIn'>, queryClient: QueryClient) {
  return session.subscribe(() => {
    if (!session.isSignedIn()) {
      void queryClient.cancelQueries()
      queryClient.clear()
    }
  })
}
