import { useQuery } from '@tanstack/react-query'
import type { SplitSuggestion, TimeEntry, UntrackedTime } from '@/api/types'
import { meQuery } from '@/auth/me'
import { addDaysToKey, startOfDayKey } from '@/lib/time/zone'
import { rangeEntriesQuery } from '../calendar/range'
import { usePermissions } from '../settings/queries'
import { useCreateEntry, useDeleteEntry, useUpdateEntry, toRequest } from '../time-entries/queries'
import { useWorkspace } from '../workspace'
import { useKeptList } from './kept-list'
import { splitSuggestionsQuery, untrackedQuery, useAgentActivitySync } from './queries'
import { reviewItems, type ReviewItem, type TakeRequests } from './review'
import { useDismissedUntracked } from './untracked'

/** What the review was told "Not now" for, kept per person in this browser, the newest of them. */
const PUT_OFF_LIMIT = 500

/**
 * What the review leaves out, as the person said so in this browser: items told "Not now" (out of the review only;
 * the chip and the calendar keep them, as on the Mac) and ghost blocks dismissed in the calendar.
 */
export function useReviewChoices() {
  const { timeZone } = useWorkspace()
  const userId = useQuery(meQuery).data?.id ?? 'anonymous'
  const putOff = useKeptList(`spindle.reviewPutOff.${userId}`, PUT_OFF_LIMIT)
  const dismissed = useDismissedUntracked(timeZone)
  const { isDayLocked } = usePermissions()
  return { putOff, isDismissed: dismissed.isDismissed, isDayLocked }
}

/** The review's items on these days from what a page has loaded already, e.g. to count them in a day's header. */
export function useReviewCounter() {
  const { timeZone } = useWorkspace()
  const { putOff, isDismissed, isDayLocked } = useReviewChoices()
  return (days: readonly string[], entries: readonly TimeEntry[], suggestions: readonly SplitSuggestion[], untracked: readonly UntrackedTime[]) =>
    reviewItems({ days, entries, suggestions, untracked, timeZone, isDayLocked, isPutOff: putOff.has, isDismissed })
}

/** Everything the review of these days shows: its items, and the days' entries around them. */
export function useReviewData(days: readonly string[], enabled = true) {
  const { workspaceId, timeZone } = useWorkspace()
  const sync = useAgentActivitySync()
  const count = useReviewCounter()
  const from = startOfDayKey(days[0], timeZone)
  const to = startOfDayKey(addDaysToKey(days[days.length - 1], 1), timeZone)
  const entries = useQuery({ ...rangeEntriesQuery(workspaceId, days, timeZone), enabled: enabled && sync })
  const suggestions = useQuery(splitSuggestionsQuery(workspaceId, from, to, enabled && sync))
  const untracked = useQuery(untrackedQuery(workspaceId, from, to, enabled && sync))
  const loaded = entries.isSuccess && suggestions.isSuccess && untracked.isSuccess
  return {
    sync,
    loaded,
    failed: entries.isError || suggestions.isError || untracked.isError,
    retry: () => Promise.all([entries.refetch(), suggestions.refetch(), untracked.refetch()]),
    entries: entries.data ?? [],
    items: loaded ? count(days, entries.data, suggestions.data, untracked.data) : ([] as ReviewItem[]),
  }
}

/** What one take saved, to take it back: the entry as it was, and the entries it added. */
export interface Saved {
  before: TimeEntry | null
  added: string[]
}

/** Saves what review items offer, and takes it back again. */
export function useReviewSaving(workspaceId: string) {
  const create = useCreateEntry(workspaceId)
  const update = useUpdateEntry(workspaceId)
  const remove = useDeleteEntry(workspaceId)

  /** Adds the earlier entries first, then changes the entry, so a failure part-way leaves the entry as it was. */
  async function save({ update: change, create: added }: TakeRequests): Promise<Saved> {
    const ids: string[] = []
    try {
      for (const body of added) {
        ids.push((await create.mutateAsync({ ...body, start: body.start! })).id)
      }
      if (change) {
        await update.mutateAsync({ id: change.entry.id, body: change.body })
      }
    } catch (error) {
      await Promise.allSettled(ids.map((id) => remove.mutateAsync(id)))
      throw error
    }
    return { before: change?.entry ?? null, added: ids }
  }

  /** Deletes what the takes added and saves the entries back as they were, the latest take first. */
  async function undo(saved: readonly Saved[]) {
    for (const change of [...saved].reverse()) {
      for (const id of change.added) {
        await remove.mutateAsync(id)
      }
      if (change.before) {
        await update.mutateAsync({ id: change.before.id, body: toRequest(change.before) })
      }
    }
  }

  return { save, undo }
}
