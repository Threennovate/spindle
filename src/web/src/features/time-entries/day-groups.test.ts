import { describe, expect, it } from 'vitest'
import type { TimeEntry } from '@/api/types'
import { entrySeconds, groupByDay } from './day-groups'

function entry(id: string, start: string, stop: string | null): TimeEntry {
  return { id, workspaceId: 'w', description: id, projectId: null, taskId: null, tagIds: [], isBillable: false, start, stop, shares: [], version: '1' }
}

describe('groupByDay', () => {
  const now = Date.parse('2026-09-28T12:00:00Z')

  it('groups by the day in the user zone, newest first, with totals', () => {
    const groups = groupByDay(
      [
        entry('morning', '2026-09-28T07:00:00Z', '2026-09-28T08:30:00Z'),
        entry('late', '2026-09-27T22:30:00Z', '2026-09-27T23:00:00Z'), // 00:30 on 28 Sep in Zagreb
        entry('yesterday', '2026-09-27T09:00:00Z', '2026-09-27T10:00:00Z'),
      ],
      'Europe/Zagreb',
      now,
    )

    expect(groups.map((g) => g.key)).toEqual(['2026-09-28', '2026-09-27'])
    expect(groups[0].entries.map((e) => e.id)).toEqual(['morning', 'late'])
    expect(groups[0].totalSeconds).toBe(2 * 3600)
    expect(groups[1].totalSeconds).toBe(3600)
  })

  it('counts a running entry until now', () => {
    const groups = groupByDay([entry('running', '2026-09-28T11:30:00Z', null)], 'UTC', now)

    expect(groups[0].totalSeconds).toBe(1800)
  })
})

describe('entrySeconds', () => {
  it('never goes negative', () => {
    expect(entrySeconds({ start: '2026-09-28T12:00:00Z', stop: null }, Date.parse('2026-09-28T11:59:00Z'))).toBe(0)
  })
})
