import { useQuery } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, ChevronLeft, ChevronRight, DollarSign } from 'lucide-react'
import { useState } from 'react'
import { cn } from 'cn'
import type { TimeEntry } from '@/api/types'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { errorMessage } from '@/lib/error-message'
import { dayKey, formatClock, formatDayAs } from '@/lib/time/zone'
import { EntryDialog } from '../time-entries/entry-dialog'
import { useDurationFormat, useWorkspace } from '../workspace'
import { money } from './charts'
import { detailedQuery, type DetailedEntry, type DetailedRequest } from './queries'
import { ReportSkeleton, ReportTotalsCards } from './report-totals'
import type { ReportSort } from './search'

interface DetailedViewProps {
  request: DetailedRequest
  onSort: (sort: ReportSort, descending: boolean) => void
  onPage: (page: number) => void
}

/** Every entry in the period, a page at a time. People open their own entries here to correct them. */
export function DetailedView({ request, onSort, onPage }: DetailedViewProps) {
  const { workspaceId, me, timeZone, hour12, isAdmin } = useWorkspace()
  const query = useQuery(detailedQuery(workspaceId, request))
  const [editing, setEditing] = useState<TimeEntry | null>(null)

  if (query.isPending) {
    return <ReportSkeleton />
  }
  if (query.isError) {
    return <p className="text-muted-foreground py-12 text-center text-sm">{errorMessage(query.error, "Couldn't load the report.")}</p>
  }

  const report = query.data
  const showMoney = report.totals.amount !== null && report.totals.amount !== undefined
  const first = (report.page - 1) * report.pageSize + 1
  const last = Math.min(report.totalCount, report.page * report.pageSize)
  const pages = Math.max(1, Math.ceil(report.totalCount / report.pageSize))

  const header = (sort: ReportSort, label: string, className?: string) => (
    <SortHeader sort={sort} label={label} current={request.sort} descending={request.descending} onSort={onSort} className={className} />
  )

  return (
    <div className={cn('grid gap-4 transition-opacity', query.isPlaceholderData && 'opacity-60')} aria-busy={query.isFetching}>
      <ReportTotalsCards totals={report.totals} currency={report.currency} />

      <Card className="py-0">
        <CardContent className="overflow-x-auto px-0">
          <table className="w-full min-w-[48rem] table-fixed text-sm">
            <caption className="sr-only">Time entries, sorted by {request.sort}</caption>
            <colgroup>
              <col />
              <col className="w-[22%]" />
              {isAdmin && <col className="w-[13%]" />}
              <col className="w-40" />
              <col className="w-28" />
              {showMoney && <col className="w-28" />}
            </colgroup>
            <thead>
              <tr className="text-muted-foreground border-b text-left text-xs">
                {header('description', 'Description', 'pl-4')}
                {header('project', 'Project')}
                {isAdmin && header('user', 'Member')}
                {header('date', 'Date')}
                {header('duration', 'Duration', 'text-right')}
                {showMoney && <th scope="col" className="px-4 py-2.5 text-right font-medium">Amount</th>}
              </tr>
            </thead>
            <tbody>
              {report.entries.length === 0 && (
                <tr>
                  <td colSpan={6} className="text-muted-foreground py-12 text-center">
                    No time entries in this period.
                  </td>
                </tr>
              )}
              {report.entries.map((entry) => (
                <EntryRow
                  key={`${entry.id}|${entry.projectId ?? ''}|${entry.taskId ?? ''}`}
                  entry={entry}
                  showMember={isAdmin}
                  showMoney={showMoney}
                  currency={report.currency}
                  timeZone={timeZone}
                  hour12={hour12}
                  onOpen={entry.userId === me.id ? () => setEditing(toTimeEntry(entry, workspaceId)) : undefined}
                />
              ))}
            </tbody>
          </table>
        </CardContent>
      </Card>

      {report.totalCount > 0 && (
        <nav aria-label="Pages" className="flex items-center justify-end gap-3 text-sm">
          <span className="text-muted-foreground tabular-nums">
            {first}–{last} of {report.totalCount}
          </span>
          <Button variant="outline" size="icon-sm" aria-label="Previous page" disabled={report.page <= 1} onClick={() => onPage(report.page - 1)}>
            <ChevronLeft />
          </Button>
          <Button variant="outline" size="icon-sm" aria-label="Next page" disabled={report.page >= pages} onClick={() => onPage(report.page + 1)}>
            <ChevronRight />
          </Button>
        </nav>
      )}

      <EntryDialog entry={editing} onClose={() => setEditing(null)} />
    </div>
  )
}

interface EntryRowProps {
  entry: DetailedEntry
  showMember: boolean
  showMoney: boolean
  currency: string
  timeZone: string
  hour12: boolean
  /** Only the person's own entries open for editing. */
  onOpen?: () => void
}

