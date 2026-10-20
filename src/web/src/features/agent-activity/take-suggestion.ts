import { toast } from 'sonner'
import type { SplitSuggestion, TimeEntry } from '@/api/types'
import { errorMessage } from '@/lib/error-message'
import { dayKey } from '@/lib/time/zone'
import { useCatalog } from '../catalog/use-catalog'
import { usePermissions } from '../settings/queries'
import { useWorkspace } from '../workspace'
import { takePlan, type ReviewItem } from './review'
import { useReviewSaving } from './review-data'

/**
 * Takes an entry's suggestion with one click, as the day's review does, saying so with Undo. When one click can't, a
 * repository still without a project or details the workspace requires, or while the entry runs, the editor opens.
 */
export function useTakeSuggestion() {
  const { workspaceId, timeZone } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  const { settings } = usePermissions()
  const saving = useReviewSaving(workspaceId)

  return async (entry: TimeEntry, suggestion: SplitSuggestion, openEditor: () => void) => {
    if (!entry.stop) {
      openEditor()
      return
    }
    const item: ReviewItem = { kind: 'split', id: `split|${entry.id}`, day: dayKey(entry.start, timeZone), start: entry.start, end: entry.stop, entry, suggestion }
    const plan = takePlan(item, { catalog, settings })
    if (plan.status !== 'ready') {
      openEditor()
      return
    }
    try {
      const saved = await saving.save(plan.requests)
      toast.success('Split saved', {
        description: entry.description || 'No description',
        // Long enough to reach Undo from the calendar.
        duration: 10_000,
        action: { label: 'Undo', onClick: () => void saving.undo([saved]).catch((error) => toast.error(errorMessage(error, "Couldn't undo."))) },
      })
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't split the entry."))
    }
  }
}
