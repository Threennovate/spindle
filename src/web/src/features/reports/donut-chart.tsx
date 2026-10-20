import { formatHoursMinutes } from '@/lib/time/duration'

export interface DonutSlice {
  key: string
  label: string
  seconds: number
  color: string
}

const RADIUS = 40
const CIRCUMFERENCE = 2 * Math.PI * RADIUS
/** The biggest groups get a slice each; the rest share one. */
const MAX_SLICES = 8

/** How the time divides between groups, with the total in the middle. */
export function DonutChart({ slices, total }: { slices: DonutSlice[]; total: number }) {
  const shown = slices.length > MAX_SLICES ? [...slices.slice(0, MAX_SLICES - 1), other(slices.slice(MAX_SLICES - 1))] : slices
  const sum = shown.reduce((s, slice) => s + slice.seconds, 0)
  let offset = 0

  return (
    <figure className="relative mx-auto size-44 shrink-0" role="img" aria-label={`Share of ${formatHoursMinutes(total)}: ${describe(shown, sum)}`}>
      <svg viewBox="0 0 100 100" className="size-full -rotate-90">
        <circle cx="50" cy="50" r={RADIUS} fill="none" stroke="var(--muted)" strokeWidth="14" />
        {sum > 0 &&
          shown.map((slice) => {
            const length = (slice.seconds / sum) * CIRCUMFERENCE
            const circle = (
              <circle
                key={slice.key}
                cx="50"
                cy="50"
                r={RADIUS}
                fill="none"
                stroke={slice.color}
                strokeWidth="14"
                strokeDasharray={`${length} ${CIRCUMFERENCE - length}`}
                strokeDashoffset={-offset}
              >
                <title>{`${slice.label}: ${formatHoursMinutes(slice.seconds)}`}</title>
              </circle>
            )
            offset += length
            return circle
          })}
      </svg>
      <figcaption className="absolute inset-0 grid place-content-center text-center">
        <span className="text-lg font-semibold tabular-nums">{formatHoursMinutes(total)}</span>
        <span className="text-muted-foreground text-xs">total</span>
      </figcaption>
    </figure>
  )
}

function other(rest: DonutSlice[]): DonutSlice {
  return { key: 'other', label: 'Other', seconds: rest.reduce((s, slice) => s + slice.seconds, 0), color: 'var(--border)' }
}

function describe(slices: DonutSlice[], sum: number): string {
  return slices.map((slice) => `${slice.label} ${Math.round((slice.seconds / Math.max(sum, 1)) * 100)}%`).join(', ')
}
