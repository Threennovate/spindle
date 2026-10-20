import { describe, expect, it } from 'vitest'
import { NONE_ID, reportFilter, sameReport, savedParameters, subGrouping, validateReportSearch } from './search'

const id = '0199a8f4-1b2c-7d3e-8f40-123456789abc'

describe('validateReportSearch', () => {
  it('keeps what is valid and drops the rest', () => {
    expect(
      validateReportSearch({
        tab: 'weekly',
        period: 'custom',
        from: '2026-09-01',
        to: 'yesterday',
        projects: [id, 'nope', id, NONE_ID],
        billable: 'maybe',
        groupBy: 'client',
        subGroupBy: 'none',
        page: 3,
        rounding: 'up',
        roundTo: 7,
        saved: 'x',
      }),
    ).toEqual({
      tab: 'weekly',
      period: 'custom',
      from: '2026-09-01',
      projects: [id, NONE_ID],
      groupBy: 'client',
      subGroupBy: 'none',
      page: 3,
      rounding: 'up',
    })
  })

  it('keeps a built-in view it knows', () => {
    expect(validateReportSearch({ view: 'my-month' })).toEqual(expect.objectContaining({ view: 'my-month' }))
    expect(validateReportSearch({ view: 'my-year' }).view).toBeUndefined()
  })

  it('ignores unknown values entirely', () => {
    const search = validateReportSearch({ tab: 'monthly', users: 'all', page: 0, q: '   ', asc: 'yes', view: 7 })
    expect(Object.values(search).every((value) => value === undefined)).toBe(true)
  })
})

describe('reportFilter', () => {
  it('turns the search into the API filter', () => {
    expect(reportFilter({ tags: [NONE_ID], billable: 'no', q: 'review', rounding: 'up', roundTo: 30 }, { from: '2026-09-01', to: '2026-09-30' })).toEqual({
      from: '2026-09-01',
      to: '2026-09-30',
      userIds: [],
      groupIds: [],
      projectIds: [],
      clientIds: [],
      tagIds: [NONE_ID],
      billable: false,
      description: 'review',
      rounding: 'up',
      roundingMinutes: 30,
    })
  })

  it('saves neither the page nor the view it was opened from', () => {
    expect(savedParameters({ tab: 'detailed', page: 4, saved: id, view: 'yesterday', sort: 'duration', q: undefined })).toEqual({ tab: 'detailed', sort: 'duration' })
  })
})

describe('sameReport', () => {
  const other = '0199a8f4-1b2c-7d3e-8f40-cba987654321'

  it('reads a default written out the same as one left out', () => {
    expect(sameReport({}, { tab: 'summary', period: 'thisWeek', groupBy: 'project', subGroupBy: 'description', sort: 'date', rounding: 'none' })).toBe(true)
    expect(sameReport({ rounding: 'up' }, { rounding: 'up', roundTo: 15 })).toBe(true)
  })

  it('ignores the order of IDs, the page and the view', () => {
    expect(sameReport({ users: [id, other] }, { users: [other, id], page: 2, view: 'my-week', saved: id })).toBe(true)
  })

  it('tells real changes apart', () => {
    expect(sameReport({ period: 'thisMonth' }, { period: 'lastMonth' })).toBe(false)
    expect(sameReport({ period: 'custom', from: '2026-09-01', to: '2026-09-30' }, { period: 'custom', from: '2026-09-01', to: '2026-09-29' })).toBe(false)
    expect(sameReport({ users: [id] }, {})).toBe(false)
    expect(sameReport({ subGroupBy: 'none' }, {})).toBe(false)
  })

  it('only compares the days of a custom period', () => {
    expect(sameReport({ period: 'thisMonth', from: '2026-09-01', to: '2026-09-30' }, { period: 'thisMonth' })).toBe(true)
  })
})

describe('subGrouping', () => {
  it('is the description unless it is the grouping itself or none', () => {
    expect(subGrouping('project', undefined)).toBe('description')
    expect(subGrouping('description', undefined)).toBeNull()
    expect(subGrouping('client', 'client')).toBeNull()
    expect(subGrouping('user', 'none')).toBeNull()
    expect(subGrouping('user', 'project')).toBe('project')
  })
})
