import type { QueryClient } from '@tanstack/react-query'

/** What the server says changed; see SyncChange in the API. */
export interface SyncChange {
  topic: 'entries' | 'catalog' | 'team' | 'settings' | 'me' | 'savedReports' | 'sharedReports' | 'billing' | 'agentActivity' | 'agentProposals'
  workspaceId: string | null
  userId: string | null
}

/** Loads again what a change elsewhere makes out of date on this screen. */
export function applyChange(queryClient: QueryClient, change: SyncChange, userId: string) {
  const invalidate = (...queryKey: unknown[]) => void queryClient.invalidateQueries({ queryKey })
  const workspace = change.workspaceId

  switch (change.topic) {
    case 'entries':
      if (change.userId === userId) {
        invalidate('current-time-entry')
        if (workspace) {
          invalidate('workspaces', workspace, 'time-entries')
        }
      }
      if (workspace) {
        // Reports add everyone's time up, projects show the time tracked on them, and billing what it is billed as.
        invalidate('workspaces', workspace, 'reports')
        invalidate('workspaces', workspace, 'projects')
        invalidate('workspaces', workspace, 'billing')
      }
      break
    case 'catalog':
      // Projects' rates are part of the catalog too.
      if (workspace) {
        invalidate('workspaces', workspace, 'projects')
        invalidate('workspaces', workspace, 'clients')
        invalidate('workspaces', workspace, 'tags')
        invalidate('workspaces', workspace, 'reports')
        invalidate('workspaces', workspace, 'billing')
        invalidate('workspaces', workspace, 'rates')
      }
      break
    case 'team':
      // Members' rates and labor costs change what reports add up to.
      if (workspace) {
        invalidate('workspaces', workspace, 'members')
        invalidate('workspaces', workspace, 'invitations')
        invalidate('workspaces', workspace, 'groups')
        invalidate('workspaces', workspace, 'reports')
        invalidate('workspaces', workspace, 'billing')
        invalidate('workspaces', workspace, 'rates')
      }
      break
    case 'sharedReports':
      if (workspace) {
        invalidate('workspaces', workspace, 'sharedReports')
      }
      break
    case 'billing':
      if (workspace) {
        invalidate('workspaces', workspace, 'billing')
        invalidate('workspaces', workspace, 'reports')
      }
      break
    case 'settings':
      if (workspace) {
        invalidate('workspaces', workspace, 'settings')
        invalidate('workspaces', workspace, 'reports')
        invalidate('workspaces', workspace, 'rates')
      }
      // The workspace's name shows in the switcher.
      invalidate('me')
      break
    case 'me':
      invalidate('me')
      break
    case 'agentActivity':
      // A Mac uploaded activity, or a repository was linked or a suggestion kept elsewhere.
      void queryClient.invalidateQueries({ predicate: (query) => query.queryKey.includes('split-suggestions') || query.queryKey.includes('untracked') })
      break
    case 'agentProposals':
      // An agent proposed entries or changes, or they were dealt with on another device.
      if (workspace) {
        invalidate('workspaces', workspace, 'agent-proposals')
        // Time a proposal waits for is not shown as untracked.
        invalidate('workspaces', workspace, 'time-entries', 'untracked')
      }
      break
    case 'savedReports':
      if (workspace) {
        invalidate('workspaces', workspace, 'reports', 'saved')
      }
      break
  }
}
