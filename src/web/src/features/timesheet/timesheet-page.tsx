import { useQuery } from '@tanstack/react-query'
import { Lock, Plus } from 'lucide-react'
import { useMemo, useState } from 'react'
import { toast } from 'sonner'
import type { TimeEntry } from '@/api/types'
import { PageHeader } from '@/components/page-header'
import { PeriodNavigator } from '@/components/period-navigator'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Table, TableBody, TableCell, TableFooter, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { errorMessage } from '@/lib/error-message'
import { parseDuration } from '@/lib/time/duration'
import { addDaysToKey, dayKey } from '@/lib/time/zone'
import { usePageTitle } from '@/lib/title'
import { useNow } from '@/lib/use-now'
import { ProjectDot } from '../catalog/project-label'
import { ProjectPicker } from '../catalog/project-picker'
import { useCatalog, type Catalog } from '../catalog/use-catalog'
import { formatRange, rangeEntriesQuery, weekDays } from '../calendar/range'
import { usePermissions } from '../settings/queries'
import { entrySeconds } from '../time-entries/day-groups'
import { toRequest, useCreateEntry, useDeleteEntry, useUpdateEntry } from '../time-entries/queries'
import { missingDetails } from '../time-entries/rules'
import { allocate, isSplit, partsOf } from '../time-entries/split'
import { useWorkspace } from '../workspace'
import { planCellChange } from './plan'

interface Row {
  key: string
  projectId: string | null
  taskId: string | null
}

const rowKey = (projectId: string | null | undefined, taskId: string | null | undefined) => `${projectId ?? ''}|${taskId ?? ''}`

/** The seconds an entry has on each row: all of it on its project, or each part of a split entry on its own. */
function secondsByRow(entry: TimeEntry, now: number): Map<string, number> {
  const parts = partsOf(entry)
  const seconds = allocate(entrySeconds(entry, now), parts.map((part) => part.percent))
  const byRow = new Map<string, number>()
  parts.forEach((part, i) => byRow.set(rowKey(part.projectId, part.taskId), (byRow.get(rowKey(part.projectId, part.taskId)) ?? 0) + seconds[i]))
  return byRow
}

