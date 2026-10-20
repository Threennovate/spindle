export interface Clock {
  hours: number
  minutes: number
}

/**
 * Reads a clock time typed by a person. Digits alone need no separator: `9` is 9:00, `930` is 9:30 and `2130` is
 * 21:30. With a separator (`:`, `.`, `,`, `h` or a space) minutes may be short: `9:5` is 9:05 and `9h` is 9:00.
 * `am`/`pm` (also `a`, `p`, `a.m.`) read the hours on a 12-hour clock.
 * Returns hours (0–23) and minutes, or null if the text is not a time.
 */
export function parseClock(text: string): Clock | null {
  const value = text.trim().toLowerCase()
  const match = /^(\d{1,4})(?:\s*[:.,h\s]\s*(\d{0,2}))?\s*(?:([ap])\.?(?:m\.?)?)?$/.exec(value)
  if (!match) {
    return null
  }

  const [, digits, typedMinutes, meridiem] = match
  let hours: number
  let minutes: number
  if (typedMinutes !== undefined) {
    // With a separator the hours come first and can't run into the minutes.
    if (digits.length > 2) {
      return null
    }
    hours = Number(digits)
    minutes = Number(typedMinutes || 0)
  } else if (digits.length <= 2) {
    hours = Number(digits)
    minutes = 0
  } else {
    // `930` and `0930`: the last two digits are the minutes.
    hours = Number(digits.slice(0, -2))
    minutes = Number(digits.slice(-2))
  }

  if (minutes > 59) {
    return null
  }
  if (meridiem) {
    if (hours < 1 || hours > 12) {
      return null
    }
    hours = (hours % 12) + (meridiem === 'p' ? 12 : 0)
  } else if (hours > 23) {
    return null
  }

  return { hours, minutes }
}

/** A clock time for a field: `09:05`, or `9:05 am` with a 12-hour clock. */
export function formatClockField({ hours, minutes }: Clock, hour12: boolean): string {
  const mm = minutes.toString().padStart(2, '0')
  if (!hour12) {
    return `${hours.toString().padStart(2, '0')}:${mm}`
  }
  return `${hours % 12 || 12}:${mm} ${hours < 12 ? 'am' : 'pm'}`
}

/** The clock time `minutes` later (or earlier, when negative), going round midnight. */
export function addMinutes({ hours, minutes }: Clock, change: number): Clock {
  const total = (((hours * 60 + minutes + change) % 1440) + 1440) % 1440
  return { hours: Math.floor(total / 60), minutes: total % 60 }
}
