import type { SplitSuggestion } from '@/api/types'
import { formatHoursMinutes } from '@/lib/time/duration'
import { formatClock } from '@/lib/time/zone'
import type { Catalog } from '../catalog/use-catalog'
import { spokenEvidence, targetColor, targetName, weakNote } from './suggestion'

interface StripProps {
  suggestion: SplitSuggestion
  catalog: Catalog
  timeZone: string
  hour12: boolean
  /** Whether to show the time's start and end under the strip. */
  showTimes?: boolean
}

/**
 * What a split suggestion is built from, at a glance, as on the Mac: the entry's time as a thin line, each prompt as
 * a tick in its project's colour, the agents' attention as thin bars under them, and the time the logs don't account
 * for hatched in the colour of the project it stays on. It draws only what the server sent, so it never works anything
 * out again.
 */
export function EvidenceStrip({ suggestion, catalog, timeZone, hour12, showTimes = true }: StripProps) {
  const start = Date.parse(suggestion.start)
  const span = Date.parse(suggestion.end) - start
  if (!(span > 0)) {
    return null
  }
  const at = (instant: string) => Math.min(100, Math.max(0, ((Date.parse(instant) - start) / span) * 100))
  const width = (from: string, to: string) => Math.max(at(to) - at(from), 0.4)
  const open = targetColor(suggestion.fallback, catalog)

  return (
    <div role="img" aria-label={spokenEvidence(suggestion, catalog, timeZone, hour12)} className="grid gap-0.5">
      <div aria-hidden="true" className="relative h-[17px]">
        {/* The entry's time, behind everything else. */}
        <span className="bg-foreground/25 absolute inset-x-0 top-[12.5px] h-px" />
        {suggestion.open.map((range, index) => (
          <span
            key={`open-${index}`}
            className="absolute top-[9.5px] h-[7px]"
            style={{
              left: `${at(range.start)}%`,
              width: `${width(range.start, range.end)}%`,
              background: `repeating-linear-gradient(-45deg, ${open} 0 1.25px, color-mix(in oklab, ${open} 18%, transparent) 1.25px 3.5px)`,
            }}
          />
        ))}
        {suggestion.pieces.map((piece, index) => (
          <span
            key={`piece-${index}`}
            className="absolute top-[11px] h-1 rounded-full"
            style={{ left: `${at(piece.start)}%`, width: `${width(piece.start, piece.end)}%`, background: targetColor(piece.target, catalog) }}
          />
        ))}
        {suggestion.prompts.map((prompt, index) => (
          <span
            key={`prompt-${index}`}
            className="absolute top-0 h-[9px] w-[1.5px] rounded-full"
            style={{ left: `min(calc(100% - 1.5px), max(0px, calc(${at(prompt.at)}% - 0.75px)))`, background: targetColor(prompt.target, catalog) }}
          />
        ))}
      </div>
      {showTimes && (
        <div aria-hidden="true" className="text-muted-foreground/80 flex justify-between text-[11px] tabular-nums">
          <span>{formatClock(suggestion.start, timeZone, hour12)}</span>
          <span>{formatClock(suggestion.end, timeZone, hour12)}</span>
        </div>
      )}
    </div>
  )
}

/** The strip with its parts named, for the chip's popover: enough to judge the suggestion without opening the entry. */
export function EvidenceDetails({ suggestion, catalog, timeZone, hour12 }: Omit<StripProps, 'showTimes'>) {
  const open = suggestion.parts.find((part) => part.unattributedSeconds > 0)
  const weak = weakNote(suggestion)
  return (
    <div className="grid w-64 gap-2">
      <EvidenceStrip suggestion={suggestion} catalog={catalog} timeZone={timeZone} hour12={hour12} />
      <ul className="grid gap-1">
        {suggestion.parts.map((part, index) => (
          <li key={index} className="flex items-center gap-1.5 text-sm">
            <span aria-hidden="true" className="size-2 shrink-0 rounded-full" style={{ background: targetColor(part.target, catalog) }} />
            <span className="min-w-0 truncate font-medium">{targetName(part.target, catalog)}</span>
            <span className="text-muted-foreground ml-auto shrink-0 tabular-nums">
              {part.prompts} {part.prompts === 1 ? 'prompt' : 'prompts'} · {formatHoursMinutes(part.attentionSeconds + part.unattributedSeconds)}
            </span>
          </li>
        ))}
      </ul>
      {open && (
        <p className="text-muted-foreground text-xs">
          Hatched: {formatHoursMinutes(open.unattributedSeconds)} the logs don't account for, which stays on {targetName(open.target, catalog)}.
        </p>
      )}
      {weak && <p className="text-muted-foreground text-xs">{weak}</p>}
    </div>
  )
}
