import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { toApiError } from '@/api/problem'
import { publicApi } from '@/api/public'
import type { components } from '@/api/schema'
import { unwrap } from '@/api/unwrap'
import { saveFile, suggestedFileName } from '@/lib/download'
import type { ExportFormat } from '../reports/queries'

type Schemas = components['schemas']
export type SharedReport = Schemas['SharedReportResponse']
export type SharedReportRequest = Schemas['SharedReportRequest']
export type SharedPeriod = Schemas['SharedPeriod']
export type HourFormat = Schemas['HourFormat']
export type SharedSender = Schemas['SharedSender']
export type SharedTimesheet = Schemas['SharedTimesheet']
export type SharedTimesheetDay = Schemas['SharedTimesheetDay']

/** The problem type of a link whose organization is read-only. */
export const UNAVAILABLE = 'urn:spindle:problem:shared-report-unavailable'

export const sharedReportKeys = {
  all: (workspaceId: string) => ['workspaces', workspaceId, 'sharedReports'] as const,
}

/** The workspace's timesheet links, for admins. */
export const sharedReportsQuery = (workspaceId: string) =>
  queryOptions({
    queryKey: sharedReportKeys.all(workspaceId),
    queryFn: () => unwrap(api.GET('/api/workspaces/{workspaceId}/shared-reports', { params: { path: { workspaceId } } })),
  })

/** Makes a timesheet link, or with an id changes one. */
export function useSaveSharedReport(workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, body }: { id?: string; body: SharedReportRequest }) =>
      id
        ? unwrap(api.PUT('/api/workspaces/{workspaceId}/shared-reports/{reportId}', { params: { path: { workspaceId, reportId: id } }, body }))
        : unwrap(api.POST('/api/workspaces/{workspaceId}/shared-reports', { params: { path: { workspaceId } }, body })),
    onSettled: () => queryClient.invalidateQueries({ queryKey: sharedReportKeys.all(workspaceId) }),
  })
}

/** Gives a link a new address; the old one stops working. */
export function useNewSharedReportAddress(workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (reportId: string) =>
      unwrap(api.POST('/api/workspaces/{workspaceId}/shared-reports/{reportId}/new-address', { params: { path: { workspaceId, reportId } } })),
    onSettled: () => queryClient.invalidateQueries({ queryKey: sharedReportKeys.all(workspaceId) }),
  })
}

export function useDeleteSharedReport(workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (reportId: string) =>
      unwrap(api.DELETE('/api/workspaces/{workspaceId}/shared-reports/{reportId}', { params: { path: { workspaceId, reportId } } })),
    onSettled: () => queryClient.invalidateQueries({ queryKey: sharedReportKeys.all(workspaceId) }),
  })
}

/** The timesheet behind a link. Public: whoever opens it has no account. */
export const sharedTimesheetQuery = (token: string, step: number) =>
  queryOptions({
    queryKey: ['shared', token, step],
    queryFn: () => unwrap(publicApi.GET('/api/shared/{token}', { params: { path: { token }, query: { step } } })),
    retry: false,
  })

/** Downloads the timesheet as a file. */
export async function downloadSharedTimesheet(token: string, step: number, format: ExportFormat): Promise<void> {
  const { data, error, response } = await publicApi.GET('/api/shared/{token}/export', {
    params: { path: { token }, query: { format, step } },
    parseAs: 'blob',
  })
  if (!response.ok || !data) {
    throw toApiError(response, error)
  }

  saveFile(data as unknown as Blob, suggestedFileName(response.headers.get('Content-Disposition')) ?? `timesheet.${format}`)
}
