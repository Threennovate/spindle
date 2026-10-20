import { cn } from 'cn'
import { Sparkles } from 'lucide-react'
import type { CSSProperties } from 'react'
import type { SplitSuggestion, SplitTarget, TimeEntry } from '@/api/types'
import { formatClock } from '@/lib/time/zone'
import type { Catalog } from '../catalog/use-catalog'
import { chipTime, isStrong, partSeconds, separateTitle, suggestedStretches, suggestionNames, targetColor, targetName } from './suggestion'

interface MarkProps {
  suggestion: SplitSuggestion
  entry: TimeEntry
  catalog: Catalog
  timeZone: string
  hour12: boolean
  style: CSSProperties
  onPreview: (on: boolean) => void
  onTake: () => void
}

/**
 * A calendar block's ✦: pointing at it, or reaching it with the keyboard, draws the suggestion on the block, only
 * while that lasts; a click takes it, with Undo. Weak suggestions stay quiet, in grey.
 */
export function SuggestionMark({ suggestion, entry, catalog, timeZone, hour12, style, onPreview, onTake }: MarkProps) {
  const seconds = entrySeconds(entry)
  const what = suggestion.stretches ? separateTitle(suggestion, timeZone, hour12) : `Split ${suggestionNames(suggestion, catalog, seconds)}`
  const opens = !takesInOneClick(entry, suggestion)
  return (
    <button
      type="button"
      data-suggestion-mark
      aria-label={`Suggested: ${what}. ${opens ? 'Open the entry' : 'Apply it'}`}
      title={`${what} · ${opens ? 'click ✦ to open the entry' : 'click ✦ to apply, with Undo'}`}
      onPointerEnter={() => onPreview(true)}
      onPointerLeave={() => onPreview(false)}
      // Only focus from the keyboard: a click leaves the focus here, and the preview goes with the pointer.
      onFocus={(event) => event.currentTarget.matches(':focus-visible') && onPreview(true)}
      onBlur={() => onPreview(false)}
      onClick={onTake}
      className={cn(
        'focus-visible:ring-ring absolute z-20 grid size-[18px] place-items-center rounded outline-none hover:bg-black/8 focus-visible:ring-2 dark:hover:bg-white/10',
        isStrong(suggestion) ? 'text-primary-text' : 'text-muted-foreground',
      )}
      style={style}
    >
      <Sparkles aria-hidden="true" className="size-3" />
    </button>
  )
}

/** Whether a click on the ✦ saves the suggestion, rather than opening the entry: finished, every repository linked. */
function takesInOneClick(entry: TimeEntry, suggestion: SplitSuggestion): boolean {
  return !!entry.stop && (suggestion.stretches ? !!suggestedStretches(suggestion) : suggestion.parts.every((part) => !!part.target.projectId))
}

function entrySeconds(entry: TimeEntry): number {
  return (Date.parse(entry.stop ?? new Date().toISOString()) - Date.parse(entry.start)) / 1000
}

/**
 * "Split Atlas 0:50 · Beacon 1:10 · click ✦ to apply", or "Split at 10:50 · …": what the ✦ would do, said once,
 * just under the block, so the block's own text stays as it is. It names the projects, as narrow or short blocks
 * label their parts with the time alone, or not at all.
 */
export function SuggestionHint({
  entry,
  suggestion,
  catalog,
  timeZone,
  hour12,
  style,
}: Omit<MarkProps, 'onPreview' | 'onTake'>) {
  const times = partSeconds(suggestion, entrySeconds(entry)).map(chipTime)
  const what = suggestion.stretches
    ? separateTitle(suggestion, timeZone, hour12)
    : `Split ${suggestion.parts.map((part, index) => `${targetName(part.target, catalog)} ${times[index]}`).join(' · ')}`
  return (
    <span
      aria-hidden="true"
      className={cn(
        'bg-popover text-popover-foreground ring-foreground/10 pointer-events-none absolute z-30 w-max max-w-72 truncate rounded-md px-1.5 py-0.5 text-[11px] font-medium tabular-nums shadow-md ring-1',
        !isStrong(suggestion) && 'text-muted-foreground',
      )}
      style={style}
    >
      <Sparkles className={cn('mr-1 inline size-3 align-[-2px]', isStrong(suggestion) ? 'text-primary-text' : 'text-muted-foreground')} />
      {what} · {takesInOneClick(entry, suggestion) ? 'click ✦ to apply' : 'click ✦ to open'}
    </span>
  )
}

