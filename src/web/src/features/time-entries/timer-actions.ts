import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef } from 'react'
import { toast } from 'sonner'
import type { components } from '@/api/schema'
import type { TimeEntry, TimeEntrySuggestion } from '@/api/types'
import { errorMessage } from '@/lib/error-message'
import { formatDuration } from '@/lib/time/duration'
import { useCatalog } from '../catalog/use-catalog'
import { currentEntryQuery, suggestionsQuery, toRequest, useCreateEntry, useDeleteEntry, useUpdateEntry } from './queries'
import { useStopTimer } from './use-stop-timer'

type TimeEntryRequest = components['schemas']['TimeEntryRequest']

/** What a new timer starts with. */
export interface StartFields {
  description?: string
  projectId?: string | null
  taskId?: string | null
  tagIds?: string[]
  isBillable?: boolean
  shares?: TimeEntryRequest['shares']
  /** When it started, if earlier than now, e.g. "since 9:40". */
  start?: Date
}

/** A finished entry to add, e.g. "Standup yesterday 9:00-9:15". */
export interface AddFields extends Omit<StartFields, 'start'> {
  start: Date
  stop: Date
}

const quoted = (description: string) => (description ? `“${description}”` : 'without a description')

/**
 * Starting, stopping and changing the timer from anywhere, e.g. from a shortcut or the command palette. Every change
 * says what it did and can be undone from the toast, so acting fast never costs anything.
 */
export function useTimerActions(workspaceId: string) {
  const queryClient = useQueryClient()
  const catalog = useCatalog(workspaceId)
  const create = useCreateEntry(workspaceId)
  const update = useUpdateEntry(workspaceId)
  const remove = useDeleteEntry(workspaceId)
  const stopper = useStopTimer(workspaceId)
  // Read when an action runs, so a callback made earlier still sees the catalog of now.
  const latest = useRef({ catalog })
  useEffect(() => {
    latest.current = { catalog }
  })

  const running = () => queryClient.getQueryData(currentEntryQuery.queryKey) ?? null

  function projectName(projectId: string | null | undefined) {
    return latest.current.catalog.project(projectId)?.name
  }

  /**
   * Puts a stopped entry back to running, e.g. to undo a stop. A stopped entry can't be restarted, so it is replaced
   * by a running one with the same details and start: the timer reads as if it never stopped.
   */
  async function resume(entry: TimeEntry) {
    await remove.mutateAsync(entry.id)
    await create.mutateAsync({ ...toRequest(entry), stop: null })
  }

  async function start(fields: StartFields) {
    const previous = running()
    const description = fields.description?.trim() ?? ''
    try {
      const entry = await create.mutateAsync({
        description,
        projectId: fields.projectId ?? null,
        taskId: fields.taskId ?? null,
        tagIds: fields.tagIds ?? [],
        isBillable: fields.isBillable,
        shares: fields.shares ?? [],
        start: (fields.start ?? new Date()).toISOString(),
      })
      const project = projectName(entry.projectId)
      toast.success(`Timer started ${quoted(description)}`, {
        description: [project, previous && `Stopped “${previous.description || 'No description'}”`].filter(Boolean).join(' · ') || undefined,
        action: {
          label: 'Undo',
          onClick: () =>
            void (async () => {
              try {
                await remove.mutateAsync(entry.id)
                // Starting stopped the timer that ran before; it runs again as if nothing happened.
                if (previous) {
                  await resume(previous)
                }
              } catch (error) {
                toast.error(errorMessage(error, "Couldn't undo."))
              }
            })(),
        },
      })
      return entry
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't start the timer."))
      return null
    }
  }

  function stop(entry: TimeEntry) {
    stopper.stopTimer(entry, () => {
      const seconds = (Date.now() - Date.parse(entry.start)) / 1000
      const project = projectName(entry.projectId)
      toast.success(`Stopped at ${formatDuration(seconds)}`, {
        description: [entry.description || 'No description', project].filter(Boolean).join(' · '),
        action: { label: 'Undo', onClick: () => void resume(entry).catch((error) => toast.error(errorMessage(error, "Couldn't undo."))) },
      })
    })
  }

  /** Adds a finished entry; the toast takes it away again. */
  async function add(fields: AddFields, summary: string) {
    const description = fields.description?.trim() ?? ''
    try {
      const entry = await create.mutateAsync({
        description,
        projectId: fields.projectId ?? null,
        taskId: fields.taskId ?? null,
        tagIds: fields.tagIds ?? [],
        isBillable: fields.isBillable,
        shares: fields.shares ?? [],
        start: fields.start.toISOString(),
        stop: fields.stop.toISOString(),
      })
      toast.success(`Added ${quoted(description)}`, {
        description: [summary, projectName(entry.projectId)].filter(Boolean).join(' · '),
        action: {
          label: 'Undo',
          onClick: () => void remove.mutateAsync(entry.id).catch((error) => toast.error(errorMessage(error, "Couldn't undo."))),
        },
      })
      return entry
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't add the entry."))
      return null
    }
  }

  /** Starts a timer, or stops the running one. */
  function toggle() {
    const current = running()
    if (current) {
      stop(current)
    } else {
      void start({})
    }
  }

  function continueSuggestion(suggestion: TimeEntrySuggestion) {
    return start({ ...suggestion, tagIds: [...suggestion.tagIds] })
  }

  async function continueLatest() {
    try {
      const [last] = await queryClient.fetchQuery(suggestionsQuery(workspaceId, ''))
      if (!last) {
        toast.info('Nothing to continue yet.')
        return
      }
      await continueSuggestion(last)
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't continue the entry."))
    }
  }

  /** Throws the running timer away; the toast brings it back, still running. */
  async function discard(entry: TimeEntry) {
    try {
      await remove.mutateAsync(entry.id)
      toast.success('Timer discarded', {
        description: entry.description || 'No description',
        action: {
          label: 'Undo',
          onClick: () =>
            void create
              .mutateAsync({ ...toRequest(entry), stop: null })
              .catch((error) => toast.error(errorMessage(error, "Couldn't bring the timer back."))),
        },
      })
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't discard the timer."))
    }
  }

  /** Changes the running entry, saying what changed with a way back. */
  async function change(entry: TimeEntry, changes: Partial<TimeEntryRequest>, message: string) {
    try {
      await update.mutateAsync({ id: entry.id, body: { ...toRequest(entry), ...changes } })
      toast.success(message, {
        action: {
          label: 'Undo',
          onClick: () =>
            void update.mutateAsync({ id: entry.id, body: toRequest(entry) }).catch((error) => toast.error(errorMessage(error, "Couldn't undo."))),
        },
      })
      return true
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't save the running entry."))
      return false
    }
  }

  return { running, start, add, stop, toggle, continueSuggestion, continueLatest, change, discard, isPending: create.isPending || stopper.isPending }
}
