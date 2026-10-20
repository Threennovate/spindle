import { cn } from 'cn'
import type { AgentProposal, TimeEntry } from '@/api/types'
import { dayKey, formatClock } from '@/lib/time/zone'
import { partColor } from '../catalog/split-names'
import type { Catalog } from '../catalog/use-catalog'
import { minuteOfDay } from '../calendar/layout'
import { partsOf, type SplitPart } from '../time-entries/split'
import { draftOf } from './proposal'

const MINUTES_PER_DAY = 24 * 60

interface Block {
  key: string
  /** The proposal the block belongs to, to tie it to its card. */
  proposalId: string | null
  kind: 'entry' | 'before' | 'proposed'
  start: number
  end: number
  parts: SplitPart[]
  label: string
}

interface DayTimelineProps {
  day: string
  /** The day's entries as they are now. */
  entries: TimeEntry[]
  /** The proposals of the day still waiting. */
  proposals: AgentProposal[]
  catalog: Catalog
  timeZone: string
  hour12: boolean
  highlighted: string | null
  onHighlight: (proposalId: string | null) => void
  onSelect: (proposalId: string) => void
}

/**
 * The day at a glance: the entries already there, solid, and what the agents propose over them, dashed. A change shows
 * the entry as it was, faint, and as it would be. Pointing at a proposal here or at its card highlights both.
 */
