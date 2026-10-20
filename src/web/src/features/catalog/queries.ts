import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { unwrap } from '@/api/unwrap'

type ProjectRequest = components['schemas']['ProjectRequest']
type TaskRequest = components['schemas']['TaskRequest']
type ProjectMemberRequest = components['schemas']['ProjectMemberRequest']

export const catalogKeys = {
  projects: (workspaceId: string) => ['workspaces', workspaceId, 'projects'] as const,
  clients: (workspaceId: string) => ['workspaces', workspaceId, 'clients'] as const,
  tags: (workspaceId: string) => ['workspaces', workspaceId, 'tags'] as const,
}

export const projectsQuery = (workspaceId: string, includeArchived = false) =>
  queryOptions({
    queryKey: [...catalogKeys.projects(workspaceId), { includeArchived }],
    queryFn: () =>
      unwrap(
        api.GET('/api/workspaces/{workspaceId}/projects', {
          params: { path: { workspaceId }, query: { includeArchived } },
        }),
      ),
  })

export const projectQuery = (workspaceId: string, projectId: string) =>
  queryOptions({
    queryKey: [...catalogKeys.projects(workspaceId), projectId],
    queryFn: () =>
      unwrap(api.GET('/api/workspaces/{workspaceId}/projects/{projectId}', { params: { path: { workspaceId, projectId } } })),
  })

export const projectMembersQuery = (workspaceId: string, projectId: string) =>
  queryOptions({
    queryKey: [...catalogKeys.projects(workspaceId), projectId, 'members'],
    queryFn: () =>
      unwrap(api.GET('/api/workspaces/{workspaceId}/projects/{projectId}/members', { params: { path: { workspaceId, projectId } } })),
  })

export const clientsQuery = (workspaceId: string, includeArchived = false) =>
  queryOptions({
    queryKey: [...catalogKeys.clients(workspaceId), { includeArchived }],
    queryFn: () =>
      unwrap(
        api.GET('/api/workspaces/{workspaceId}/clients', {
          params: { path: { workspaceId }, query: { includeArchived } },
        }),
      ),
  })

export const tagsQuery = (workspaceId: string) =>
  queryOptions({
    queryKey: catalogKeys.tags(workspaceId),
    queryFn: () => unwrap(api.GET('/api/workspaces/{workspaceId}/tags', { params: { path: { workspaceId } } })),
  })

/** Changes to projects also change what clients show (project counts), so both are refreshed. */
function useCatalogMutation<TVariables, TResult>(workspaceId: string, mutationFn: (variables: TVariables) => Promise<TResult>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn,
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: catalogKeys.projects(workspaceId) }),
        queryClient.invalidateQueries({ queryKey: catalogKeys.clients(workspaceId) }),
      ]),
  })
}

export function useSaveProject(workspaceId: string) {
  return useCatalogMutation(workspaceId, ({ id, body }: { id?: string; body: ProjectRequest }) =>
    id
      ? unwrap(api.PUT('/api/workspaces/{workspaceId}/projects/{projectId}', { params: { path: { workspaceId, projectId: id } }, body }))
      : unwrap(api.POST('/api/workspaces/{workspaceId}/projects', { params: { path: { workspaceId } }, body })),
  )
}

export function useArchiveProject(workspaceId: string) {
  return useCatalogMutation(workspaceId, ({ id, archive }: { id: string; archive: boolean }) =>
    archive
      ? unwrap(api.POST('/api/workspaces/{workspaceId}/projects/{projectId}/archive', { params: { path: { workspaceId, projectId: id } } }))
      : unwrap(api.POST('/api/workspaces/{workspaceId}/projects/{projectId}/restore', { params: { path: { workspaceId, projectId: id } } })),
  )
}

export function useDeleteProject(workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) =>
      unwrap(api.DELETE('/api/workspaces/{workspaceId}/projects/{projectId}', { params: { path: { workspaceId, projectId: id } } })),
    // Entries lose the deleted project, so they are refreshed too.
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId] }),
  })
}

