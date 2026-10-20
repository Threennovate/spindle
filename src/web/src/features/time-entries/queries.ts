import { infiniteQueryOptions, queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { toApiError } from '@/api/problem'
import type { components } from '@/api/schema'
import type { TimeEntry } from '@/api/types'
import { unwrap } from '@/api/unwrap'
import { addDaysToKey, startOfDayKey, weekBefore, type WeekStart } from '@/lib/time/zone'
import { catalogKeys } from '../catalog/queries'

type CreateTimeEntryRequest = components['schemas']['CreateTimeEntryRequest']
type TimeEntryRequest = components['schemas']['TimeEntryRequest']
type BulkUpdateRequest = components['schemas']['BulkUpdateTimeEntriesRequest']

export const entryKeys = {
  all: (workspaceId: string) => ['workspaces', workspaceId, 'time-entries'] as const,
  current: ['current-time-entry'] as const,
}

/** The running entry in any workspace, or null. */
export const currentEntryQuery = queryOptions({
  queryKey: entryKeys.current,
  queryFn: async (): Promise<TimeEntry | null> => {
    const { data, error, response } = await api.GET('/api/me/time-entries/current')
    if (response.status === 204) {
      return null
    }
    if (!data) {
      throw toApiError(response, error)
    }
    return data
  },
  // Picks up a timer started or stopped on another device.
  refetchInterval: 60_000,
})

/** The user's entries a week at a time, newest week first. */
export const entryWeeksQuery = (workspaceId: string, timeZone: string, weekStartsOn: WeekStart) =>
  infiniteQueryOptions({
    queryKey: [...entryKeys.all(workspaceId), 'weeks', timeZone, weekStartsOn],
    initialPageParam: 0,
    queryFn: async ({ pageParam }) => {
      const week = weekBefore(pageParam, new Date(), timeZone, weekStartsOn)
      const entries = await unwrap(
        api.GET('/api/workspaces/{workspaceId}/time-entries', {
          params: { path: { workspaceId }, query: { from: week.from.toISOString(), to: week.to.toISOString() } },
        }),
      )
      return { weeksBack: pageParam, from: week.from, to: week.to, entries }
    },
    getNextPageParam: (lastPage) => lastPage.weeksBack + 1,
  })

/** The user's entries from the start of yesterday on, e.g. to find the one before the running timer. */
export const recentEntriesQuery = (workspaceId: string, today: string, timeZone: string) =>
  queryOptions({
    queryKey: [...entryKeys.all(workspaceId), 'recent', today, timeZone],
    queryFn: () =>
      unwrap(
        api.GET('/api/workspaces/{workspaceId}/time-entries', {
          params: {
            path: { workspaceId },
            query: {
              from: startOfDayKey(addDaysToKey(today, -1), timeZone).toISOString(),
              to: startOfDayKey(addDaysToKey(today, 1), timeZone).toISOString(),
            },
          },
        }),
      ),
  })

/** The entries of one day, e.g. to place time added for that day after them. */
export const dayEntriesQuery = (workspaceId: string, day: string, timeZone: string) =>
  queryOptions({
    queryKey: [...entryKeys.all(workspaceId), 'day', day, timeZone],
    queryFn: () =>
      unwrap(
        api.GET('/api/workspaces/{workspaceId}/time-entries', {
          params: { path: { workspaceId }, query: { from: startOfDayKey(day, timeZone).toISOString(), to: startOfDayKey(addDaysToKey(day, 1), timeZone).toISOString() } },
        }),
      ),
  })

export const suggestionsQuery = (workspaceId: string, search: string) =>
  queryOptions({
    queryKey: [...entryKeys.all(workspaceId), 'suggestions', search],
    queryFn: () =>
      unwrap(
        api.GET('/api/workspaces/{workspaceId}/time-entries/suggestions', {
          params: { path: { workspaceId }, query: { search: search || undefined, limit: 8 } },
        }),
      ),
    staleTime: 10_000,
  })

/** Descriptions written before that contain `search`, those on `projectId` first, to complete the one being typed. */
export const descriptionsQuery = (workspaceId: string, search: string, projectId: string | null | undefined) =>
  queryOptions({
    queryKey: [...entryKeys.all(workspaceId), 'descriptions', search, projectId ?? null],
    queryFn: () =>
      unwrap(
        api.GET('/api/workspaces/{workspaceId}/time-entries/descriptions', {
          params: { path: { workspaceId }, query: { search: search || undefined, projectId: projectId ?? undefined, limit: 8 } },
        }),
      ),
    staleTime: 10_000,
  })

/** Any change to time can change the timer, the list and the time tracked per project. */
function useEntryMutation<TVariables, TResult>(workspaceId: string, mutationFn: (variables: TVariables) => Promise<TResult>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn,
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: entryKeys.current }),
        queryClient.invalidateQueries({ queryKey: entryKeys.all(workspaceId) }),
        queryClient.invalidateQueries({ queryKey: catalogKeys.projects(workspaceId) }),
        // Reports add entries up, so a changed entry changes them.
        queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId, 'reports'] }),
      ]),
  })
}

