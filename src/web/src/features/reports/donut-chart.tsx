import { formatHoursMinutes } from '@/lib/time/duration'

export interface DonutSlice {
  key: string
  label: string
  seconds: number
  color: string
}

const RADIUS = 40
/** The biggest groups get a slice each; the rest share one. */
const MAX_SLICES = 8

/** How the time divides between groups, with the total in the middle. */
export function DonutChart({ slices, total }: { slices: DonutSlice[]; total: number }) {
  const shown = slices.length > MAX_SLICES ? [...slices.slice(0, MAX_SLICES - 1), other(slices.slice(MAX_SLICES - 1))] : slices
  const sum = shown.reduce((s, slice) => s + slice.seconds, 0)
  let start = 0

  return (
    <figure className="relative mx-auto size-44 shrink-0" role="img" aria-label={`Share of ${formatHoursMinutes(total)}: ${describe(shown, sum)}`}>
      <svg viewBox="0 0 100 100" className="size-full">
        <circle cx="50" cy="50" r={RADIUS} fill="none" stroke="var(--muted)" strokeWidth="14" />
        {sum > 0 &&
          shown.map((slice) => {
            const end = start + slice.seconds / sum
            const title = <title>{`${slice.label}: ${formatHoursMinutes(slice.seconds)}`}</title>
            // An arc per slice: dashes on one circle leave a stray stroke join where the circle closes.
            const shape =
              end - start >= 1 ? (
                <circle key={slice.key} cx="50" cy="50" r={RADIUS} fill="none" stroke={slice.color} strokeWidth="14">
                  {title}
                </circle>
              ) : (
                <path key={slice.key} d={arc(start, end)} fill="none" stroke={slice.color} strokeWidth="14">
                  {title}
                </path>
              )
            start = end
            return shape
          })}
      </svg>
      <figcaption className="absolute inset-0 grid place-content-center text-center">
        <span className="text-lg font-semibold tabular-nums">{formatHoursMinutes(total)}</span>
        <span className="text-muted-foreground text-xs">total</span>
      </figcaption>
    </figure>
  )
}

/** The arc from one share of the circle to another, clockwise from the top. */
function arc(from: number, to: number): string {
  const [x0, y0] = point(from)
  const [x1, y1] = point(to)
  return `M ${x0} ${y0} A ${RADIUS} ${RADIUS} 0 ${to - from > 0.5 ? 1 : 0} 1 ${x1} ${y1}`
}

function point(share: number): [number, number] {
  const angle = share * 2 * Math.PI
  return [50 + RADIUS * Math.sin(angle), 50 - RADIUS * Math.cos(angle)]
}

function other(rest: DonutSlice[]): DonutSlice {
  return { key: 'other', label: 'Other', seconds: rest.reduce((s, slice) => s + slice.seconds, 0), color: 'var(--border)' }
}

function describe(slices: DonutSlice[], sum: number): string {
  return slices.map((slice) => `${slice.label} ${Math.round((slice.seconds / Math.max(sum, 1)) * 100)}%`).join(', ')
}