interface PreviewProps {
  suggestion: SplitSuggestion
  catalog: Catalog
  timeZone: string
  hour12: boolean
  /** The block's start and end, in milliseconds: the part of the entry it shows on this day. */
  from: number
  to: number
  /** Labels each part with its project and time; off for a block too short for them beside its own text. */
  labelled: boolean
}

/**
 * The suggestion drawn over its block as a proposal, never as a saved split: the block stays as it is underneath, and
 * each proposed part is a translucent hatched band in its project's colour with dashed dividers, labelled with its
 * project and time. A move is cut with a dashed line at its time; a split by percent becomes columns as wide as each
 * share. Weak suggestions are drawn fainter.
 */
export function SuggestionPreview({ suggestion, catalog, timeZone, hour12, from, to, labelled }: PreviewProps) {
  const span = to - from
  if (!(span > 0)) {
    return null
  }
  const strong = isStrong(suggestion)
  const at = (instant: string) => Math.min(100, Math.max(0, ((Date.parse(instant) - from) / span) * 100))
  const band = (target: SplitTarget | null | undefined) => {
    const color = targetColor(target, catalog)
    const strength = strong ? 22 : 12
    return {
      background: `repeating-linear-gradient(-45deg, color-mix(in oklab, ${color} ${strength + 14}%, transparent) 0 1.5px, color-mix(in oklab, ${color} ${strength}%, transparent) 1.5px 6px)`,
    }
  }
  const divider = strong ? 'border-foreground/55' : 'border-foreground/30'

  if (suggestion.stretches) {
    const stretches = suggestion.stretches
    return (
      <span aria-hidden="true" data-preview className="pointer-events-none absolute inset-0">
        {stretches.map((stretch, index) => {
          const top = index === 0 ? 0 : at(stretch.start)
          const bottom = index === stretches.length - 1 ? 100 : at(stretches[index + 1].start)
          const start = index === 0 ? from : Date.parse(stretch.start)
          const end = index === stretches.length - 1 ? to : Date.parse(stretches[index + 1].start)
          return (
            <span key={index} className="@container absolute inset-x-0" style={{ top: `${top}%`, height: `${Math.max(0, bottom - top)}%`, ...band(stretch.target) }}>
              {/* The first stretch's label keeps clear of the ✦ in the block's corner. */}
              {labelled && <PartLabel target={stretch.target} catalog={catalog} time={chipTime((end - start) / 1000)} className={cn('bottom-1', index === 0 ? 'right-6' : 'right-1')} />}
            </span>
          )
        })}
        {stretches.slice(1).map((stretch, index) => (
          <span key={`cut-${index}`} className="absolute inset-x-0 flex -translate-y-1/2 items-center" style={{ top: `${at(stretch.start)}%` }}>
            <span className={cn('flex-1 border-t-2 border-dashed', divider)} />
            <span className="bg-foreground text-background mx-1 rounded-sm px-1 text-[10px] leading-[14px] font-semibold tabular-nums">
              {formatClock(stretch.start, timeZone, hour12)}
            </span>
            <span className={cn('flex-1 border-t-2 border-dashed', divider)} />
          </span>
        ))}
      </span>
    )
  }

  const times = partSeconds(suggestion, span / 1000)
  return (
    <span aria-hidden="true" data-preview className="pointer-events-none absolute inset-0 flex">
      {suggestion.parts.map((part, index) => (
        <span
          key={index}
          className={cn('@container relative h-full', index > 0 && cn('border-l-2 border-dashed', divider))}
          style={{ width: `${part.percent}%`, ...band(part.target) }}
        >
          {labelled && <PartLabel target={part.target} catalog={catalog} time={chipTime(times[index])} className="bottom-1 left-1" />}
        </span>
      ))}
    </span>
  )
}

/** "● Atlas 0:50" on its part, or the time alone where the part is narrow. */
function PartLabel({ target, catalog, time, className }: { target: SplitTarget; catalog: Catalog; time: string; className?: string }) {
  return (
    <span className={cn('bg-card/90 text-foreground absolute flex max-w-[calc(100%-0.5rem)] items-center gap-1 rounded-sm px-1 text-[10px] leading-[14px] font-medium tabular-nums shadow-xs', className)}>
      <span className="hidden size-1.5 shrink-0 rounded-full @[5.5rem]:inline-block" style={{ background: targetColor(target, catalog) }} />
      <span className="hidden truncate @[5.5rem]:inline">{targetName(target, catalog)}</span>
      <span className="text-muted-foreground @[5.5rem]:inline">{time}</span>
    </span>
  )
}
