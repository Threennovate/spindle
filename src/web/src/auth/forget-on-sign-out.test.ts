import { QueryClient } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'
import { forgetOnSignOut } from './forget-on-sign-out'
import { createSession, type SessionDependencies } from './session-core'

function signedIn() {
  const session = createSession({
    refresh: vi.fn<SessionDependencies['refresh']>().mockResolvedValue(null),
    logout: vi.fn<SessionDependencies['logout']>().mockResolvedValue(undefined),
    exclusive: (callback) => callback(),
    broadcastSignOut: vi.fn(),
    now: () => 0,
  })
  session.start({ accessToken: 'token', accessTokenExpiresAt: '2030-01-01T00:00:00Z' })
  const queryClient = new QueryClient()
  queryClient.setQueryData(['me'], { email: 'a@example.com' })
  forgetOnSignOut(session, queryClient)
  return { session, queryClient }
}

describe('forgetOnSignOut', () => {
  it('forgets the account when it signs out, wherever it signs out', async () => {
    const { session, queryClient } = signedIn()

    await session.signOut()

    expect(queryClient.getQueryData(['me'])).toBeUndefined()
  })

  it('forgets the account when another tab signs out', () => {
    const { session, queryClient } = signedIn()

    session.endLocally()

    expect(queryClient.getQueryData(['me'])).toBeUndefined()
  })

  it('keeps it while signed in', () => {
    const { session, queryClient } = signedIn()

    session.start({ accessToken: 'renewed', accessTokenExpiresAt: '2030-01-01T00:00:00Z' })

    expect(queryClient.getQueryData(['me'])).toEqual({ email: 'a@example.com' })
  })
})
