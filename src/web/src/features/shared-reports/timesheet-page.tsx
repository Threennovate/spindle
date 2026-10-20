import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { ArrowUpRight, ChevronLeft, ChevronRight, Download, Loader2 } from 'lucide-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import { Logo } from '@/components/logo'
import { Button } from '@/components/ui/button'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Skeleton } from '@/components/ui/skeleton'
import { errorMessage } from '@/lib/error-message'
import { formatMoney } from '@/lib/money'
import { keepLight } from '@/lib/theme'
import { formatDurationAs } from '@/lib/time/duration'
import { addDaysToKey } from '@/lib/time/zone'
import { usePageTitle } from '@/lib/title'
import { formatDayRange } from '../reports/period'
import type { ExportFormat } from '../reports/queries'
import { downloadSharedTimesheet, sharedTimesheetQuery, UNAVAILABLE, type HourFormat, type SharedTimesheet, type SharedTimesheetDay } from './queries'

interface TimesheetPageProps {
  token: string
  step: number
  onStep: (step: number) => void
}

/**
 * The page is a document someone sends a client, so it wears the public site's colours (paper ground, white sheets,
 * midnight ink) and stays light in every browser: the sender and the client see the same thing, and it prints.
 */
const PAPER =
  '[--background:#F7F6F3] [--foreground:#0E1322] [--card:#FFFFFF] [--muted:#F4F3EF] [--muted-foreground:#5F6474] [--border:rgba(14,19,34,0.09)] [--ink-2:#454A59] [--hair-2:rgba(14,19,34,0.16)] [--marigold-soft:rgba(247,190,43,0.26)]'
const SHEET = 'bg-card rounded-[20px] shadow-[0_0_0_1px_var(--border)]'
const SPINDLE = 'https://spindletrack.com/?utm_source=timesheet&utm_medium=referral'

/** What a client sees at a timesheet link: the billed time, day by day. No account, nothing to change. */
export function TimesheetPage({ token, step, onStep }: TimesheetPageProps) {
  // The previous period stays on screen while the next one loads.
  const query = useQuery({ ...sharedTimesheetQuery(token, step), placeholderData: keepPreviousData })
  usePageTitle(query.data ? `${query.data.title} timesheet` : 'Timesheet')
  useEffect(() => keepLight(), [])

  if (query.isPending) {
    return (
      <Frame>
        <div className="grid gap-12" aria-busy="true" aria-label="Loading the timesheet">
          <div className="grid gap-5">
            <Skeleton className="h-5 w-40" />
            <Skeleton className="h-14 w-80 max-w-full" />
            <Skeleton className="h-7 w-56" />
          </div>
          <Skeleton className="h-64 rounded-[20px]" />
          <Skeleton className="h-96 rounded-[20px]" />
        </div>
      </Frame>
    )
  }

  if (query.isError) {
    const error = query.error
    if (error instanceof ApiError && error.problem?.type === UNAVAILABLE) {
      return <Message title="This timesheet isn't available right now" description="Ask whoever sent you the link when it will be back." />
    }
    const unknown = error instanceof ApiError && error.status === 404
    return (
      <Message
        title={unknown ? "This link doesn't work" : "Couldn't open the timesheet"}
        description={unknown ? 'It may have been replaced or removed. Ask whoever sent it for a new one.' : errorMessage(error, 'Try again in a moment.')}
      />
    )
  }

  return (
    <Frame>
      <Timesheet timesheet={query.data} token={token} loading={query.isPlaceholderData} onStep={onStep} />
    </Frame>
  )
}

function Frame({ children }: { children: React.ReactNode }) {
  return (
    <div className={`${PAPER} bg-background text-foreground flex min-h-svh flex-col [color-scheme:light] print:bg-white`}>
      <main className="mx-auto grid w-full max-w-[60rem] flex-1 grid-cols-[minmax(0,1fr)] content-start gap-12 px-4 pt-10 pb-20 sm:gap-14 sm:px-8 sm:pt-16 print:max-w-none print:p-0">
        {children}
      </main>
      <Colophon />
    </div>
  )
}

