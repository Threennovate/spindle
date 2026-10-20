import { QueryClient } from '@tanstack/react-query'
import { describe, expect, it } from 'vitest'
import { applyChange } from './changes'

function staleAfter(change: Parameters<typeof applyChange>[1], userId = 'me') {
  const client = new QueryClient()
  const keys = [
    ['me'],
    ['current-time-entry'],
    ['workspaces', 'w1', 'time-entries', 'week'],
    ['workspaces', 'w1', 'reports', 'summary'],
    ['workspaces', 'w1', 'projects', { includeArchived: true }],
    ['workspaces', 'w1', 'members'],
    ['workspaces', 'w1', 'settings'],
    ['workspaces', 'w2', 'projects', { includeArchived: true }],
  ]
  for (const key of keys) {
    client.setQueryData(key, 'data')
  }
  applyChange(client, change, userId)
  return keys.filter((key) => client.getQueryState(key)?.isInvalidated).map((key) => key.slice(0, 3).join('/'))
}

describe('applyChange', () => {
  it('refreshes your own entries and timer, and everyone’s reports', () => {
    expect(staleAfter({ topic: 'entries', workspaceId: 'w1', userId: 'me' })).toEqual([
      'current-time-entry',
      'workspaces/w1/time-entries',
      'workspaces/w1/reports',
      'workspaces/w1/projects',
    ])
    expect(staleAfter({ topic: 'entries', workspaceId: 'w1', userId: 'someone else' })).toEqual(['workspaces/w1/reports', 'workspaces/w1/projects'])
  })

  it('refreshes only the workspace that changed', () => {
    expect(staleAfter({ topic: 'catalog', workspaceId: 'w2', userId: null })).toEqual(['workspaces/w2/projects'])
    expect(staleAfter({ topic: 'me', workspaceId: null, userId: 'me' })).toEqual(['me'])
  })

  it('refreshes reports when rates or billed time change', () => {
    expect(staleAfter({ topic: 'team', workspaceId: 'w1', userId: null })).toEqual(['workspaces/w1/reports', 'workspaces/w1/members'])
    expect(staleAfter({ topic: 'settings', workspaceId: 'w1', userId: null })).toEqual(['me', 'workspaces/w1/reports', 'workspaces/w1/settings'])
    expect(staleAfter({ topic: 'billing', workspaceId: 'w1', userId: null })).toEqual(['workspaces/w1/reports'])
  })

  it('refreshes split suggestions, and nothing else, when agent activity changes', () => {
    const client = new QueryClient()
    const suggestions = ['workspaces', 'w1', 'time-entries', 'split-suggestions', 'from', 'to']
    const week = ['workspaces', 'w1', 'time-entries', 'weeks']
    client.setQueryData(suggestions, [])
    client.setQueryData(week, [])

    applyChange(client, { topic: 'agentActivity', workspaceId: null, userId: 'me' }, 'me')

    expect(client.getQueryState(suggestions)?.isInvalidated).toBe(true)
    expect(client.getQueryState(week)?.isInvalidated).toBe(false)
  })
})
