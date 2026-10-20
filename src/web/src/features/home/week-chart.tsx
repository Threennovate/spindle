import { cn } from 'cn'
import { Skeleton } from '@/components/ui/skeleton'
import { formatDurationAs, formatHoursMinutes } from '@/lib/time/duration'
import { formatHourTick, hourTicks } from '../reports/charts'
import type { Catalog } from '../catalog/use-catalog'
import { useDurationFormat, useWorkspace } from '../workspace'
import type { ProjectWeek, WeekSummary } from './week'

/** Projects that get a colour of their own in the chart; the rest are "Other". */
const SHOWN_PROJECTS = 5

const NO_PROJECT = 'var(--muted-foreground)'
const OTHER = 'var(--chart-2)'

interface Series {
  key: string
  name: string
  color: string
  /** Seconds on each day of the week. */
  days: number[]
}

/** The week's projects as series, the biggest first, with the smallest folded into "Other". */
function weekSeries(projects: readonly ProjectWeek[], catalog: Catalog): Series[] {
  const shown = projects.slice(0, SHOWN_PROJECTS)
  const rest = projects.slice(SHOWN_PROJECTS)
  const series = shown.map((project) => {
    const found = catalog.project(project.projectId)
    return {
      key: project.projectId ?? 'none',
      name: found?.name ?? 'No project',
      color: found?.color ?? NO_PROJECT,
      days: project.days,
    }
  })
  if (rest.length > 0) {
    series.push({
      key: 'other',
      name: rest.length === 1 ? (catalog.project(rest[0].projectId)?.name ?? 'No project') : `${rest.length} other projects`,
      color: OTHER,
      days: rest[0].days.map((_, day) => rest.reduce((sum, project) => sum + project.days[day], 0)),
    })
  }
  return series
}

const weekday = new Intl.DateTimeFormat('en-GB', { weekday: 'short', timeZone: 'UTC' })
const longDay = new Intl.DateTimeFormat('en-GB', { weekday: 'long', day: 'numeric', month: 'short', timeZone: 'UTC' })
const asDate = (day: string) => new Date(`${day}T00:00:00Z`)

interface WeekChartProps {
  summary: WeekSummary | null
  catalog: Catalog
  today: string
  failed: boolean
  onRetry: () => void
}

/**
 * Time on each day of the week, stacked by project, each part of a split entry on its own project as reports count it.
 * Pointing at a day lists its projects; the projects card below says the same in words.
 */
export function WeekChart({ summary, catalog, today, failed, onRetry }: WeekChartProps) {
  const duration = useDurationFormat()

  return (
    <section aria-labelledby="week-chart-title" className="bg-card ring-foreground/10 grid min-w-0 flex-1 content-start gap-4 rounded-xl p-4 ring-1">
      <h3 id="week-chart-title" className="text-base font-medium">
        Hours per day
      </h3>
      {failed ? (
        <p className="text-muted-foreground text-sm">
          Couldn’t load the week.{' '}
          <button type="button" onClick={onRetry} className="text-primary-text font-medium underline-offset-4 hover:underline">
            Try again
          </button>
        </p>
      ) : !summary ? (
        <Skeleton className="h-56" />
      ) : (
        <Chart summary={summary} series={weekSeries(summary.projects, catalog)} today={today} duration={duration} />
      )}
    </section>
  )
}

