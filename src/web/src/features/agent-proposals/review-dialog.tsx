import { useQuery } from '@tanstack/react-query'
import { cn } from 'cn'
import { AlertTriangle, ArrowRight, Ban, Check, MessageSquareText, Sparkles, Undo2, X } from 'lucide-react'
import { useState, type KeyboardEvent } from 'react'
import { toast } from 'sonner'
import type { AgentProposal, TimeEntry } from '@/api/types'
import { Kbd } from '@/components/kbd'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { errorMessage } from '@/lib/error-message'
import { formatDurationAs, formatHoursMinutes, type DurationFormat } from '@/lib/time/duration'
import { formatClock, formatDayHeading } from '@/lib/time/zone'
import { ProjectLabel } from '../catalog/project-label'
import { partColor } from '../catalog/split-names'
import { SplitLabel } from '../catalog/split-label'
import { useCatalog, type Catalog } from '../catalog/use-catalog'
import { EntryDialog } from '../time-entries/entry-dialog'
import { dayEntriesQuery } from '../time-entries/queries'
import { isSplit, partsOf } from '../time-entries/split'
import { useWorkspace } from '../workspace'
import { DayTimeline, TimelineLegend } from './day-timeline'
import { addedSeconds, byDay, changeLines, clientName, draftOf, proposedBy, secondsOf, waitingSummary } from './proposal'
import { useApplyProposal, useReopenProposal, useResolveProposal, type Saved } from './queries'

/** What the person did with a proposal while the review is open, so it can be taken back. */
type Done = { proposal: AgentProposal } & ({ kind: 'added' | 'changed'; saved: Saved } | { kind: 'edited'; entry: TimeEntry } | { kind: 'discarded' })

interface ReviewDialogProps {
  open: boolean
  pending: AgentProposal[]
  onClose: () => void
}

/**
 * The entries and changes agents proposed, by day: a strip of the day with what is there and what is proposed over it,
 * and a card for each proposal with its time, where it goes, what it changes, the agent's reason and what stands in
 * its way. Add saves one as it is, Edit opens it in the entry editor, Discard throws it away; all of it can be taken
 * back until the review closes. The keyboard works too: arrows move, A adds, E edits, Delete discards.
 */
