import { useQuery } from '@tanstack/react-query'
import { toast } from 'sonner'
import { entryChangedBy } from '@/api/problem'
import type { TimeEntry } from '@/api/types'
import { errorMessage } from '@/lib/error-message'
import { useCatalog } from '../catalog/use-catalog'
import { settingsQuery } from '../settings/queries'
import { editEntry } from './events'
import { useStopEntry } from './queries'
import { isDetailsError, missingDetails } from './rules'

const ADD_DETAILS = 'Add the details this workspace requires to stop the timer.'

/**
 * Stops the running timer. A finished entry must have the details the workspace requires, so when they are missing
 * the entry opens for the person to add them, instead of the timer silently running on.
 */
export function useStopTimer(workspaceId: string) {
  // Not suspending: the sidebar's stop button lives outside the pages. Without settings yet, the server decides.
  const { data: settings } = useQuery(settingsQuery(workspaceId))
  const catalog = useCatalog(workspaceId)
  const stop = useStopEntry(workspaceId)

  function stopTimer(entry: TimeEntry, onStopped?: () => void) {
    if (settings && Object.keys(missingDetails(settings, entry, catalog.project(entry.projectId))).length > 0) {
      toast.error(ADD_DETAILS)
      editEntry({ entry, stopping: true })
      return
    }

    // A promise rather than mutate's callbacks, which don't run once the caller unmounts, e.g. the palette that closed.
    stopChanged(entry).then(onStopped, (error: unknown) => {
      // The settings may have changed since they were loaded; the server's word counts.
      if (isDetailsError(error)) {
        toast.error(ADD_DETAILS)
        editEntry({ entry, stopping: true })
      } else {
        toast.error(errorMessage(error, "Couldn't stop the timer."))
      }
    })
  }

  /**
   * Stops the entry as it was loaded. Changed elsewhere since, e.g. renamed on another device, it is stopped anyway,
   * since stopping overwrites nothing of that; stopped there already, it stays as that device stopped it.
   */
  async function stopChanged(entry: TimeEntry) {
    try {
      await stop.mutateAsync({ id: entry.id, version: entry.version })
    } catch (error) {
      const current = entryChangedBy(error)
      if (!current) {
        throw error
      }
      if (current.stop) {
        toast.info('The timer was already stopped on another device.')
        return
      }
      await stop.mutateAsync({ id: current.id, version: current.version })
    }
  }

  return { stopTimer, isPending: stop.isPending }
}
