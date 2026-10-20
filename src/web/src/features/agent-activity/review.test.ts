import { describe, expect, it } from 'vitest'
import type { Project, SplitSuggestion, SplitSuggestionPart, TimeEntry, UntrackedTime } from '@/api/types'
import type { Catalog } from '../catalog/use-catalog'
import { breakdown, outcomes, reviewItems, takePlan, type ReviewItem } from './review'

const atlas = { id: 'atlas', name: 'Atlas', color: '#0B83D9', isBillable: true, tasks: [] } as unknown as Project
const beacon = { id: 'beacon', name: 'Beacon', color: '#D97706', isBillable: false, tasks: [{ id: 't1', name: 'Build', isDone: false }] } as unknown as Project
const catalog = {
  projects: [atlas, beacon],
  project: (id: string | null | undefined) => [atlas, beacon].find((project) => project.id === id),
} as Catalog
const zone = 'Europe/Zagreb'
const relaxed = { requireDescription: false, requireProject: false, requireTask: false, requireTag: false }

// Monday 14 September 2026, 09:00 in Zagreb is 07:00 UTC.
const at = (hours: number, minutes = 0) => new Date(Date.UTC(2026, 8, 14, hours - 2, minutes)).toISOString()

function entry(id: string, from: number, to: number | null, overrides: Partial<TimeEntry> = {}): TimeEntry {
  return {
    id,
    description: 'Search ranking',
    projectId: 'atlas',
    taskId: 'own-task',
    tagIds: ['tag'],
    isBillable: false,
    shares: [],
    start: at(from),
    stop: to === null ? null : at(to),
    ...overrides,
  } as TimeEntry
}

const part = (projectId: string | null, percent: number): SplitSuggestionPart => ({
  target: projectId ? { projectId } : { repository: { identity: 'origin:github.com/acme/site', name: 'site' } },
  repositories: [],
  attentionSeconds: 1800,
  unattributedSeconds: 0,
  percent,
  prompts: 3,
})

function suggestion(entryId: string, overrides: Partial<SplitSuggestion> = {}): SplitSuggestion {
  return {
    entryId,
    parts: [part('atlas', 60), part('beacon', 40)],
    coverage: 0.9,
    stretches: null,
    strength: 'strong',
    start: at(9),
    end: at(10),
    pieces: [],
    open: [],
    prompts: [],
    fallback: { projectId: 'atlas' },
    ...overrides,
  }
}

const moved = [
  { target: { projectId: 'atlas' }, start: at(9), end: at(9, 40) },
  { target: { projectId: 'beacon' }, start: at(9, 40), end: at(10) },
]

function untracked(from: number, to: number, overrides: Partial<UntrackedTime> = {}): UntrackedTime {
  return { start: at(from), end: at(to), projectIds: ['beacon'], unlinkedSeconds: 0, parts: null, stretches: null, ...overrides }
}

const split = (e: TimeEntry, s: SplitSuggestion): ReviewItem => ({ kind: 'split', id: `split|${e.id}`, day: '2026-09-14', start: e.start, end: e.stop!, entry: e, suggestion: s })

describe('the review', () => {
  const finished = entry('finished', 9, 10)
  const running = entry('running', 11, null)
  const later = entry('later', 15, 16)
  const input = {
    days: ['2026-09-14'],
    entries: [finished, running, later],
    suggestions: [suggestion('later', { strength: 'weak' }), suggestion('finished'), suggestion('running')],
    untracked: [untracked(12, 13)],
    timeZone: zone,
    isDayLocked: () => false,
    isPutOff: () => false,
    isDismissed: () => false,
  }

  it('holds the finished splits and untracked time of the day, in the order of the day', () => {
    const items = reviewItems(input)
    expect(items.map((item) => item.id)).toEqual(['split|finished', 'untracked|2026-09-14|720', 'split|later'])
    expect(breakdown(items)).toBe('2 splits, 1 untracked stretch')
  })

  it('leaves out what was put off, dismissed in the calendar, on locked days or on other days', () => {
    expect(reviewItems({ ...input, isPutOff: (id) => id === 'split|later' }).map((item) => item.id)).toEqual(['split|finished', 'untracked|2026-09-14|720'])
    expect(reviewItems({ ...input, isDismissed: () => true }).map((item) => item.kind)).toEqual(['split', 'split'])
    expect(reviewItems({ ...input, isDayLocked: () => true })).toEqual([])
    expect(reviewItems({ ...input, days: ['2026-09-15'] })).toEqual([])
  })
})

