import { describe, expect, it } from 'vitest'
import { formatHourTick, hasBilledTime, hourTicks, percent, perHour } from './charts'

describe('hourTicks', () => {
  it('picks friendly steps that cover the tallest bar', () => {
    expect(hourTicks(7.5)).toEqual([0, 2, 4, 6, 8])
    expect(hourTicks(0.4)).toEqual([0, 0.25, 0.5])
    expect(hourTicks(0)).toEqual([0, 0.25])
    expect(hourTicks(160)).toEqual([0, 40, 80, 120, 160])
    expect(hourTicks(9000).at(-1)).toBeGreaterThanOrEqual(9000)
  })

  it('labels hours and minutes', () => {
    expect([0, 0.25, 1.5, 8].map(formatHourTick)).toEqual(['0', '15m', '1h 30m', '8h'])
  })
})

describe('percent', () => {
  it('rounds shares sensibly', () => {
    expect(percent(1, 3)).toBe('33%')
    expect(percent(1, 30)).toBe('3.3%')
    expect(percent(0, 30)).toBe('0%')
    expect(percent(5, 0)).toBe('0%')
  })
})

describe('perHour', () => {
  it('spreads the amount over all the time tracked', () => {
    expect(perHour(300, 4 * 3600, 'EUR')).toBe('€75.00/h')
    expect(perHour(300, 0, 'EUR')).toBe('—')
    expect(perHour(null, 3600, 'EUR')).toBe('—')
  })
})

describe('hasBilledTime', () => {
  it('tells when billed time was entered apart from what was tracked', () => {
    expect(hasBilledTime({ billableSeconds: 3600, billedSeconds: 5400 })).toBe(true)
    expect(hasBilledTime({ billableSeconds: 3600, billedSeconds: 3600 })).toBe(false)
    expect(hasBilledTime({ billableSeconds: 3600, billedSeconds: null })).toBe(false)
  })
})