/**
 * Spindle's quiet line at the foot of the page: where the tool comes from, for a client who wonders. It never
 * competes with the timesheet above it.
 */
function Colophon() {
  return (
    <footer className="border-t print:border-0">
      <div className="mx-auto flex max-w-[60rem] flex-wrap items-center gap-x-6 gap-y-3 px-4 py-8 sm:px-8 print:px-0 print:py-4">
        <a href={SPINDLE} aria-label="Spindle" className="text-foreground max-sm:flex-1">
          <Logo />
        </a>
        <p className="text-muted-foreground min-w-0 flex-1 text-sm text-pretty max-sm:order-last max-sm:basis-full">The open-source time tracker for parallel work.</p>
        <a
          href={SPINDLE}
          className="text-foreground decoration-(--hair-2) hover:decoration-foreground inline-flex items-center gap-1 text-sm font-medium underline decoration-[1.5px] underline-offset-4 print:no-underline"
        >
          spindletrack.com
          <ArrowUpRight className="size-3.5 print:hidden" aria-hidden="true" />
        </a>
      </div>
    </footer>
  )
}

function Message({ title, description }: { title: string; description: string }) {
  return (
    <Frame>
      <div className="grid justify-items-start gap-3 py-[12vh]">
        <h1 className="text-[clamp(2rem,5vw,3rem)] leading-[1.05] font-[620] tracking-[-0.03em] text-balance">{title}</h1>
        <p className="text-(--ink-2) max-w-[32em] text-lg">{description}</p>
      </div>
    </Frame>
  )
}

function Timesheet({ timesheet, token, loading, onStep }: { timesheet: SharedTimesheet; token: string; loading: boolean; onStep: (step: number) => void }) {
  const showProjects = timesheet.projects.length > 1
  const [picked, setPicked] = useState<string | null>(null)
  const timer = useRef<number>(undefined)

  // A bar in the strip takes the reader to that day's rows, and marks them for a moment.
  function pick(day: string) {
    document.getElementById(`day-${day}`)?.scrollIntoView({ behavior: 'smooth', block: 'center' })
    setPicked(day)
    window.clearTimeout(timer.current)
    timer.current = window.setTimeout(() => setPicked(null), 1600)
  }
  useEffect(() => () => window.clearTimeout(timer.current), [])

  return (
    <>
      <header className="grid gap-7 sm:gap-9">
        <div className="flex items-center justify-between gap-4">
          <p className="min-w-0 truncate text-[15px]">
            <span className="font-semibold">{timesheet.workspace}</span>
            <span className="text-muted-foreground"> · Timesheet</span>
          </p>
          <DownloadMenu token={token} step={timesheet.step} />
        </div>
        <div className="grid gap-4">
          <h1 className="text-[clamp(2.5rem,7.2vw,4.5rem)] leading-[0.98] font-[640] tracking-[-0.037em] text-balance">{timesheet.title}</h1>
          <Period timesheet={timesheet} loading={loading} onStep={onStep} />
        </div>
      </header>

      <div className={`grid grid-cols-[minmax(0,1fr)] gap-12 transition-opacity duration-200 sm:gap-14 ${loading ? 'opacity-50' : ''}`}>
        <Summary timesheet={timesheet} onPick={pick} />
        {showProjects && <Projects timesheet={timesheet} />}
        <Days timesheet={timesheet} showProjects={showProjects} picked={picked} />
      </div>
    </>
  )
}

