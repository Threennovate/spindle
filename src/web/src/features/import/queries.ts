import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { unwrap } from '@/api/unwrap'
import { meQuery } from '@/auth/me'

type Schemas = components['schemas']
export type ImportPreview = Schemas['ImportPreview']
export type ImportResult = Schemas['ImportResult']
export type DateOrder = Schemas['DateOrder']

/** A file chosen for import, and how to read it. */
export interface ImportInput {
  /** Identifies the file, so a preview is not asked for again while nothing changed. */
  fileKey: string
  content: string
  timeZone: string
  dateOrder: DateOrder
  /** Whose time each person in the file becomes; null leaves them out. */
  assignments: Record<string, string | null>
}

function body(input: ImportInput): Schemas['ImportRequest'] {
  return {
    content: input.content,
    timeZone: input.timeZone,
    dateOrder: input.dateOrder,
    assignments: Object.entries(input.assignments).map(([key, userId]) => ({ key, userId })),
  }
}

/** What the import would do; asked again whenever the file or how to read it changes. */
export function useImportPreview(workspaceId: string, input: ImportInput | null) {
  return useQuery({
    // Outside the workspace's keys, so the import itself does not ask for the preview again.
    queryKey: ['import-preview', workspaceId, input?.fileKey, input?.timeZone, input?.dateOrder, input?.assignments],
    queryFn: () => unwrap(api.POST('/api/workspaces/{workspaceId}/imports/preview', { params: { path: { workspaceId } }, body: body(input!) })),
    enabled: input !== null,
    staleTime: Infinity,
    retry: false,
    placeholderData: (previous) => previous,
  })
}

export function useRunImport(workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: ImportInput) => unwrap(api.POST('/api/workspaces/{workspaceId}/imports', { params: { path: { workspaceId } }, body: body(input) })),
    // Everything in the workspace may have changed.
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId] }),
        queryClient.invalidateQueries({ queryKey: ['current-time-entry'] }),
        queryClient.invalidateQueries({ queryKey: meQuery.queryKey }),
      ]),
  })
}
