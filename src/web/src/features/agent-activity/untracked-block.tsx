import { Sparkles, X } from 'lucide-react'
import type { UntrackedTime } from '@/api/types'
import { formatClock } from '@/lib/time/zone'
import type { Catalog } from '../catalog/use-catalog'

interface GhostProps {
  block: UntrackedTime
  catalog: Catalog
  timeZone: string
  hour12: boolean
  top: number
  height: number
  onOpen: () => void
  onDismiss: () => void
}

/**
 * "✦ Atlas · 14:00–15:30": agents were at work and nothing was tracked. No fill and a hatched edge in the project's
 * colour, unlike the dashed outline of time being drawn. A click opens a new entry for it; × dismisses it.
 */
export function UntrackedBlock({ block, catalog, timeZone, hour12, top, height, onOpen, onDismiss }: GhostProps) {
  const projects = block.projectIds.map((id) => catalog.project(id)).filter((project) => project !== undefined)
  const color = projects[0]?.color ?? 'var(--muted-foreground)'
  const names = projects.length > 0 ? projects.slice(0, 3).map((project) => project.name).join(', ') : 'Not tracked'
  const times = `${formatClock(block.start, timeZone, hour12)}–${formatClock(block.end, timeZone, hour12)}`
  const short = height < 24

  return (
    <div data-untracked className="group/ghost absolute inset-x-1 rounded-md" style={{ top, height }}>
      {/* A 4px band of diagonal hatching inside the outline, with nothing filled in. */}
      <span
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 rounded-md p-1"
        style={{
          background: `repeating-linear-gradient(-45deg, color-mix(in oklab, ${color} 70%, transparent) 0 1px, transparent 1px 5px)`,
          mask: 'linear-gradient(#000 0 0) content-box exclude, linear-gradient(#000 0 0)',
        }}
      />
      <button
        type="button"
        onClick={onOpen}
        title={`Agents were at work ${times} with nothing tracked. Click to add an entry for it.`}
        aria-label={`${names}, ${times}: agents were at work, not tracked. Add an entry`}
        className={
          'text-muted-foreground hover:bg-muted/40 focus-visible:ring-ring absolute inset-0 flex flex-col items-start justify-start overflow-hidden rounded-md px-2 text-left text-xs outline-none focus-visible:ring-2 ' +
          (short ? 'py-0 leading-[14px]' : 'py-1')
        }
      >
        {/* On the card behind it, so the hatching never runs through the text. */}
        <span className="bg-card -mx-0.5 flex max-w-[calc(100%-1rem)] items-center gap-1 rounded-sm px-0.5 font-medium">
          <Sparkles aria-hidden="true" className="size-3 shrink-0" />
          <span className="truncate tabular-nums">
            {names} · {times}
          </span>
        </span>
        {height > 40 && <span className="bg-card -mx-0.5 max-w-full truncate rounded-sm px-0.5">Agents at work · not tracked</span>}
      </button>
      <button
        type="button"
        onClick={onDismiss}
        aria-label={`Dismiss untracked time ${times}`}
        title="Dismiss"
        className="text-muted-foreground hover:text-foreground focus-visible:ring-ring absolute top-0.5 right-0.5 grid size-4 place-items-center rounded opacity-0 outline-none group-hover/ghost:opacity-100 focus-visible:opacity-100 focus-visible:ring-2"
      >
        <X className="size-3" />
      </button>
    </div>
  )
}