export function DayTimeline({ day, entries, proposals, catalog, timeZone, hour12, highlighted, onHighlight, onSelect }: DayTimelineProps) {
  const minute = (instant: string) => {
    const date = new Date(instant)
    // A time on another day sits at the day's edge.
    const key = dayKey(date, timeZone)
    return key < day ? 0 : key > day ? MINUTES_PER_DAY : minuteOfDay(date, timeZone)
  }
  const changed = new Set(proposals.map((p) => p.entryId).filter(Boolean))
  const blocks: Block[] = []
  for (const entry of entries) {
    blocks.push({
      key: entry.id,
      proposalId: null,
      kind: changed.has(entry.id) ? 'before' : 'entry',
      start: minute(entry.start),
      end: minute(entry.stop ?? new Date().toISOString()),
      parts: partsOf(entry),
      label: entry.description || 'No description',
    })
  }
  for (const proposal of proposals) {
    const draft = draftOf(proposal)
    if (!draft) {
      continue
    }
    if (proposal.entryId) {
      // The entry as it was belongs to its proposal too.
      const before = blocks.find((block) => block.key === proposal.entryId)
      if (before) {
        before.proposalId = proposal.id
      }
    }
    blocks.push({
      key: proposal.id,
      proposalId: proposal.id,
      kind: 'proposed',
      start: minute(draft.start),
      end: minute(draft.stop ?? new Date().toISOString()),
      parts: partsOf(draft),
      label: draft.description || 'No description',
    })
  }
  if (blocks.length === 0) {
    return null
  }

  const first = Math.min(...blocks.map((b) => b.start))
  const last = Math.max(...blocks.map((b) => b.end))
  const from = Math.max(0, Math.floor((first - 30) / 60) * 60)
  const to = Math.min(MINUTES_PER_DAY, Math.max(from + 4 * 60, Math.ceil((last + 30) / 60) * 60))
  const span = to - from
  const step = span > 10 * 60 ? 180 : span > 6 * 60 ? 120 : 60
  const ticks: number[] = []
  for (let tick = Math.ceil(from / step) * step; tick <= to; tick += step) {
    ticks.push(tick)
  }
  const left = (m: number) => `${((m - from) / span) * 100}%`
  const width = (b: Block) => `max(3px, ${((b.end - b.start) / span) * 100}%)`
  const clock = (m: number) => {
    const date = new Date(Date.UTC(2000, 0, 1, Math.floor(m / 60) % 24, m % 60))
    return formatClock(date, 'UTC', hour12)
  }
  // Proposed blocks go over the rest, and the one pointed at over everything.
  const order = { before: 0, entry: 1, proposed: 2 }
  const sorted = [...blocks].sort((a, b) => order[a.kind] - order[b.kind])

  return (
    <div className="grid gap-1">
      <div className="bg-muted/50 relative h-9 rounded-md" role="img" aria-label="Entries of the day with the proposals over them">
        {ticks.map((tick) => (
          <span key={tick} aria-hidden="true" className="bg-border absolute inset-y-0 w-px" style={{ left: left(tick) }} />
        ))}
        {sorted.map((block) => {
          const lit = block.proposalId !== null && block.proposalId === highlighted
          const dimmed = highlighted !== null && !lit
          return (
            <button
              key={`${block.kind}-${block.key}`}
              type="button"
              tabIndex={-1}
              disabled={!block.proposalId}
              title={`${block.kind === 'before' ? 'Before: ' : block.kind === 'proposed' ? 'Proposed: ' : ''}${block.label}, ${clock(Math.round(block.start))}–${clock(Math.round(block.end))}`}
              onPointerEnter={() => block.proposalId && onHighlight(block.proposalId)}
              onPointerLeave={() => block.proposalId && onHighlight(null)}
              onClick={() => block.proposalId && onSelect(block.proposalId)}
              className={cn(
                'absolute flex flex-col overflow-hidden rounded-[5px] transition-[opacity,box-shadow] duration-150',
                block.kind === 'proposed' && 'inset-y-1 z-20 border-[1.5px] border-dashed',
                block.kind === 'entry' && 'inset-y-2 z-10',
                block.kind === 'before' && 'inset-y-3 z-0 opacity-60',
                block.proposalId ? 'cursor-pointer' : 'cursor-default',
                lit && 'ring-foreground z-30 ring-2 ring-offset-1 ring-offset-transparent',
                dimmed && 'opacity-35',
              )}
              style={{
                left: left(block.start),
                width: width(block),
                // Lighter than the project's color, so the dashes read on the dark strip too.
                borderColor: block.kind === 'proposed' ? `color-mix(in oklab, ${partColor(catalog, largest(block.parts))} 70%, white)` : undefined,
                backgroundImage:
                  block.kind === 'before'
                    ? `repeating-linear-gradient(135deg, ${partColor(catalog, largest(block.parts))} 0 1.5px, transparent 1.5px 5px)`
                    : undefined,
              }}
            >
              {block.kind !== 'before' &&
                block.parts.map((part, i) => (
                  <span
                    key={i}
                    className="w-full"
                    style={{
                      height: `${part.percent}%`,
                      background: block.kind === 'proposed' ? `color-mix(in oklab, ${partColor(catalog, part)} 22%, transparent)` : partColor(catalog, part),
                    }}
                  />
                ))}
            </button>
          )
        })}
      </div>
      <div aria-hidden="true" className="text-muted-foreground relative h-4 text-[11px] tabular-nums">
        {ticks.map((tick, i) => (
          <span
            key={tick}
            className={cn('absolute', i === 0 ? '' : i === ticks.length - 1 && tick === to ? '-translate-x-full' : '-translate-x-1/2')}
            style={{ left: left(tick) }}
          >
            {clock(tick)}
          </span>
        ))}
      </div>
    </div>
  )
}

function largest(parts: readonly SplitPart[]): SplitPart {
  return parts.reduce((best, part) => (part.percent > best.percent ? part : best), parts[0])
}

/** What the strip's three kinds of block mean. */
export function TimelineLegend({ changes }: { changes: boolean }) {
  return (
    <span aria-hidden="true" className="text-muted-foreground hidden items-center gap-3 text-[11px] sm:flex">
      <span className="flex items-center gap-1">
        <span className="bg-muted-foreground size-2.5 rounded-[3px]" />
        Tracked
      </span>
      <span className="flex items-center gap-1">
        <span className="border-muted-foreground size-2.5 rounded-[3px] border border-dashed" />
        Proposed
      </span>
      {changes && (
        <span className="flex items-center gap-1">
          <span
            className="size-2.5 rounded-[3px] opacity-70"
            style={{
              backgroundImage: 'repeating-linear-gradient(135deg, var(--muted-foreground) 0 1.5px, transparent 1.5px 4px)',
            }}
          />
          Before the change
        </span>
      )}
    </span>
  )
}
