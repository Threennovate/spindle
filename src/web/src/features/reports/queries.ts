import { keepPreviousData, queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { toApiError } from '@/api/problem'
import type { components } from '@/api/schema'
import { unwrap } from '@/api/unwrap'
import { saveFile, suggestedFileName } from '@/lib/download'
import type { ReportFilter, ReportGrouping, ReportSearch, ReportSort, ReportTab } from './search'

type Schemas = components['schemas']
export type SummaryReport = Schemas['SummaryReport']
export type SummaryGroup = Schemas['SummaryGroup']
export type DetailedReport = Schemas['DetailedReport']
export type DetailedEntry = Schemas['DetailedEntry']
export type WeeklyReport = Schemas['WeeklyReport']
export type SavedReport = Schemas['SavedReportResponse']
export type ExportFormat = 'csv' | 'xlsx' | 'pdf'

export type SummaryRequest = ReportFilter & { groupBy: ReportGrouping; subGroupBy: ReportGrouping | null }
export type DetailedRequest = ReportFilter & { sort: ReportSort; descending: boolean; page: number; pageSize: number }
export type WeeklyRequest = ReportFilter & { groupBy: ReportGrouping }

const reportKeys = {
  all: (workspaceId: string) => ['workspaces', workspaceId, 'reports'] as const,
  saved: (workspaceId: string) => ['workspaces', workspaceId, 'reports', 'saved'] as const,
}

// While a changed report loads, the previous one stays on screen instead of flashing away.
export const summaryQuery = (workspaceId: string, body: SummaryRequest) =>
  queryOptions({
    queryKey: [...reportKeys.all(workspaceId), 'summary', body],
    queryFn: () => unwrap(api.POST('/api/workspaces/{workspaceId}/reports/summary', { params: { path: { workspaceId } }, body })),
    placeholderData: keepPreviousData,
  })

export const detailedQuery = (workspaceId: string, body: DetailedRequest) =>
  queryOptions({
    queryKey: [...reportKeys.all(workspaceId), 'detailed', body],
    queryFn: () => unwrap(api.POST('/api/workspaces/{workspaceId}/reports/detailed', { params: { path: { workspaceId } }, body })),
    placeholderData: keepPreviousData,
  })

export const weeklyQuery = (workspaceId: string, body: WeeklyRequest) =>
  queryOptions({
    queryKey: [...reportKeys.all(workspaceId), 'weekly', body],
    queryFn: () => unwrap(api.POST('/api/workspaces/{workspaceId}/reports/weekly', { params: { path: { workspaceId } }, body })),
    placeholderData: keepPreviousData,
  })

export const savedReportsQuery = (workspaceId: string) =>
  queryOptions({
    queryKey: reportKeys.saved(workspaceId),
    queryFn: () => unwrap(api.GET('/api/workspaces/{workspaceId}/reports/saved', { params: { path: { workspaceId } } })),
  })

export function useSaveReport(workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, name, parameters }: { id?: string; name: string; parameters: ReportSearch | SavedReport }) => {
      // The kind tells other apps which report it is; the web app reads everything from the parameters. A saved
      // report given whole, e.g. to rename it, keeps its settings exactly as they were stored.
      const body =
        'parameters' in parameters
          ? { name, kind: parameters.kind, parameters: parameters.parameters }
          : { name, kind: parameters.tab ?? 'summary', parameters: JSON.stringify(parameters) }
      return id
        ? unwrap(api.PUT('/api/workspaces/{workspaceId}/reports/saved/{reportId}', { params: { path: { workspaceId, reportId: id } }, body }))
        : unwrap(api.POST('/api/workspaces/{workspaceId}/reports/saved', { params: { path: { workspaceId } }, body }))
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: reportKeys.saved(workspaceId) }),
  })
}

export function useDeleteSavedReport(workspaceId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (reportId: string) =>
      unwrap(api.DELETE('/api/workspaces/{workspaceId}/reports/saved/{reportId}', { params: { path: { workspaceId, reportId } } })),
    onSettled: () => queryClient.invalidateQueries({ queryKey: reportKeys.saved(workspaceId) }),
  })
}

/** Asks the server for the report as a file and hands it to the browser to save. */
export async function downloadReport(
  workspaceId: string,
  tab: ReportTab,
  format: ExportFormat,
  body: SummaryRequest | DetailedRequest | WeeklyRequest,
): Promise<void> {
  const options = { params: { path: { workspaceId }, query: { format } }, parseAs: 'blob' as const }
  const { data, error, response } =
    tab === 'summary'
      ? await api.POST('/api/workspaces/{workspaceId}/reports/summary/export', { ...options, body: body as SummaryRequest })
      : tab === 'detailed'
        ? await api.POST('/api/workspaces/{workspaceId}/reports/detailed/export', { ...options, body: body as DetailedRequest })
        : await api.POST('/api/workspaces/{workspaceId}/reports/weekly/export', { ...options, body: body as WeeklyRequest })
  if (!response.ok || !data) {
    throw toApiError(response, error)
  }

  saveFile(data as unknown as Blob, suggestedFileName(response.headers.get('Content-Disposition')) ?? `spindle-${tab}.${format}`)
}
