import { describe, expect, it } from 'vitest'
import type { TimeEntry } from '@/api/types'
import { recentEntries, runningLine, runningTotals, weekBefore, weekMoment, weekSummary } from './week'

const DAYS = ['2026-10-12', '2026-10-13', '2026-10-14', '2026-10-15', '2026-10-16', '2026-10-17', '2026-10-18']

function entry(id: string, start: string, stop: string | null, fields: Partial<TimeEntry> = {}): TimeEntry {
  return { id, workspaceId: 'w', description: id, projectId: 'api', taskId: null, tagIds: [], isBillable: true, start, stop, shares: [], version: '1', ...fields }
}

const share = (projectId: string, percent: number, isBillable = true) => ({ projectId, taskId: null, isBillable, percent, description: null })

describe('weekSummary', () => {
  const now = Date.parse('2026-10-15T14:00:00Z')

  it('counts each part of a split entry on its own project', () => {
    const summary = weekSummary(
      [
        entry('one', '2026-10-12T08:00:00Z', '2026-10-12T10:00:00Z'),
        entry('split', '2026-10-13T08:00:00Z', '2026-10-13T09:00:00Z', { shares: [share('api', 60), share('web', 40, false)] }),
      ],
      DAYS,
      'UTC',
      now,
    )

    expect(summary.seconds).toBe(3 * 3600)
    expect(summary.dayTotals.slice(0, 2)).toEqual([2 * 3600, 3600])
    expect(summary.splitSeconds).toBe(3600)
    expect(summary.splitEntries).toBe(1)
    expect(summary.billableSeconds).toBe(2 * 3600 + 2160)
    expect(summary.projects).toEqual([
      { projectId: 'api', seconds: 2 * 3600 + 2160, splitSeconds: 2160, days: [7200, 2160, 0, 0, 0, 0, 0] },
      { projectId: 'web', seconds: 1440, splitSeconds: 1440, days: [0, 1440, 0, 0, 0, 0, 0] },
    ])
  })

  it('puts an entry on the day it starts, as reports do, and leaves out other weeks', () => {
    const summary = weekSummary(
      [
        // 23:30 on Sunday 11 Oct in Zagreb, so last week's, though it ends on Monday.
        entry('late', '2026-10-11T21:30:00Z', '2026-10-11T23:30:00Z'),
        // 00:30 on Monday 12 Oct in Zagreb.
        entry('early', '2026-10-11T22:30:00Z', '2026-10-11T23:00:00Z'),
      ],
      DAYS,
      'Europe/Zagreb',
      now,
    )

    expect(summary.seconds).toBe(1800)
    expect(summary.dayTotals[0]).toBe(1800)
  })

  it('counts a running entry until now, and time on no project', () => {
    const summary = weekSummary([entry('running', '2026-10-15T13:15:00Z', null, { projectId: null, isBillable: false })], DAYS, 'UTC', now)

    expect(summary.seconds).toBe(45 * 60)
    expect(summary.billableSeconds).toBe(0)
    expect(summary.projects.map((project) => project.projectId)).toEqual([null])
  })

  it('is empty without entries', () => {
    const summary = weekSummary([], DAYS, 'UTC', now)

    expect(summary.seconds).toBe(0)
    expect(summary.projects).toEqual([])
    expect(summary.dayTotals).toEqual([0, 0, 0, 0, 0, 0, 0])
  })
})

describe('weekBefore', () => {
  it('counts the week up to the cutoff', () => {
    const cutoff = Date.parse('2026-10-14T09:00:00Z')
    const week = weekBefore(
      [
        entry('before', '2026-10-12T08:00:00Z', '2026-10-12T09:00:00Z'),
        entry('across', '2026-10-14T08:00:00Z', '2026-10-14T10:00:00Z', { isBillable: false }),
        entry('after', '2026-10-14T11:00:00Z', '2026-10-14T12:00:00Z'),
        entry('running', '2026-10-14T08:30:00Z', null),
      ],
      DAYS,
      'UTC',
      cutoff,
    )

    expect(week.seconds).toBe(2 * 3600 + 1800)
    expect(week.dayTotals).toEqual([3600, 0, 3600 + 1800, 0, 0, 0, 0])
    expect(week.billableSeconds).toBe(3600 + 1800)
  })
})

describe('runningTotals', () => {
  it('adds each day to the ones before it', () => {
    expect(runningTotals([3600, 0, 1800])).toEqual([3600, 3600, 5400])
  })
})

describe('weekMoment', () => {
  it('is how far into the week now is, in days', () => {
    expect(weekMoment(DAYS, 'UTC', Date.parse('2026-10-13T12:00:00Z'))).toBe(1.5)
    expect(weekMoment(DAYS, 'Europe/Zagreb', Date.parse('2026-10-13T22:00:00Z'))).toBe(2)
  })

  it('is nothing before the week and all of it after', () => {
    expect(weekMoment(DAYS, 'UTC', Date.parse('2026-10-11T12:00:00Z'))).toBe(0)
    expect(weekMoment(DAYS, 'UTC', Date.parse('2026-10-20T12:00:00Z'))).toBe(7)
  })
})

describe('runningLine', () => {
  it('passes through the moment inside a day, between the ends of the days around it', () => {
    expect(runningLine([3600, 1800, 0], { at: 1.5, seconds: 4000 })).toEqual([
      [0, 0],
      [1, 3600],
      [1.5, 4000],
      [2, 5400],
      [3, 5400],
    ])
  })

  it('adds nothing for a moment at the end of a day', () => {
    expect(runningLine([3600, 1800], { at: 2, seconds: 5400 })).toEqual([
      [0, 0],
      [1, 3600],
      [2, 5400],
    ])
  })
})

describe('recentEntries', () => {
  it('keeps the latest of each description and project, finished ones only', () => {
    const recent = recentEntries(
      [
        entry('Review', '2026-10-12T08:00:00Z', '2026-10-12T09:00:00Z'),
        entry('Review', '2026-10-13T08:00:00Z', '2026-10-13T09:00:00Z'),
        entry('Review', '2026-10-13T10:00:00Z', '2026-10-13T11:00:00Z', { projectId: 'web' }),
        entry('Running', '2026-10-15T10:00:00Z', null),
        entry('Planning', '2026-10-14T10:00:00Z', '2026-10-14T11:00:00Z'),
      ],
      2,
    )

    expect(recent.map((each) => [each.description, each.projectId, each.start])).toEqual([
      ['Planning', 'api', '2026-10-14T10:00:00Z'],
      ['Review', 'web', '2026-10-13T10:00:00Z'],
    ])
  })
})
