import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import type { AgentActivitySettings, AgentRepository, SplitSuggestion, TimeEntry } from '@/api/types'
import { unwrap } from '@/api/unwrap'
import { meQuery } from '@/auth/me'

/**
 * Split suggestions live beside the entries, so whatever loads entries again (a change on another device included)
 * loads them again too.
 */
export const suggestionKeys = {
  all: (workspaceId: string) => ['workspaces', workspaceId, 'time-entries', 'split-suggestions'] as const,
  untracked: (workspaceId: string) => ['workspaces', workspaceId, 'time-entries', 'untracked'] as const,
  now: (workspaceId: string) => ['workspaces', workspaceId, 'agent-activity', 'now'] as const,
}

/** Whether the person's Macs upload agent activity, so the server can suggest splits. */
export function useAgentActivitySync(): boolean {
  return useQuery(meQuery).data?.agentActivity?.sync ?? false
}

/** Splits agent activity suggests for the person's own entries that start in [from, to). */
export const splitSuggestionsQuery = (workspaceId: string, from: Date, to: Date, enabled = true) =>
  queryOptions({
    queryKey: [...suggestionKeys.all(workspaceId), from.toISOString(), to.toISOString()],
    queryFn: () =>
      unwrap(
        api.GET('/api/workspaces/{workspaceId}/time-entries/split-suggestions', {
          params: { path: { workspaceId }, query: { from: from.toISOString(), to: to.toISOString() } },
        }),
      ),
    enabled,
    staleTime: 60_000,
  })

/**
 * Agent attention on the person's days in [from, to) that no entry of theirs covers, nor a proposal waiting for their
 * review: the calendar's ghost blocks. Beside the entries too, so a new entry makes its ghost go.
 */
export const untrackedQuery = (workspaceId: string, from: Date, to: Date, enabled = true) =>
  queryOptions({
    queryKey: [...suggestionKeys.untracked(workspaceId), from.toISOString(), to.toISOString()],
    queryFn: () =>
      unwrap(
        api.GET('/api/workspaces/{workspaceId}/agent-activity/untracked', {
          params: { path: { workspaceId }, query: { from: from.toISOString(), to: to.toISOString() } },
        }),
      ),
    enabled,
    staleTime: 60_000,
  })

/**
 * The projects agents worked in during the last 15 minutes, by the person's repository links, the most recent first:
 * "Agents now" at the top of the project picker. Asked again each minute while a picker is open.
 */
export const agentsNowQuery = (workspaceId: string, enabled = true) =>
  queryOptions({
    queryKey: suggestionKeys.now(workspaceId),
    queryFn: () => unwrap(api.GET('/api/workspaces/{workspaceId}/agent-activity/now', { params: { path: { workspaceId } } })),
    enabled,
    staleTime: 60_000,
  })

/** The split suggested for one entry, if any: none for one already split, or while sync is off. */
export function useEntrySuggestion(workspaceId: string, entry: TimeEntry | undefined, enabled = true) {
  const sync = useAgentActivitySync()
  const queryClient = useQueryClient()
  const start = entry ? new Date(entry.start) : new Date(0)
  const query = useQuery({
    ...splitSuggestionsQuery(workspaceId, start, new Date(start.getTime() + 1), enabled && sync && !!entry),
    // The list or calendar usually knows it already, so the editor shows it at once rather than a moment later.
    placeholderData: () => {
      const known = queryClient
        .getQueriesData<SplitSuggestion[]>({ queryKey: suggestionKeys.all(workspaceId) })
        .flatMap(([, data]) => data ?? [])
        .find((suggestion) => suggestion.entryId === entry?.id)
      return known ? [known] : undefined
    },
  })
  return query.data?.find((suggestion) => suggestion.entryId === entry?.id)
}

/** Stops suggesting a split for an entry, on every device ("Keep as is"). */
export function useDismissSuggestion(workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (entryId: string) =>
      unwrap(api.POST('/api/workspaces/{workspaceId}/time-entries/{entryId}/split-suggestion/dismiss', { params: { path: { workspaceId, entryId } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: suggestionKeys.all(workspaceId) }),
  })
}

/**
 * Links a repository to a project from now on, on the Mac too. With no project, `noProject` says its work goes to no
 * project from now on, so neither a learned link nor a project named like it takes it; without it the link is forgotten.
 */
export function useLinkRepository(workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ repository, projectId, noProject = false }: { repository: AgentRepository; projectId: string | null; noProject?: boolean }) =>
      unwrap(
        api.PUT('/api/workspaces/{workspaceId}/agent-activity/links', {
          params: { path: { workspaceId } },
          body: { repository, projectId, noProject },
        }),
      ),
    // Untracked time names its projects by the same links.
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: suggestionKeys.all(workspaceId) }),
        queryClient.invalidateQueries({ queryKey: suggestionKeys.untracked(workspaceId) }),
        queryClient.invalidateQueries({ queryKey: suggestionKeys.now(workspaceId) }),
      ]),
  })
}

/** Turns uploading agent activity on or off for all the person's Macs; off deletes what was uploaded. */
export function useUpdateAgentActivitySettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: { sync: boolean; titles: boolean }) => unwrap(api.PUT('/api/me/agent-activity/settings', { body })),
    onSuccess: (agentActivity: AgentActivitySettings) => {
      queryClient.setQueryData(meQuery.queryKey, (me) => (me ? { ...me, agentActivity } : me))
      void queryClient.invalidateQueries({ predicate: (query) => query.queryKey.includes('split-suggestions') })
    },
  })
}
