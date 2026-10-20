import { describe, expect, it } from 'vitest'
import { addMinutes, formatClockField, parseClock } from './clock'
import { formatDuration, formatDurationAs, formatHoursMinutes, parseDuration } from './duration'
import { addDaysToKey, atTime, dayKey, formatClock, latestAt, readClockField, readStartField, formatDayAs, formatDayHeading, startOfDayKey, weekBefore, weekOf } from './zone'

describe('formatDuration', () => {
  it.each([
    [0, '0:00:00'],
    [309, '0:05:09'],
    [45_000, '12:30:00'],
    [482_400, '134:00:00'],
    [-5, '0:00:00'],
  ])('formats %d seconds as %s', (seconds, text) => {
    expect(formatDuration(seconds)).toBe(text)
  })
})

describe('formatHoursMinutes', () => {
  it.each([
    [0, '0m'],
    [2_700, '45m'],
    [43_500, '12h 05m'],
  ])('formats %d seconds as %s', (seconds, text) => {
    expect(formatHoursMinutes(seconds)).toBe(text)
  })
})

describe('parseDuration', () => {
  it.each([
    ['1:30', 5_400],
    ['1:30:15', 5_415],
    ['0:45', 2_700],
    ['1h 30m', 5_400],
    ['1h30m', 5_400],
    ['90m', 5_400],
    ['90 min', 5_400],
    ['45s', 45],
    ['2h', 7_200],
    ['1.5h', 5_400],
    ['1.5', 5_400],
    ['1,25', 4_500],
    ['90', 5_400],
    ['  15  ', 900],
  ])('reads %s as %d seconds', (text, seconds) => {
    expect(parseDuration(text)).toBe(seconds)
  })

  it.each(['', 'abc', '1:75', 'h', '1:2:3:4'])('rejects %s', (text) => {
    expect(parseDuration(text)).toBeNull()
  })
})

describe('parseClock', () => {
  it.each([
    ['9', 9, 0],
    ['09', 9, 0],
    ['21', 21, 0],
    ['930', 9, 30],
    ['0930', 9, 30],
    ['2130', 21, 30],
    ['005', 0, 5],
    ['9:30', 9, 30],
    ['09.30', 9, 30],
    ['9,30', 9, 30],
    ['9 30', 9, 30],
    ['9h30', 9, 30],
    ['9h', 9, 0],
    ['9:', 9, 0],
    ['9:5', 9, 5],
    ['21:30', 21, 30],
    ['9:30pm', 21, 30],
    ['930pm', 21, 30],
    ['9 am', 9, 0],
    ['9p', 21, 0],
    ['9:30 p.m.', 21, 30],
    ['12am', 0, 0],
    ['12:15 pm', 12, 15],
    ['0', 0, 0],
    ['  7:45  ', 7, 45],
  ])('reads %s as %d:%d', (text, hours, minutes) => {
    expect(parseClock(text)).toEqual({ hours, minutes })
  })

  it.each(['', '24', '93', '960', '2400', '9:60', '13pm', '0am', 'noon', '12345', '930:15', '9::30', '-15'])('rejects %s', (text) => {
    expect(parseClock(text)).toBeNull()
  })
})

describe('formatClockField', () => {
  it.each([
    [9, 5, false, '09:05'],
    [21, 30, false, '21:30'],
    [0, 0, true, '12:00 am'],
    [9, 5, true, '9:05 am'],
    [12, 15, true, '12:15 pm'],
    [21, 30, true, '9:30 pm'],
  ])('writes %d:%d (12-hour: %s) as %s', (hours, minutes, hour12, text) => {
    expect(formatClockField({ hours, minutes }, hour12)).toBe(text)
  })

  it('writes what parseClock reads back', () => {
    for (const hour12 of [false, true]) {
      for (let total = 0; total < 1440; total += 7) {
        const clock = { hours: Math.floor(total / 60), minutes: total % 60 }
        expect(parseClock(formatClockField(clock, hour12))).toEqual(clock)
      }
    }
  })
})

describe('addMinutes', () => {
  it.each([
    [9, 30, 1, 9, 31],
    [9, 59, 1, 10, 0],
    [9, 0, -15, 8, 45],
    [23, 50, 15, 0, 5],
    [0, 5, -15, 23, 50],
  ])('moves %d:%d by %d minutes to %d:%d', (hours, minutes, change, toHours, toMinutes) => {
    expect(addMinutes({ hours, minutes }, change)).toEqual({ hours: toHours, minutes: toMinutes })
  })
})

describe('readClockField', () => {
  const zagreb = 'Europe/Zagreb'
  const now = new Date('2026-10-02T18:27:40Z') // 20:27:40 in Zagreb (CEST)

  it.each([
    ['2015', 20, 15],
    ['now', 20, 27],
    ['NOW', 20, 27],
    ['-15', 20, 12],
    ['-1:30', 18, 57],
    ['-1.5', 18, 57],
    ['-2h', 18, 27],
    ['-90m', 18, 57],
  ])('reads %s as %d:%d', (text, hours, minutes) => {
    expect(readClockField(text, now, zagreb)).toEqual({ hours, minutes })
  })

  it('goes back past midnight', () => {
    expect(readClockField('-30', new Date('2026-10-01T22:10:00Z'), zagreb)).toEqual({ hours: 23, minutes: 40 })
  })

  it.each(['-', '-abc', 'later'])('rejects %s', (text) => {
    expect(readClockField(text, now, zagreb)).toBeNull()
  })
})

