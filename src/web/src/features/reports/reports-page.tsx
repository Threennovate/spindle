import { Link2 } from 'lucide-react'
import { useState } from 'react'
import { PageHeader } from '@/components/page-header'
import { Button } from '@/components/ui/button'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { dayKey, type WeekStart } from '@/lib/time/zone'
import { usePageTitle } from '@/lib/title'
import { ShareDialog } from '../shared-reports/share-dialog'
import { useWorkspace } from '../workspace'
import { DetailedView } from './detailed-view'
import { PeriodPicker } from './period-picker'
import { resolvePeriod, shiftRange, weekContaining, type DayRange, type PeriodKey } from './period'
import { downloadReport, type DetailedRequest, type SummaryRequest, type WeeklyRequest } from './queries'
import { ExportMenu, SavedReportsMenu } from './report-actions'
import { ReportFilters } from './report-filters'
import {
  DEFAULT_PERIOD,
  DEFAULT_SUB_GROUPING,
  parseSavedParameters,
  reportFilter,
  TABS,
  withoutFilters,
  type ReportSearch,
  type ReportTab,
} from './search'
import { SummaryView } from './summary-view'
import { WeeklyView } from './weekly-view'

const TAB_LABELS: Record<ReportTab, string> = { summary: 'Summary', detailed: 'Detailed', weekly: 'Weekly' }
const PAGE_SIZE = 50

interface ReportsPageProps {
  search: ReportSearch
  onNavigate: (update: (current: ReportSearch) => ReportSearch) => void
}

/** Summary, detailed and weekly reports of the workspace's time, kept in the address bar. */
export function ReportsPage({ search, onNavigate }: ReportsPageProps) {
  usePageTitle('Reports')
  const { workspaceId, workspace, timeZone, weekStartsOn, isAdmin } = useWorkspace()
  const today = dayKey(new Date(), timeZone)
  const tab = search.tab ?? 'summary'
  const period = search.period ?? DEFAULT_PERIOD
  const range = reportRange(tab, period, search, today, weekStartsOn)
  const filter = reportFilter(search, range)
  const [sharing, setSharing] = useState(false)

  const groupBy = search.groupBy ?? 'project'
  const summaryRequest: SummaryRequest = {
    ...filter,
    groupBy,
    subGroupBy: search.subGroupBy === 'none' ? null : (search.subGroupBy ?? (groupBy === DEFAULT_SUB_GROUPING ? null : DEFAULT_SUB_GROUPING)),
  }
  const detailedRequest: DetailedRequest = { ...filter, sort: search.sort ?? 'date', descending: !search.asc, page: search.page ?? 1, pageSize: PAGE_SIZE }
  const weeklyRequest: WeeklyRequest = { ...filter, groupBy: groupBy === 'description' ? 'project' : groupBy }

  /** Changes the report; anything that changes what it covers starts again from its first page. */
  function update(changes: Partial<ReportSearch>) {
    onNavigate((current) => ({ ...current, page: undefined, ...changes }))
  }

  function choosePeriod(key: PeriodKey, custom?: DayRange) {
    update(key === 'custom' && custom ? { period: 'custom', ...custom } : { period: key, from: undefined, to: undefined })
  }

  return (
    <div className="mx-auto grid max-w-6xl gap-5">
      <PageHeader
        title="Reports"
        description={isAdmin ? `Time tracked in ${workspace.name}.` : `Your time in ${workspace.name}. Admins see everyone's.`}
        actions={
          <>
            <SavedReportsMenu
              workspaceId={workspaceId}
              search={search}
              onOpen={(report) => onNavigate(() => ({ ...parseSavedParameters(report.parameters), saved: report.id }))}
              onSaved={(report) => onNavigate((current) => ({ ...current, saved: report.id }))}
              onDeleted={() => onNavigate((current) => ({ ...current, saved: undefined }))}
            />
            {isAdmin && (
              <Button variant="outline" onClick={() => setSharing(true)}>
                <Link2 />
                Share
              </Button>
            )}
            <ExportMenu
              onExport={(format) =>
                downloadReport(workspaceId, tab, format, tab === 'summary' ? summaryRequest : tab === 'detailed' ? detailedRequest : weeklyRequest)
              }
            />
          </>
        }
      />

      <div className="flex flex-wrap items-center justify-between gap-3">
        <Tabs value={tab} onValueChange={(value) => update({ tab: value as ReportTab })}>
          <TabsList aria-label="Report">
            {TABS.map((value) => (
              <TabsTrigger key={value} value={value}>
                {TAB_LABELS[value]}
              </TabsTrigger>
            ))}
          </TabsList>
        </Tabs>
        <PeriodPicker
          key={tab === 'weekly' ? 'week' : 'period'}
          period={tab === 'weekly' && period !== 'thisWeek' && period !== 'lastWeek' ? 'custom' : period}
          range={range}
          unit={tab === 'weekly' ? 'week' : 'period'}
          presets={tab === 'weekly' ? ['thisWeek', 'lastWeek'] : undefined}
          onChange={choosePeriod}
          onShift={(direction) => update({ period: 'custom', ...shiftRange(range, direction) })}
        />
      </div>

      <ReportFilters search={search} onChange={update} onClear={() => onNavigate(withoutFilters)} />

      {tab === 'summary' && (
        <SummaryView
          workspaceId={workspaceId}
          request={summaryRequest}
          onGroupingChange={(nextGroupBy, subGroupBy) => update({ groupBy: nextGroupBy, subGroupBy: subGroupBy ?? 'none' })}
        />
      )}
      {tab === 'detailed' && (
        <DetailedView
          request={detailedRequest}
          onSort={(sort, descending) => update({ sort, asc: descending ? undefined : true })}
          onPage={(page) => onNavigate((current) => ({ ...current, page: page > 1 ? page : undefined }))}
        />
      )}
      {isAdmin && <ShareDialog workspaceId={workspaceId} open={sharing} onOpenChange={setSharing} filter={filter} period={period} range={range} />}
      {tab === 'weekly' && <WeeklyView workspaceId={workspaceId} request={weeklyRequest} onGroupBy={(nextGroupBy) => update({ groupBy: nextGroupBy })} />}
    </div>
  )
}

/**
 * The days the report covers. The weekly report always shows a whole week: this or last week, the week of a day
 * picked for it, or otherwise the latest week of the chosen period that has begun.
 */
function reportRange(tab: ReportTab, period: PeriodKey, search: ReportSearch, today: string, weekStartsOn: WeekStart): DayRange {
  const range = resolvePeriod(period, search, today, weekStartsOn)
  if (tab !== 'weekly') {
    return range
  }
  if (period === 'custom') {
    return weekContaining(range.from, weekStartsOn)
  }
  return weekContaining(range.to < today ? range.to : today, weekStartsOn)
}

