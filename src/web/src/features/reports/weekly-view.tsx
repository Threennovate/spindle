import { useQuery } from '@tanstack/react-query'
import { cn } from 'cn'
import { Card, CardAction, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { errorMessage } from '@/lib/error-message'
import { useDurationFormat } from '../workspace'
import { groupColor, money } from './charts'
import { weeklyQuery, type WeeklyRequest } from './queries'
import { ReportSkeleton, ReportTotalsCards } from './report-totals'
import { GROUPING_LABELS, type ReportGrouping } from './search'

const WEEKLY_GROUPINGS = ['project', 'client', 'user', 'tag', 'task'] as const satisfies readonly ReportGrouping[]

interface WeeklyViewProps {
  workspaceId: string
  request: WeeklyRequest
  onGroupBy: (groupBy: ReportGrouping) => void
}

/** A week as a grid: a row per project (or member, client…), a column per day. */
export function WeeklyView({ workspaceId, request, onGroupBy }: WeeklyViewProps) {
  const query = useQuery(weeklyQuery(workspaceId, request))
  const duration = useDurationFormat()

  if (query.isPending) {
    return <ReportSkeleton />
  }
  if (query.isError) {
    return <p className="text-muted-foreground py-12 text-center text-sm">{errorMessage(query.error, "Couldn't load the report.")}</p>
  }

  const report = query.data
  const showMoney = report.totals.amount !== null && report.totals.amount !== undefined
  const cell = (seconds: number) => (seconds > 0 ? duration(seconds) : '–')

  return (
    <div className={cn('grid gap-4 transition-opacity', query.isPlaceholderData && 'opacity-60')} aria-busy={query.isFetching}>
      <ReportTotalsCards totals={report.totals} currency={report.currency} />
      <Card className="gap-0 pb-0">
        <CardHeader className="pb-4">
          <CardTitle>Week</CardTitle>
          <CardAction className="flex items-center gap-2 text-sm">
            <span className="text-muted-foreground">Rows</span>
            <Select value={WEEKLY_GROUPINGS.includes(request.groupBy as never) ? request.groupBy : 'project'} onValueChange={(value) => onGroupBy(value as ReportGrouping)}>
              <SelectTrigger size="sm" aria-label="Rows" className="w-32">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {WEEKLY_GROUPINGS.map((grouping) => (
                  <SelectItem key={grouping} value={grouping}>
                    {GROUPING_LABELS[grouping]}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </CardAction>
        </CardHeader>
        <CardContent className="overflow-x-auto px-0">
          <table className="w-full min-w-[48rem] text-sm">
            <caption className="sr-only">Time per day by {GROUPING_LABELS[request.groupBy].toLowerCase()}</caption>
            <thead>
              <tr className="text-muted-foreground border-y text-xs">
                <th scope="col" className="py-2.5 pl-4 text-left font-medium">
                  {GROUPING_LABELS[request.groupBy]}
                </th>
                {report.days.map((day) => (
                  <th key={day} scope="col" className="px-2 py-2.5 text-right font-medium whitespace-nowrap">
                    {dayHeading(day)}
                  </th>
                ))}
                <th scope="col" className="px-2 py-2.5 text-right font-medium">
                  Total
                </th>
                {showMoney && (
                  <th scope="col" className="py-2.5 pr-4 text-right font-medium">
                    Amount
                  </th>
                )}
              </tr>
            </thead>
            <tbody>
              {report.rows.length === 0 && (
                <tr>
                  <td colSpan={report.days.length + 3} className="text-muted-foreground py-12 text-center">
                    No time tracked this week.
                  </td>
                </tr>
              )}
              {report.rows.map((row, index) => (
                <tr key={row.key} className="border-b">
                  <th scope="row" className="max-w-56 py-2.5 pl-4 text-left font-normal">
                    <span className="flex min-w-0 items-center gap-2">
                      <span aria-hidden="true" className="size-2.5 shrink-0 rounded-full" style={{ backgroundColor: groupColor(row, index) }} />
                      <span className={cn('truncate font-medium', row.key === 'none' && 'text-muted-foreground')}>{row.name}</span>
                      {row.detail && <span className="text-muted-foreground truncate">{row.detail}</span>}
                    </span>
                  </th>
                  {row.days.map((seconds, day) => (
                    <td key={report.days[day]} className={cn('px-2 py-2.5 text-right tabular-nums', seconds === 0 && 'text-muted-foreground')}>
                      {cell(seconds)}
                    </td>
                  ))}
                  <td className="px-2 py-2.5 text-right font-medium tabular-nums">{duration(row.seconds)}</td>
                  {showMoney && <td className="py-2.5 pr-4 text-right tabular-nums">{money(row.amount, report.currency)}</td>}
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr className="bg-muted/40 font-medium">
                <th scope="row" className="py-2.5 pl-4 text-left">
                  Total
                </th>
                {report.dayTotals.map((seconds, day) => (
                  <td key={report.days[day]} className="px-2 py-2.5 text-right tabular-nums">
                    {cell(seconds)}
                  </td>
                ))}
                <td className="px-2 py-2.5 text-right font-semibold tabular-nums">{duration(report.totals.seconds)}</td>
                {showMoney && <td className="py-2.5 pr-4 text-right tabular-nums">{money(report.totals.amount, report.currency)}</td>}
              </tr>
            </tfoot>
          </table>
        </CardContent>
      </Card>
    </div>
  )
}

function dayHeading(day: string): string {
  return new Intl.DateTimeFormat('en-GB', { weekday: 'short', day: 'numeric', timeZone: 'UTC' }).format(new Date(`${day}T00:00:00Z`))
}