describe('taking an item', () => {
  it('splits by percent, the entry keeping its own task and billable setting', () => {
    const plan = takePlan(split(entry('e', 9, 10), suggestion('e')), { catalog, settings: relaxed })
    expect(plan.status).toBe('ready')
    const { update, create } = plan.status === 'ready' ? plan.requests : { update: null, create: [] }
    expect(create).toEqual([])
    expect(update?.body.shares).toEqual([
      { projectId: 'atlas', taskId: 'own-task', isBillable: false, percent: 60 },
      { projectId: 'beacon', taskId: null, isBillable: false, percent: 40 },
    ])
    expect(update?.body.description).toBe('Search ranking')
  })

  it('cuts separate entries where the work moved: the entry becomes the last one, the earlier ones are added', () => {
    const plan = takePlan(split(entry('e', 9, 10), suggestion('e', { stretches: moved })), { catalog, settings: relaxed })
    if (plan.status !== 'ready') {
      throw new Error(plan.status)
    }
    const { update, create } = plan.requests
    expect(create.map((body) => [body.projectId, body.taskId, body.start, body.stop, body.tagIds])).toEqual([['atlas', 'own-task', at(9), at(9, 40), ['tag']]])
    expect([update?.entry.id, update?.body.projectId, update?.body.taskId, update?.body.start, update?.body.stop]).toEqual(['e', 'beacon', null, at(9, 40), at(10)])
  })

  it('needs the editor for a repository without a project, or details one click cannot fill in', () => {
    const unlinked = suggestion('e', { parts: [part('atlas', 60), part(null, 40)] })
    expect(takePlan(split(entry('e', 9, 10), unlinked), { catalog, settings: relaxed }).status).toBe('unlinked')
    expect(takePlan(split(entry('e', 9, 10), suggestion('e')), { catalog, settings: { ...relaxed, requireTask: true } })).toEqual({
      status: 'details',
      reason: 'Needs a task on Beacon',
    })
    const block: ReviewItem = { kind: 'untracked', id: 'u', day: '2026-09-14', start: at(12), end: at(13), block: untracked(12, 13) }
    expect(takePlan(block, { catalog, settings: { ...relaxed, requireDescription: true } })).toEqual({ status: 'details', reason: 'Needs a description' })
  })

  it('adds untracked time as a new entry on its project, as its project bills', () => {
    const block: ReviewItem = { kind: 'untracked', id: 'u', day: '2026-09-14', start: at(12), end: at(13), block: untracked(12, 13) }
    const plan = takePlan(block, { catalog, settings: relaxed })
    expect(plan.status === 'ready' && plan.requests.create.map((body) => [body.projectId, body.isBillable, body.start, body.stop])).toEqual([['beacon', false, at(12), at(13)]])
    const nowhere: ReviewItem = { ...block, block: untracked(12, 13, { projectIds: [], unlinkedSeconds: 3600 }) }
    expect(takePlan(nowhere, { catalog, settings: relaxed }).status).toBe('unlinked')
  })
})

describe('what an item becomes', () => {
  it('reads as the parts of one entry, or the entries the work moved between', () => {
    expect(outcomes(split(entry('e', 9, 10), suggestion('e'))).map((result) => result.parts.map((p) => [p.target.projectId, p.seconds]))).toEqual([
      [
        ['atlas', 2160],
        ['beacon', 1440],
      ],
    ])
    expect(outcomes(split(entry('e', 9, 10), suggestion('e', { stretches: moved }))).map((result) => [result.parts[0].target.projectId, result.start, result.end])).toEqual([
      ['atlas', at(9), at(9, 40)],
      ['beacon', at(9, 40), at(10)],
    ])
  })

  it('shows untracked time as it is saved: on its first project while a repository has none', () => {
    const block = untracked(12, 13, { projectIds: ['beacon'], parts: [part('beacon', 70), part(null, 30)] })
    const results = outcomes({ kind: 'untracked', id: 'u', day: '2026-09-14', start: block.start, end: block.end, block })
    expect(results.map((result) => result.parts.map((p) => [p.target.projectId, p.seconds]))).toEqual([[['beacon', 3600]]])
  })
})