function Chart({ summary, series, today, duration }: { summary: WeekSummary; series: Series[]; today: string; duration: (seconds: number) => string }) {
  const { durationFormat } = useWorkspace()
  // Seven totals side by side leave no room for seconds.
  const dayTotal = (seconds: number) => formatDurationAs(seconds, durationFormat === 'hms' ? 'hm' : durationFormat)
  const maxHours = Math.max(0, ...summary.dayTotals) / 3600
  // An empty week still shows an axis up to a working day, so the chart keeps its shape.
  const ticks = hourTicks(maxHours > 0 ? maxHours : 8)
  const top = ticks[ticks.length - 1]
  const empty = summary.seconds === 0
  const future = summary.days[0] > today
  const busiest = summary.dayTotals.reduce((best, seconds, day) => (seconds > summary.dayTotals[best] ? day : best), 0)

  return (
    <figure
      className="grid gap-3"
      role="img"
      aria-label={
        empty
          ? 'Chart of the week: no time tracked.'
          : `Chart of ${formatHoursMinutes(summary.seconds)} by day; the most on ${longDay.format(asDate(summary.days[busiest]))}, ${formatHoursMinutes(summary.dayTotals[busiest])}.`
      }
    >
      <div className="relative h-48 pl-12">
        {ticks.map((tick) => (
          <div key={tick} aria-hidden="true" className="absolute inset-x-0 flex items-center" style={{ bottom: `${(tick / top) * 100}%` }}>
            <span className="text-muted-foreground w-10 translate-y-1/2 pr-2 text-right text-[11px] whitespace-nowrap tabular-nums">{formatHourTick(tick)}</span>
            <span className={cn('h-px flex-1 translate-y-1/2', tick === 0 ? 'bg-border' : 'bg-border/60')} />
          </div>
        ))}
        <div className="absolute inset-y-0 right-0 left-12 grid grid-cols-7 items-end gap-2 sm:gap-4">
          {summary.days.map((day, index) => {
            const total = summary.dayTotals[index]
            const segments = series.filter((each) => each.days[index] > 0)
            return (
              <div key={day} className="group relative flex h-full flex-col justify-end">
                <div className="flex flex-col-reverse gap-0.5 overflow-hidden rounded-t-[4px]" style={{ height: `${(total / 3600 / top) * 100}%` }}>
                  {segments.map((each) => (
                    <div key={each.key} className="min-h-px shrink-0" style={{ flexGrow: each.days[index], flexBasis: 0, background: each.color }} />
                  ))}
                </div>
                {total > 0 && (
                  <div className="bg-popover text-popover-foreground pointer-events-none absolute bottom-full left-1/2 z-10 mb-1 hidden w-max max-w-56 -translate-x-1/2 rounded-md px-2 py-1.5 text-xs shadow-md ring-1 ring-black/5 group-hover:block">
                    <p className="font-medium">
                      {longDay.format(asDate(day))} · <span className="tabular-nums">{duration(total)}</span>
                    </p>
                    <ul className="mt-1 grid gap-0.5">
                      {segments.map((each) => (
                        <li key={each.key} className="flex items-center gap-1.5">
                          <span className="size-2 shrink-0 rounded-full" style={{ background: each.color }} />
                          <span className="min-w-0 flex-1 truncate">{each.name}</span>
                          <span className="text-muted-foreground tabular-nums">{duration(each.days[index])}</span>
                        </li>
                      ))}
                    </ul>
                  </div>
                )}
              </div>
            )
          })}
        </div>
        {empty && (
          <p className="text-muted-foreground absolute inset-x-12 top-1/3 text-center text-sm">
            {future ? 'This week hasn’t started yet.' : summary.days.includes(today) ? 'No time tracked this week yet.' : 'No time tracked in this week.'}
          </p>
        )}
      </div>
      <div aria-hidden="true" className="grid grid-cols-7 gap-2 pl-12 sm:gap-4">
        {summary.days.map((day, index) => (
          <span key={day} className={cn('text-muted-foreground text-center text-[11px] leading-4', day === today && 'text-foreground font-semibold')}>
            {day === today ? 'Today' : weekday.format(asDate(day))}
            <span className="text-foreground block font-medium tabular-nums">{summary.dayTotals[index] > 0 ? dayTotal(summary.dayTotals[index]) : '–'}</span>
          </span>
        ))}
      </div>
      {series.length > 0 && (
        <figcaption className="text-muted-foreground flex flex-wrap items-center gap-x-4 gap-y-1 text-xs">
          {series.map((each) => (
            <span key={each.key} className="flex min-w-0 items-center gap-1.5">
              <span className="size-2.5 shrink-0 rounded-full" style={{ background: each.color }} />
              <span className="truncate">{each.name}</span>
            </span>
          ))}
        </figcaption>
      )}
    </figure>
  )
}
