import { cn } from 'cn'
import { formatHoursMinutes } from '@/lib/time/duration'
import { useDurationFormat } from '../workspace'
import { formatHourTick, hourTicks } from './charts'
import type { SummaryReport } from './queries'

type Bucket = SummaryReport['buckets'][number]
type Resolution = SummaryReport['resolution']

const format = (day: string, options: Intl.DateTimeFormatOptions) =>
  new Intl.DateTimeFormat('en-GB', { ...options, timeZone: 'UTC' }).format(new Date(`${day}T00:00:00Z`))

/** Time per day, week or month as bars, the billable part in full color. */
export function BarChart({ buckets, resolution }: { buckets: Bucket[]; resolution: Resolution }) {
  const duration = useDurationFormat()
  const maxHours = Math.max(0, ...buckets.map((b) => b.seconds)) / 3600
  const ticks = hourTicks(maxHours)
  const top = ticks[ticks.length - 1]
  // Crowded axes label every few bars.
  const every = Math.ceil(buckets.length / (resolution === 'day' && buckets.length <= 7 ? 7 : 16))
  const multiYear = new Set(buckets.map((b) => b.start.slice(0, 4))).size > 1
  const total = buckets.reduce((sum, b) => sum + b.seconds, 0)
  const busiest = buckets.reduce<Bucket | null>((best, b) => (b.seconds > (best?.seconds ?? 0) ? b : best), null)

  return (
    <figure
      className="grid gap-2"
      role="img"
      aria-label={
        busiest
          ? `Chart of ${formatHoursMinutes(total)} by ${resolution}; the most on ${bucketTitle(busiest.start, resolution)}, ${formatHoursMinutes(busiest.seconds)}.`
          : `Chart by ${resolution}: no time tracked.`
      }
    >
      <div className="relative h-52 pl-14">
        {ticks.map((tick) => (
          <div key={tick} aria-hidden="true" className="absolute inset-x-0 flex items-center" style={{ bottom: `${(tick / top) * 100}%` }}>
            <span className="text-muted-foreground w-12 translate-y-1/2 pr-2 text-right text-[11px] whitespace-nowrap tabular-nums">{formatHourTick(tick)}</span>
            <span className={cn('h-px flex-1 translate-y-1/2', tick === 0 ? 'bg-border' : 'bg-border/60')} />
          </div>
        ))}
        <div className="absolute inset-y-0 right-0 left-14 flex items-end gap-[3px] sm:gap-1.5">
          {buckets.map((bucket) => {
            const billable = (bucket.billableSeconds / 3600 / top) * 100
            const other = ((bucket.seconds - bucket.billableSeconds) / 3600 / top) * 100
            return (
              <div key={bucket.start} className="group relative flex h-full max-w-16 flex-1 flex-col justify-end">
                <div className="bg-primary/30 rounded-t-[3px]" style={{ height: `${other}%` }} />
                <div className={cn('bg-primary', other === 0 && 'rounded-t-[3px]')} style={{ height: `${billable}%` }} />
                {bucket.seconds > 0 && (
                  <div className="bg-popover text-popover-foreground pointer-events-none absolute bottom-full left-1/2 z-10 mb-1 hidden w-max -translate-x-1/2 rounded-md px-2 py-1 text-xs shadow-md ring-1 ring-black/5 group-hover:block">
                    <p className="font-medium">{bucketTitle(bucket.start, resolution)}</p>
                    <p className="tabular-nums">
                      {duration(bucket.seconds)}
                      {bucket.billableSeconds > 0 && <span className="text-muted-foreground"> · {duration(bucket.billableSeconds)} billable</span>}
                    </p>
                  </div>
                )}
              </div>
            )
          })}
        </div>
      </div>
      <div aria-hidden="true" className="flex gap-[3px] pl-14 sm:gap-1.5">
        {buckets.map((bucket, index) => (
          <span key={bucket.start} className="text-muted-foreground max-w-16 flex-1 truncate text-center text-[11px]">
            {index % every === 0 && <AxisLabel start={bucket.start} resolution={resolution} count={buckets.length} multiYear={multiYear} />}
          </span>
        ))}
      </div>
      <figcaption className="text-muted-foreground flex items-center justify-end gap-4 text-xs">
        <span className="flex items-center gap-1.5">
          <span className="bg-primary size-2.5 rounded-sm" /> Billable
        </span>
        <span className="flex items-center gap-1.5">
          <span className="bg-primary/30 size-2.5 rounded-sm" /> Non-billable
        </span>
      </figcaption>
    </figure>
  )
}

function AxisLabel({ start, resolution, count, multiYear }: { start: string; resolution: Resolution; count: number; multiYear: boolean }) {
  if (resolution === 'month') {
    return format(start, multiYear ? { month: 'short', year: '2-digit' } : { month: 'short' })
  }
  if (resolution === 'week') {
    return format(start, { day: 'numeric', month: 'short' })
  }
  // A week's days name their weekday where there is room for it.
  return count <= 7 ? (
    <>
      <span className="hidden sm:inline">{format(start, { weekday: 'short' })} </span>
      {format(start, { day: 'numeric' })}
    </>
  ) : (
    format(start, { day: 'numeric' })
  )
}

function bucketTitle(start: string, resolution: Resolution): string {
  if (resolution === 'month') {
    return format(start, { month: 'long', year: 'numeric' })
  }
  if (resolution === 'week') {
    return `Week of ${format(start, { day: 'numeric', month: 'short', year: 'numeric' })}`
  }
  return format(start, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' })
}