export function useSaveTask(workspaceId: string, projectId: string) {
  return useCatalogMutation(workspaceId, ({ id, body }: { id?: string; body: TaskRequest }) =>
    id
      ? unwrap(
          api.PUT('/api/workspaces/{workspaceId}/projects/{projectId}/tasks/{taskId}', {
            params: { path: { workspaceId, projectId, taskId: id } },
            body,
          }),
        )
      : unwrap(api.POST('/api/workspaces/{workspaceId}/projects/{projectId}/tasks', { params: { path: { workspaceId, projectId } }, body })),
  )
}

export function useDeleteTask(workspaceId: string, projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) =>
      unwrap(
        api.DELETE('/api/workspaces/{workspaceId}/projects/{projectId}/tasks/{taskId}', {
          params: { path: { workspaceId, projectId, taskId: id } },
        }),
      ),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId] }),
  })
}

/**
 * Who is on a project decides who sees a private project and who manages it,
 * so the projects are refreshed as well, also after a refusal.
 */
function useProjectMemberMutation<TVariables, TResult>(workspaceId: string, mutationFn: (variables: TVariables) => Promise<TResult>) {
  const queryClient = useQueryClient()
  return useMutation({ mutationFn, onSettled: () => queryClient.invalidateQueries({ queryKey: catalogKeys.projects(workspaceId) }) })
}

export function useSetProjectMember(workspaceId: string, projectId: string) {
  return useProjectMemberMutation(workspaceId, ({ userId, body }: { userId: string; body: ProjectMemberRequest }) =>
    unwrap(api.PUT('/api/workspaces/{workspaceId}/projects/{projectId}/members/{userId}', { params: { path: { workspaceId, projectId, userId } }, body })),
  )
}

export function useRemoveProjectMember(workspaceId: string, projectId: string) {
  return useProjectMemberMutation(workspaceId, (userId: string) =>
    unwrap(api.DELETE('/api/workspaces/{workspaceId}/projects/{projectId}/members/{userId}', { params: { path: { workspaceId, projectId, userId } } })),
  )
}

export function useSaveClient(workspaceId: string) {
  return useCatalogMutation(workspaceId, ({ id, name }: { id?: string; name: string }) =>
    id
      ? unwrap(api.PUT('/api/workspaces/{workspaceId}/clients/{clientId}', { params: { path: { workspaceId, clientId: id } }, body: { name } }))
      : unwrap(api.POST('/api/workspaces/{workspaceId}/clients', { params: { path: { workspaceId } }, body: { name } })),
  )
}

export function useArchiveClient(workspaceId: string) {
  return useCatalogMutation(workspaceId, ({ id, archive }: { id: string; archive: boolean }) =>
    archive
      ? unwrap(api.POST('/api/workspaces/{workspaceId}/clients/{clientId}/archive', { params: { path: { workspaceId, clientId: id } } }))
      : unwrap(api.POST('/api/workspaces/{workspaceId}/clients/{clientId}/restore', { params: { path: { workspaceId, clientId: id } } })),
  )
}

export function useDeleteClient(workspaceId: string) {
  return useCatalogMutation(workspaceId, (id: string) =>
    unwrap(api.DELETE('/api/workspaces/{workspaceId}/clients/{clientId}', { params: { path: { workspaceId, clientId: id } } })),
  )
}

export function useSaveTag(workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, name }: { id?: string; name: string }) =>
      id
        ? unwrap(api.PUT('/api/workspaces/{workspaceId}/tags/{tagId}', { params: { path: { workspaceId, tagId: id } }, body: { name } }))
        : unwrap(api.POST('/api/workspaces/{workspaceId}/tags', { params: { path: { workspaceId } }, body: { name } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: catalogKeys.tags(workspaceId) }),
  })
}

export function useDeleteTag(workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) =>
      unwrap(api.DELETE('/api/workspaces/{workspaceId}/tags/{tagId}', { params: { path: { workspaceId, tagId: id } } })),
    // Entries lose the deleted tag, so they are refreshed too.
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId] }),
  })
}
