import { useQuery } from '@tanstack/react-query'
import { ChevronDown, Link2 } from 'lucide-react'
import { useEffect, useEffectEvent, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle, SheetTrigger } from '@/components/ui/sheet'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { dayKey } from '@/lib/time/zone'
import { usePageTitle } from '@/lib/title'
import { ShareDialog } from '../shared-reports/share-dialog'
import { useWorkspace } from '../workspace'
import { DetailedView } from './detailed-view'
import { PeriodPicker } from './period-picker'
import { shiftRange, type DayRange, type PeriodKey } from './period'
import { downloadReport, savedReportsQuery, type SavedReport } from './queries'
import { ExportMenu, ViewActions } from './report-actions'
import { ReportFilters, RoundingFilter } from './report-filters'
import { ReportSkeleton } from './report-totals'
import { reportRequests } from './requests'
import { parseSavedParameters, TABS, type ReportSearch, type ReportTab } from './search'
import { SummaryView } from './summary-view'
import { builtInViews, isBlank, isEdited, lastView, openingSearch, rememberView, savedReportSearch, viewRef, viewSearch, type BuiltInView } from './views'
import { ViewsList } from './views-list'
import { WeeklyView } from './weekly-view'

const TAB_LABELS: Record<ReportTab, string> = { summary: 'Summary', detailed: 'Detailed', weekly: 'Weekly' }

interface ReportsPageProps {
  search: ReportSearch
  onNavigate: (update: (current: ReportSearch) => ReportSearch, options?: { replace?: boolean }) => void
}

/**
 * Summary, detailed and weekly reports of the workspace's time, kept in the address bar and opened from a list of
 * views: built-in ones and the person's own saved ones.
 */
