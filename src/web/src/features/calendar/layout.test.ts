import { describe, expect, it } from 'vitest'
import type { TimeEntry } from '@/api/types'
import { layoutDays, snapMinutes } from './layout'

const zone = 'Europe/Zagreb'

function entry(id: string, start: string, stop: string | null): TimeEntry {
  return { id, workspaceId: 'w', description: id, projectId: null, taskId: null, tagIds: [], isBillable: false, start, stop, shares: [], version: '1' }
}

describe('layoutDays', () => {
  it('places entries by wall-clock minutes in the user zone', () => {
    const [segment] = layoutDays([entry('a', '2026-09-28T07:00:00Z', '2026-09-28T08:30:00Z')], ['2026-09-28'], zone, 0)

    expect(segment).toMatchObject({ day: '2026-09-28', startMinute: 540, endMinute: 630, lane: 0, lanes: 1 })
  })

  it('splits an entry that runs past midnight across both days', () => {
    const late = entry('late', '2026-09-28T20:00:00Z', '2026-09-28T23:30:00Z') // 22:00–01:30 in Zagreb

    const segments = layoutDays([late], ['2026-09-28', '2026-09-29'], zone, 0)

    expect(segments).toEqual([
      expect.objectContaining({ day: '2026-09-28', startMinute: 1320, endMinute: 1440, continuesAfter: true }),
      expect.objectContaining({ day: '2026-09-29', startMinute: 0, endMinute: 90, continuesBefore: true }),
    ])
  })

  it('shows a running entry until now', () => {
    const now = Date.parse('2026-09-28T09:15:00Z')

    const [segment] = layoutDays([entry('running', '2026-09-28T09:00:00Z', null)], ['2026-09-28'], zone, now)

    expect(segment).toMatchObject({ startMinute: 660, endMinute: 675 })
  })

  it('puts overlapping entries side by side and the rest full width', () => {
    const segments = layoutDays(
      [
        entry('a', '2026-09-28T07:00:00Z', '2026-09-28T09:00:00Z'),
        entry('b', '2026-09-28T08:00:00Z', '2026-09-28T08:30:00Z'),
        entry('c', '2026-09-28T08:45:00Z', '2026-09-28T09:30:00Z'),
        entry('d', '2026-09-28T12:00:00Z', '2026-09-28T13:00:00Z'),
      ],
      ['2026-09-28'],
      zone,
      0,
    )
    const byId = Object.fromEntries(segments.map((s) => [s.entry.id, s]))

    expect(byId.a).toMatchObject({ lane: 0, lanes: 2 })
    expect(byId.b).toMatchObject({ lane: 1, lanes: 2 })
    // c starts after b ends, so it reuses b's lane next to a.
    expect(byId.c).toMatchObject({ lane: 1, lanes: 2 })
    expect(byId.d).toMatchObject({ lane: 0, lanes: 1 })
  })

  it('leaves out days an entry does not touch', () => {
    expect(layoutDays([entry('a', '2026-09-28T07:00:00Z', '2026-09-28T08:00:00Z')], ['2026-09-27', '2026-09-29'], zone, 0)).toEqual([])
  })
})

describe('snapMinutes', () => {
  it.each([
    [7, 15, 0],
    [8, 15, 15],
    [542, 15, 540],
    [1439, 15, 1440],
    [-20, 15, 0],
  ])('snaps %d to %d-minute steps as %d', (minutes, step, snapped) => {
    expect(snapMinutes(minutes, step)).toBe(snapped)
  })
})
