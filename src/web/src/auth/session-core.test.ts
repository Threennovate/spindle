import { describe, expect, it, vi } from 'vitest'
import { createSession, type SessionDependencies } from './session-core'

const MINUTE = 60_000

function jwt(lifetimeSeconds: number): string {
  const json = JSON.stringify({ iat: 1_000, exp: 1_000 + lifetimeSeconds })
  const payload = btoa(json).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_')
  return `header.${payload}.signature`
}

function tokenResponse(accessToken = jwt(15 * 60)) {
  return { accessToken, accessTokenExpiresAt: '2030-01-01T00:00:00Z' }
}

function setup() {
  let now = 0
  const deps = {
    refresh: vi.fn<SessionDependencies['refresh']>().mockResolvedValue(tokenResponse()),
    logout: vi.fn<SessionDependencies['logout']>().mockResolvedValue(undefined),
    exclusive: vi.fn((callback: () => Promise<unknown>) => callback()),
    broadcastSignOut: vi.fn(),
  }
  const session = createSession({
    ...deps,
    exclusive: deps.exclusive as unknown as SessionDependencies['exclusive'],
    now: () => now,
  })
  return { session, deps, advance: (ms: number) => (now += ms) }
}

describe('session', () => {
  it('is restored from the cookie after a reload', async () => {
    const { session, deps } = setup()

    await expect(session.restore()).resolves.toBe(true)
    await expect(session.getAccessToken()).resolves.toBe(tokenResponse().accessToken)
    expect(deps.refresh).toHaveBeenCalledOnce()
  })

  it('stays signed out when the server has no session', async () => {
    const { session, deps } = setup()
    deps.refresh.mockResolvedValue(null)

    await expect(session.restore()).resolves.toBe(false)
    await expect(session.getAccessToken()).resolves.toBeNull()
    expect(deps.refresh).toHaveBeenCalledOnce()
  })

  it('renews the token shortly before it expires, once for concurrent requests', async () => {
    const { session, deps, advance } = setup()
    session.start(tokenResponse(jwt(15 * 60)))
    deps.refresh.mockResolvedValue(tokenResponse(jwt(900)))
    advance(14 * MINUTE + 1)

    const tokens = await Promise.all([session.getAccessToken(), session.getAccessToken(), session.getAccessToken()])

    expect(new Set(tokens)).toEqual(new Set([jwt(900)]))
    expect(deps.refresh).toHaveBeenCalledOnce()
    expect(deps.exclusive).toHaveBeenCalledOnce()
  })

  it('measures expiry by the token lifetime, not by a device clock that may be wrong', async () => {
    const { session, deps } = setup()
    // The device clock is far ahead of the server, so the server's expiry time looks long past.
    session.start({ accessToken: jwt(15 * 60), accessTokenExpiresAt: '2000-01-01T00:00:00Z' })

    await session.getAccessToken()

    expect(deps.refresh).not.toHaveBeenCalled()
  })

  it('ends the session when the server rejects the renewal', async () => {
    const { session, deps } = setup()
    deps.refresh.mockResolvedValue(null)
    session.start(tokenResponse())
    const listener = vi.fn()
    session.subscribe(listener)

    await session.renewAfterRejection()

    expect(session.isSignedIn()).toBe(false)
    expect(listener).toHaveBeenCalledOnce()
  })

  it('signs out locally and tells other tabs even if the server cannot be reached', async () => {
    const { session, deps } = setup()
    deps.logout.mockRejectedValue(new TypeError('Failed to fetch'))
    session.start(tokenResponse())

    await session.signOut()

    expect(session.isSignedIn()).toBe(false)
    expect(deps.broadcastSignOut).toHaveBeenCalledOnce()
    expect(deps.logout).toHaveBeenCalledOnce()
  })

  it('ends the session on the server before signing out locally', async () => {
    let finishLogout!: () => void
    const { session, deps } = setup()
    deps.logout.mockImplementation(() => new Promise((resolve) => (finishLogout = resolve)))
    session.start(tokenResponse())

    const signingOut = session.signOut()
    expect(session.isSignedIn()).toBe(true)
    expect(deps.broadcastSignOut).not.toHaveBeenCalled()

    finishLogout()
    await signingOut
    expect(session.isSignedIn()).toBe(false)
    expect(deps.broadcastSignOut).toHaveBeenCalledOnce()
  })

  it('does not sign back in when a renewal finishes after sign-out', async () => {
    let finishRefresh!: (value: ReturnType<typeof tokenResponse>) => void
    const { session, deps } = setup()
    deps.refresh.mockImplementation(() => new Promise((resolve) => (finishRefresh = resolve)))
    session.start(tokenResponse())

    const renewal = session.renewAfterRejection()
    await session.signOut()
    finishRefresh(tokenResponse())
    await renewal

    expect(session.isSignedIn()).toBe(false)
  })

  it('notifies subscribers only when the signed-in state changes', async () => {
    const { session } = setup()
    const listener = vi.fn()
    const unsubscribe = session.subscribe(listener)

    session.start(tokenResponse())
    session.start(tokenResponse())
    session.endLocally()
    unsubscribe()
    session.start(tokenResponse())

    expect(listener).toHaveBeenCalledTimes(2)
  })
})