function EntryRow({ entry, showMember, showMoney, currency, timeZone, hour12, onOpen }: EntryRowProps) {
  const { dateFormat } = useWorkspace()
  const duration = useDurationFormat()
  const description = entry.description || 'No description'
  const day = dayKey(entry.start, timeZone)
  const weekday = new Intl.DateTimeFormat('en-GB', { weekday: 'short', timeZone: 'UTC' }).format(new Date(`${day}T00:00:00Z`))
  const date = `${weekday}, ${formatDayAs(day, dateFormat)}`

  return (
    <tr className={cn('border-b last:border-b-0', onOpen && 'hover:bg-muted/40 cursor-pointer')} onClick={onOpen}>
      <td className="max-w-0 py-2.5 pr-3 pl-4">
        {onOpen ? (
          <button type="button" className="block max-w-full truncate text-left hover:underline" onClick={(event) => {
              event.stopPropagation()
              onOpen()
            }}>
            <span className={cn(!entry.description && 'text-muted-foreground')}>{description}</span>
          </button>
        ) : (
          <span className={cn('block truncate', !entry.description && 'text-muted-foreground')}>{description}</span>
        )}
        {entry.tags.length > 0 && (
          <span className="mt-1 flex flex-wrap gap-1">
            {entry.tags.map((tag) => (
              <Badge key={tag} variant="secondary" className="font-normal">
                {tag}
              </Badge>
            ))}
          </span>
        )}
      </td>
      <td className="max-w-0 py-2.5 pr-3">
        {entry.projectName ? (
          <>
            <span className="flex min-w-0 items-center gap-1.5">
              <span aria-hidden="true" className="size-2.5 shrink-0 rounded-full" style={{ backgroundColor: entry.projectColor ?? undefined }} />
              <span className="truncate">{entry.projectName}</span>
              {entry.percent < 100 && (
                <span className="text-muted-foreground shrink-0 text-xs tabular-nums" title={`${entry.percent}% of a split entry`}>
                  {entry.percent}%
                </span>
              )}
            </span>
            {(entry.taskName || entry.clientName) && (
              <span className="text-muted-foreground block truncate pl-4 text-xs">{[entry.taskName, entry.clientName].filter(Boolean).join(' · ')}</span>
            )}
          </>
        ) : (
          <span className="text-muted-foreground">No project{entry.percent < 100 && ` · ${entry.percent}%`}</span>
        )}
      </td>
      {showMember && <td className="max-w-0 truncate py-2.5 pr-3">{entry.userName}</td>}
      <td className="py-2.5 pr-3 whitespace-nowrap">
        <span className="block">{date}</span>
        <span className="text-muted-foreground text-xs tabular-nums">
          {formatClock(entry.start, timeZone, hour12)} – {formatClock(entry.stop, timeZone, hour12)}
        </span>
      </td>
      <td className="py-2.5 pr-4 text-right whitespace-nowrap tabular-nums">
        <span className="inline-flex items-center gap-1">
          {entry.isBillable && <DollarSign aria-label="Billable" className="text-muted-foreground size-3.5" />}
          {duration(entry.seconds)}
        </span>
      </td>
      {showMoney && <td className="py-2.5 pr-4 text-right whitespace-nowrap tabular-nums">{money(entry.amount, currency)}</td>}
    </tr>
  )
}

function SortHeader({
  sort,
  label,
  current,
  descending,
  onSort,
  className,
}: {
  sort: ReportSort
  label: string
  current: ReportSort
  descending: boolean
  onSort: (sort: ReportSort, descending: boolean) => void
  className?: string
}) {
  const active = current === sort
  // Dates and durations start with the most recent and longest; names start from A.
  const firstDescending = sort === 'date' || sort === 'duration'
  return (
    <th scope="col" aria-sort={active ? (descending ? 'descending' : 'ascending') : undefined} className={cn('py-2.5 pr-3 font-medium', className)}>
      <button
        type="button"
        onClick={() => onSort(sort, active ? !descending : firstDescending)}
        className={cn('hover:text-foreground inline-flex items-center gap-1', active && 'text-foreground')}
      >
        {label}
        {active && (descending ? <ArrowDown className="size-3.5" /> : <ArrowUp className="size-3.5" />)}
      </button>
    </th>
  )
}

/** The entry a row shows. A row of a split entry is one part; the entry is all of them, on the largest as its project. */
function toTimeEntry(entry: DetailedEntry, workspaceId: string): TimeEntry {
  const largest = entry.shares.reduce<DetailedEntry['shares'][number] | null>((best, share) => (!best || share.percent > best.percent ? share : best), null)
  return {
    id: entry.id,
    workspaceId,
    description: entry.description,
    projectId: largest ? largest.projectId : entry.projectId,
    taskId: largest ? largest.taskId : entry.taskId,
    tagIds: entry.tagIds,
    isBillable: largest ? largest.isBillable : entry.isBillable,
    start: entry.start,
    stop: entry.stop,
    shares: entry.shares,
    // Reports don't carry it; the web saves without one.
    version: '',
  }
}