/** The period, and arrows to the ones before it on a link whose period moves. */
function Period({ timesheet, loading, onStep }: { timesheet: SharedTimesheet; loading: boolean; onStep: (step: number) => void }) {
  const moves = timesheet.canStepBack || timesheet.step < 0
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
      {moves && (
        <div className="flex items-center gap-1.5 print:hidden">
          <StepButton label="Earlier period" disabled={!timesheet.canStepBack || loading} onClick={() => onStep(timesheet.step - 1)}>
            <ChevronLeft />
          </StepButton>
          <StepButton label="Later period" disabled={timesheet.step >= 0 || loading} onClick={() => onStep(timesheet.step + 1)}>
            <ChevronRight />
          </StepButton>
        </div>
      )}
      <p className="text-(--ink-2) text-xl font-medium tracking-[-0.01em] sm:text-2xl" aria-live="polite">
        {periodLabel(timesheet.from, timesheet.to)}
      </p>
      {loading && <Loader2 className="text-muted-foreground size-4 animate-spin" aria-label="Loading" />}
    </div>
  )
}

function StepButton({ label, disabled, onClick, children }: { label: string; disabled: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      aria-label={label}
      disabled={disabled}
      onClick={onClick}
      className="bg-card hover:bg-muted grid size-9 place-items-center rounded-full shadow-[inset_0_0_0_1px_var(--hair-2)] transition-colors disabled:opacity-35 [&_svg]:size-4"
    >
      {children}
    </button>
  )
}

/** The totals, and the period as a strip of days, one bar for each, as tall as the time billed on it. */
function Summary({ timesheet, onPick }: { timesheet: SharedTimesheet; onPick: (day: string) => void }) {
  const days = new Set(timesheet.days.map((day) => day.day)).size
  return (
    <section aria-label="Summary" className={`${SHEET} grid grid-cols-[minmax(0,1fr)] gap-9 p-6 sm:p-9 print:shadow-none`}>
      <dl className="grid grid-cols-2 gap-x-10 gap-y-7 sm:flex sm:gap-x-16">
        <Figure label={timesheet.hourFormat === 'decimal' ? 'Hours' : 'Time'} value={formatHours(timesheet.seconds, timesheet.hourFormat)} />
        <Figure label={days === 1 ? 'Day' : 'Days'} value={String(days)} />
        {timesheet.amount != null && <Figure label="Amount" value={formatMoney(timesheet.amount, timesheet.currency)} />}
      </dl>
      <DayStrip timesheet={timesheet} onPick={onPick} />
    </section>
  )
}

function Figure({ label, value }: { label: string; value: string }) {
  return (
    <div className="grid content-start gap-1.5">
      <dt className="text-muted-foreground text-sm">{label}</dt>
      <dd className="text-[clamp(2rem,5vw,2.75rem)] leading-none font-[620] tracking-[-0.03em] tabular-nums">{value}</dd>
    </div>
  )
}

/**
 * One column a day, in its project's colour; with several projects a bar is cut by thin seams into theirs, like a
 * split entry. Days without time keep a hairline, so the rhythm of the period still reads.
 * Long fixed periods skip the strip: the table says it better.
 */
