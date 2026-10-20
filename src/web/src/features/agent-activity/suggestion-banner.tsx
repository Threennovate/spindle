import { Link2, Sparkles } from 'lucide-react'
import { Tooltip as TooltipPrimitive } from 'radix-ui'
import { toast } from 'sonner'
import type { SplitSuggestion, SplitSuggestionPart } from '@/api/types'
import { Button } from '@/components/ui/button'
import { cn } from 'cn'
import { errorMessage } from '@/lib/error-message'
import { formatHoursMinutes } from '@/lib/time/duration'
import { ProjectPicker } from '../catalog/project-picker'
import type { Catalog } from '../catalog/use-catalog'
import type { SplitPart } from '../time-entries/split'
import { EvidenceDetails, EvidenceStrip } from './evidence-strip'
import { useLinkRepository } from './queries'
import {
  chipTime,
  isStrong,
  movedNote,
  partSeconds,
  percentTitle,
  separateTitle,
  spokenEvidence,
  suggestedParts,
  suggestedStretches,
  suggestionNames,
  targetColor,
  targetName,
  weakNote,
  type Stretch,
} from './suggestion'

interface ChipProps {
  suggestion: SplitSuggestion
  catalog: Catalog
  timeZone: string
  hour12: boolean
  /** The entry's time, which the chip divides; a running entry's grows. */
  seconds: number
  onOpen: () => void
}

/**
 * "✦ 1:20 · 0:40", each with its project's colour, or "✦ Split at 10:42" when the work moved from one project to the
 * next: beside an entry whose time agent activity says went to several projects. Hours here; percentages are for the
 * editor. Resting on it shows what the suggestion is built from; a click opens the entry, whose editor offers it.
 * Weak suggestions stay quiet, in grey.
 */
export function SplitSuggestionChip({ suggestion, catalog, timeZone, hour12, seconds, onOpen }: ChipProps) {
  const times = partSeconds(suggestion, seconds)
  const names = suggestionNames(suggestion, catalog, seconds)
  const label = suggestion.stretches ? `${separateTitle(suggestion, timeZone, hour12)}, as agents worked: ${names}` : `Split as agents worked: ${names}`
  return (
    <TooltipPrimitive.Root delayDuration={600}>
      <TooltipPrimitive.Trigger asChild>
        <button
          type="button"
          onClick={onOpen}
          aria-label={label}
          aria-description={spokenEvidence(suggestion, catalog, timeZone, hour12)}
          className={cn(
            'focus-visible:ring-ring inline-flex shrink-0 items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-medium tabular-nums outline-none focus-visible:ring-2',
            isStrong(suggestion) ? 'bg-primary/12 text-foreground hover:bg-primary/20' : 'bg-muted text-muted-foreground hover:bg-muted/70',
          )}
        >
          <Sparkles aria-hidden="true" className={cn('size-3', isStrong(suggestion) ? 'text-primary-text' : 'text-muted-foreground')} />
          {suggestion.stretches
            ? separateTitle(suggestion, timeZone, hour12)
            : suggestion.parts.map((part, index) => (
                <span key={index} className="inline-flex items-center gap-1">
                  {index > 0 && <span className="text-muted-foreground -ml-0.5 pr-0.5">·</span>}
                  <span aria-hidden="true" className="size-1.5 rounded-full" style={{ background: targetColor(part.target, catalog) }} />
                  {chipTime(times[index])}
                </span>
              ))}
        </button>
      </TooltipPrimitive.Trigger>
      <TooltipPrimitive.Portal>
        <TooltipPrimitive.Content
          side="bottom"
          sideOffset={6}
          collisionPadding={12}
          aria-hidden="true"
          className="bg-popover text-popover-foreground ring-foreground/10 data-open:animate-in data-open:fade-in-0 z-50 rounded-lg p-3 shadow-md ring-1"
        >
          <EvidenceDetails suggestion={suggestion} catalog={catalog} timeZone={timeZone} hour12={hour12} />
        </TooltipPrimitive.Content>
      </TooltipPrimitive.Portal>
    </TooltipPrimitive.Root>
  )
}

interface BannerProps {
  suggestion: SplitSuggestion
  workspaceId: string
  catalog: Catalog
  timeZone: string
  hour12: boolean
  /** Shares the entry's time by percent; nothing is saved until the entry is. */
  onApply: (parts: SplitPart[]) => void
  /** Makes separate entries cut where the work moved; nothing is saved until the entry is. */
  onSeparate: (stretches: Stretch[]) => void
  /** "Keep as is": never suggest a split for this entry again, on any device. */
  onDismiss: () => void
}

/**
 * "Agents worked on 2 projects in this time": the split agent activity suggests for the entry in the editor, with
 * its evidence, to take with one click. When the work moved from one project to the next, it also offers separate
 * entries cut where it moved. A repository without a project is linked to one right here, and stays linked, on the
 * Mac too.
 */
