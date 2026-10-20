import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import type { AgentProposal, TimeEntry } from '@/api/types'
import { unwrap } from '@/api/unwrap'
import { catalogKeys } from '../catalog/queries'
import { entryKeys, toRequest } from '../time-entries/queries'

export const proposalKeys = {
  all: (workspaceId: string) => ['workspaces', workspaceId, 'agent-proposals'] as const,
}

/** What the person's agents proposed in the workspace and is still waiting, and what became of the rest lately. */
export const agentProposalsQuery = (workspaceId: string) =>
  queryOptions({
    queryKey: proposalKeys.all(workspaceId),
    queryFn: () =>
      unwrap(
        api.GET('/api/workspaces/{workspaceId}/agent-proposals', {
          params: { path: { workspaceId } },
        }),
      ),
    // Proposals arrive through live sync; this catches them when it was down.
    refetchInterval: 5 * 60_000,
  })

/** Saving or undoing a proposal changes entries, so the timer, the lists and reports load again too. */
function useProposalMutation<TVariables, TResult>(workspaceId: string, mutationFn: (variables: TVariables) => Promise<TResult>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn,
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({
          queryKey: proposalKeys.all(workspaceId),
        }),
        queryClient.invalidateQueries({ queryKey: entryKeys.current }),
        queryClient.invalidateQueries({
          queryKey: entryKeys.all(workspaceId),
        }),
        queryClient.invalidateQueries({
          queryKey: catalogKeys.projects(workspaceId),
        }),
        queryClient.invalidateQueries({
          queryKey: ['workspaces', workspaceId, 'reports'],
        }),
      ]),
  })
}

/** What saving a proposal did, so that it can be taken back while the review is open. */
export interface Saved {
  proposal: AgentProposal
  /** The entry it became. */
  entry: TimeEntry
  /** The entry as it was before a change was saved; none for a new entry. */
  before: TimeEntry | null
}

/** Saves a proposal as it is, on the server, through the same checks as saving the entry. */
export function useApplyProposal(workspaceId: string) {
  return useProposalMutation(workspaceId, async (proposal: AgentProposal): Promise<Saved> => {
    const before = proposal.entryId
      ? await unwrap(
          api.GET('/api/workspaces/{workspaceId}/time-entries/{entryId}', {
            params: {
              path: {
                workspaceId,
                entryId: proposal.entryId,
              },
            },
          }),
        )
      : null
    const applied = await unwrap(
      api.POST('/api/workspaces/{workspaceId}/agent-proposals/{proposalId}/apply', {
        params: {
          path: { workspaceId, proposalId: proposal.id },
        },
      }),
    )
    return { proposal, entry: applied.entry, before }
  })
}

/** Records that the person saved a proposal after editing it, or discarded it. */
export function useResolveProposal(workspaceId: string) {
  return useProposalMutation(workspaceId, ({ id, outcome, entryId }: { id: string; outcome: 'saved' | 'discarded'; entryId?: string }) =>
    unwrap(
      api.POST('/api/workspaces/{workspaceId}/agent-proposals/{proposalId}/resolve', {
        params: { path: { workspaceId, proposalId: id } },
        body: { outcome, entryId: entryId ?? null },
      }),
    ),
  )
}

/**
 * Takes back what saving or discarding a proposal did: the entry it added goes, a change is undone, and the proposal
 * waits again.
 */
export function useReopenProposal(workspaceId: string) {
  return useProposalMutation(workspaceId, async ({ id, saved }: { id: string; saved?: Saved }) => {
    if (saved?.before) {
      await unwrap(
        api.PUT('/api/workspaces/{workspaceId}/time-entries/{entryId}', {
          params: {
            path: { workspaceId, entryId: saved.before.id },
          },
          body: toRequest(saved.before),
        }),
      )
    } else if (saved) {
      await unwrap(
        api.DELETE('/api/workspaces/{workspaceId}/time-entries/{entryId}', {
          params: {
            path: { workspaceId, entryId: saved.entry.id },
          },
        }),
      )
    }
    await unwrap(api.POST('/api/workspaces/{workspaceId}/agent-proposals/{proposalId}/reopen', { params: { path: { workspaceId, proposalId: id } } }))
  })
}