function DayStrip({ timesheet, onPick }: { timesheet: SharedTimesheet; onPick: (day: string) => void }) {
  const [hovered, setHovered] = useState<string | null>(null)
  const columns = useMemo(() => stripColumns(timesheet), [timesheet])
  if (columns.length < 2 || columns.length > 62) {
    return null
  }

  const max = Math.max(...columns.map((column) => column.seconds), 1)
  // Days not counted yet: those still to come, and today when the link leaves it out.
  const today = todayKey()
  const counted = (key: string) => key <= today && key <= timesheet.countedTo
  const labelled = (index: number, key: string) => columns.length <= 31 || weekday(key) === 1 || index === 0
  return (
    <div className="grid grid-cols-[minmax(0,1fr)] gap-2 print:hidden" onMouseLeave={() => setHovered(null)}>
      <div className="flex h-28 items-end gap-[3px] sm:h-32 sm:gap-1">
        {columns.map((column) => {
          const height = column.seconds > 0 ? Math.max(6, (column.seconds / max) * 100) : 0
          const active = hovered === column.key
          return (
            <div key={column.key} className="relative flex h-full min-w-0 flex-1 items-end justify-center">
              {column.seconds > 0 ? (
                <button
                  type="button"
                  aria-label={`${formatDay(column.key, true)}, ${formatHours(column.seconds, timesheet.hourFormat)}`}
                  onClick={() => onPick(column.key)}
                  onMouseEnter={() => setHovered(column.key)}
                  onFocus={() => setHovered(column.key)}
                  onBlur={() => setHovered(null)}
                  className="flex w-full max-w-4 flex-col-reverse gap-px overflow-hidden rounded-[4px] outline-offset-2 transition-[height,opacity] duration-500 ease-out"
                  style={{ height: `${height}%`, opacity: hovered && !active ? 0.45 : 1 }}
                >
                  {column.parts.map((part) => (
                    <span key={part.color} className="min-h-[2px] w-full" style={{ flexGrow: part.seconds, backgroundColor: part.color }} />
                  ))}
                </button>
              ) : (
                <span aria-hidden="true" className={`h-[2px] w-full max-w-4 rounded-full ${counted(column.key) ? 'bg-(--hair-2)' : 'bg-border'}`} />
              )}
              {active && (
                <span
                  className="bg-foreground text-background pointer-events-none absolute z-10 rounded-md px-2 py-1 text-xs font-medium whitespace-nowrap tabular-nums shadow-lg"
                  style={{ bottom: `calc(${height}% + 8px)` }}
                >
                  {formatDay(column.key)} · {formatHours(column.seconds, timesheet.hourFormat)}
                </span>
              )}
            </div>
          )
        })}
      </div>
      <div aria-hidden="true" className="flex gap-[3px] sm:gap-1">
        {columns.map((column, index) => (
          <span
            key={column.key}
            className={`min-w-0 flex-1 text-center text-[11px] tabular-nums ${isWeekend(column.key) ? 'text-muted-foreground/55' : 'text-muted-foreground'} ${
              labelled(index, column.key) ? '' : 'invisible'
            } ${weekday(column.key) === 1 || index === 0 ? '' : 'max-sm:invisible'}`}
          >
            {Number(column.key.slice(8))}
          </span>
        ))}
      </div>
    </div>
  )
}

/** Each project's share of the time, as one bar cut by thin seams, and the projects under it. */
function Projects({ timesheet }: { timesheet: SharedTimesheet }) {
  const showAmounts = timesheet.amount != null
  const total = Math.max(timesheet.seconds, 1)
  return (
    <section aria-labelledby="projects-heading" className="grid grid-cols-[minmax(0,1fr)] gap-5">
      <h2 id="projects-heading" className="text-xl font-semibold tracking-[-0.015em]">
        By project
      </h2>
      <div className={`${SHEET} grid grid-cols-[minmax(0,1fr)] gap-2 p-5 sm:p-7 print:shadow-none`}>
        <div aria-hidden="true" className="flex h-11 gap-[3px] overflow-hidden rounded-[10px]">
          {timesheet.projects.map((project) => {
            const share = project.seconds / total
            return (
              <span
                key={`${project.name}-${project.color}`}
                className="flex min-w-1 items-center overflow-hidden px-3 text-[13px] font-semibold whitespace-nowrap"
                style={{ flexGrow: project.seconds, flexBasis: 0, ...tone(project.color) }}
              >
                {share >= 0.14 && <span className="truncate">{project.name}</span>}
              </span>
            )
          })}
        </div>
        <ul className="mt-3">
          {timesheet.projects.map((project) => (
            <li key={`${project.name}-${project.color}`} className="flex items-center gap-3 border-t py-3.5 text-[15px] first:border-t-0">
              <ProjectDot color={project.color} />
              <span className="min-w-0 flex-1 truncate">
                <span className="font-medium">{project.name}</span>
                {project.client && <span className="text-muted-foreground"> · {project.client}</span>}
              </span>
              <span className="text-muted-foreground hidden w-12 text-right text-sm tabular-nums sm:block">{Math.round((project.seconds / total) * 100)}%</span>
              <span className="w-14 text-right font-semibold tabular-nums sm:w-16">{formatHours(project.seconds, timesheet.hourFormat)}</span>
              {showAmounts && <span className="text-(--ink-2) w-20 text-right tabular-nums sm:w-24">{formatMoney(project.amount ?? 0, timesheet.currency)}</span>}
            </li>
          ))}
        </ul>
      </div>
    </section>
  )
}

