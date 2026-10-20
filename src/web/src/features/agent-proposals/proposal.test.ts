import { describe, expect, it } from 'vitest'
import type { AgentProposal, Project } from '@/api/types'
import type { Catalog } from '../catalog/use-catalog'
import { addedSeconds, byDay, changeLines, clientName, draftOf, proposedBy, waitingSummary } from './proposal'

const ZONE = 'Europe/Zagreb'
const atlas = { id: 'atlas', name: 'Atlas', color: '#0B83D9' } as Project
const beacon = { id: 'beacon', name: 'Beacon', color: '#D9480B' } as Project
const catalog = {
  project: (id: string | null | undefined) => [atlas, beacon].find((p) => p.id === id),
  task: () => undefined,
} as unknown as Catalog

function proposal(overrides: Partial<AgentProposal>): AgentProposal {
  return {
    id: 'p1',
    workspaceId: 'w1',
    entryId: null,
    original: null,
    fields: { start: null, stop: null, description: null, work: null },
    reason: '',
    client: 'claude-code',
    proposedAt: '2026-09-14T12:00:00Z',
    expiresAt: '2026-09-21T12:00:00Z',
    saves: {
      start: '2026-09-14T07:00:00Z',
      stop: '2026-09-14T08:30:00Z',
      description: 'Fix login',
      projectId: 'atlas',
      shares: [],
    },
    blocking: [],
    warnings: [],
    ...overrides,
  }
}

describe('proposals', () => {
  it('name the agent as people know it', () => {
    expect(clientName('claude-code')).toBe('Claude Code')
    expect(clientName('codex-mcp-client')).toBe('Codex')
    expect(clientName('Cursor')).toBe('Cursor')
    expect(clientName(null)).toBeNull()
  })

  it('fill the editor with what saving would store', () => {
    const draft = draftOf(proposal({}))!
    expect(draft).toMatchObject({
      id: 'proposal-p1',
      description: 'Fix login',
      projectId: 'atlas',
      start: '2026-09-14T07:00:00Z',
      stop: '2026-09-14T08:30:00Z',
    })
    expect(draftOf(proposal({ entryId: 'e1' }))!.id).toBe('e1')
    expect(draftOf(proposal({ saves: null }))).toBeNull()
  })

  it('order by when they start, whatever offset their times are written with', () => {
    const utc = proposal({
      id: 'utc',
      saves: {
        start: '2026-09-14T13:00:00Z',
        stop: '2026-09-14T14:00:00Z',
      },
    })
    const offset = proposal({
      id: 'offset',
      saves: {
        start: '2026-09-14T13:30:00+02:00',
        stop: '2026-09-14T14:00:00+02:00',
      },
    })
    expect(byDay([utc, offset], ZONE)[0].proposals.map((p) => p.id)).toEqual(['offset', 'utc'])
  })

  it('group by the day they are about, in the person’s zone', () => {
    const late = proposal({
      id: 'late',
      saves: {
        start: '2026-09-14T22:30:00Z',
        stop: '2026-09-14T23:00:00Z',
      },
    })
    const early = proposal({ id: 'early' })
    expect(byDay([late, early], ZONE)).toEqual([
      { day: '2026-09-14', proposals: [early] },
      { day: '2026-09-15', proposals: [late] },
    ])
  })

  it('say what a change does, a line per thing', () => {
    const change = proposal({
      entryId: 'e1',
      original: {
        start: '2026-09-14T07:00:00Z',
        stop: '2026-09-14T08:00:00Z',
        description: 'Standup',
        projectId: 'atlas',
        taskId: null,
        isBillable: false,
        shares: [],
      },
      fields: {
        start: null,
        stop: '2026-09-14T08:15:00Z',
        description: null,
        work: {
          projectId: 'beacon',
          taskId: null,
          isBillable: false,
          shares: [],
        },
      },
    })
    expect(changeLines(change, catalog, ZONE, false)).toEqual([
      { label: 'Time', from: '09:00–10:00', to: '09:00–10:15' },
      { label: 'Project', from: 'Atlas', to: 'Beacon' },
    ])
  })

  it('sum up what is waiting and who proposed it', () => {
    const list = [proposal({}), proposal({ id: 'p2', entryId: 'e1' }), proposal({ id: 'p3' })]
    expect(waitingSummary(list)).toBe('2 new entries and 1 change')
    expect(proposedBy(list)).toBe('Claude Code')
    expect(proposedBy([...list, proposal({ id: 'p4', client: 'codex' })])).toBe('Your agents')
  })

  it('count what they add to the day, a change by what it adds or takes away', () => {
    expect(addedSeconds(proposal({}))).toBe(5400)
    const shorter = proposal({
      entryId: 'e1',
      original: {
        start: '2026-09-14T07:00:00Z',
        stop: '2026-09-14T09:00:00Z',
        description: '',
        projectId: null,
        taskId: null,
        isBillable: false,
        shares: [],
      },
    })
    expect(addedSeconds(shorter)).toBe(-1800)
  })
})