export function ReviewDialog({ open, pending, onClose }: ReviewDialogProps) {
  const { workspaceId, timeZone, hour12, durationFormat } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  const apply = useApplyProposal(workspaceId)
  const resolve = useResolveProposal(workspaceId)
  const reopen = useReopenProposal(workspaceId)
  const [done, setDone] = useState<Map<string, Done>>(new Map())
  const [editing, setEditing] = useState<AgentProposal | null>(null)
  const [busy, setBusy] = useState(false)
  const [highlighted, setHighlighted] = useState<string | null>(null)

  const waiting = pending.filter((p) => !done.has(p.id))
  const shown = [...waiting, ...[...done.values()].map((d) => d.proposal)]
  const ready = waiting.filter((p) => p.blocking.length === 0)

  function mark(entry: Done) {
    setDone((current) => new Map(current).set(entry.proposal.id, entry))
  }

  async function add(proposal: AgentProposal) {
    try {
      const saved = await apply.mutateAsync(proposal)
      mark({
        proposal,
        kind: proposal.entryId ? 'changed' : 'added',
        saved,
      })
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't save the proposal."))
    }
  }

  async function discard(proposal: AgentProposal) {
    try {
      await resolve.mutateAsync({
        id: proposal.id,
        outcome: 'discarded',
      })
      mark({ proposal, kind: 'discarded' })
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't discard the proposal."))
    }
  }

  async function all(action: (proposal: AgentProposal) => Promise<void>, proposals: AgentProposal[]) {
    setBusy(true)
    for (const proposal of proposals) {
      await action(proposal)
    }
    setBusy(false)
  }

  async function edited(proposal: AgentProposal, entry: TimeEntry) {
    try {
      await resolve.mutateAsync({
        id: proposal.id,
        outcome: 'saved',
        entryId: entry.id,
      })
      mark({ proposal, kind: 'edited', entry })
    } catch (error) {
      toast.error(errorMessage(error, 'The entry is saved, but the proposal still waits.'))
    }
  }

  async function undo(entry: Done) {
    try {
      await reopen.mutateAsync({
        id: entry.proposal.id,
        saved: entry.kind === 'added' || entry.kind === 'changed' ? entry.saved : undefined,
      })
      setDone((current) => {
        const next = new Map(current)
        next.delete(entry.proposal.id)
        return next
      })
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't take it back."))
    }
  }

  function close() {
    setDone(new Map())
    setHighlighted(null)
    onClose()
  }

  /** Brings a proposal's card into view and focus, e.g. from its block in the day's strip. */
  function select(proposalId: string) {
    const card = document.querySelector<HTMLElement>(`[data-proposal-id="${proposalId}"]`)
    card?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
    card?.focus({ preventScroll: true })
  }

  /** Arrows move between cards; A or Enter adds, E edits, Delete or Backspace discards the one in focus. */
  function handleKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const card = (event.target as HTMLElement).closest<HTMLElement>('[data-proposal-id]')
    if (!card || event.target !== card || event.metaKey || event.ctrlKey || event.altKey) {
      return
    }
    const cards = [...event.currentTarget.querySelectorAll<HTMLElement>('[data-proposal-id]')]
    const index = cards.indexOf(card)
    const proposal = waiting.find((p) => p.id === card.dataset.proposalId)
    const key = event.key.toLowerCase()
    if (key === 'arrowdown' || key === 'arrowup') {
      event.preventDefault()
      cards[Math.min(cards.length - 1, Math.max(0, index + (key === 'arrowdown' ? 1 : -1)))]?.focus()
      return
    }
    if (!proposal || busy) {
      return
    }
    if ((key === 'a' || key === 'enter') && proposal.blocking.length === 0) {
      event.preventDefault()
      void add(proposal).then(() => cards[index + 1]?.focus())
    } else if (key === 'e' && draftOf(proposal)) {
      event.preventDefault()
      setEditing(proposal)
    } else if (key === 'delete' || key === 'backspace') {
      event.preventDefault()
      void discard(proposal).then(() => cards[index + 1]?.focus())
    }
  }

  const draft = editing && draftOf(editing)
  const who = proposedBy(shown.length > 0 ? shown : pending)

  return (
    <>
      <Dialog open={open && !editing} onOpenChange={(isOpen) => !isOpen && close()}>
        <DialogContent className="grid max-h-[min(92dvh,56rem)] grid-rows-[auto_minmax(0,1fr)_auto] gap-0 overflow-hidden p-0 sm:max-w-3xl">
          <DialogHeader className="flex-row items-center gap-3 border-b p-4 text-left sm:px-5">
            <span aria-hidden="true" className="bg-primary/15 text-primary-text grid size-10 shrink-0 place-items-center rounded-lg">
              <Sparkles className="size-5" />
            </span>
            <div className="grid min-w-0 gap-0.5">
              <DialogTitle className="text-base">Proposed by {who}</DialogTitle>
              <DialogDescription>
                {waiting.length > 0
                  ? `${waitingSummary(waiting)}. Nothing is saved until you add it.`
                  : 'All dealt with. You can still undo anything until you close this.'}
              </DialogDescription>
            </div>
          </DialogHeader>

          <div className="grid content-start gap-7 overflow-y-auto p-4 sm:px-5" onKeyDown={handleKeyDown}>
            {shown.length === 0 && <p className="text-muted-foreground py-10 text-center text-sm">Nothing is waiting.</p>}
            {byDay(shown, timeZone).map(({ day, proposals }) => (
              <DaySection
                key={day}
                day={day}
                proposals={proposals}
                done={done}
                catalog={catalog}
                timeZone={timeZone}
                hour12={hour12}
                durationFormat={durationFormat}
                busy={busy}
                highlighted={highlighted}
                onHighlight={setHighlighted}
                onSelect={select}
                onAdd={(p) => void add(p)}
                onEdit={setEditing}
                onDiscard={(p) => void discard(p)}
                onUndo={(entry) => void undo(entry)}
              />
            ))}
          </div>

          <DialogFooter className="static m-0 flex-row flex-wrap items-center justify-between sm:justify-between sm:px-5">
            <div className="flex items-center gap-3">
              {waiting.length > 1 && (
                <Button type="button" variant="outline" disabled={busy} onClick={() => void all(discard, waiting)}>
                  Discard all
                </Button>
              )}
              {waiting.length > 0 && (
                <p className="text-muted-foreground hidden items-center gap-1 text-xs md:flex">
                  <Kbd>↑</Kbd>
                  <Kbd>↓</Kbd> move · <Kbd>A</Kbd> add · <Kbd>E</Kbd> edit · <Kbd>⌫</Kbd> discard
                </p>
              )}
            </div>
            <div className="flex items-center gap-2">
              <Button type="button" variant="ghost" onClick={close}>
                Close
              </Button>
              {ready.length > 0 && (
                <Button type="button" disabled={busy} onClick={() => void all(add, ready)}>
                  {ready.length === 1 ? 'Add it' : `Add all ${ready.length}`}
                </Button>
              )}
            </div>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {editing && draft && (
        <EntryDialog
          entry={editing.entryId ? draft : null}
          newEntry={
            editing.entryId
              ? null
              : {
                  start: new Date(draft.start),
                  stop: new Date(draft.stop ?? draft.start),
                }
          }
          draft={draft}
          onSaved={(entry) => void edited(editing, entry)}
          onClose={() => setEditing(null)}
        />
      )}
    </>
  )
}