/** A week as a grid: one row per project and task, one column per day. Typing a total adds or removes time. */
export function TimesheetPage({ date, onNavigate }: { date?: string; onNavigate: (date?: string) => void }) {
  usePageTitle('Timesheet')
  const { workspaceId, timeZone, weekStartsOn } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  const { settings, isDayLocked, lockMessage } = usePermissions()
  const now = useNow()
  const today = dayKey(new Date(), timeZone)
  const days = useMemo(() => weekDays(date ?? today, timeZone, weekStartsOn), [date, today, timeZone, weekStartsOn])
  const query = useQuery(rangeEntriesQuery(workspaceId, days, timeZone))
  const [addedRows, setAddedRows] = useState<Row[]>([])

  const weekEntries = useMemo(() => (query.data ?? []).filter((entry) => days.includes(dayKey(entry.start, timeZone))), [query.data, days, timeZone])

  const rows = useMemo(() => {
    const byKey = new Map<string, Row>()
    for (const part of weekEntries.flatMap((entry) => partsOf(entry))) {
      const key = rowKey(part.projectId, part.taskId)
      byKey.set(key, { key, projectId: part.projectId, taskId: part.taskId })
    }
    for (const row of addedRows) {
      byKey.set(row.key, row)
    }
    return [...byKey.values()].sort((a, b) => rowName(a, catalog).localeCompare(rowName(b, catalog)))
  }, [weekEntries, addedRows, catalog])

  const cellEntries = (row: Row, day: string) =>
    weekEntries.filter((entry) => partsOf(entry).some((part) => rowKey(part.projectId, part.taskId) === row.key) && dayKey(entry.start, timeZone) === day)
  const total = (entries: readonly TimeEntry[]) => entries.reduce((sum, entry) => sum + entrySeconds(entry, now), 0)
  const rowTotal = (row: Row, entries: readonly TimeEntry[]) => entries.reduce((sum, entry) => sum + (secondsByRow(entry, now).get(row.key) ?? 0), 0)

  return (
    <div className="mx-auto grid max-w-6xl gap-4">
      <PageHeader title="Timesheet" description="Your week by project. Type a duration such as 1:30, 90m or 1.5 into a day to change it." />
      <PeriodNavigator
        label={formatRange(days)}
        unit="week"
        onPrevious={() => onNavigate(addDaysToKey(days[0], -7))}
        onNext={() => onNavigate(addDaysToKey(days[0], 7))}
        onToday={() => onNavigate(undefined)}
      />

      <div className="bg-card overflow-x-auto rounded-xl border">
        <Table className="min-w-[48rem]">
          <TableHeader>
            <TableRow>
              <TableHead className="w-64 pl-4">Project</TableHead>
              {days.map((day) => (
                <TableHead key={day} className={day === today ? 'text-primary-text text-center' : 'text-center'}>
                  <span className="inline-flex items-center gap-1">
                    {formatDayColumn(day)}
                    {isDayLocked(day) && <Lock aria-label="Locked" className="size-3" />}
                  </span>
                </TableHead>
              ))}
              <TableHead className="pr-4 text-right">Total</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.length === 0 && (
              <TableRow>
                <TableCell colSpan={days.length + 2} className="text-muted-foreground py-10 text-center">
                  {query.isPending ? 'Loading…' : 'Nothing tracked this week. Add a row to start filling in your time.'}
                </TableCell>
              </TableRow>
            )}
            {rows.map((row) => {
              // New time from a cell has no description or tags, which the workspace may require.
              const missing = missingDetails(
                settings,
                { description: '', projectId: row.projectId, taskId: row.taskId, tagIds: [] },
                catalog.project(row.projectId),
              )
              return (
                <TableRow key={row.key}>
                  <TableCell className="max-w-64 pl-4">
                    <RowLabel row={row} catalog={catalog} />
                  </TableCell>
                  {days.map((day) => (
                    <TableCell key={day} className="p-1 text-center">
                      <TimesheetCell
                        workspaceId={workspaceId}
                        timeZone={timeZone}
                        row={row}
                        day={day}
                        label={`${rowName(row, catalog) || 'No project'} on ${formatDayColumn(day)}`}
                        entries={cellEntries(row, day)}
                        dayEntries={weekEntries.filter((entry) => dayKey(entry.start, timeZone) === day)}
                        seconds={rowTotal(row, cellEntries(row, day))}
                        isBillable={catalog.project(row.projectId)?.isBillable ?? false}
                        lockMessage={isDayLocked(day) ? lockMessage : null}
                        cannotAdd={Object.values(missing)[0] ?? null}
                      />
                    </TableCell>
                  ))}
                  <TableCell className="pr-4 text-right font-medium tabular-nums">{formatCell(rowTotal(row, weekEntries))}</TableCell>
                </TableRow>
              )
            })}
          </TableBody>
          <TableFooter>
            <TableRow>
              <TableCell className="pl-4 font-medium">Total</TableCell>
              {days.map((day) => (
                <TableCell key={day} className="text-center font-medium tabular-nums">
                  {formatCell(total(weekEntries.filter((entry) => dayKey(entry.start, timeZone) === day))) || '–'}
                </TableCell>
              ))}
              <TableCell className="pr-4 text-right font-semibold tabular-nums">{formatCell(total(weekEntries)) || '0:00'}</TableCell>
            </TableRow>
          </TableFooter>
        </Table>
      </div>

      <div>
        <ProjectPicker
          workspaceId={workspaceId}
          projectId={null}
          taskId={null}
          onChange={(projectId, taskId) => {
            const key = rowKey(projectId, taskId)
            setAddedRows((current) => (current.some((row) => row.key === key) ? current : [...current, { key, projectId, taskId }]))
          }}
        >
          <Button variant="outline">
            <Plus />
            Add row
          </Button>
        </ProjectPicker>
      </div>
    </div>
  )
}

interface TimesheetCellProps {
  workspaceId: string
  timeZone: string
  row: Row
  day: string
  label: string
  entries: TimeEntry[]
  dayEntries: TimeEntry[]
  seconds: number
  isBillable: boolean
  /** Why the day cannot be changed, if it is locked. */
  lockMessage: string | null
  /** Why time cannot be added here, if new entries would lack details the workspace requires. */
  cannotAdd: string | null
}

