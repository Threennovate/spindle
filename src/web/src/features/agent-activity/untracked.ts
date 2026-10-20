import { useQuery } from '@tanstack/react-query'
import type { UntrackedTime } from '@/api/types'
import { meQuery } from '@/auth/me'
import { dayKey, startOfDayKey } from '@/lib/time/zone'
import type { Catalog } from '../catalog/use-catalog'
import type { NewEntry } from '../time-entries/entry-dialog'
import { useKeptList } from './kept-list'
import { suggestedParts, suggestedStretches } from './suggestion'

/** Dismissed ghost blocks kept per person in this browser; the newest are kept. */
const DISMISSED_LIMIT = 200

/** A ghost block's identity, as the Mac keeps it: its day and the minute of the day it starts at. */
export function untrackedId(block: UntrackedTime, timeZone: string): string {
  const day = dayKey(block.start, timeZone)
  const minute = Math.floor((Date.parse(block.start) - startOfDayKey(day, timeZone).getTime()) / 60_000)
  return `${day}|${minute}`
}

/**
 * Ghost blocks the person dismissed, in this browser, for their account only. Like the Mac's, they stay on the device:
 * a dismissed block only hides a hint, and its identity (day and start minute) moves as more activity syncs.
 */
export function useDismissedUntracked(timeZone: string) {
  const userId = useQuery(meQuery).data?.id ?? 'anonymous'
  const kept = useKeptList(`spindle.dismissedUntrackedTime.${userId}`, DISMISSED_LIMIT)
  return {
    isDismissed: (block: UntrackedTime) => kept.has(untrackedId(block, timeZone)),
    dismiss: (block: UntrackedTime) => kept.add(untrackedId(block, timeZone)),
  }
}

/**
 * A new entry for untracked time, as the Mac opens it: separate entries where the work moved from one project to the
 * next, the split its attention suggests when it went back and forth, or else the project most of it went to.
 */
export function untrackedEntry(block: UntrackedTime, catalog: Catalog): NewEntry {
  const split = { parts: block.parts ?? [], stretches: block.stretches ?? null }
  const stretches = block.parts ? suggestedStretches(split) : null
  return {
    start: new Date(block.start),
    stop: new Date(block.end),
    projectId: block.projectIds[0] ?? null,
    parts: !stretches && block.parts ? suggestedParts(split, catalog) : null,
    stretches,
  }
}
