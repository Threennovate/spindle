import type { TimeEntry } from '@/api/types'

/** The most parts an entry's time can be split into, as the server allows. */
export const MAX_PARTS = 6

/** The smallest part, in percent. */
export const MIN_PERCENT = 1

/** Where part of an entry's time went: a project (and task), whether it is billable, and how much of the time. */
export interface SplitPart {
  projectId: string | null
  taskId: string | null
  isBillable: boolean
  percent: number
  /** What was done on this part, when each part of a split is described on its own; left out when they share one. */
  description?: string
}

/** Where an entry's time went: its parts when split, otherwise all of it on its one project. */
export function partsOf(entry: Pick<TimeEntry, 'projectId' | 'taskId' | 'isBillable' | 'shares'>): SplitPart[] {
  return entry.shares.length > 1
    ? entry.shares.map((share) => ({
        projectId: share.projectId ?? null,
        taskId: share.taskId ?? null,
        isBillable: share.isBillable,
        percent: share.percent,
        ...(share.description != null && { description: share.description }),
      }))
    : [{ projectId: entry.projectId ?? null, taskId: entry.taskId ?? null, isBillable: entry.isBillable, percent: 100 }]
}

/** Whether each part has a description of its own, which its project's reports and timesheets show. */
export function describesParts(parts: readonly SplitPart[] | null): boolean {
  return !!parts && parts.length > 1 && parts.some((part) => part.description !== undefined)
}

/**
 * Gives each part a description of its own. The one description so far goes to the largest part, as it most likely
 * describes the project the timer was on; the others start empty rather than repeat it on other projects.
 */
export function describeEachPart(parts: readonly SplitPart[], description: string): SplitPart[] {
  const largest = parts.reduce((best, part, i) => (part.percent > parts[best].percent ? i : best), 0)
  return parts.map((part, i) => ({ ...part, description: i === largest ? description.trim() : '' }))
}

/** The parts' own descriptions as one, as the server makes an entry's description of them. */
export function joinDescriptions(parts: readonly SplitPart[]): string {
  return parts
    .map((part) => part.description?.trim())
    .filter(Boolean)
    .join('; ')
}

/** One description for all parts again: theirs joined, and none of their own. */
export function shareOneDescription(parts: readonly SplitPart[]): { parts: SplitPart[]; description: string } {
  return { parts: parts.map((part) => ({ ...part, description: undefined })), description: joinDescriptions(parts) }
}

export function isSplit(entry: Pick<TimeEntry, 'shares'>): boolean {
  return entry.shares.length > 1
}

/**
 * Whole numbers in proportion to `weights` that add up to `total`, each at least `min`: what rounding leaves over goes
 * to the largest remainders, the earlier one on a tie. As the server divides seconds, so the two always agree.
 */
export function apportion(weights: readonly number[], total: number, min = 0): number[] {
  if (weights.length === 0) {
    return []
  }
  const free = total - min * weights.length
  // Whole weights keep the arithmetic exact, so remainders compare as the server's do.
  const whole = weights.map((w) => Math.max(0, Math.round(w)))
  const sum = whole.reduce((a, b) => a + b, 0)
  const shares = sum > 0 ? whole : whole.map(() => 1)
  const divisor = sum > 0 ? sum : whole.length
  const result = shares.map((w) => Math.floor((w * free) / divisor))
  const rests = shares.map((w) => (w * free) % divisor)
  let left = free - result.reduce((a, b) => a + b, 0)
  const order = rests.map((rest, i) => ({ i, rest })).sort((a, b) => b.rest - a.rest || a.i - b.i)
  for (const { i } of order) {
    if (left <= 0) {
      break
    }
    result[i] += 1
    left -= 1
  }
  return result.map((value) => value + min)
}

/** Divides seconds between parts, as reports do. */
export function allocate(seconds: number, percents: readonly number[]): number[] {
  return apportion(percents, Math.max(0, Math.round(seconds)))
}

/** `n` equal parts; the first ones get what does not divide evenly. */
export function equalPercents(n: number): number[] {
  return apportion(Array(n).fill(1), 100, MIN_PERCENT)
}

/**
 * Moves the boundary after part `index` to `position` percent along the bar. Only the two parts beside it change,
 * and neither gets smaller than the minimum. With `step`, the boundary snaps to multiples of it.
 */
export function moveBoundary(percents: readonly number[], index: number, position: number, step = 1): number[] {
  const before = percents.slice(0, index).reduce((a, b) => a + b, 0)
  const pair = percents[index] + percents[index + 1]
  let at = Math.round(position / step) * step
  at = Math.min(before + pair - MIN_PERCENT, Math.max(before + MIN_PERCENT, at))
  const next = [...percents]
  next[index] = at - before
  next[index + 1] = pair - next[index]
  return next
}

/** Gives part `index` the percentage typed for it; the others make room in proportion to their size. */
export function setPercent(percents: readonly number[], index: number, value: number): number[] {
  const others = percents.length - 1
  const clamped = Math.min(100 - others * MIN_PERCENT, Math.max(MIN_PERCENT, Math.round(value)))
  const rest = apportion(
    percents.filter((_, i) => i !== index),
    100 - clamped,
    MIN_PERCENT,
  )
  rest.splice(index, 0, clamped)
  return rest
}

/** Adds a part with an equal share of the time; the others shrink in proportion. */
export function addPercent(percents: readonly number[]): number[] {
  const share = Math.round(100 / (percents.length + 1))
  return [...apportion(percents, 100 - share, MIN_PERCENT), share]
}

/** Removes part `index`; its time goes to the others in proportion to their size. */
export function removePercent(percents: readonly number[], index: number): number[] {
  const rest = percents.filter((_, i) => i !== index)
  return apportion(rest, 100, rest.length > 1 ? MIN_PERCENT : 0)
}

/** The percentage closest to a duration typed for a part of `total` seconds. */
export function percentOfDuration(seconds: number, total: number): number {
  return total > 0 ? Math.round((seconds / total) * 100) : 0
}

/**
 * Sorts a server's validation errors into those of a split's parts (`shares[1].projectId`), of the split as a whole
 * (`shares`), and the rest.
 */
export function splitErrors(fieldErrors: Record<string, string[]>): {
  parts: Record<number, string>
  split?: string
  rest: Record<string, string[]>
} {
  const parts: Record<number, string> = {}
  const rest: Record<string, string[]> = {}
  let split: string | undefined
  for (const [key, messages] of Object.entries(fieldErrors)) {
    const part = /^shares\[(\d+)\]/.exec(key)
    if (part) {
      parts[Number(part[1])] ??= messages[0]
    } else if (key === 'shares') {
      split ??= messages[0]
    } else {
      rest[key] = messages
    }
  }
  return { parts, split, rest }
}

/** The request fields for where an entry's time went: its parts when split, otherwise its one project. */
export function sharesRequest(parts: readonly SplitPart[] | null, single: { projectId: string | null; taskId: string | null; isBillable: boolean }) {
  if (!parts || parts.length < 2) {
    return { ...single, shares: [] }
  }
  const largest = parts.reduce((best, part) => (part.percent > best.percent ? part : best), parts[0])
  const describeParts = describesParts(parts)
  return {
    projectId: largest.projectId,
    taskId: largest.taskId,
    isBillable: largest.isBillable,
    describeParts,
    shares: parts.map(({ projectId, taskId, isBillable, percent, description }) => ({
      projectId,
      taskId,
      isBillable,
      percent,
      ...(describeParts && { description: description?.trim() ?? '' }),
    })),
  }
}
