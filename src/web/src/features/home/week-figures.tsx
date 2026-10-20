import { cn } from 'cn'
import { ArrowRight, Minus, TrendingDown, TrendingUp } from 'lucide-react'
import type { ReactNode } from 'react'
import { Card, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { formatHoursMinutes } from '@/lib/time/duration'
import type { Catalog } from '../catalog/use-catalog'
import { percent } from '../reports/charts'
import { runningLine, type WeekSummary } from './week'

/** The week before the one shown, to compare with. */
export interface WeekBefore {
  /** As it stood by this moment a week ago when the week shown is this one, or whole for a week gone by. */
  summary: WeekSummary
  /** Its whole days, for the line the week shown is drawn against. */
  dayTotals: number[]
  /** "by this time last week", or "the week before". */
  label: string
}

interface WeekFiguresProps {
  summary: WeekSummary | null
  before: WeekBefore | null
  /** The day the week has reached: today in this week, past its end for a week gone by. */
  today: string
  /** How far the week has got, in days (`weekMoment`): where its line ends and the week before is read by this time. */
  moment: number
  catalog: Catalog
  /** Agent time nobody tracked in the week, while activity sync is on, with its seconds on each day. */
  untracked: { seconds: number; stretches: number; days: number[] } | null
  onReview: () => void
}

/**
 * The week in a few numbers: time tracked, billable, divided between projects, and agent time with no entry. Each
 * carries a small picture of itself and, where it makes sense, how it compares with the week before. A change is
 * shown without praise or blame: more hours aren't better, fewer aren't worse. Totals are in hours and minutes, as
 * reports' are.
 */
export function WeekFigures({ summary, before, today, moment, catalog, untracked, onReview }: WeekFiguresProps) {
  const count = untracked ? 4 : 3
  const columns = cn('grid grid-cols-2 gap-3 sm:gap-4', count === 4 ? 'lg:grid-cols-4' : 'lg:grid-cols-3')

  if (!summary) {
    return (
      <div className={columns} aria-busy="true" aria-label="Loading the week">
        {Array.from({ length: count }, (_, index) => (
          <Skeleton key={index} className="h-[9.5rem]" />
        ))}
      </div>
    )
  }

  const days = summary.dayTotals.filter((seconds) => seconds > 0).length
  const previous = before && before.summary.seconds > 0 ? before : null
  const share = summary.seconds > 0 ? summary.billableSeconds / summary.seconds : null
  const previousShare = previous ? previous.summary.billableSeconds / previous.summary.seconds : null

  return (
    <div className={columns}>
      <Figure
        label="Tracked"
        value={formatHoursMinutes(summary.seconds)}
        change={previous && <Change seconds={summary.seconds - previous.summary.seconds} whole={previous.summary.seconds} against={previous.label} />}
        picture={<RunningTotal summary={summary} moment={moment} before={previous} />}
        note={
          previous
            ? `${formatHoursMinutes(previous.summary.seconds)} ${previous.label}`
            : days > 0
              ? `On ${days} ${days === 1 ? 'day' : 'days'}`
              : 'Nothing tracked yet'
        }
      />
      <Figure
        label="Billable"
        value={formatHoursMinutes(summary.billableSeconds)}
        change={share !== null && previousShare !== null && <PointsChange points={Math.round((share - previousShare) * 100)} against={previous!.label} />}
        picture={<Share share={share} before={previousShare} />}
        note={
          share === null
            ? 'Billable time shows here'
            : previousShare === null
              ? `${percent(summary.billableSeconds, summary.seconds)} of tracked time`
              : `${percent(summary.billableSeconds, summary.seconds)} of tracked time, ${Math.round(previousShare * 100)}% ${previous!.label}`
        }
      />
      <Figure
        label="Split between projects"
        value={formatHoursMinutes(summary.splitSeconds)}
        change={
          summary.splitSeconds > 0 && <span className="text-muted-foreground text-xs whitespace-nowrap">{percent(summary.splitSeconds, summary.seconds)} of tracked</span>
        }
        picture={<SplitMix summary={summary} catalog={catalog} />}
        note={
          summary.splitEntries > 0
            ? `In ${summary.splitEntries} ${summary.splitEntries === 1 ? 'entry' : 'entries'}, each part on its own project`
            : 'No entry split between projects'
        }
      />
      {untracked && (
        <Figure
          label="Agents at work, not tracked"
          value={formatHoursMinutes(untracked.seconds)}
          tone={untracked.seconds > 0 ? 'attention' : undefined}
          picture={<UntrackedDays seconds={untracked.days} days={summary.days} today={today} />}
          note={
            untracked.stretches > 0 ? (
              <span className="flex flex-wrap items-baseline justify-between gap-x-2">
                <span>
                  In {untracked.stretches} {untracked.stretches === 1 ? 'stretch' : 'stretches'} with no entry
                </span>
                <button type="button" onClick={onReview} className="text-primary-text inline-flex items-center gap-1 font-medium underline-offset-4 hover:underline">
                  Review
                  <ArrowRight className="size-3" aria-hidden="true" />
                </button>
              </span>
            ) : (
              'Every stretch of agent work has an entry'
            )
          }
        />
      )}
    </div>
  )
}

interface FigureProps {
  label: string
  value: string
  change?: ReactNode
  picture: ReactNode
  note: ReactNode
  tone?: 'attention'
}

function Figure({ label, value, change, picture, note, tone }: FigureProps) {
  return (
    <Card>
      <CardHeader className="gap-1.5">
        <CardDescription>{label}</CardDescription>
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <CardTitle className={cn('text-2xl tabular-nums', tone === 'attention' && 'text-primary-text')}>{value}</CardTitle>
          {change}
        </div>
        {/* The pictures share a height, so the notes line up across the row. */}
        <div className="flex h-9 items-end">{picture}</div>
        <div className="text-muted-foreground text-xs">{note}</div>
      </CardHeader>
    </Card>
  )
}

/** A change from the week before in hours and minutes, and as a share of it. */
function Change({ seconds, whole, against }: { seconds: number; whole: number; against: string }) {
  const minutes = Math.round(seconds / 60)
  const amount = formatHoursMinutes(Math.abs(minutes) * 60)
  const share = percent(Math.abs(seconds), whole)
  return (
    <Pill
      direction={minutes === 0 ? 0 : Math.sign(minutes)}
      text={minutes === 0 ? 'Same' : `${amount} · ${share}`}
      label={minutes === 0 ? `The same as ${against}` : `${amount} ${minutes > 0 ? 'more' : 'less'} than ${against}`}
    />
  )
}

/** A change in the billable share, in percentage points. */
function PointsChange({ points, against }: { points: number; against: string }) {
  const amount = `${Math.abs(points)} ${Math.abs(points) === 1 ? 'pt' : 'pts'}`
  return (
    <Pill
      direction={Math.sign(points)}
      text={points === 0 ? 'Same' : amount}
      label={points === 0 ? `The same share as ${against}` : `${Math.abs(points)} percentage points ${points > 0 ? 'more' : 'less'} than ${against}`}
    />
  )
}

function Pill({ direction, text, label }: { direction: number; text: string; label: string }) {
  const Icon = direction > 0 ? TrendingUp : direction < 0 ? TrendingDown : Minus
  return (
    <span className="bg-muted text-muted-foreground inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs tabular-nums" title={label}>
      <Icon className="size-3.5" aria-hidden="true" />
      <span aria-hidden="true">{text}</span>
      <span className="sr-only">{label}</span>
    </span>
  )
}

/**
 * The week's running total as a line, from nothing at its start to where it stands now, drawn over the week before's
 * as a dashed one. Both pass through this moment of the week, so the end of the line and the dashed one under it
 * compare what the numbers above do.
 */
function RunningTotal({ summary, moment, before }: { summary: WeekSummary; moment: number; before: WeekBefore | null }) {
  const length = summary.dayTotals.length
  const totals = moment > 0 ? runningLine(summary.dayTotals, { at: moment, seconds: summary.seconds }).filter(([at]) => at <= moment) : []
  const previous = before ? runningLine(before.dayTotals, { at: moment, seconds: before.summary.seconds }) : []
  const top = Math.max(1, ...[...totals, ...previous].map(([, seconds]) => seconds))
  const line = (points: [number, number][]) => points.map(([at, seconds]) => `${(at / length) * 100},${36 - (seconds / top) * 34}`).join(' ')
  const last = totals.at(-1)
  const end = last ? { left: (last[0] / length) * 100, bottom: (last[1] / top) * (34 / 36) * 100 } : null

  return (
    <div className="relative h-9 w-full" aria-hidden="true">
      <svg viewBox="0 0 100 36" preserveAspectRatio="none" className="absolute inset-0 size-full overflow-visible">
        <line x1="0" y1="36" x2="100" y2="36" className="stroke-border" strokeWidth="1" vectorEffect="non-scaling-stroke" />
        {previous.length > 0 && (
          <polyline points={line(previous)} fill="none" className="stroke-muted-foreground/70" strokeWidth="1.25" strokeDasharray="3 3" vectorEffect="non-scaling-stroke" />
        )}
        {totals.length > 0 && (
          <polyline points={line(totals)} fill="none" className="stroke-primary" strokeWidth="2" strokeLinejoin="round" vectorEffect="non-scaling-stroke" />
        )}
      </svg>
      {end && <span className="bg-primary absolute size-1.5 -translate-x-1/2 translate-y-1/2 rounded-full" style={{ left: `${end.left}%`, bottom: `${end.bottom}%` }} />}
    </div>
  )
}

/** The billable share as a bar, with a mark where the week before's stood. */
function Share({ share, before }: { share: number | null; before: number | null }) {
  return (
    <div className="relative mb-1 h-2 w-full" aria-hidden="true">
      <div className="bg-muted absolute inset-0 overflow-hidden rounded-full">
        <div className="bg-primary h-full rounded-full" style={{ width: `${(share ?? 0) * 100}%` }} />
      </div>
      {before !== null && <span className="bg-muted-foreground absolute -top-1 h-4 w-0.5 -translate-x-1/2 rounded-full" style={{ left: `${before * 100}%` }} />}
    </div>
  )
}

/** Projects that get a colour of their own in the split bar; the rest are folded together. */
const SHOWN_PROJECTS = 5

/** The time split between projects as a bar in their colours, each project's parts together. */
function SplitMix({ summary, catalog }: { summary: WeekSummary; catalog: Catalog }) {
  const projects = summary.projects.filter((project) => project.splitSeconds > 0).sort((a, b) => b.splitSeconds - a.splitSeconds)
  const shown = projects.slice(0, SHOWN_PROJECTS)
  const rest = projects.slice(SHOWN_PROJECTS).reduce((sum, project) => sum + project.splitSeconds, 0)
  const segments = [
    ...shown.map((project) => {
      const found = catalog.project(project.projectId)
      return { key: project.projectId ?? 'none', name: found?.name ?? 'No project', color: found?.color ?? 'var(--muted-foreground)', seconds: project.splitSeconds }
    }),
    ...(rest > 0 ? [{ key: 'other', name: 'Other projects', color: 'var(--chart-2)', seconds: rest }] : []),
  ]

  return (
    <div className="mb-1 flex h-2 w-full gap-0.5 overflow-hidden rounded-full" aria-hidden="true">
      {segments.length === 0 ? (
        <div className="bg-muted flex-1" />
      ) : (
        segments.map((segment) => (
          <div
            key={segment.key}
            className="h-full min-w-1"
            style={{ flexGrow: segment.seconds, backgroundColor: segment.color }}
            title={`${segment.name}: ${formatHoursMinutes(segment.seconds)}`}
          />
        ))
      )}
    </div>
  )
}

const weekday = new Intl.DateTimeFormat('en-GB', { weekday: 'short', timeZone: 'UTC' })

/** The week's days, marked where agents worked with no entry; days still to come are left open. */
function UntrackedDays({ seconds, days, today }: { seconds: number[]; days: readonly string[]; today: string }) {
  return (
    <div className="grid w-full grid-cols-7 gap-1" aria-hidden="true">
      {days.map((day, index) => {
        const name = weekday.format(new Date(`${day}T00:00:00Z`))
        return (
          <div
            key={day}
            className={cn(
              'h-5 rounded-sm',
              day > today ? 'border-border border border-dashed' : seconds[index] > 0 ? 'bg-primary' : 'bg-muted',
            )}
            title={day > today ? name : seconds[index] > 0 ? `${name}: ${formatHoursMinutes(seconds[index])}` : `${name}: nothing untracked`}
          />
        )
      })}
    </div>
  )
}