export function ReportsPage({ search, onNavigate }: ReportsPageProps) {
  const { workspaceId, workspace, me, timeZone, weekStartsOn, isAdmin } = useWorkspace()
  const today = dayKey(new Date(), timeZone)
  const { tab, period, range, filter, summary, detailed, weekly } = reportRequests(search, today, weekStartsOn)
  const [sharing, setSharing] = useState(false)
  const [picking, setPicking] = useState(false)

  const views = builtInViews(me.id, isAdmin)
  const saved = useQuery(savedReportsQuery(workspaceId))
  const savedReports = saved.data ?? []
  const savedReport = search.saved ? savedReports.find((report) => report.id === search.saved) : undefined
  const builtIn = search.saved ? undefined : views.find((view) => view.id === search.view)
  const base = savedReport ? parseSavedParameters(savedReport.parameters) : builtIn?.search
  const edited = base ? isEdited(base, search) : false
  const blank = isBlank(search)
  const openRef = viewRef(search)
  const title = builtIn?.label ?? savedReport?.name ?? (search.saved && saved.isPending ? 'Saved view' : 'Report')
  usePageTitle(blank ? 'Reports' : `${title} · Reports`)

  // Reports opened from the menu shows the view opened last, or My week the first time; a saved view has to load first.
  const remembered = blank ? lastView(me.id, workspaceId) : null
  const waiting = blank && !!remembered?.startsWith('saved:') && saved.isPending
  const openFirstView = useEffectEvent(() => onNavigate(() => openingSearch(remembered, views, savedReports), { replace: true }))
  useEffect(() => {
    if (blank && !waiting) {
      openFirstView()
    }
  }, [blank, waiting])

  useEffect(() => {
    if (openRef) {
      rememberView(me.id, workspaceId, openRef)
    }
  }, [me.id, workspaceId, openRef])

  /** Changes the report; anything that changes what it covers starts again from its first page. */
  function update(changes: Partial<ReportSearch>) {
    onNavigate((current) => ({ ...current, page: undefined, ...changes }))
  }

  function choosePeriod(key: PeriodKey, custom?: DayRange) {
    update(key === 'custom' && custom ? { period: 'custom', ...custom } : { period: key, from: undefined, to: undefined })
  }

  function openView(view: BuiltInView) {
    setPicking(false)
    onNavigate(() => viewSearch(view))
  }

  function openSaved(report: SavedReport) {
    setPicking(false)
    onNavigate(() => savedReportSearch(report))
  }

  const list = <ViewsList views={views} savedReports={savedReports} current={openRef} onOpenView={openView} onOpenSaved={openSaved} />
  const aside = (
    <aside className="hidden lg:block">
      <div className="sticky top-20 grid gap-5">
        <h2 className="px-2 text-base font-semibold">Reports</h2>
        {list}
      </div>
    </aside>
  )

  if (blank) {
    return (
      <div className="mx-auto grid max-w-7xl gap-8 lg:grid-cols-[13rem_minmax(0,1fr)]">
        {aside}
        <ReportSkeleton />
      </div>
    )
  }

  const description = builtIn
    ? builtIn.description
    : savedReport
      ? 'A view you saved.'
      : isAdmin
        ? `Time tracked in ${workspace.name}.`
        : `Your time in ${workspace.name}. Admins see everyone's.`

  return (
    <div className="mx-auto grid max-w-7xl gap-8 lg:grid-cols-[13rem_minmax(0,1fr)]">
      {aside}

      <div className="grid min-w-0 content-start gap-5">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div className="grid min-w-0 gap-1">
            <div className="flex min-w-0 items-center gap-2.5">
              <h1 className="hidden truncate text-2xl font-semibold tracking-tight lg:block">{title}</h1>
              <Sheet open={picking} onOpenChange={setPicking}>
                <SheetTrigger asChild>
                  <button type="button" className="flex min-w-0 items-center gap-1.5 text-left text-2xl font-semibold tracking-tight lg:hidden" aria-label={`${title}: choose a view`}>
                    <span className="truncate">{title}</span>
                    <ChevronDown aria-hidden="true" className="text-muted-foreground size-5 shrink-0" />
                  </button>
                </SheetTrigger>
                <SheetContent side="bottom" className="max-h-[85svh] gap-3 overflow-y-auto rounded-t-2xl px-3 pb-6">
                  <SheetHeader className="px-1 pb-0">
                    <SheetTitle>Views</SheetTitle>
                    <SheetDescription className="sr-only">Choose a report to open.</SheetDescription>
                  </SheetHeader>
                  {list}
                </SheetContent>
              </Sheet>
              {edited ? <Badge className="bg-primary/15 text-primary-text font-semibold">Edited</Badge> : builtIn && <Badge variant="secondary">Built in</Badge>}
            </div>
            <p className="text-muted-foreground text-sm">{description}</p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <ViewActions
              workspaceId={workspaceId}
              search={search}
              period={period}
              range={range}
              builtIn={builtIn}
              savedReport={savedReport}
              edited={edited}
              onReset={() => onNavigate((current) => (savedReport ? savedReportSearch(savedReport) : builtIn ? viewSearch(builtIn) : current))}
              onSaved={openSaved}
              onDeleted={() => onNavigate(() => openingSearch(null, views, []))}
            />
            {isAdmin && (
              <Button variant="outline" onClick={() => setSharing(true)}>
                <Link2 />
                Share
              </Button>
            )}
            <ExportMenu onExport={(format) => downloadReport(workspaceId, tab, format, tab === 'summary' ? summary : tab === 'detailed' ? detailed : weekly)} />
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-2 border-b pb-4">
          <PeriodPicker
            key={tab === 'weekly' ? 'week' : 'period'}
            period={tab === 'weekly' && period !== 'thisWeek' && period !== 'lastWeek' ? 'custom' : period}
            range={range}
            unit={tab === 'weekly' ? 'week' : 'period'}
            presets={tab === 'weekly' ? ['thisWeek', 'lastWeek'] : undefined}
            onChange={choosePeriod}
            onShift={(direction) => update({ period: 'custom', ...shiftRange(range, direction) })}
          />
          <span aria-hidden="true" className="bg-border mx-1 hidden h-5 w-px sm:block" />
          <ReportFilters search={search} onChange={update} />
          <div className="ml-auto flex flex-wrap items-center gap-2">
            <RoundingFilter
              mode={search.rounding ?? 'none'}
              minutes={search.roundTo ?? 15}
              onChange={(rounding, roundTo) => update({ rounding: rounding === 'none' ? undefined : rounding, roundTo: rounding === 'none' ? undefined : roundTo })}
            />
            <Tabs value={tab} onValueChange={(value) => update({ tab: value as ReportTab })}>
              <TabsList aria-label="Report">
                {TABS.map((value) => (
                  <TabsTrigger key={value} value={value}>
                    {TAB_LABELS[value]}
                  </TabsTrigger>
                ))}
              </TabsList>
            </Tabs>
          </div>
        </div>

        {tab === 'summary' && (
          <SummaryView
            workspaceId={workspaceId}
            request={summary}
            onGroupingChange={(nextGroupBy, subGroupBy) => update({ groupBy: nextGroupBy, subGroupBy: subGroupBy ?? 'none' })}
          />
        )}
        {tab === 'detailed' && (
          <DetailedView
            request={detailed}
            onSort={(sort, descending) => update({ sort, asc: descending ? undefined : true })}
            onPage={(page) => onNavigate((current) => ({ ...current, page: page > 1 ? page : undefined }))}
          />
        )}
        {tab === 'weekly' && <WeeklyView workspaceId={workspaceId} request={weekly} onGroupBy={(nextGroupBy) => update({ groupBy: nextGroupBy })} />}
        {isAdmin && <ShareDialog workspaceId={workspaceId} open={sharing} onOpenChange={setSharing} filter={filter} period={period} range={range} />}
      </div>
    </div>
  )
}
