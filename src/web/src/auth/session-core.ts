import type { components } from '@/api/schema'

type AccessTokenResponse = components['schemas']['AccessTokenResponse']

interface AccessToken {
  value: string
  /** Local clock time, in milliseconds, at which the token expires. */
  expiresAt: number
}

export interface SessionDependencies {
  /** Exchanges the refresh cookie for an access token. Resolves to null when the server ends the session. */
  refresh(): Promise<AccessTokenResponse | null>
  logout(): Promise<void>
  /** Runs the callback while no other tab is refreshing, so tabs never rotate the same cookie at once. */
  exclusive<T>(callback: () => Promise<T>): Promise<T>
  broadcastSignOut(): void
  now(): number
}

// Renew a minute early, so a request never leaves with a token that expires on the way.
const RENEW_MARGIN_MS = 60_000

/**
 * The browser's session. The access token lives only in memory; the refresh token is in an HttpOnly cookie that
 * scripts cannot read, so after a reload the session is restored by asking the server for a new access token.
 */
export function createSession(deps: SessionDependencies) {
  let token: AccessToken | null = null
  let renewal: Promise<string | null> | null = null
  // Bumped on sign-out so a renewal that was already in flight cannot sign the user back in.
  let epoch = 0
  const listeners = new Set<() => void>()

  function set(next: AccessToken | null) {
    const changed = (token === null) !== (next === null)
    token = next
    if (changed) {
      listeners.forEach((listener) => listener())
    }
  }

  function renew(): Promise<string | null> {
    const startedIn = epoch
    renewal ??= deps
      .exclusive(async () => {
        const response = await deps.refresh()
        if (startedIn === epoch) {
          set(response ? toAccessToken(response, deps.now()) : null)
        }
        return token?.value ?? null
      })
      .finally(() => {
        renewal = null
      })
    return renewal
  }

  return {
    isSignedIn: () => token !== null,

    subscribe(listener: () => void) {
      listeners.add(listener)
      return () => {
        listeners.delete(listener)
      }
    },

    /** Restores the session from the cookie, e.g. after a reload. Resolves to whether the user is signed in. */
    restore: async () => (await renew()) !== null,

    /** Starts a session from a sign-in or registration response. */
    start(response: AccessTokenResponse) {
      epoch++
      set(toAccessToken(response, deps.now()))
    },

    /** The access token for the next request, renewed first if it is about to expire. Null when signed out. */
    async getAccessToken(): Promise<string | null> {
      if (token === null) {
        return null
      }
      if (token.expiresAt - RENEW_MARGIN_MS > deps.now()) {
        return token.value
      }
      return renew()
    },

    /** The server rejected the access token (e.g. the session was revoked): renew it, or end the session. */
    renewAfterRejection: () => renew(),

    /**
     * Ends the session on the server first: if the page moved on before that request finished, the cookie
     * would stay valid and the next visit would sign the user straight back in.
     */
    async signOut() {
      epoch++
      try {
        await deps.logout()
      } catch {
        // Offline: end the session here anyway. The server-side session expires on its own.
      }
      set(null)
      deps.broadcastSignOut()
    },

    /** Another tab signed out. */
    endLocally() {
      epoch++
      set(null)
    },
  }
}

export type Session = ReturnType<typeof createSession>

function toAccessToken(response: AccessTokenResponse, receivedAt: number): AccessToken {
  // Measure the token's lifetime from when it arrived, so a wrong clock on this device does not matter.
  const lifetime = tokenLifetimeMs(response.accessToken) ?? Date.parse(response.accessTokenExpiresAt) - receivedAt
  return { value: response.accessToken, expiresAt: receivedAt + lifetime }
}

function tokenLifetimeMs(jwt: string): number | null {
  try {
    const payload = JSON.parse(atob(jwt.split('.')[1].replace(/-/g, '+').replace(/_/g, '/'))) as { iat?: unknown; exp?: unknown }
    return typeof payload.iat === 'number' && typeof payload.exp === 'number' ? (payload.exp - payload.iat) * 1000 : null
  } catch {
    return null
  }
}