/**
 * The days as a ledger on a white sheet: the date once, on a day's first row, what was done, and the time. On a phone
 * the date and time share a line and what was done runs under them.
 */
function Days({ timesheet, showProjects, picked }: { timesheet: SharedTimesheet; showProjects: boolean; picked: string | null }) {
  const showAmounts = timesheet.amount != null
  const hasMiddle = showProjects || timesheet.showPeople
  // Static class names, so Tailwind finds them: date, project or person, what was done, time, amount.
  const columns = hasMiddle
    ? showAmounts
      ? 'sm:grid-cols-[7.5rem_10rem_minmax(0,1fr)_4.5rem_6rem]'
      : 'sm:grid-cols-[7.5rem_10rem_minmax(0,1fr)_4.5rem]'
    : showAmounts
      ? 'sm:grid-cols-[7.5rem_minmax(0,1fr)_4.5rem_6rem]'
      : 'sm:grid-cols-[7.5rem_minmax(0,1fr)_4.5rem]'
  const grid = `grid grid-cols-[minmax(0,1fr)_auto] gap-x-6 gap-y-1.5 px-5 sm:px-7 ${columns}`
  const timeLabel = timesheet.hourFormat === 'decimal' ? 'Hours' : 'Time'

  return (
    <section aria-labelledby="days-heading" className="grid grid-cols-[minmax(0,1fr)] gap-5">
      <h2 id="days-heading" className="text-xl font-semibold tracking-[-0.015em]">
        Day by day
      </h2>
      {timesheet.days.length === 0 ? (
        <p className={`${SHEET} text-muted-foreground px-6 py-14 text-center`}>No time billed in {periodLabel(timesheet.from, timesheet.to)}.</p>
      ) : (
        <div role="table" aria-label="Billed time by day" className={`${SHEET} overflow-hidden print:shadow-none`}>
          <div role="row" className={`${grid} text-muted-foreground hidden py-3.5 text-[12.5px] sm:grid`}>
            <span role="columnheader">Date</span>
            {hasMiddle && <span role="columnheader">{showProjects && timesheet.showPeople ? 'Project and person' : showProjects ? 'Project' : 'Person'}</span>}
            <span role="columnheader">What was done</span>
            <span role="columnheader" className="text-right">
              {timeLabel}
            </span>
            {showAmounts && (
              <span role="columnheader" className="text-right">
                Amount
              </span>
            )}
          </div>
          {timesheet.days.map((day, index) => {
            const firstOfDay = index === 0 || timesheet.days[index - 1].day !== day.day
            const lastOfDay = index === timesheet.days.length - 1 || timesheet.days[index + 1].day !== day.day
            // On a phone the time sits at the end of the row's first line, with the amount under it.
            const mobileTime = (
              <span role="cell" className="grid justify-items-end text-right tabular-nums sm:hidden">
                <span className="text-[15px] font-semibold">{formatHours(day.seconds, timesheet.hourFormat)}</span>
                {showAmounts && <span className="text-muted-foreground text-[13px]">{formatMoney(day.amount ?? 0, timesheet.currency)}</span>}
              </span>
            )
            return (
              <div
                role="row"
                id={firstOfDay ? `day-${day.day}` : undefined}
                key={`${day.day}-${day.project}-${day.person ?? ''}-${index}`}
                className={`${grid} break-inside-avoid scroll-mt-24 transition-colors duration-700 ${firstOfDay ? 'border-t pt-4' : 'pt-1'} ${lastOfDay ? 'pb-4' : 'pb-1'} ${
                  picked === day.day ? 'bg-(--marigold-soft) duration-150' : ''
                }`}
              >
                <span role="cell" className={firstOfDay ? (hasMiddle ? 'max-sm:col-span-2' : '') : 'max-sm:hidden sm:invisible'}>
                  <span className="block text-[15px] font-semibold tabular-nums">{formatDay(day.day)}</span>
                  <span className="text-muted-foreground block text-[13px] max-sm:hidden">{formatWeekday(day.day)}</span>
                </span>
                {!hasMiddle && mobileTime}
                {hasMiddle && (
                  <span role="cell" className="flex min-w-0 items-start gap-2 text-sm max-sm:pt-0.5 sm:pt-0.5">
                    {showProjects && <ProjectDot color={day.projectColor} className="mt-[5px]" />}
                    <span className="min-w-0">
                      {showProjects && <span className="block truncate font-medium">{day.project}</span>}
                      {day.person && <span className="text-muted-foreground block truncate">{day.person}</span>}
                    </span>
                  </span>
                )}
                {hasMiddle && mobileTime}
                <span role="cell" className="text-(--ink-2) col-span-2 text-[15px] leading-relaxed text-pretty sm:col-span-1">
                  <Descriptions day={day} />
                </span>
                <span role="cell" className="hidden text-right text-[15px] font-semibold tabular-nums sm:block">
                  {formatHours(day.seconds, timesheet.hourFormat)}
                </span>
                {showAmounts && (
                  <span role="cell" className="text-muted-foreground hidden pt-0.5 text-right text-sm tabular-nums sm:block">
                    {formatMoney(day.amount ?? 0, timesheet.currency)}
                  </span>
                )}
              </div>
            )
          })}
          <div role="row" className={`${grid} bg-muted border-(--hair-2) border-t-[1.5px] py-4 text-[15px] font-semibold`}>
            <span role="cell">Total</span>
            {hasMiddle && <span role="cell" className="hidden sm:block" />}
            <span role="cell" className="hidden sm:block" />
            <span role="cell" className="text-right tabular-nums">
              {formatHours(timesheet.seconds, timesheet.hourFormat)}
            </span>
            {showAmounts && (
              <span role="cell" className="col-span-2 tabular-nums max-sm:text-right sm:col-span-1 sm:text-right">
                {formatMoney(timesheet.amount ?? 0, timesheet.currency)}
              </span>
            )}
          </div>
        </div>
      )}
      <p className="text-muted-foreground text-[13px]">
        <span className="print:hidden">
          Read-only. It updates as time is tracked{timesheet.countedTo < timesheet.to && timesheet.step === 0 ? ', and today’s time shows from tomorrow' : ''}.
        </span>
        <span className="hidden print:inline">As of {formatToday()}. The timesheet at the link updates as time is tracked.</span>
      </p>
    </section>
  )
}

