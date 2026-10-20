import { useQueries } from '@tanstack/react-query'
import { Bookmark, Building2, CalendarDays, ChartColumn, CircleDashed, Coins, History, LayoutGrid, Users, type LucideIcon } from 'lucide-react'
import type { ReactNode } from 'react'
import { cn } from 'cn'
import { dayKey } from '@/lib/time/zone'
import { useDurationFormat, useWorkspace } from '../workspace'
import { summaryQuery, type SavedReport } from './queries'
import { reportRequests } from './requests'
import type { ViewId } from './search'
import type { BuiltInView, ViewRef } from './views'

const ICONS: Record<ViewId, LucideIcon> = {
  'my-week': CalendarDays,
  'my-month': ChartColumn,
  yesterday: History,
  'billable-last-month': Coins,
  'team-week': LayoutGrid,
  'who-worked': Users,
  'billable-by-client': Building2,
  'no-project': CircleDashed,
}

interface ViewsListProps {
  views: BuiltInView[]
  savedReports: SavedReport[]
  current: ViewRef | undefined
  onOpenView: (view: BuiltInView) => void
  onOpenSaved: (report: SavedReport) => void
}

/** The views to open a report from: the person's own, the team's for admins, and the ones they saved. */
export function ViewsList({ views, savedReports, current, onOpenView, onOpenSaved }: ViewsListProps) {
  const yours = views.filter((view) => view.group === 'you')
  const team = views.filter((view) => view.group === 'team')
  const totals = useViewTotals(yours)

  return (
    <nav aria-label="Report views" className="grid content-start gap-5">
      <ViewGroup heading="For you">
        {yours.map((view, index) => (
          <ViewItem key={view.id} icon={ICONS[view.id]} label={view.label} meta={totals[index]} current={current === view.id} onOpen={() => onOpenView(view)} />
        ))}
      </ViewGroup>
      {team.length > 0 && (
        <ViewGroup heading="Team">
          {team.map((view) => (
            <ViewItem key={view.id} icon={ICONS[view.id]} label={view.label} current={current === view.id} onOpen={() => onOpenView(view)} />
          ))}
        </ViewGroup>
      )}
      {savedReports.length > 0 && (
        <ViewGroup heading="Saved by you">
          {savedReports.map((report) => (
            <ViewItem key={report.id} icon={Bookmark} label={report.name} current={current === `saved:${report.id}`} onOpen={() => onOpenSaved(report)} />
          ))}
        </ViewGroup>
      )}
    </nav>
  )
}

function ViewGroup({ heading, children }: { heading: string; children: ReactNode }) {
  return (
    <div className="grid gap-px">
      <h2 className="text-muted-foreground px-2 pb-1 text-xs font-medium">{heading}</h2>
      <ul className="grid gap-px">{children}</ul>
    </div>
  )
}

function ViewItem({ icon: Icon, label, meta, current, onOpen }: { icon: LucideIcon; label: string; meta?: string; current: boolean; onOpen: () => void }) {
  return (
    <li>
      <button
        type="button"
        aria-current={current ? 'page' : undefined}
        onClick={onOpen}
        className={cn(
          'hover:bg-muted focus-visible:ring-ring/50 flex min-h-9 w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-sm outline-none focus-visible:ring-3',
          current && 'bg-primary/15 hover:bg-primary/15 font-medium',
        )}
      >
        <Icon aria-hidden="true" className={cn('text-muted-foreground size-4 shrink-0', current && 'text-primary-text')} />
        <span className="min-w-0 flex-1 truncate">{label}</span>
        {meta && <span className="text-muted-foreground text-xs font-normal tabular-nums">{meta}</span>}
      </button>
    </li>
  )
}

/** The time in each of the person's own views, from the summary report's totals. */
function useViewTotals(views: BuiltInView[]): (string | undefined)[] {
  const { workspaceId, timeZone, weekStartsOn } = useWorkspace()
  const duration = useDurationFormat()
  const today = dayKey(new Date(), timeZone)
  // A summary view asks for just what it shows, so it opens at once; the others need no breakdown.
  const results = useQueries({
    queries: views.map((view) => {
      const { summary } = reportRequests(view.search, today, weekStartsOn)
      return summaryQuery(workspaceId, view.search.tab === 'summary' ? summary : { ...summary, subGroupBy: null })
    }),
  })
  return results.map((result) => (result.data ? duration(result.data.totals.seconds) : undefined))
}
