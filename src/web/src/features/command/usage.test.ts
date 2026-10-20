import { describe, expect, it } from 'vitest'
import { mostUsed, recordUse, usageScore, type Usage } from './usage'

const DAY = 86_400_000
const now = Date.UTC(2026, 9, 5, 12)

describe('usage', () => {
  it('counts uses and remembers the last one', () => {
    let usage: Usage = {}
    usage = recordUse(usage, 'a', now - DAY)
    usage = recordUse(usage, 'a', now)
    expect(usage.a).toEqual({ count: 2, last: now })
  })

  it('scores frequent and recent use higher, and lets old use fade', () => {
    const once = usageScore({ count: 1, last: now }, now)
    const often = usageScore({ count: 8, last: now }, now)
    const oftenLongAgo = usageScore({ count: 8, last: now - 90 * DAY }, now)
    expect(often).toBeGreaterThan(once)
    expect(oftenLongAgo).toBeLessThan(once)
    expect(often).toBeLessThan(1)
    expect(usageScore(undefined, now)).toBe(0)
  })

  it('keeps only the most recently used items', () => {
    let usage: Usage = {}
    for (let i = 0; i < 130; i++) {
      usage = recordUse(usage, `item-${i}`, now + i)
    }
    expect(Object.keys(usage)).toHaveLength(120)
    expect(usage['item-0']).toBeUndefined()
    expect(usage['item-129']).toBeDefined()
  })

  it('lists the most used, leaving out what was barely used', () => {
    const usage: Usage = {
      reports: { count: 6, last: now },
      timer: { count: 3, last: now },
      once: { count: 1, last: now - 60 * DAY },
    }
    expect(mostUsed(usage, now, 5)).toEqual(['reports', 'timer'])
  })
})