interface DaySectionProps {
  day: string
  proposals: AgentProposal[]
  done: Map<string, Done>
  catalog: Catalog
  timeZone: string
  hour12: boolean
  durationFormat: DurationFormat
  busy: boolean
  highlighted: string | null
  onHighlight: (id: string | null) => void
  onSelect: (id: string) => void
  onAdd: (proposal: AgentProposal) => void
  onEdit: (proposal: AgentProposal) => void
  onDiscard: (proposal: AgentProposal) => void
  onUndo: (entry: Done) => void
}

/** One day: what it adds up to, the strip of the day, and a card for each proposal. */
function DaySection({
  day,
  proposals,
  done,
  catalog,
  timeZone,
  hour12,
  durationFormat,
  busy,
  highlighted,
  onHighlight,
  onSelect,
  ...actions
}: DaySectionProps) {
  const { workspaceId } = useWorkspace()
  const { data: entries = [] } = useQuery(dayEntriesQuery(workspaceId, day, timeZone))
  const waiting = proposals.filter((p) => !done.has(p.id))
  const added = waiting.reduce((sum, p) => sum + addedSeconds(p), 0)
  const heading = formatDayHeading(day, timeZone, new Date())

  return (
    <section aria-label={heading} className="grid gap-3">
      {/* The day stays in view while its cards scroll by, so pointing at one always shows where it falls. */}
      <div className="bg-popover sticky -top-4 z-30 -mx-4 -mt-4 grid gap-3 px-4 pt-4 pb-1 sm:-mx-5 sm:px-5">
        <div className="flex items-baseline justify-between gap-3">
          <h3 className="text-sm font-semibold">{heading}</h3>
          <div className="flex items-baseline gap-4">
            {waiting.length > 0 && <TimelineLegend changes={waiting.some((p) => p.entryId)} />}
            {waiting.length > 0 && added !== 0 && (
              <span className="text-muted-foreground text-xs font-medium tabular-nums">
                {added > 0 ? '+' : '−'}
                {formatDurationAs(Math.abs(added), durationFormat)} proposed
              </span>
            )}
          </div>
        </div>
        <DayTimeline
          day={day}
          entries={entries}
          proposals={waiting}
          catalog={catalog}
          timeZone={timeZone}
          hour12={hour12}
          highlighted={highlighted}
          onHighlight={onHighlight}
          onSelect={onSelect}
        />
      </div>
      <ul className="grid gap-2">
        {proposals.map((proposal) => {
          const entry = done.get(proposal.id)
          return entry ? (
            <DoneCard key={proposal.id} entry={entry} timeZone={timeZone} hour12={hour12} onUndo={() => actions.onUndo(entry)} />
          ) : (
            <ProposalCard
              key={proposal.id}
              proposal={proposal}
              catalog={catalog}
              timeZone={timeZone}
              hour12={hour12}
              durationFormat={durationFormat}
              busy={busy}
              lit={highlighted === proposal.id}
              onHighlight={onHighlight}
              onAdd={() => actions.onAdd(proposal)}
              onEdit={() => actions.onEdit(proposal)}
              onDiscard={() => actions.onDiscard(proposal)}
            />
          )
        })}
      </ul>
    </section>
  )
}

interface CardProps {
  proposal: AgentProposal
  catalog: Catalog
  timeZone: string
  hour12: boolean
  durationFormat: DurationFormat
  busy: boolean
  lit: boolean
  onHighlight: (id: string | null) => void
  onAdd: () => void
  onEdit: () => void
  onDiscard: () => void
}

