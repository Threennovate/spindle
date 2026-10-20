import { describe, expect, it } from 'vitest'
import { pickedNames } from './filters'
import type { SavedReport } from './queries'
import { reportRequests } from './requests'
import { NONE_ID, parseSavedParameters, savedParameters, validateReportSearch } from './search'
import { builtInViews, isBlank, isEdited, openingSearch, viewRef, viewSearch, whoseTime } from './views'

const me = '0199a8f4-1b2c-7d3e-8f40-123456789abc'
const ivo = '0199a8f4-1b2c-7d3e-8f40-cba987654321'
const savedId = '0199a8f4-1b2c-7d3e-8f40-000000000001'

function savedReport(parameters: object): SavedReport {
  return { id: savedId, name: 'Acme · October', kind: 'summary', parameters: JSON.stringify(parameters), updatedAt: '2026-10-20T10:00:00Z' }
}

describe('builtInViews', () => {
  it('gives members only their own views, and admins the team’s too', () => {
    expect(builtInViews(me, false).map((view) => view.id)).toEqual(['my-week', 'my-month', 'yesterday', 'billable-last-month'])
    expect(builtInViews(me, true).filter((view) => view.group === 'team').map((view) => view.id)).toEqual(['team-week', 'who-worked', 'billable-by-client', 'no-project'])
  })

  it('counts the person’s own time by their user ID, which survives being saved and read by the other apps', () => {
    for (const view of builtInViews(me, true).filter((candidate) => candidate.group === 'you')) {
      expect(view.search.users).toEqual([me])
      // What the web saves, read back as carefully as the Mac and Android apps read it: the user ID stays.
      const stored = JSON.stringify(savedParameters(viewSearch(view)))
      expect(JSON.parse(stored)).not.toHaveProperty('view')
      expect(parseSavedParameters(stored).users).toEqual([me])
    }
  })

  it('opens time without a project as the empty project ID', () => {
    expect(builtInViews(me, true).find((view) => view.id === 'no-project')?.search).toEqual({ tab: 'detailed', period: 'thisMonth', projects: [NONE_ID] })
  })

  it('makes every view valid in the address bar', () => {
    for (const view of builtInViews(me, true)) {
      expect(validateReportSearch({ ...viewSearch(view) })).toEqual(expect.objectContaining(viewSearch(view)))
    }
  })
})

describe('openingSearch', () => {
  const views = builtInViews(me, false)

  it('opens My week the first time', () => {
    expect(openingSearch(null, views, [])).toEqual({ tab: 'weekly', period: 'thisWeek', users: [me], view: 'my-week' })
  })

  it('opens the view opened last', () => {
    expect(openingSearch('yesterday', views, [])).toEqual(expect.objectContaining({ view: 'yesterday', period: 'yesterday' }))
    expect(openingSearch(`saved:${savedId}`, views, [savedReport({ period: 'lastMonth', clients: [ivo] })])).toEqual({ period: 'lastMonth', clients: [ivo], saved: savedId })
  })

  it('falls back to My week for a view that is gone or not offered', () => {
    expect(openingSearch(`saved:${savedId}`, views, [])).toEqual(expect.objectContaining({ view: 'my-week' }))
    expect(openingSearch('team-week', views, [])).toEqual(expect.objectContaining({ view: 'my-week' }))
    expect(openingSearch('nonsense', views, [])).toEqual(expect.objectContaining({ view: 'my-week' }))
  })
})

describe('views in the address bar', () => {
  it('knows which view is open', () => {
    expect(viewRef({ view: 'my-month' })).toBe('my-month')
    expect(viewRef({ saved: savedId, view: 'my-month' })).toBe(`saved:${savedId}`)
    expect(viewRef({ period: 'thisMonth' })).toBeUndefined()
  })

  it('is blank only with nothing in it', () => {
    expect(isBlank({})).toBe(true)
    expect(isBlank(validateReportSearch({}))).toBe(true)
    expect(isBlank({ tab: 'detailed' })).toBe(false)
  })

  it('shows a view as edited once it differs from what it opened with', () => {
    const month = builtInViews(me, false).find((view) => view.id === 'my-month')!
    expect(isEdited(month.search, viewSearch(month))).toBe(false)
    expect(isEdited(month.search, { ...viewSearch(month), page: 3 })).toBe(false)
    expect(isEdited(month.search, { ...viewSearch(month), billable: 'yes' })).toBe(true)
    expect(isEdited(month.search, { ...viewSearch(month), users: undefined })).toBe(true)
  })
})

describe('whoseTime', () => {
  it('tells me, everyone and a choice apart', () => {
    expect(whoseTime({}, me)).toBe('everyone')
    expect(whoseTime({ users: [me] }, me)).toBe('me')
    expect(whoseTime({ users: [ivo] }, me)).toBe('some')
    expect(whoseTime({ users: [me, ivo] }, me)).toBe('some')
    expect(whoseTime({ users: [me], groups: [ivo] }, me)).toBe('some')
  })
})

describe('reportRequests', () => {
  it('asks for the summary grouped as the address bar says, by description within by default', () => {
    const requests = reportRequests({ period: 'thisMonth', groupBy: 'client', users: [me] }, '2026-10-20', 1)
    expect(requests.range).toEqual({ from: '2026-10-01', to: '2026-10-31' })
    expect(requests.summary).toEqual(expect.objectContaining({ groupBy: 'client', subGroupBy: 'description', userIds: [me] }))
    expect(requests.detailed).toEqual(expect.objectContaining({ sort: 'date', descending: true, page: 1, pageSize: 50 }))
  })

  it('shows the weekly report a whole week, by project instead of description', () => {
    const requests = reportRequests({ tab: 'weekly', period: 'lastMonth', groupBy: 'description' }, '2026-10-20', 1)
    expect(requests.range).toEqual({ from: '2026-09-28', to: '2026-10-04' })
    expect(requests.weekly.groupBy).toBe('project')
  })
})

describe('pickedNames', () => {
  const sections = [{ options: [{ id: NONE_ID, label: 'No project' }] }, { options: ['Acme API', 'Acme Web', 'Internal'].map((label, index) => ({ id: String(index), label })) }]

  it('names two and counts the rest', () => {
    expect(pickedNames(sections, ['0', NONE_ID])).toBe('Acme API, No project')
    expect(pickedNames(sections, ['0', '1', '2'])).toBe('Acme API, Acme Web +1')
    expect(pickedNames(sections, ['gone'])).toBe('1 picked')
  })
})