function Descriptions({ day }: { day: SharedTimesheetDay }) {
  if (day.descriptions.length === 0) {
    return <span className="text-muted-foreground">—</span>
  }
  if (day.descriptions.length === 1) {
    return <p className="whitespace-pre-line">{day.descriptions[0]}</p>
  }
  return (
    <ul className="grid gap-1">
      {day.descriptions.map((description) => (
        <li key={description} className="before:bg-(--hair-2) relative pl-3.5 whitespace-pre-line before:absolute before:top-[0.8em] before:left-0 before:h-px before:w-2">
          {description}
        </li>
      ))}
    </ul>
  )
}

function DownloadMenu({ token, step }: { token: string; step: number }) {
  const [pending, setPending] = useState<ExportFormat | null>(null)

  async function run(format: ExportFormat) {
    setPending(format)
    try {
      await downloadSharedTimesheet(token, step, format)
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't download the timesheet."))
    } finally {
      setPending(null)
    }
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          variant="outline"
          disabled={pending !== null}
          className="bg-card hover:bg-muted h-9 rounded-[10px] border-0 px-3.5 shadow-[inset_0_0_0_1px_var(--hair-2)] print:hidden"
        >
          {pending ? <Loader2 className="animate-spin" /> : <Download />}
          Download
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuItem onSelect={() => void run('pdf')}>PDF</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => void run('xlsx')}>Excel (.xlsx)</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => void run('csv')}>CSV</DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