/**
 * A proposal, dashed like its block in the strip because it isn't saved yet: what it is (the description), where it
 * goes, what a change changes, why the agent thinks so, and what stands in its way; its time and actions on the right.
 */
function ProposalCard({ proposal, catalog, timeZone, hour12, durationFormat, busy, lit, onHighlight, onAdd, onEdit, onDiscard }: CardProps) {
  const draft = draftOf(proposal)
  const lines = changeLines(proposal, catalog, timeZone, hour12)
  const blocked = proposal.blocking.length > 0
  const parts = draft ? partsOf(draft) : []
  const seconds = draft ? secondsOf(draft) : null
  const delta = proposal.original && seconds !== null ? seconds - (secondsOf(proposal.original) ?? seconds) : 0
  const describedParts = draft && isSplit(draft) && draft.shares.some((share) => share.description)
  const title = draft?.description || proposal.original?.description || ''

  return (
    <li
      tabIndex={0}
      data-proposal-id={proposal.id}
      aria-label={`${proposal.entryId ? 'Proposed change' : 'Proposed entry'}: ${title || 'No description'}`}
      onPointerEnter={() => onHighlight(proposal.id)}
      onPointerLeave={() => onHighlight(null)}
      // Only when moved to with the keyboard, not when the dialog opens on it.
      onFocus={(event) => event.target === event.currentTarget && event.currentTarget.matches(':focus-visible') && onHighlight(proposal.id)}
      onBlur={() => onHighlight(null)}
      className={cn(
        'focus-visible:ring-ring grid grid-cols-[4px_minmax(0,1fr)] gap-3 rounded-lg border border-dashed p-3 transition-colors outline-none focus-visible:ring-2 sm:grid-cols-[4px_minmax(0,1fr)_auto]',
        lit ? 'border-foreground/35 bg-accent/40' : 'bg-card',
        blocked && 'border-destructive/40',
      )}
    >
      <span aria-hidden="true" className="row-span-2 flex flex-col overflow-hidden rounded-full sm:row-span-1">
        {(parts.length > 0 ? parts : [{ projectId: null, percent: 100 }]).map((part, i) => (
          <span
            key={i}
            style={{
              height: `${part.percent}%`,
              background: partColor(catalog, part),
            }}
          />
        ))}
      </span>

      <div className="grid min-w-0 content-start gap-1.5">
        <p className="flex min-w-0 items-center gap-2">
          {proposal.entryId && <Badge variant="outline">Change</Badge>}
          <span className={cn('truncate font-semibold', !title && 'text-muted-foreground font-normal')}>{title || 'No description'}</span>
        </p>

        {draft &&
          (isSplit(draft) ? (
            <SplitLabel catalog={catalog} parts={parts} />
          ) : catalog.project(draft.projectId) ? (
            <ProjectLabel project={catalog.project(draft.projectId)!} task={catalog.task(draft.taskId)} />
          ) : (
            <span className="text-muted-foreground text-sm">No project</span>
          ))}

        {describedParts && (
          <ul className="grid gap-0.5 text-sm">
            {draft.shares.map((share, i) => (
              <li key={i} className="flex min-w-0 items-baseline gap-1.5">
                <span
                  aria-hidden="true"
                  className="size-1.5 shrink-0 -translate-y-px rounded-full"
                  style={{
                    background: partColor(catalog, share),
                  }}
                />
                <span className="text-muted-foreground shrink-0">{catalog.project(share.projectId)?.name ?? 'No project'}</span>
                <span className={cn('truncate', !share.description && 'text-muted-foreground')}>{share.description || 'No description'}</span>
              </li>
            ))}
          </ul>
        )}

        {lines.length > 0 && (
          <dl className="bg-muted/50 grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-1 rounded-md px-2.5 py-2 text-sm">
            {lines.map((line) => (
              <div key={line.label} className="contents">
                <dt className="text-muted-foreground">{line.label}</dt>
                <dd className="flex min-w-0 flex-wrap items-center gap-x-1.5">
                  <span className="text-muted-foreground line-through decoration-1">{line.from || 'None'}</span>
                  <ArrowRight aria-label="becomes" className="text-muted-foreground size-3.5 shrink-0" />
                  <span className="font-medium">{line.to || 'None'}</span>
                </dd>
              </div>
            ))}
          </dl>
        )}

        {proposal.reason && (
          <p className="text-muted-foreground flex items-start gap-1.5 text-sm">
            <MessageSquareText aria-hidden="true" className="mt-0.5 size-3.5 shrink-0" />
            <span>
              {proposal.reason}
              {clientName(proposal.client) && <span className="text-muted-foreground/70"> · {clientName(proposal.client)}</span>}
            </span>
          </p>
        )}

        {(blocked || proposal.warnings.length > 0) && (
          <ul className="grid gap-1 text-sm">
            {proposal.blocking.map((message) => (
              <li key={message} className="text-destructive flex items-start gap-1.5">
                <Ban aria-hidden="true" className="mt-0.5 size-3.5 shrink-0" />
                {message}
              </li>
            ))}
            {proposal.warnings.map((message) => (
              <li key={message} className="flex items-start gap-1.5 text-orange-600 dark:text-orange-400">
                <AlertTriangle aria-hidden="true" className="mt-0.5 size-3.5 shrink-0" />
                {message}
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="col-start-2 flex flex-wrap items-center justify-between gap-x-4 gap-y-2 sm:col-start-3 sm:grid sm:content-start sm:justify-items-end sm:gap-1">
        {draft && (
          <div className="grid tabular-nums sm:justify-items-end">
            <span className="text-muted-foreground text-sm">
              {formatClock(draft.start, timeZone, hour12)} – {draft.stop ? formatClock(draft.stop, timeZone, hour12) : 'now'}
            </span>
            <span className="flex items-baseline gap-1.5">
              {delta !== 0 && (
                <span className={cn('text-xs', delta > 0 ? 'text-primary-text' : 'text-muted-foreground')}>
                  {delta > 0 ? '+' : '−'}
                  {formatHoursMinutes(Math.abs(delta))}
                </span>
              )}
              {seconds !== null && <span className="font-semibold">{formatDurationAs(seconds, durationFormat)}</span>}
            </span>
          </div>
        )}
        <div className="flex items-center gap-1 sm:mt-1 sm:grid sm:justify-items-end sm:gap-1">
          <Button
            type="button"
            size="sm"
            className="sm:min-w-16"
            disabled={busy || blocked}
            onClick={onAdd}
            title={blocked ? 'Edit it first, or discard it.' : proposal.entryId ? 'Change the entry as proposed (A)' : 'Add the entry as proposed (A)'}
          >
            {proposal.entryId ? 'Apply' : 'Add'}
          </Button>
          <div className="flex items-center">
            <Button type="button" variant="ghost" size="sm" disabled={busy || !draft} onClick={onEdit} title="Open it in the editor (E)">
              Edit…
            </Button>
            <Button type="button" variant="ghost" size="icon-sm" disabled={busy} onClick={onDiscard} aria-label="Discard" title="Discard (⌫)">
              <X />
            </Button>
          </div>
        </div>
      </div>
    </li>
  )
}

const DONE_LABELS = {
  added: 'Added',
  changed: 'Changed',
  edited: 'Saved after editing',
  discarded: 'Discarded',
} as const

/** A proposal dealt with: what became of it, as saved, with Undo while the review is open. */
function DoneCard({ entry, timeZone, hour12, onUndo }: { entry: Done; timeZone: string; hour12: boolean; onUndo: () => void }) {
  const shown = entry.kind === 'edited' ? entry.entry : entry.kind === 'discarded' ? draftOf(entry.proposal) : entry.saved.entry
  const discarded = entry.kind === 'discarded'
  return (
    <li className={cn('flex min-w-0 items-center gap-2 rounded-lg border px-3 py-2 text-sm', discarded ? 'text-muted-foreground border-dashed' : 'bg-card')}>
      {discarded ? <Ban aria-hidden="true" className="size-3.5 shrink-0" /> : <Check aria-hidden="true" className="text-primary-text size-4 shrink-0" />}
      <span className="shrink-0 font-medium">{DONE_LABELS[entry.kind]}</span>
      {shown && (
        <span className={cn('min-w-0 truncate', discarded ? 'line-through decoration-1' : 'text-muted-foreground')}>
          <span className="tabular-nums">
            {formatClock(shown.start, timeZone, hour12)}
            {shown.stop && `–${formatClock(shown.stop, timeZone, hour12)}`}
          </span>
          {shown.description && ` · ${shown.description}`}
        </span>
      )}
      <Button type="button" variant="ghost" size="xs" className="ml-auto shrink-0" onClick={onUndo}>
        <Undo2 />
        Undo
      </Button>
    </li>
  )
}
