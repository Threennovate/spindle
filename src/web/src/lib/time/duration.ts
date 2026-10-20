/** Formats seconds as `h:mm:ss`, the way Toggl shows durations: 0:05:09, 12:30:00, 134:00:00. */
export function formatDuration(totalSeconds: number): string {
  const seconds = Math.max(0, Math.floor(totalSeconds))
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const rest = seconds % 60
  return `${hours}:${pad(minutes)}:${pad(rest)}`
}

/** How a person likes durations written: `hms` (1:30:00), `hm` (1:30) or `decimal` (1.50 h). */
export type DurationFormat = 'hms' | 'hm' | 'decimal'

/** Formats a finished duration the way the person chose. A running timer always shows seconds. */
export function formatDurationAs(totalSeconds: number, format: DurationFormat): string {
  const seconds = Math.max(0, Math.floor(totalSeconds))
  if (format === 'decimal') {
    return `${(seconds / 3600).toFixed(2)} h`
  }
  if (format === 'hm') {
    const minutes = Math.floor(seconds / 60)
    return `${Math.floor(minutes / 60)}:${pad(minutes % 60)}`
  }
  return formatDuration(seconds)
}

/** Formats seconds as hours and minutes for totals and estimates: `12h 05m`, `45m`, `0m`. */
export function formatHoursMinutes(totalSeconds: number): string {
  const minutes = Math.floor(Math.max(0, totalSeconds) / 60)
  const hours = Math.floor(minutes / 60)
  return hours > 0 ? `${hours}h ${pad(minutes % 60)}m` : `${minutes}m`
}

/**
 * Reads a duration typed by a person. Returns seconds, or null if the text is not a duration.
 *
 * - `1:30` is 1 hour 30 minutes, `1:30:15` adds seconds
 * - `1h 30m`, `90m`, `45s`, `2h` are units
 * - `1.5` or `1,5` (with a decimal) is hours; a whole number like `90` is minutes
 */
export function parseDuration(text: string): number | null {
  const value = text.trim().toLowerCase()
  if (!value) {
    return null
  }

  const clock = /^(\d+):([0-5]?\d)(?::([0-5]?\d))?$/.exec(value)
  if (clock) {
    return Number(clock[1]) * 3600 + Number(clock[2]) * 60 + Number(clock[3] ?? 0)
  }

  const units = /^(?:(\d+(?:[.,]\d+)?)\s*h)?\s*(?:(\d+)\s*m(?:in)?)?\s*(?:(\d+)\s*s)?$/.exec(value)
  if (units && (units[1] || units[2] || units[3])) {
    return Math.round(decimal(units[1]) * 3600) + Number(units[2] ?? 0) * 60 + Number(units[3] ?? 0)
  }

  if (/^\d+[.,]\d+$/.test(value)) {
    return Math.round(decimal(value) * 3600)
  }

  if (/^\d+$/.test(value)) {
    return Number(value) * 60
  }

  return null
}

function decimal(text: string | undefined): number {
  return text ? Number(text.replace(',', '.')) : 0
}

function pad(value: number): string {
  return value.toString().padStart(2, '0')
}
