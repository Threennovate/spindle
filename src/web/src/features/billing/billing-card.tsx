import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { MessageSquareText } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import type { Project } from '@/api/types'
import { PeriodNavigator } from '@/components/period-navigator'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { Skeleton } from '@/components/ui/skeleton'
import { Textarea } from '@/components/ui/textarea'
import { Table, TableBody, TableCell, TableFooter, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { errorMessage } from '@/lib/error-message'
import { formatMoney, formatRate } from '@/lib/money'
import { parseDuration } from '@/lib/time/duration'
import { addDaysToKey, dayKey } from '@/lib/time/zone'
import { formatRange, weekDays } from '../calendar/range'
import { usePermissions } from '../settings/queries'
import { useWorkspace } from '../workspace'
import { projectBillingQuery, useDescribeBilledDay, useSetBilledTime, type BillingRow, type ProjectBilling } from './queries'

/**
 * What a project's client is billed, day by day, for projects whose billed time is entered separately. Admins and the
 * project's managers type the billed time over what was tracked; days left empty are billed as tracked.
 */
export function BillingCard({ project }: { project: Project }) {
  const { workspaceId, timeZone, weekStartsOn } = useWorkspace()
  const today = dayKey(new Date(), timeZone)
  const [day, setDay] = useState(today)
  const days = weekDays(day, timeZone, weekStartsOn)
  // The previous week stays on screen while the next one loads.
  const query = useQuery({ ...projectBillingQuery(workspaceId, project.id, days[0], days[days.length - 1]), placeholderData: keepPreviousData })

  return (
    <Card>
      <CardHeader>
        <CardTitle>Billing</CardTitle>
        <CardDescription>
          {project.billingMode === 'fullDay'
            ? `What the client is billed for each day: a full day of ${project.dailyHours ?? '–'} h on every working day with time tracked. Type over a day where it differs, e.g. 4 h for half a day or 0 for a day not billed.`
            : 'What the client is billed for each day. Type over the tracked time where it differs, e.g. 1:30 or 1.5 for a call nobody tracked. Empty days are billed as tracked.'}
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <PeriodNavigator
          label={formatRange(days)}
          unit="week"
          onPrevious={() => setDay(addDaysToKey(days[0], -7))}
          onNext={() => setDay(addDaysToKey(days[0], 7))}
          onToday={() => setDay(today)}
        />
        {query.isPending ? (
          <Skeleton className="h-32 w-full" />
        ) : query.isError ? (
          <p className="text-muted-foreground text-sm">{errorMessage(query.error, "Couldn't load the billed time.")}</p>
        ) : (
          <div className={query.isPlaceholderData ? 'opacity-60 transition-opacity' : 'transition-opacity'} aria-busy={query.isFetching}>
            <BillingWeek billing={query.data} projectId={project.id} />
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function BillingWeek({ billing, projectId }: { billing: ProjectBilling; projectId: string }) {
  const rows = billing.rows.filter((row) => row.isMember || row.trackedSeconds > 0 || row.billedSeconds > 0)
  const billedOn = (row: BillingRow, index: number) => row.billedDays[index] ?? row.defaultDays[index]
  const tracked = rows.reduce((sum, row) => sum + row.trackedSeconds, 0)
  const billed = rows.reduce((sum, row) => sum + row.billedSeconds, 0)

  return (
    <div className="grid gap-3">
      <BillingSummary billing={billing} rows={rows} tracked={tracked} billed={billed} />
      <div className="overflow-x-auto rounded-lg border">
        <Table className="min-w-[46rem]">
          <TableHeader>
            <TableRow>
              <TableHead className="w-48 pl-4">Member</TableHead>
              {billing.days.map((day) => (
                <TableHead key={day} className="text-center">
                  {formatDayColumn(day)}
                </TableHead>
              ))}
              <TableHead className="pr-4 text-right">Billed</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.length === 0 && (
              <TableRow>
                <TableCell colSpan={billing.days.length + 2} className="text-muted-foreground py-8 text-center">
                  Nobody tracked time on the project this week.
                </TableCell>
              </TableRow>
            )}
            {rows.map((row) => (
              <TableRow key={row.userId}>
                <TableCell className="max-w-48 pl-4">
                  <div className="grid min-w-0">
                    <span className="truncate text-sm font-medium">{row.fullName}</span>
                    <span className="text-muted-foreground text-xs">{formatHm(row.trackedSeconds) || '0:00'} tracked</span>
                  </div>
                </TableCell>
                {billing.days.map((day, index) => (
                  <TableCell key={day} className="group/day p-1 text-center">
                    <div className="flex flex-col items-center gap-0.5">
                      <BilledCell projectId={projectId} row={row} day={day} index={index} />
                      <DayDescription projectId={projectId} row={row} day={day} index={index} />
                    </div>
                  </TableCell>
                ))}
                <TableCell className="pr-4 text-right">
                  <div className="grid">
                    <span className="font-medium tabular-nums">{formatHm(row.billedSeconds) || '0:00'}</span>
                    <Difference billed={row.billedSeconds} tracked={row.trackedDays.reduce((sum, seconds) => sum + seconds, 0)} />
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
          <TableFooter>
            <TableRow>
              <TableCell className="pl-4 font-medium">Total</TableCell>
              {billing.days.map((day, index) => (
                <TableCell key={day} className="text-center font-medium tabular-nums">
                  {formatHm(rows.reduce((sum, row) => sum + billedOn(row, index), 0)) || '–'}
                </TableCell>
              ))}
              <TableCell className="pr-4 text-right font-semibold tabular-nums">{formatHm(billed) || '0:00'}</TableCell>
            </TableRow>
          </TableFooter>
        </Table>
      </div>
    </div>
  )
}

/** The week in a line: time tracked and billed and, for admins, what it earns per hour of work. */
function BillingSummary({ billing, rows, tracked, billed }: { billing: ProjectBilling; rows: BillingRow[]; tracked: number; billed: number }) {
  const amount = rows.every((row) => row.amount != null) ? rows.reduce((sum, row) => sum + (row.amount ?? 0), 0) : null
  const cost = rows.reduce((sum, row) => sum + (row.laborCost ?? 0), 0)
  const parts = [`${formatHm(tracked) || '0:00'} tracked`, `${formatHm(billed) || '0:00'} billed`]
  if (tracked > 0) {
    parts.push(`${(billed / tracked).toFixed(2)}× the time tracked`)
  }

  return (
    <div className="text-muted-foreground flex flex-wrap gap-x-3 gap-y-1 text-sm">
      <span>{parts.join(' · ')}</span>
      {amount !== null && rows.length > 0 && (
        <span>
          {formatMoney(amount, billing.currency)} billed
          {cost > 0 && ` · ${formatMoney(cost, billing.currency)} labor cost`}
          {tracked > 0 && (
            <>
              {' · '}
              <span className="text-foreground font-medium">{formatRate(amount / (tracked / 3600), billing.currency)}</span> per hour tracked
            </>
          )}
        </span>
      )}
    </div>
  )
}

/** One person's billed time on one day: what was entered, or the tracked time shown faintly as what is billed. */
function BilledCell({ projectId, row, day, index }: { projectId: string; row: BillingRow; day: string; index: number }) {
  const { workspaceId } = useWorkspace()
  const { isDayLocked, lockMessage } = usePermissions()
  const save = useSetBilledTime(workspaceId, projectId)
  const [text, setText] = useState<string | null>(null)
  const entered = row.billedDays[index]
  const tracked = row.trackedDays[index]
  const fallback = row.defaultDays[index]
  const fullDay = fallback !== tracked
  const locked = isDayLocked(day)
  const label = `Billed for ${row.fullName} on ${formatDayColumn(day)}`

  function commit() {
    const typed = text?.trim()
    setText(null)
    if (typed === undefined || typed === (entered == null ? '' : formatHm(entered) || '0:00')) {
      return
    }
    const seconds = typed === '' ? null : parseDuration(typed)
    if (typed !== '' && seconds === null) {
      toast.error(`“${typed}” is not a duration. Try 1:30, 90m or 1.5.`)
      return
    }
    if (seconds === null && entered == null) {
      return
    }
    save.mutate({ userId: row.userId, day, seconds }, { onError: (error) => toast.error(errorMessage(error, "Couldn't save the billed time.")) })
  }

  // While saving, the cell shows what was typed rather than what the server had.
  const shown = save.isPending ? save.variables.seconds : entered

  return (
    <Input
      aria-label={label}
      value={text ?? (shown == null ? '' : formatHm(shown) || '0:00')}
      placeholder={formatHm(fallback) || '–'}
      disabled={!row.isMember || locked || save.isPending}
      title={
        locked
          ? lockMessage
          : !row.isMember
            ? 'No longer in the workspace.'
            : entered != null
              ? `Tracked ${formatHm(tracked) || '0:00'}. Clear to bill ${fullDay ? 'a full day' : 'as tracked'}.`
              : fullDay
                ? `A full day; ${formatHm(tracked) || 'no billable time'} tracked`
                : 'Billed as tracked'
      }
      onFocus={(event) => {
        setText(entered == null ? '' : formatHm(entered) || '0:00')
        event.currentTarget.select()
      }}
      onChange={(event) => setText(event.target.value)}
      onBlur={commit}
      onKeyDown={(event) => {
        if (event.key === 'Enter') {
          event.currentTarget.blur()
        } else if (event.key === 'Escape') {
          setText(null)
          event.currentTarget.blur()
        }
      }}
      className={`mx-auto h-8 w-16 text-center tabular-nums ${shown != null ? 'border-primary/50 font-medium' : ''}`}
    />
  )
}

/**
 * What timesheet links say was done on someone's day: their billable entries' descriptions, unless the day was
 * described by hand, e.g. to add a call nobody tracked. A day described by hand no longer follows its entries.
 */
function DayDescription({ projectId, row, day, index }: { projectId: string; row: BillingRow; day: string; index: number }) {
  const { workspaceId } = useWorkspace()
  const { isDayLocked } = usePermissions()
  const describe = useDescribeBilledDay(workspaceId, projectId)
  const [open, setOpen] = useState(false)
  const [text, setText] = useState('')
  const described = row.descriptions[index]
  const fromEntries = row.entryDescriptions[index]
  if (described == null && fromEntries.length === 0 && (!row.isMember || isDayLocked(day))) {
    return <span className="h-5" />
  }

  const readOnly = !row.isMember || isDayLocked(day)
  const id = `day-description-${row.userId}-${day}`

  function save(description: string | null) {
    describe.mutate(
      { userId: row.userId, day, description },
      {
        onSuccess: () => setOpen(false),
        onError: (error) => toast.error(errorMessage(error, "Couldn't save the description.")),
      },
    )
  }

  return (
    <Popover
      open={open}
      onOpenChange={(next) => {
        setOpen(next)
        if (next) {
          setText(described ?? fromEntries.join('\n'))
        }
      }}
    >
      <PopoverTrigger asChild>
        <Button
          variant="ghost"
          size="icon"
          aria-label={`What was done: ${row.fullName} on ${formatDayColumn(day)}${described != null ? ', described by hand' : ''}`}
          className={`size-5 rounded-sm ${described != null ? 'text-primary-text' : 'text-muted-foreground/60 opacity-0 group-hover/day:opacity-100 focus-visible:opacity-100 data-[state=open]:opacity-100'}`}
        >
          <MessageSquareText className="size-3.5" />
        </Button>
      </PopoverTrigger>
      <PopoverContent className="grid w-80 gap-3">
        <div className="grid gap-2">
          <Label htmlFor={id}>
            What was done · {row.fullName}, {formatDayColumn(day)}
          </Label>
          <Textarea id={id} value={text} onChange={(event) => setText(event.target.value)} rows={4} maxLength={2000} disabled={readOnly} placeholder="e.g. Call with the client" />
          <p className="text-muted-foreground text-xs">
            {described != null
              ? 'Described by hand. Timesheet links show this instead of the entries’ descriptions.'
              : 'From the day’s entries. Edit it to change what timesheet links show for this day.'}
          </p>
        </div>
        {!readOnly && (
          <div className="flex justify-end gap-2">
            {described != null && (
              <Button variant="ghost" size="sm" disabled={describe.isPending} onClick={() => save(null)}>
                Use entries’ descriptions
              </Button>
            )}
            <Button
              size="sm"
              disabled={describe.isPending || text.trim() === (described ?? fromEntries.join('\n')).trim()}
              onClick={() => save(text.trim() ? text : null)}
            >
              Save
            </Button>
          </div>
        )}
      </PopoverContent>
    </Popover>
  )
}

/** How far the billed time is from the billable time tracked, e.g. `+0:45`; nothing when they match. */
function Difference({ billed, tracked }: { billed: number; tracked: number }) {
  const difference = billed - tracked
  if (Math.round(difference / 60) === 0) {
    return null
  }
  return <span className="text-muted-foreground text-xs tabular-nums">{`${difference > 0 ? '+' : '−'}${formatHm(Math.abs(difference))}`}</span>
}

/** `h:mm`, empty for nothing. */
function formatHm(seconds: number): string {
  const minutes = Math.round(seconds / 60)
  return minutes === 0 ? '' : `${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, '0')}`
}

function formatDayColumn(day: string): string {
  const [year, month, date] = day.split('-').map(Number)
  return new Intl.DateTimeFormat('en-GB', { weekday: 'short', day: 'numeric', timeZone: 'UTC' }).format(new Date(Date.UTC(year, month - 1, date)))
}
