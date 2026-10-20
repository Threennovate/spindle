import createClient, { type Middleware } from 'openapi-fetch'
import { session } from '@/auth/session'
import { queryClient } from '@/lib/query-client'
import type { paths } from './schema'
import { currentSyncConnection } from './sync-connection'

const authorize: Middleware = {
  async onRequest({ request }) {
    const token = await session.getAccessToken()
    if (token) {
      request.headers.set('Authorization', `Bearer ${token}`)
    }
    const syncConnection = currentSyncConnection()
    if (syncConnection) {
      request.headers.set('X-Sync-Connection', syncConnection)
    }
    return request
  },
  async onResponse({ response }) {
    if (response.status === 401) {
      await session.renewAfterRejection()
    }
    // An organization that became read-only since the page loaded: the profile says so, and the banner shows.
    if (response.status === 402) {
      void queryClient.invalidateQueries({ queryKey: ['me'] })
    }
    return response
  },
}

/** Client for signed-in requests. Adds the access token, renewing it first when it is about to expire. */
export const api = createClient<paths>()
api.use(authorize)
