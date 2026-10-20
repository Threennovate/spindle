import { useWorkspace } from '../workspace'

/**
 * Whether the current workspace's organization is read-only, because its trial or plan has ended. Its time can still
 * be read, reported and exported, and a running timer stopped.
 */
export function useReadOnly() {
  const { me, workspace } = useWorkspace()
  const organization = me.organizations.find((o) => o.id === workspace.organizationId)
  return {
    readOnly: organization?.access === 'readOnly',
    organization,
    /** Owners and admins of the organization, who can do something about it. */
    canManage: organization !== undefined && organization.role !== 'member',
  }
}