/** Starts a timer (no stop) or adds a finished entry. */
export function useCreateEntry(workspaceId: string) {
  return useEntryMutation(workspaceId, (body: CreateTimeEntryRequest) =>
    unwrap(api.POST('/api/workspaces/{workspaceId}/time-entries', { params: { path: { workspaceId } }, body })),
  )
}

/**
 * An entry to change: its ID, or its ID and the version the web loaded, so the server refuses the change (412, see
 * `entryChangedBy`) instead of overwriting one made elsewhere since.
 */
export type EntryTarget = string | { id: string; version?: string }

/** The path and `If-Match` header for a change to an entry. */
export function entryParams(workspaceId: string, target: EntryTarget) {
  const { id, version } = typeof target === 'string' ? { id: target, version: undefined } : target
  return { path: { workspaceId, entryId: id }, header: version ? { 'If-Match': `"${version}"` } : undefined }
}

export function useUpdateEntry(workspaceId: string) {
  return useEntryMutation(workspaceId, ({ id, version, body }: { id: string; version?: string; body: TimeEntryRequest }) =>
    unwrap(api.PUT('/api/workspaces/{workspaceId}/time-entries/{entryId}', { params: entryParams(workspaceId, { id, version }), body })),
  )
}

export function useStopEntry(workspaceId: string) {
  return useEntryMutation(workspaceId, (target: EntryTarget) =>
    unwrap(
      api.POST('/api/workspaces/{workspaceId}/time-entries/{entryId}/stop', {
        params: entryParams(workspaceId, target),
        body: {},
      }),
    ),
  )
}

export function useDeleteEntry(workspaceId: string) {
  return useEntryMutation(workspaceId, (target: EntryTarget) =>
    unwrap(api.DELETE('/api/workspaces/{workspaceId}/time-entries/{entryId}', { params: entryParams(workspaceId, target) })),
  )
}

/** Applies the same changes to several entries at once, all or none. */
export function useBulkUpdateEntries(workspaceId: string) {
  return useEntryMutation(workspaceId, (body: BulkUpdateRequest) =>
    unwrap(api.POST('/api/workspaces/{workspaceId}/time-entries/bulk-update', { params: { path: { workspaceId } }, body })),
  )
}

export function useBulkDeleteEntries(workspaceId: string) {
  return useEntryMutation(workspaceId, (ids: string[]) =>
    unwrap(api.POST('/api/workspaces/{workspaceId}/time-entries/bulk-delete', { params: { path: { workspaceId } }, body: { ids } })),
  )
}

/** The request that saves an entry as it is, e.g. to change one field. */
export function toRequest(entry: TimeEntry): TimeEntryRequest {
  return {
    description: entry.description,
    projectId: entry.projectId ?? null,
    taskId: entry.taskId ?? null,
    tagIds: entry.tagIds,
    isBillable: entry.isBillable,
    shares: entry.shares,
    start: entry.start,
    stop: entry.stop ?? null,
  }
}