export function SplitSuggestionBanner({ suggestion, workspaceId, catalog, timeZone, hour12, onApply, onSeparate, onDismiss }: BannerProps) {
  const parts = suggestedParts(suggestion, catalog)
  const stretches = suggestedStretches(suggestion)
  const unlinked = suggestion.parts.some((part) => !part.target.projectId)
  const strong = isStrong(suggestion)
  const notes = [movedNote(suggestion, catalog, timeZone, hour12), weakNote(suggestion)].filter(Boolean).join(' ')
  const chooseFirst = 'Choose a project for each repository first.'

  return (
    <section
      aria-label="Suggested split"
      className={cn('grid gap-2.5 rounded-lg border p-3', strong ? 'border-primary/25 bg-primary/5' : 'bg-muted/40')}
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <p className="flex min-w-0 flex-1 items-center gap-1.5 text-sm font-semibold">
          <Sparkles aria-hidden="true" className={cn('size-4 shrink-0', strong ? 'text-primary-text' : 'text-muted-foreground')} />
          {unlinked ? `Agents worked in ${suggestion.parts.length} places in this time` : `Agents worked on ${suggestion.parts.length} projects in this time`}
        </p>
        <div className="flex items-center gap-1">
          <Button type="button" variant="ghost" size="sm" onClick={onDismiss} title="Leave the entry as it is and stop suggesting a split for it">
            Keep as is
          </Button>
          {suggestion.stretches ? (
            <>
              <Button
                type="button"
                variant="outline"
                size="sm"
                disabled={!parts}
                onClick={() => parts && onApply(parts)}
                title={parts ? 'Keep one entry and share its time by percent; nothing is saved until you save.' : chooseFirst}
              >
                {percentTitle(suggestion)}
              </Button>
              <Button
                type="button"
                size="sm"
                variant={strong ? 'default' : 'outline'}
                disabled={!stretches}
                onClick={() => stretches && onSeparate(stretches)}
                title={stretches ? 'Make separate entries, cut where the work moved; nothing is saved until you save.' : chooseFirst}
              >
                {separateTitle(suggestion, timeZone, hour12)}
              </Button>
            </>
          ) : (
            <Button
              type="button"
              size="sm"
              variant={strong ? 'default' : 'outline'}
              disabled={!parts}
              onClick={() => parts && onApply(parts)}
              title={parts ? 'Split the time as suggested; nothing is saved until you save.' : chooseFirst}
            >
              Split
            </Button>
          )}
        </div>
      </div>

      <EvidenceStrip suggestion={suggestion} catalog={catalog} timeZone={timeZone} hour12={hour12} />

      <ul className="flex flex-wrap gap-x-4 gap-y-1.5 text-sm">
        {suggestion.parts.map((part, index) => (
          <li key={index} className="flex items-center gap-1.5">
            <span aria-hidden="true" className="size-2 shrink-0 rounded-full" style={{ background: targetColor(part.target, catalog) }} />
            <span className="font-medium">{targetName(part.target, catalog)}</span>
            <span className="text-muted-foreground tabular-nums">{part.percent}%</span>
            <span className="text-muted-foreground/70 tabular-nums" title={partHelp(part)}>
              · {formatHoursMinutes(part.attentionSeconds + part.unattributedSeconds)}
            </span>
            {part.target.repository && !part.target.projectId && <LinkRepository workspaceId={workspaceId} repository={part.target.repository} />}
          </li>
        ))}
      </ul>

      {notes && <p className="text-muted-foreground text-xs">{notes}</p>}
    </section>
  )
}

function partHelp(part: SplitSuggestionPart): string {
  const sent = `${part.prompts} ${part.prompts === 1 ? 'prompt' : 'prompts'}`
  return part.unattributedSeconds > 0
    ? `${formatHoursMinutes(part.attentionSeconds)} in agent sessions there (${sent}), and ${formatHoursMinutes(part.unattributedSeconds)} the logs don't account for (hatched)`
    : `Time in agent sessions there, ${sent}`
}

/**
 * "Choose project" for a repository without one: linked for the person's account from now on, on the Mac too. "No
 * project" keeps it without one, so a project named like it doesn't take its work either.
 */
export function LinkRepository({
  workspaceId,
  repository,
  label = 'Choose project',
}: {
  workspaceId: string
  repository: NonNullable<SplitSuggestionPart['target']['repository']>
  label?: string
}) {
  const link = useLinkRepository(workspaceId)
  return (
    <ProjectPicker
      workspaceId={workspaceId}
      projectId={undefined}
      taskId={null}
      placeholder={`Which project is ${repository.name} for?`}
      onChange={(projectId) => {
        const onError = (error: unknown) => toast.error(errorMessage(error, "Couldn't link the repository."))
        if (projectId) {
          link.mutate({ repository, projectId }, { onError })
          return
        }
        link.mutate(
          { repository, projectId: null, noProject: true },
          {
            onError,
            onSuccess: () =>
              toast.success(`Unlinked ${repository.name}`, {
                action: { label: 'Undo', onClick: () => link.mutate({ repository, projectId: null }, { onError }) },
              }),
          },
        )
      }}
    >
      <Button type="button" variant="link" size="xs" className="h-auto px-0" title={`Which project is ${repository.name} for? It is remembered for your account, on the Mac too.`}>
        <Link2 />
        {label}
      </Button>
    </ProjectPicker>
  )
}
