import { describe, expect, it } from 'vitest'
import { focusLeftMs, formatCountdown, intervalsDone } from './pomodoro'

const minutes = (value: number) => value * 60_000

describe('pomodoro intervals', () => {
  it('counts whole focus intervals', () => {
    expect(intervalsDone(minutes(24), 25)).toBe(0)
    expect(intervalsDone(minutes(25), 25)).toBe(1)
    expect(intervalsDone(minutes(51), 25)).toBe(2)
    expect(intervalsDone(-5000, 25)).toBe(0)
  })

  it('counts down the current interval', () => {
    expect(focusLeftMs(0, 25)).toBe(minutes(25))
    expect(focusLeftMs(minutes(10), 25)).toBe(minutes(15))
    expect(focusLeftMs(minutes(26), 25)).toBe(minutes(24))
  })

  it('shows countdowns as minutes and seconds', () => {
    expect(formatCountdown(minutes(12) + 5000)).toBe('12:05')
    expect(formatCountdown(400)).toBe('0:01')
    expect(formatCountdown(-10)).toBe('0:00')
  })
})
