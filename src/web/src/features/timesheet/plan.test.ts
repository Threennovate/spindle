import { describe, expect, it } from 'vitest'
import type { TimeEntry } from '@/api/types'
import { planCellChange } from './plan'

const zone = 'Europe/Zagreb'
const day = '2026-09-28'

function entry(id: string, start: string, stop: string): TimeEntry {
  return { id, workspaceId: 'w', description: '', projectId: 'p', taskId: null, tagIds: [], isBillable: false, start, stop, shares: [], version: '1' }
}

describe('planCellChange', () => {
  it('adds more time as one entry after the day’s last entry', () => {
    const morning = entry('m', '2026-09-28T07:00:00Z', '2026-09-28T08:00:00Z') // 09:00–10:00 local
    const other = entry('o', '2026-09-28T09:00:00Z', '2026-09-28T10:30:00Z') // another row, until 12:30

    const plan = planCellChange({ rowEntries: [morning], dayEntries: [morning, other], day, timeZone: zone, targetSeconds: 2.5 * 3600 })

    expect(plan).toEqual([{ kind: 'create', start: new Date('2026-09-28T10:30:00Z'), stop: new Date('2026-09-28T12:00:00Z') }])
  })

  it('starts at 9:00 on an empty day', () => {
    const plan = planCellChange({ rowEntries: [], dayEntries: [], day, timeZone: zone, targetSeconds: 1800 })

    expect(plan).toEqual([{ kind: 'create', start: new Date('2026-09-28T07:00:00Z'), stop: new Date('2026-09-28T07:30:00Z') }])
  })

  it('keeps added time on its day', () => {
    const late = entry('late', '2026-09-28T19:00:00Z', '2026-09-28T21:00:00Z') // until 23:00 local

    const [operation] = planCellChange({ rowEntries: [], dayEntries: [late], day, timeZone: zone, targetSeconds: 3 * 3600 })

    expect(operation).toEqual({ kind: 'create', start: new Date('2026-09-28T19:00:00Z'), stop: new Date('2026-09-28T22:00:00Z') })
  })

  it('takes less time from the latest entries, deleting the ones used up', () => {
    const first = entry('first', '2026-09-28T07:00:00Z', '2026-09-28T09:00:00Z') // 2h
    const second = entry('second', '2026-09-28T10:00:00Z', '2026-09-28T10:30:00Z') // 30m

    const plan = planCellChange({ rowEntries: [first, second], dayEntries: [first, second], day, timeZone: zone, targetSeconds: 3600 })

    expect(plan).toEqual([
      { kind: 'delete', entry: second },
      { kind: 'shorten', entry: first, stop: new Date('2026-09-28T08:00:00Z') },
    ])
  })

  it('clears the cell when set to zero, and does nothing when unchanged', () => {
    const only = entry('only', '2026-09-28T07:00:00Z', '2026-09-28T08:00:00Z')

    expect(planCellChange({ rowEntries: [only], dayEntries: [only], day, timeZone: zone, targetSeconds: 0 })).toEqual([{ kind: 'delete', entry: only }])
    expect(planCellChange({ rowEntries: [only], dayEntries: [only], day, timeZone: zone, targetSeconds: 3600 })).toEqual([])
  })
})