describe('readStartField', () => {
  const zagreb = 'Europe/Zagreb'
  const now = new Date('2026-10-02T18:27:40Z') // 20:27:40 in Zagreb (CEST)

  it.each([
    ['2015', '2026-10-02T18:15:00.000Z'],
    ['2115', '2026-10-01T19:15:00.000Z'],
    ['-15', '2026-10-02T18:12:00.000Z'],
    ['-25h', '2026-10-01T17:27:00.000Z'],
  ])('reads %s as %s', (text, instant) => {
    expect(readStartField(text, now, zagreb)?.toISOString()).toBe(instant)
  })

  it('rejects what is not a time', () => {
    expect(readStartField('soon', now, zagreb)).toBeNull()
  })
})

describe('latestAt', () => {
  const zagreb = 'Europe/Zagreb'
  const now = new Date('2026-10-02T18:27:40Z') // 20:27:40 in Zagreb (CEST)

  it('is today when the time has passed', () => {
    expect(latestAt({ hours: 20, minutes: 15 }, now, zagreb).toISOString()).toBe('2026-10-02T18:15:00.000Z')
  })

  it('is yesterday when the time is still to come today', () => {
    expect(latestAt({ hours: 21, minutes: 15 }, now, zagreb).toISOString()).toBe('2026-10-01T19:15:00.000Z')
  })
})

describe('time zones', () => {
  const zagreb = 'Europe/Zagreb'
  const tokyo = 'Asia/Tokyo'

  it('puts an instant on the day it is in the user zone, not in UTC', () => {
    const lateEvening = '2026-09-28T22:30:00Z' // 00:30 on 29 Sep in Zagreb (CEST), 07:30 in Tokyo

    expect(dayKey(lateEvening, zagreb)).toBe('2026-09-29')
    expect(dayKey(lateEvening, 'UTC')).toBe('2026-09-28')
    expect(dayKey(lateEvening, tokyo)).toBe('2026-09-29')
  })

  it('finds midnight and clock times in the zone, across daylight saving changes', () => {
    expect(startOfDayKey('2026-09-28', zagreb).toISOString()).toBe('2026-09-27T22:00:00.000Z')
    expect(startOfDayKey('2026-12-01', zagreb).toISOString()).toBe('2026-11-30T23:00:00.000Z')
    // The clocks go back on 25 October 2026: 09:00 that day is UTC+1.
    expect(atTime('2026-10-25', 9, 0, zagreb).toISOString()).toBe('2026-10-25T08:00:00.000Z')
  })

  it('moves between days by calendar, not by 24 hours', () => {
    expect(addDaysToKey('2026-10-24', 1)).toBe('2026-10-25')
    expect(addDaysToKey('2026-03-01', -1)).toBe('2026-02-28')
  })

  it('finds the week containing an instant, starting on the chosen weekday', () => {
    const wednesday = new Date('2026-09-30T10:00:00Z')

    const mondayWeek = weekOf(wednesday, zagreb, 1)
    const sundayWeek = weekOf(wednesday, zagreb, 0)

    expect(mondayWeek.from.toISOString()).toBe('2026-09-27T22:00:00.000Z')
    expect(mondayWeek.to.toISOString()).toBe('2026-10-04T22:00:00.000Z')
    expect(sundayWeek.from.toISOString()).toBe('2026-09-26T22:00:00.000Z')
  })

  it('shows clock times in the zone', () => {
    expect(formatClock('2026-09-28T07:05:00Z', zagreb)).toBe('09:05')
    expect(formatClock('2026-09-28T19:05:00Z', zagreb, true)).toBe('9:05 pm')
  })

  it('names days relative to today', () => {
    const now = new Date('2026-09-28T10:00:00Z')

    expect(formatDayHeading('2026-09-28', zagreb, now)).toBe('Today')
    expect(formatDayHeading('2026-09-27', zagreb, now)).toBe('Yesterday')
    expect(formatDayHeading('2026-09-22', zagreb, now)).toBe('Tue, 22 Sep')
    expect(formatDayHeading('2025-12-31', zagreb, now)).toBe('Wed, 31 Dec 2025')
  })
})

describe('weekBefore', () => {
  it('steps back whole calendar weeks across the daylight saving change', () => {
    const now = new Date('2026-10-28T10:00:00Z') // Wednesday after the clocks went back on Sunday 25 Oct

    const thisWeek = weekBefore(0, now, 'Europe/Zagreb', 1)
    const lastWeek = weekBefore(1, now, 'Europe/Zagreb', 1)

    expect(thisWeek.from.toISOString()).toBe('2026-10-25T23:00:00.000Z')
    expect(lastWeek.from.toISOString()).toBe('2026-10-18T22:00:00.000Z')
    expect(lastWeek.to.toISOString()).toBe(thisWeek.from.toISOString())
  })
})

describe('person-chosen formats', () => {
  it('writes durations three ways', () => {
    expect(formatDurationAs(5400, 'hms')).toBe('1:30:00')
    expect(formatDurationAs(5459, 'hm')).toBe('1:30')
    expect(formatDurationAs(5400, 'decimal')).toBe('1.50 h')
    expect(formatDurationAs(-5, 'decimal')).toBe('0.00 h')
  })

  it('writes days in the chosen pattern', () => {
    expect(formatDayAs('2026-09-28', 'dd.MM.yyyy')).toBe('28.09.2026')
    expect(formatDayAs('2026-09-28', 'MM/dd/yyyy')).toBe('09/28/2026')
    expect(formatDayAs('2026-09-08', 'd MMM yyyy')).toBe('8 Sep 2026')
  })
})