function ProjectDot({ color, className = '' }: { color: string | null | undefined; className?: string }) {
  return <span aria-hidden="true" className={`size-2.5 shrink-0 rounded-full ${className}`} style={{ backgroundColor: color ?? 'var(--muted-foreground)' }} />
}

interface StripColumn {
  key: string
  seconds: number
  parts: { color: string; seconds: number }[]
}

/** Every day of the period, with its time and, by project colour, what it was made of. */
function stripColumns(timesheet: SharedTimesheet): StripColumn[] {
  const byDay = new Map<string, Map<string, number>>()
  for (const row of timesheet.days) {
    const parts = byDay.get(row.day) ?? new Map<string, number>()
    const color = row.projectColor ?? '#5F6474'
    parts.set(color, (parts.get(color) ?? 0) + row.seconds)
    byDay.set(row.day, parts)
  }
  // The projects keep one order in every bar: the order of the list under it.
  const order = timesheet.projects.map((project) => project.color ?? '#5F6474')
  const columns: StripColumn[] = []
  for (let key = timesheet.from; key <= timesheet.to && columns.length <= 62; key = addDaysToKey(key, 1)) {
    const parts = [...(byDay.get(key) ?? new Map<string, number>())]
      .filter(([, seconds]) => seconds > 0)
      .sort(([a], [b]) => order.indexOf(a) - order.indexOf(b))
      .map(([color, seconds]) => ({ color, seconds }))
    columns.push({ key, seconds: parts.reduce((sum, part) => sum + part.seconds, 0), parts })
  }
  return columns
}

/** A project's colour as a band: a pale fill with the name in a deep shade of the same colour. */
function tone(color: string | null | undefined): React.CSSProperties {
  const base = color ?? '#5F6474'
  return { backgroundColor: `color-mix(in srgb, ${base} 20%, white)`, color: `color-mix(in srgb, ${base} 62%, #0E1322)` }
}

/** `3:40` or `3.67`, as the link was set up. */
function formatHours(seconds: number, format: HourFormat): string {
  return format === 'decimal' ? (Math.max(0, seconds) / 3600).toFixed(2) : formatDurationAs(seconds, 'hm')
}

/** "September 2026" for a whole month, otherwise the range. */
function periodLabel(from: string, to: string): string {
  if (from.endsWith('-01') && addDaysToKey(to, 1).endsWith('-01') && from.slice(0, 7) === to.slice(0, 7)) {
    return format(from, { month: 'long', year: 'numeric' })
  }
  return formatDayRange({ from, to })
}

/** "14 Sept", or with the weekday "Mon 14 Sept". */
function formatDay(key: string, withWeekday = false): string {
  return format(key, withWeekday ? { weekday: 'short', day: 'numeric', month: 'short' } : { day: 'numeric', month: 'short' })
}

/** "Monday". */
function formatWeekday(key: string): string {
  return format(key, { weekday: 'long' })
}

/** Today in this browser, as a day key; the days after it in a running period have not happened yet. */
function todayKey(): string {
  const now = new Date()
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`
}

function formatToday(): string {
  return new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'long', year: 'numeric' }).format(new Date())
}

function format(key: string, options: Intl.DateTimeFormatOptions): string {
  return new Intl.DateTimeFormat('en-GB', { ...options, timeZone: 'UTC' }).format(new Date(`${key}T00:00:00Z`))
}

/** 0 for Sunday to 6 for Saturday. */
function weekday(key: string): number {
  return new Date(`${key}T00:00:00Z`).getUTCDay()
}

function isWeekend(key: string): boolean {
  const day = weekday(key)
  return day === 0 || day === 6
}
