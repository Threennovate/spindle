import { ApiError } from '@/api/problem'
import { publicApi } from '@/api/public'
import { createSession } from './session-core'

// The browser wiring for the session: the cookie endpoints, Web Locks between tabs, and a channel that
// tells other tabs when this one signs out.
const channel = typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel('spindle:session')

export const session = createSession({
  async refresh() {
    const { data, response } = await publicApi.POST('/api/auth/web/refresh')
    if (data) {
      return data
    }
    if (response.status === 401) {
      return null
    }
    throw new ApiError(response.status)
  },
  async logout() {
    await publicApi.POST('/api/auth/web/logout')
  },
  exclusive: (callback) =>
    'locks' in navigator ? navigator.locks.request('spindle:session-refresh', callback) : callback(),
  broadcastSignOut: () => channel?.postMessage('signed-out'),
  now: () => Date.now(),
})

channel?.addEventListener('message', (event: MessageEvent) => {
  if (event.data === 'signed-out') {
    session.endLocally()
  }
})