function TimesheetCell({ workspaceId, timeZone, row, day, label, entries, dayEntries, seconds, isBillable, lockMessage, cannotAdd }: TimesheetCellProps) {
  const create = useCreateEntry(workspaceId)
  const update = useUpdateEntry(workspaceId)
  const remove = useDeleteEntry(workspaceId)
  const [text, setText] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const running = entries.some((entry) => !entry.stop)

  async function commit() {
    const typed = text
    setText(null)
    if (typed === null || typed.trim() === formatCell(seconds)) {
      return
    }
    const target = typed.trim() === '' ? 0 : parseDuration(typed)
    if (target === null) {
      toast.error(`“${typed}” is not a duration. Try 1:30, 90m or 1.5.`)
      return
    }

    // Time in split entries stays as it is: changing it here would change the other projects' rows too.
    const split = entries.filter(isSplit)
    const fixed = split.reduce((sum, entry) => sum + (secondsByRow(entry, Date.now()).get(row.key) ?? 0), 0)
    if (target < fixed) {
      toast.error(`${formatCell(fixed)} of this is part of a split entry. Change the split on the Timer or Calendar page.`)
      return
    }
    const operations = planCellChange({
      rowEntries: entries.filter((entry) => !isSplit(entry)),
      dayEntries,
      day,
      timeZone,
      targetSeconds: target - fixed,
    })
    if (cannotAdd && operations.some((operation) => operation.kind === 'create')) {
      toast.error(`${cannotAdd} Add this time on the Timer or Calendar page instead.`)
      return
    }
    setSaving(true)
    try {
      for (const operation of operations) {
        if (operation.kind === 'create') {
          await create.mutateAsync({
            description: '',
            projectId: row.projectId,
            taskId: row.taskId,
            tagIds: [],
            isBillable,
            start: operation.start.toISOString(),
            stop: operation.stop.toISOString(),
          })
        } else if (operation.kind === 'shorten') {
          await update.mutateAsync({ id: operation.entry.id, body: { ...toRequest(operation.entry), stop: operation.stop.toISOString() } })
        } else {
          await remove.mutateAsync(operation.entry.id)
        }
      }
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't change the time."))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Input
      aria-label={label}
      value={text ?? formatCell(seconds)}
      placeholder="–"
      disabled={running || saving || !!lockMessage}
      title={lockMessage ?? (running ? 'A timer is running here. Stop it to change this day.' : undefined)}
      onFocus={(event) => {
        setText(formatCell(seconds))
        event.currentTarget.select()
      }}
      onChange={(event) => setText(event.target.value)}
      onBlur={() => void commit()}
      onKeyDown={(event) => {
        if (event.key === 'Enter') {
          event.currentTarget.blur()
        } else if (event.key === 'Escape') {
          setText(null)
          event.currentTarget.blur()
        }
      }}
      className="mx-auto h-8 w-16 text-center tabular-nums"
    />
  )
}

/** The project on the first line, its task and client below, so long names still fit the column. */
function RowLabel({ row, catalog }: { row: Row; catalog: Catalog }) {
  const project = catalog.project(row.projectId)
  if (!project) {
    return <span className="text-muted-foreground text-sm">No project</span>
  }
  const details = [catalog.task(row.taskId)?.name, catalog.client(project.clientId)?.name].filter(Boolean).join(' · ')
  return (
    <div className="grid min-w-0">
      <span className="flex min-w-0 items-center gap-1.5 text-sm font-medium">
        <ProjectDot color={project.color} />
        <span className="truncate">{project.name}</span>
      </span>
      {details && <span className="text-muted-foreground truncate pl-4 text-xs">{details}</span>}
    </div>
  )
}

function rowName(row: Row, catalog: Catalog): string {
  const project = catalog.project(row.projectId)
  if (!project) {
    return ''
  }
  const task = catalog.task(row.taskId)
  return task ? `${project.name} · ${task.name}` : project.name
}

/** A cell total as `h:mm`, empty for nothing tracked. */
function formatCell(seconds: number): string {
  const minutes = Math.round(seconds / 60)
  return minutes === 0 ? '' : `${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, '0')}`
}

function formatDayColumn(day: string): string {
  const [year, month, date] = day.split('-').map(Number)
  return new Intl.DateTimeFormat('en-GB', { weekday: 'short', day: 'numeric', timeZone: 'UTC' }).format(new Date(Date.UTC(year, month - 1, date)))
}
