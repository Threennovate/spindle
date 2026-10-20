import { describe, expect, it } from 'vitest'
import { dayCount, formatDayRange, resolvePeriod, shiftRange, weekContaining } from './period'

// Wednesday, 30 September 2026.
const today = '2026-09-30'

describe('resolvePeriod', () => {
  it.each([
    ['today', '2026-09-30', '2026-09-30'],
    ['yesterday', '2026-09-29', '2026-09-29'],
    ['thisWeek', '2026-09-28', '2026-10-04'],
    ['lastWeek', '2026-09-21', '2026-09-27'],
    ['thisMonth', '2026-09-01', '2026-09-30'],
    ['lastMonth', '2026-08-01', '2026-08-31'],
    ['thisYear', '2026-01-01', '2026-12-31'],
    ['lastYear', '2025-01-01', '2025-12-31'],
  ] as const)('%s runs from %s to %s', (period, from, to) => {
    expect(resolvePeriod(period, {}, today, 1)).toEqual({ from, to })
  })

  it('starts weeks on the chosen day', () => {
    expect(resolvePeriod('thisWeek', {}, today, 0)).toEqual({ from: '2026-09-27', to: '2026-10-03' })
    expect(weekContaining('2026-09-27', 1)).toEqual({ from: '2026-09-21', to: '2026-09-27' })
  })

  it('takes a custom range as it is, unless it runs backwards', () => {
    expect(resolvePeriod('custom', { from: '2026-09-03', to: '2026-09-17' }, today, 1)).toEqual({ from: '2026-09-03', to: '2026-09-17' })
    expect(resolvePeriod('custom', { from: '2026-09-17', to: '2026-09-03' }, today, 1)).toEqual({ from: '2026-09-28', to: '2026-10-04' })
    expect(resolvePeriod('lastMonth', {}, '2026-01-15', 1)).toEqual({ from: '2025-12-01', to: '2025-12-31' })
  })
})

describe('shiftRange', () => {
  it('moves weeks and custom ranges by their length', () => {
    expect(shiftRange({ from: '2026-09-28', to: '2026-10-04' }, -1)).toEqual({ from: '2026-09-21', to: '2026-09-27' })
    expect(shiftRange({ from: '2026-09-03', to: '2026-09-05' }, 1)).toEqual({ from: '2026-09-06', to: '2026-09-08' })
  })

  it('moves months and years by the calendar', () => {
    expect(shiftRange({ from: '2026-01-01', to: '2026-01-31' }, 1)).toEqual({ from: '2026-02-01', to: '2026-02-28' })
    expect(shiftRange({ from: '2026-03-01', to: '2026-03-31' }, -1)).toEqual({ from: '2026-02-01', to: '2026-02-28' })
    expect(shiftRange({ from: '2026-01-01', to: '2026-12-31' }, -1)).toEqual({ from: '2025-01-01', to: '2025-12-31' })
    expect(shiftRange({ from: '2026-11-01', to: '2027-01-31' }, 1)).toEqual({ from: '2027-02-01', to: '2027-04-30' })
  })
})

describe('formatting', () => {
  it('names ranges compactly', () => {
    // Engines differ on "Sep" and "Sept".
    expect(formatDayRange({ from: '2026-09-14', to: '2026-09-20' })).toMatch(/^14 – 20 Sept? 2026$/)
    expect(formatDayRange({ from: '2026-09-28', to: '2026-10-04' })).toMatch(/^28 Sept? – 4 Oct 2026$/)
    expect(formatDayRange({ from: '2026-09-30', to: '2026-09-30' })).toMatch(/^Wed,? 30 Sept? 2026$/)
    expect(formatDayRange({ from: '2026-12-28', to: '2027-01-03' })).toMatch(/^28 Dec 2026 – 3 Jan 2027$/)
    expect(dayCount({ from: '2026-01-01', to: '2026-12-31' })).toBe(365)
  })
})
