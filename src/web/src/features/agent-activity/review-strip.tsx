import { cn } from 'cn'
import type { TimeEntry } from '@/api/types'
import { dayKey, formatClock } from '@/lib/time/zone'
import { minuteOfDay } from '../calendar/layout'
import { partColor } from '../catalog/split-names'
import type { Catalog } from '../catalog/use-catalog'
import { partsOf } from '../time-entries/split'
import type { ReviewItem } from './review'

const MINUTES_PER_DAY = 24 * 60

interface StripProps {
  day: string
  /** The day's entries as they are now. */
  entries: readonly TimeEntry[]
  /** The items still waiting on the day. */
  items: readonly ReviewItem[]
  catalog: Catalog
  timeZone: string
  hour12: boolean
  selected: string | null
  onSelect: (id: string) => void
}

/**
 * The day at a glance, as on the Mac: its entries as bars in their projects' colours, the time each suggestion is
 * about outlined in marigold over them, and untracked time as a dashed outline. Pointing at an outline selects its
 * item. The rows below say the same in words, so the strip is hidden from screen readers.
 */
export function ReviewStrip({ day, entries, items, catalog, timeZone, hour12, selected, onSelect }: StripProps) {
  const minute = (instant: string) => {
    const key = dayKey(instant, timeZone)
    return key < day ? 0 : key > day ? MINUTES_PER_DAY : minuteOfDay(new Date(instant), timeZone)
  }
  const now = new Date().toISOString()
  const spans = [
    ...entries.map((entry) => [minute(entry.start), minute(entry.stop ?? now)]),
    ...items.map((item) => [minute(item.start), minute(item.end)]),
  ]
  if (spans.length === 0) {
    return null
  }
  // Whole hours around what is there, at least four of them.
  const from = Math.max(0, Math.floor(Math.min(...spans.map(([start]) => start)) / 60) * 60)
  const to = Math.min(MINUTES_PER_DAY, Math.max(from + 4 * 60, Math.ceil(Math.max(...spans.map(([, end]) => end)) / 60) * 60))
  const span = to - from
  const step = span > 12 * 60 ? 180 : span > 6 * 60 ? 120 : 60
  const ticks: number[] = []
  for (let tick = from; tick <= to; tick += step) {
    ticks.push(tick)
  }
  const left = (m: number) => `${((m - from) / span) * 100}%`
  const width = (start: number, end: number, least: number) => `max(${least}px, ${((end - start) / span) * 100}%)`
  const clock = (m: number) => formatClock(new Date(Date.UTC(2000, 0, 1, Math.floor(m / 60) % 24, m % 60)), 'UTC', hour12)

  return (
    <div aria-hidden="true" className="grid gap-1">
      <div className="bg-muted/50 relative h-8 rounded-md">
        {ticks.map((tick) => (
          <span key={tick} className="bg-border absolute inset-y-0 w-px" style={{ left: left(tick) }} />
        ))}
        {entries.map((entry) => {
          const parts = partsOf(entry)
          const start = minute(entry.start)
          return (
            <span
              key={entry.id}
              className="absolute inset-y-2 flex flex-col overflow-hidden rounded-[4px] opacity-45"
              style={{ left: left(start), width: width(start, minute(entry.stop ?? now), 2) }}
            >
              {parts.map((part, index) => (
                <span key={index} className="w-full" style={{ height: `${part.percent}%`, background: partColor(catalog, part) }} />
              ))}
            </span>
          )
        })}
        {items.map((item) => {
          const start = minute(item.start)
          const lit = item.id === selected
          return (
            <button
              key={item.id}
              type="button"
              tabIndex={-1}
              onClick={() => onSelect(item.id)}
              className={cn(
                'absolute inset-y-0.5 cursor-pointer rounded-[5px] border-logo-accent border-[1.5px] transition-[background-color,box-shadow]',
                item.kind === 'untracked' && 'border-dashed',
                lit ? 'bg-logo-accent/12 ring-logo-accent/50 z-10 ring-2' : 'bg-logo-accent/6',
              )}
              style={{ left: left(start), width: width(start, minute(item.end), 4) }}
            />
          )
        })}
      </div>
      <div className="text-muted-foreground relative h-4 text-[11px] tabular-nums">
        {ticks.map((tick, index) => (
          <span
            key={tick}
            className={cn('absolute', index === 0 ? '' : index === ticks.length - 1 && tick === to ? '-translate-x-full' : '-translate-x-1/2')}
            style={{ left: left(tick) }}
          >
            {clock(tick)}
          </span>
        ))}
      </div>
    </div>
  )
}
