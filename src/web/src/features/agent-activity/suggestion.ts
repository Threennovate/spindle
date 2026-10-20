import type { SplitSuggestion, SplitTarget } from '@/api/types'
import { formatHoursMinutes } from '@/lib/time/duration'
import { formatClock } from '@/lib/time/zone'
import type { Catalog } from '../catalog/use-catalog'
import { allocate, type SplitPart } from '../time-entries/split'

/** The colour of a repository not yet linked to a project. */
export const REPOSITORY_COLOR = 'var(--muted-foreground)'

/** One of the separate entries a suggestion cuts an entry into, where the work moved. */
export interface Stretch {
  projectId: string
  start: Date
  end: Date
}

/** The parts as an entry takes them, once every repository has a project. Billable follows each project. */
export function suggestedParts(suggestion: Pick<SplitSuggestion, 'parts'>, catalog: Catalog): SplitPart[] | null {
  if (suggestion.parts.some((part) => !part.target.projectId)) {
    return null
  }
  return suggestion.parts.map((part) => ({
    projectId: part.target.projectId!,
    taskId: null,
    isBillable: catalog.project(part.target.projectId)?.isBillable ?? false,
    percent: part.percent,
  }))
}

/** The separate entries, cut where the work moved, once every repository has a project; null when it went back and forth. */
export function suggestedStretches(suggestion: Pick<SplitSuggestion, 'stretches'>): Stretch[] | null {
  const stretches = suggestion.stretches
  if (!stretches || stretches.some((stretch) => !stretch.target.projectId)) {
    return null
  }
  return stretches.map((stretch) => ({ projectId: stretch.target.projectId!, start: new Date(stretch.start), end: new Date(stretch.end) }))
}

/** "Split 60 · 40", beside "Split at 10:42", where both can be taken. */
export function percentTitle(suggestion: SplitSuggestion): string {
  return `Split ${suggestion.parts.map((part) => part.percent).join(' · ')}`
}

/** "Split at 10:42", or "Split into 3 entries" when the work moved more than once; "Split" when it went back and forth. */
export function separateTitle(suggestion: Pick<SplitSuggestion, 'stretches'>, timeZone: string, hour12: boolean): string {
  const stretches = suggestion.stretches
  if (!stretches) {
    return 'Split'
  }
  return stretches.length === 2 ? `Split at ${formatClock(stretches[1].start, timeZone, hour12)}` : `Split into ${stretches.length} entries`
}

/**
 * Each part's time for an entry of `seconds`, in whole minutes that add up to the entry, as the chip shows it: hours
 * there, percentages in the editor, where a split is set.
 */
export function partSeconds(suggestion: Pick<SplitSuggestion, 'parts'>, seconds: number): number[] {
  const minutes = Math.round(Math.max(0, seconds) / 60)
  return allocate(minutes, suggestion.parts.map((part) => part.percent)).map((part) => part * 60)
}

/** "1:20", as the chip writes a part's time. */
export function chipTime(seconds: number): string {
  const minutes = Math.round(seconds / 60)
  return `${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, '0')}`
}

export function targetName(target: SplitTarget, catalog: Catalog): string {
  return target.projectId ? (catalog.project(target.projectId)?.name ?? 'a project') : (target.repository?.name ?? 'a repository')
}

/** A target's colour: its project's, or a quiet one while it is a repository without a project. */
export function targetColor(target: SplitTarget | null | undefined, catalog: Catalog): string {
  return (target?.projectId && catalog.project(target.projectId)?.color) || REPOSITORY_COLOR
}

/** Whether the logs back it firmly enough to say it out loud; weak ones stay quiet. */
export function isStrong(suggestion: SplitSuggestion): boolean {
  return suggestion.strength === 'strong'
}

/** "Atlas 1:20, beacon 0:40", to read out. */
export function suggestionNames(suggestion: SplitSuggestion, catalog: Catalog, seconds: number): string {
  const times = partSeconds(suggestion, seconds)
  return suggestion.parts.map((part, index) => `${targetName(part.target, catalog)} ${chipTime(times[index])}`).join(', ')
}

/** "The work moved to Beacon at 10:42.", or null when it went back and forth. */
export function movedNote(suggestion: SplitSuggestion, catalog: Catalog, timeZone: string, hour12: boolean): string | null {
  const stretches = suggestion.stretches
  if (!stretches || stretches.length < 2) {
    return null
  }
  const moves = stretches.slice(1).map((stretch) => formatClock(stretch.start, timeZone, hour12))
  return moves.length === 1
    ? `The work moved to ${targetName(stretches[1].target, catalog)} at ${moves[0]}.`
    : `The work moved at ${moves.slice(0, -1).join(', ')} and ${moves.at(-1)}.`
}

/** "The logs back this only lightly: 54% of the time, few prompts.", for a weak suggestion, as the Mac's review says it. */
export function weakNote(suggestion: SplitSuggestion): string | null {
  return isStrong(suggestion) ? null : `The logs back this only lightly: ${Math.round(suggestion.coverage * 100)}% of the time, few prompts.`
}

/**
 * The evidence in words, for screen readers: "Agent activity from 09:00 to 11:00: Atlas 50m, 4 prompts; Beacon 1h 10m,
 * 6 prompts. The work moved to Beacon at 09:50."
 */
export function spokenEvidence(suggestion: SplitSuggestion, catalog: Catalog, timeZone: string, hour12: boolean): string {
  const parts = suggestion.parts.map((part) => {
    const time = formatHoursMinutes(part.attentionSeconds + part.unattributedSeconds)
    let text = `${targetName(part.target, catalog)} ${time}, ${part.prompts} ${part.prompts === 1 ? 'prompt' : 'prompts'}`
    if (part.unattributedSeconds > 0) {
      text += `, with ${formatHoursMinutes(part.unattributedSeconds)} the agent logs don't account for`
    }
    return text
  })
  const from = formatClock(suggestion.start, timeZone, hour12)
  const to = formatClock(suggestion.end, timeZone, hour12)
  const moved = (suggestion.stretches ?? [])
    .slice(1)
    .map((stretch) => `The work moved to ${targetName(stretch.target, catalog)} at ${formatClock(stretch.start, timeZone, hour12)}.`)
  return [`Agent activity from ${from} to ${to}: ${parts.join('; ')}.`, ...moved].join(' ')
}
