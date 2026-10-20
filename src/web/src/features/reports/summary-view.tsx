import { useQuery } from '@tanstack/react-query'
import { ChevronRight } from 'lucide-react'
import { useState } from 'react'
import { cn } from 'cn'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { errorMessage } from '@/lib/error-message'
import { useDurationFormat } from '../workspace'
import { BarChart } from './bar-chart'
import { groupColor, hasBilledTime, money, percent, perHour } from './charts'
import { DonutChart } from './donut-chart'
import { summaryQuery, type SummaryGroup, type SummaryRequest } from './queries'
import { ReportSkeleton, ReportTotalsCards } from './report-totals'
import { GROUPING_LABELS, GROUPINGS, type ReportGrouping } from './search'

interface SummaryViewProps {
  workspaceId: string
  request: SummaryRequest
  onGroupingChange: (groupBy: ReportGrouping, subGroupBy: ReportGrouping | null) => void
}

/** Where the time went: totals, a chart over the period, and the time by group and sub-group. */
export function SummaryView({ workspaceId, request, onGroupingChange }: SummaryViewProps) {
  const query = useQuery(summaryQuery(workspaceId, request))

  if (query.isPending) {
    return <ReportSkeleton />
  }
  if (query.isError) {
    return <p className="text-muted-foreground py-12 text-center text-sm">{errorMessage(query.error, "Couldn't load the report.")}</p>
  }

  const report = query.data
  const showMoney = report.totals.amount !== null && report.totals.amount !== undefined
  const showBilled = hasBilledTime(report.totals)

  return (
    <div className={cn('grid gap-4 transition-opacity', query.isPlaceholderData && 'opacity-60')} aria-busy={query.isFetching}>
      <ReportTotalsCards totals={report.totals} currency={report.currency} />

      <Card>
        <CardHeader>
          <CardTitle>Time by {report.resolution}</CardTitle>
        </CardHeader>
        <CardContent>
          <BarChart buckets={report.buckets} resolution={report.resolution} />
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="flex flex-wrap items-center gap-2">
          <CardTitle className="mr-auto">Breakdown</CardTitle>
          <CardAction className="flex flex-wrap items-center gap-2 text-sm">
            <span className="text-muted-foreground">Group by</span>
            <GroupingSelect label="Group by" value={request.groupBy} onChange={(groupBy) => groupBy && onGroupingChange(groupBy, request.subGroupBy === groupBy ? null : request.subGroupBy)} />
            <span className="text-muted-foreground">then</span>
            <GroupingSelect label="Then by" value={request.subGroupBy} exclude={request.groupBy} allowNone onChange={(sub) => onGroupingChange(request.groupBy, sub)} />
          </CardAction>
        </CardHeader>
        <CardContent>
          {report.groups.length === 0 ? (
            <p className="text-muted-foreground py-10 text-center text-sm">No time tracked in this period{request.projectIds?.length || request.tagIds?.length ? ' with these filters' : ''}.</p>
          ) : (
            <div className="grid items-start gap-6 lg:grid-cols-[11rem_1fr]">
              <DonutChart
                total={report.totals.seconds}
                slices={report.groups.map((group, index) => ({ key: group.key, label: group.name, seconds: group.seconds, color: groupColor(group, index) }))}
              />
              <GroupTable
                groups={report.groups}
                total={report.totals.seconds}
                currency={report.currency}
                showMoney={showMoney}
                showBilled={showBilled}
                grouping={request.groupBy}
              />
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  )
}

function GroupingSelect({
  label,
  value,
  onChange,
  exclude,
  allowNone = false,
}: {
  label: string
  value: ReportGrouping | null
  onChange: (value: ReportGrouping | null) => void
  exclude?: ReportGrouping
  allowNone?: boolean
}) {
  return (
    <Select value={value ?? 'none'} onValueChange={(next) => onChange(next === 'none' ? null : (next as ReportGrouping))}>
      <SelectTrigger size="sm" aria-label={label} className="w-36">
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        {allowNone && <SelectItem value="none">Nothing</SelectItem>}
        {GROUPINGS.filter((grouping) => grouping !== exclude).map((grouping) => (
          <SelectItem key={grouping} value={grouping}>
            {GROUPING_LABELS[grouping]}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}

interface GroupTableProps {
  groups: SummaryGroup[]
  total: number
  currency: string
  showMoney: boolean
  /** Whether some billed time was entered separately, so it and what each tracked hour earns are worth showing. */
  showBilled: boolean
  grouping: ReportGrouping
}

function GroupTable({ groups, total, currency, showMoney, showBilled, grouping }: GroupTableProps) {
  const [open, setOpen] = useState<ReadonlySet<string>>(new Set())
  const expandable = groups.some((group) => group.subGroups.length > 0)
  const allOpen = expandable && groups.every((group) => group.subGroups.length === 0 || open.has(group.key))

  function toggle(key: string) {
    setOpen((current) => {
      const next = new Set(current)
      if (next.has(key)) {
        next.delete(key)
      } else {
        next.add(key)
      }
      return next
    })
  }

  return (
    <div className="grid gap-2">
      <table className="w-full table-fixed text-sm">
        <caption className="sr-only">Time by {GROUPING_LABELS[grouping].toLowerCase()}</caption>
        <colgroup>
          <col />
          <col className="w-20 sm:w-24" />
          {/* With billing columns there is room for the share on wide screens only. */}
          <col className={cn('hidden', showBilled ? 'w-32 xl:table-column' : 'w-44 sm:table-column')} />
          {showBilled && <col className="w-20 sm:w-24" />}
          {showMoney && <col className="w-24 sm:w-28" />}
          {showBilled && <col className="hidden w-24 md:table-column" />}
        </colgroup>
        <thead>
          <tr className="text-muted-foreground border-b text-left text-xs">
            <th scope="col" className="py-2 font-medium">
              {GROUPING_LABELS[grouping]}
              {expandable && (
                <>
                  <span aria-hidden="true"> · </span>
                  <Button variant="link" size="sm" className="text-muted-foreground h-auto p-0 text-xs font-medium" onClick={() => setOpen(allOpen ? new Set() : new Set(groups.map((g) => g.key)))}>
                    {allOpen ? 'Collapse all' : 'Expand all'}
                  </Button>
                </>
              )}
            </th>
            <th scope="col" className="py-2 text-right font-medium">
              Duration
            </th>
            <th scope="col" className={cn('hidden w-40 py-2 pl-4 font-medium', showBilled ? 'xl:table-cell' : 'sm:table-cell')}>
              Share
            </th>
            {showBilled && (
              <th scope="col" className="py-2 text-right font-medium" title="Billable time, with the billed time entered separately where projects have it">
                Billed
              </th>
            )}
            {showMoney && (
              <th scope="col" className="py-2 text-right font-medium">
                Amount
              </th>
            )}
            {showBilled && (
              <th scope="col" className="hidden py-2 text-right font-medium md:table-cell" title="The amount for each hour tracked, billable or not">
                Per hour
              </th>
            )}
          </tr>
        </thead>
        <tbody>
          {groups.map((group, index) => (
            <GroupRows
              key={group.key}
              group={group}
              color={groupColor(group, index)}
              total={total}
              currency={currency}
              showMoney={showMoney}
              showBilled={showBilled}
              open={open.has(group.key)}
              onToggle={() => toggle(group.key)}
            />
          ))}
        </tbody>
      </table>
    </div>
  )
}

interface GroupRowsProps {
  group: SummaryGroup
  color: string
  total: number
  currency: string
  showMoney: boolean
  showBilled: boolean
  open: boolean
  onToggle: () => void
}

function GroupRows({ group, color, total, currency, showMoney, showBilled, open, onToggle }: GroupRowsProps) {
  const duration = useDurationFormat()
  const hasSubGroups = group.subGroups.length > 0
  return (
    <>
      <tr className="border-b last:border-b-0">
        <td className="max-w-0 py-2 pr-3">
          <div className="flex min-w-0 items-center gap-2">
            {hasSubGroups ? (
              <button
                type="button"
                onClick={onToggle}
                aria-expanded={open}
                aria-label={`${open ? 'Hide' : 'Show'} the breakdown of ${group.name}`}
                className="hover:bg-muted -ml-1 rounded p-0.5"
              >
                <ChevronRight className={cn('size-4 transition-transform', open && 'rotate-90')} />
              </button>
            ) : (
              <span className="w-4 shrink-0" />
            )}
            <span aria-hidden="true" className="size-2.5 shrink-0 rounded-full" style={{ backgroundColor: color }} />
            <span className={cn('truncate font-medium', group.key === 'none' && 'text-muted-foreground')}>{group.name}</span>
            {group.detail && <span className="text-muted-foreground hidden truncate sm:inline">{group.detail}</span>}
          </div>
        </td>
        <td className="py-2 text-right font-medium whitespace-nowrap tabular-nums">{duration(group.seconds)}</td>
        <td className={cn('hidden py-2 pl-4', showBilled ? 'xl:table-cell' : 'sm:table-cell')}>
          <Share seconds={group.seconds} total={total} color={color} />
        </td>
        {showBilled && <td className="py-2 text-right whitespace-nowrap tabular-nums">{signedDuration(group.billedSeconds, duration)}</td>}
        {showMoney && <td className="py-2 text-right whitespace-nowrap tabular-nums">{money(group.amount, currency)}</td>}
        {showBilled && <td className="hidden py-2 text-right whitespace-nowrap tabular-nums md:table-cell">{perHour(group.amount, group.seconds, currency)}</td>}
      </tr>
      {open &&
        group.subGroups.map((sub) => (
          <tr key={sub.key} className="bg-muted/30 border-b last:border-b-0">
            <td className="max-w-0 py-1.5 pr-3 pl-12">
              <span className={cn('block truncate', sub.key === 'none' && 'text-muted-foreground')}>
                {sub.name}
                {sub.detail && <span className="text-muted-foreground"> · {sub.detail}</span>}
              </span>
            </td>
            <td className="py-1.5 text-right whitespace-nowrap tabular-nums">{duration(sub.seconds)}</td>
            <td className={cn('hidden py-1.5 pl-4', showBilled ? 'xl:table-cell' : 'sm:table-cell')}>
              <Share seconds={sub.seconds} total={total} color={color} muted />
            </td>
            {showBilled && <td className="py-1.5 text-right whitespace-nowrap tabular-nums">{signedDuration(sub.billedSeconds, duration)}</td>}
            {showMoney && <td className="py-1.5 text-right whitespace-nowrap tabular-nums">{money(sub.amount, currency)}</td>}
            {showBilled && <td className="hidden py-1.5 text-right whitespace-nowrap tabular-nums md:table-cell">{perHour(sub.amount, sub.seconds, currency)}</td>}
          </tr>
        ))}
    </>
  )
}

/** A duration that can be negative: a billing adjustment that bills less than was tracked. */
function signedDuration(seconds: number | null | undefined, duration: (seconds: number) => string): string {
  if (seconds == null) {
    return '—'
  }
  return seconds < 0 ? `−${duration(-seconds)}` : duration(seconds)
}

function Share({ seconds, total, color, muted = false }: { seconds: number; total: number; color: string; muted?: boolean }) {
  const width = total > 0 ? Math.min(100, (seconds / total) * 100) : 0
  return (
    <div className="flex items-center gap-2">
      <div className="bg-muted h-1.5 flex-1 overflow-hidden rounded-full">
        <div className={cn('h-full rounded-full', muted && 'opacity-60')} style={{ width: `${width}%`, backgroundColor: color }} />
      </div>
      <span className="text-muted-foreground w-11 text-right text-xs tabular-nums">{percent(seconds, total)}</span>
    </div>
  )
}
