import { describe, expect, it } from 'vitest'
import { NONE_ID, reportFilter, savedParameters, validateReportSearch } from './search'

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

  it('ignores unknown values entirely', () => {
    const search = validateReportSearch({ tab: 'monthly', users: 'all', page: 0, q: '   ', asc: 'yes' })
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

  it('saves neither the page nor the saved report', () => {
    expect(savedParameters({ tab: 'detailed', page: 4, saved: id, sort: 'duration', q: undefined })).toEqual({ tab: 'detailed', sort: 'duration' })
  })
})
