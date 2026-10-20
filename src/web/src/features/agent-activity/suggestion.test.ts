import { describe, expect, it } from 'vitest'
import type { Project, SplitSuggestion, SplitSuggestionPart, UntrackedTime } from '@/api/types'
import type { Catalog } from '../catalog/use-catalog'
import { chipTime, partSeconds, separateTitle, spokenEvidence, suggestedStretches, weakNote } from './suggestion'
import { untrackedEntry, untrackedId } from './untracked'

const atlas = { id: 'atlas', name: 'Atlas', color: '#0B83D9', isBillable: true } as Project
const beacon = { id: 'beacon', name: 'Beacon', color: '#D97706', isBillable: false } as Project
const catalog = {
  projects: [atlas, beacon],
  project: (id: string | null | undefined) => [atlas, beacon].find((project) => project.id === id),
} as Catalog

const part = (projectId: string | null, percent: number, minutes: number, prompts: number, unattributed = 0): SplitSuggestionPart => ({
  target: projectId ? { projectId } : { repository: { identity: 'origin:github.com/acme/site', name: 'site' } },
  repositories: [],
  attentionSeconds: minutes * 60,
  unattributedSeconds: unattributed * 60,
  percent,
  prompts,
})

function suggestion(overrides: Partial<SplitSuggestion> = {}): SplitSuggestion {
  return {
    entryId: 'e1',
    parts: [part('atlas', 67, 70, 4, 10), part('beacon', 33, 40, 3)],
    coverage: 0.92,
    stretches: null,
    strength: 'strong',
    start: '2026-09-14T07:00:00Z',
    end: '2026-09-14T09:00:00Z',
    pieces: [],
    open: [],
    prompts: [],
    fallback: { projectId: 'atlas' },
    ...overrides,
  }
}

describe('chips', () => {
  it('show hours in whole minutes that add up to the entry', () => {
    const times = partSeconds(suggestion(), 2 * 3600 + 20)
    expect(times.map(chipTime)).toEqual(['1:20', '0:40'])
    expect(times.reduce((a, b) => a + b, 0)).toBe(2 * 3600)
  })

  it('say where to cut when the work moved', () => {
    const moved = [
      { target: { projectId: 'atlas' }, start: '2026-09-14T07:00:00Z', end: '2026-09-14T07:42:00Z' },
      { target: { projectId: 'beacon' }, start: '2026-09-14T07:42:00Z', end: '2026-09-14T09:00:00Z' },
    ]
    expect(separateTitle(suggestion({ stretches: moved }), 'Europe/Zagreb', false)).toBe('Split at 09:42')
    expect(separateTitle(suggestion({ stretches: [...moved, moved[0]] }), 'Europe/Zagreb', false)).toBe('Split into 3 entries')
    expect(separateTitle(suggestion(), 'Europe/Zagreb', false)).toBe('Split')
    expect(suggestedStretches({ stretches: moved })?.map((stretch) => stretch.projectId)).toEqual(['atlas', 'beacon'])
  })
})

describe('evidence', () => {
  it('reads out each part, its prompts and what the logs leave open', () => {
    expect(spokenEvidence(suggestion(), catalog, 'Europe/Zagreb', false)).toBe(
      "Agent activity from 09:00 to 11:00: Atlas 1h 20m, 4 prompts, with 10m the agent logs don't account for; Beacon 40m, 3 prompts.",
    )
  })

  it('says only a weak suggestion is lightly backed', () => {
    expect(weakNote(suggestion())).toBeNull()
    expect(weakNote(suggestion({ strength: 'weak', coverage: 0.54 }))).toBe('The logs back this only lightly: 54% of the time, few prompts.')
  })
})

describe('untracked time', () => {
  const block: UntrackedTime = {
    start: '2026-09-14T12:00:00Z',
    end: '2026-09-14T13:30:00Z',
    projectIds: ['beacon', 'atlas'],
    unlinkedSeconds: 0,
    parts: [part('atlas', 40, 36, 2), part('beacon', 60, 54, 3)],
    stretches: null,
  }

  it('is known by its day and start minute, as on the Mac', () => {
    expect(untrackedId(block, 'Europe/Zagreb')).toBe('2026-09-14|840')
  })

  it('opens as the split its attention suggests', () => {
    const entry = untrackedEntry(block, catalog)
    expect(entry.projectId).toBe('beacon')
    expect(entry.parts?.map((p) => [p.projectId, p.percent, p.isBillable])).toEqual([
      ['atlas', 40, true],
      ['beacon', 60, false],
    ])
    expect(entry.stretches).toBeNull()
  })

  it('opens on its project when a repository has none', () => {
    const entry = untrackedEntry({ ...block, projectIds: ['atlas'], parts: [part('atlas', 70, 60, 3), part(null, 30, 30, 2)] }, catalog)
    expect([entry.projectId, entry.parts, entry.stretches]).toEqual(['atlas', null, null])
  })
})
