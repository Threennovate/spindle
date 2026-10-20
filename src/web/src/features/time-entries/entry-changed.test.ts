import { describe, expect, it } from 'vitest'
import { ApiError, entryChangedBy } from '@/api/problem'
import type { TimeEntry } from '@/api/types'
import { changedEntrySummary } from './entry-changed'
import { entryParams } from './queries'

const theirs: TimeEntry = {
  id: 'e1',
  workspaceId: 'w',
  description: 'Changed elsewhere',
  projectId: 'p',
  taskId: null,
  tagIds: [],
  isBillable: false,
  start: '2026-10-15T07:00:00Z',
  stop: '2026-10-15T08:30:00Z',
  shares: [],
  version: '42',
}

describe('entryChangedBy', () => {
  it('finds the entry as it is now in a refused write', () => {
    const error = new ApiError(412, { type: 'urn:spindle:problem:entry-changed', status: 412, current: theirs } as never)
    expect(entryChangedBy(error)).toEqual(theirs)
  })

  it('ignores other errors', () => {
    expect(entryChangedBy(new ApiError(412, { type: 'about:blank', status: 412 }))).toBeNull()
    expect(entryChangedBy(new ApiError(400, { title: 'Invalid' }))).toBeNull()
    expect(entryChangedBy(new TypeError('Failed to fetch'))).toBeNull()
  })
})

describe('entryParams', () => {
  it('sends the version loaded as If-Match', () => {
    expect(entryParams('w', { id: 'e1', version: '42' })).toEqual({ path: { workspaceId: 'w', entryId: 'e1' }, header: { 'If-Match': '"42"' } })
  })

  it('sends no condition without a version, as for entries not saved yet', () => {
    expect(entryParams('w', 'e1').header).toBeUndefined()
    expect(entryParams('w', { id: 'e1', version: '' }).header).toBeUndefined()
  })
})

describe('changedEntrySummary', () => {
  const now = new Date('2026-10-15T12:00:00Z')

  it('says what the other device left: description, project and time', () => {
    expect(changedEntrySummary(theirs, 'Website', 'Europe/Zagreb', false, now)).toBe('“Changed elsewhere” · Website · Today, 09:00–10:30')
  })

  it('says when there is no description or the timer runs', () => {
    expect(changedEntrySummary({ ...theirs, description: '', stop: null }, undefined, 'Europe/Zagreb', false, now)).toBe(
      'No description · Today, running since 09:00',
    )
  })
})
