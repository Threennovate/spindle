import { cn } from 'cn'
import { ArrowRight, Check, ChevronLeft, ChevronRight, CircleCheck, Sparkles, Undo2 } from 'lucide-react'
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import type { TimeEntry } from '@/api/types'
import { Kbd } from '@/components/kbd'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { errorMessage } from '@/lib/error-message'
import { formatDurationAs, type DurationFormat } from '@/lib/time/duration'
import { addDaysToKey, dayKey, formatClock, formatDayHeading } from '@/lib/time/zone'
import { formatRange, weekDays } from '../calendar/range'
import { ProjectLabel } from '../catalog/project-label'
import { SplitLabel } from '../catalog/split-label'
import { useCatalog, type Catalog } from '../catalog/use-catalog'
import { usePermissions } from '../settings/queries'
import { EntryDialog } from '../time-entries/entry-dialog'
import { isSplit, partsOf } from '../time-entries/split'
import { useWorkspace } from '../workspace'
import { EvidenceStrip } from './evidence-strip'
import { actionTitle, breakdown, draftWithParts, outcomes, takePlan, takenTitle, type Outcome, type ReviewItem } from './review'
import { useReviewChoices, useReviewData, useReviewSaving, type Saved } from './review-data'
import type { ReviewScope } from './review-events'
import { ReviewStrip } from './review-strip'
import { LinkRepository } from './suggestion-banner'
import { chipTime, suggestedParts, suggestedStretches, targetColor, targetName, weakNote } from './suggestion'
import { untrackedEntry } from './untracked'

/** What the person did with an item while the review is open; it stays in its place, and a take can be undone. */
type Done = { item: ReviewItem } & ({ kind: 'taken'; title: string; saved: Saved } | { kind: 'changed'; entryId: string } | { kind: 'notNow' })

/** The last thing done, said in the footer with Undo: one take, or everything "Take all" took. */
interface Notice {
  text: string
  /** The items it covers, to take back together. */
  ids: string[]
  error?: boolean
}

interface DayReviewProps {
  scope: ReviewScope | null
  onScopeChange: (scope: ReviewScope) => void
  onClose: () => void
}

/**
 * The day's review, as on the Mac: every suggestion for a day (or a week) in its place on the day, between the day's
 * other entries, to take, change or put off one by one, with the keyboard, or all at once. Each row says what the time
 * is now and what it becomes; nothing is saved without a click, and every take can be undone until the review closes.
 */
export function DayReview({ scope, onScopeChange, onClose }: DayReviewProps) {
  const [editing, setEditing] = useState<ReviewItem | null>(null)
  const [done, setDone] = useState<Map<string, Done>>(new Map())
  const [notice, setNotice] = useState<Notice | null>(null)

  function close() {
    setDone(new Map())
    setNotice(null)
    onClose()
  }

  return (
    <Dialog open={!!scope && !editing} onOpenChange={(open) => !open && close()}>
      {scope && (
        <ReviewContent
          scope={scope}
          onScopeChange={onScopeChange}
          done={done}
          setDone={setDone}
          notice={notice}
          setNotice={setNotice}
          editing={editing}
          setEditing={setEditing}
          onClose={close}
        />
      )}
    </Dialog>
  )
}

interface ContentProps {
  scope: ReviewScope
  onScopeChange: (scope: ReviewScope) => void
  done: Map<string, Done>
  setDone: (update: (current: Map<string, Done>) => Map<string, Done>) => void
  notice: Notice | null
  setNotice: (notice: Notice | null) => void
  editing: ReviewItem | null
  setEditing: (item: ReviewItem | null) => void
  onClose: () => void
}

function ReviewContent({ scope, onScopeChange, done, setDone, notice, setNotice, editing, setEditing, onClose }: ContentProps) {
  const { workspaceId, timeZone, hour12, weekStartsOn, durationFormat } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  const { settings } = usePermissions()
  const data = useReviewData(scope.days)
  const { putOff } = useReviewChoices()
  const saving = useReviewSaving(workspaceId)
  const [busy, setBusy] = useState<ReadonlySet<string>>(new Set())
  const [takingAll, setTakingAll] = useState(false)
  const [selected, setSelected] = useState<string | null>(null)
  const listRef = useRef<HTMLDivElement>(null)
  // A row to focus once it is on screen, e.g. the next one after a take.
  const focusNext = useRef<string | null>(null)

  const isWeek = scope.days.length > 1
  const today = dayKey(new Date(), timeZone)
  const inScope = (day: string) => scope.days.includes(day)
  const waiting = data.items.filter((item) => !done.has(item.id))
  const handled = [...done.values()].filter((entry) => inScope(entry.item.day))
  const plans = useMemo(() => new Map(waiting.map((item) => [item.id, takePlan(item, { catalog, settings })])), [waiting, catalog, settings])
  const takeable = waiting.filter((item) => plans.get(item.id)?.status === 'ready' && !busy.has(item.id))
  const total = waiting.length + handled.length

  // The selected item, or the first while none is: the review opens on it, ready for the keyboard.
  const current = waiting.some((item) => item.id === selected) ? selected : (waiting[0]?.id ?? null)
  const openedOn = useRef(false)
  function focusRow(id: string | null) {
    const row = id && listRef.current?.querySelector<HTMLElement>(`[data-review-id="${CSS.escape(id)}"]`)
    if (row) {
      row.focus({ preventScroll: true })
      row.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
      openedOn.current = true
    }
    return !!row
  }
  useEffect(() => {
    if (focusRow(focusNext.current ?? (!openedOn.current && data.loaded ? current : null))) {
      focusNext.current = null
    }
  })

  /** After an item is dealt with, the one after it is selected, or the one before at the end. */
  function moveOn(id: string) {
    const ids = waiting.map((item) => item.id)
    const index = ids.indexOf(id)
    const next = ids[index + 1] ?? ids[index - 1] ?? null
    setSelected(next)
    focusNext.current = next
  }

  function mark(entry: Done) {
    setDone((current) => new Map(current).set(entry.item.id, entry))
  }

  function unmark(ids: readonly string[]) {
    setDone((current) => {
      const next = new Map(current)
      ids.forEach((id) => next.delete(id))
      return next
    })
  }

  async function take(item: ReviewItem) {
    const plan = plans.get(item.id)
    if (busy.has(item.id) || takingAll) {
      return
    }
    if (plan?.status !== 'ready') {
      setEditing(item)
      return
    }
    setBusy((current) => new Set(current).add(item.id))
    try {
      const saved = await saving.save(plan.requests)
      const title = takenTitle(item, catalog, timeZone, hour12)
      mark({ item, kind: 'taken', title, saved })
      setNotice({ text: title, ids: [item.id] })
      moveOn(item.id)
    } catch (error) {
      setNotice({ text: errorMessage(error, "Couldn't save it."), ids: [], error: true })
    } finally {
      setBusy((current) => {
        const next = new Set(current)
        next.delete(item.id)
        return next
      })
    }
  }

  /** Takes every item one click can take, one after another, with one Undo for all of them; the rest stay. */
  async function takeAll() {
    const items = takeable
    setTakingAll(true)
    setBusy(new Set(items.map((item) => item.id)))
    const taken: string[] = []
    let failed = 0
    for (const item of items) {
      const plan = plans.get(item.id)
      if (plan?.status !== 'ready') {
        continue
      }
      try {
        const saved = await saving.save(plan.requests)
        mark({ item, kind: 'taken', title: takenTitle(item, catalog, timeZone, hour12), saved })
        taken.push(item.id)
      } catch {
        failed += 1
      }
    }
    setBusy(new Set())
    setTakingAll(false)
    const left = waiting.length - taken.length - failed
    const text = [
      taken.length === 1 ? 'Saved 1 suggestion' : `Saved ${taken.length} suggestions`,
      failed > 0 && `${failed} couldn't be saved`,
      left > 0 && `${left} ${left === 1 ? 'needs' : 'need'} a project or details`,
    ]
      .filter(Boolean)
      .join(' · ')
    setNotice({ text, ids: taken, error: taken.length === 0 && failed > 0 })
    const next = waiting.find((item) => !taken.includes(item.id))
    setSelected(next?.id ?? null)
    focusNext.current = next?.id ?? null
  }

  function notNow(item: ReviewItem) {
    putOff.add(item.id)
    mark({ item, kind: 'notNow' })
    moveOn(item.id)
  }

  /** Takes back what was done with these items: takes are undone, "Not now" brings the item back. */
  async function undo(ids: readonly string[]) {
    const entries = ids.map((id) => done.get(id)).filter((entry) => entry !== undefined)
    try {
      await saving.undo(entries.flatMap((entry) => (entry.kind === 'taken' ? [entry.saved] : [])))
      entries.filter((entry) => entry.kind === 'notNow').forEach((entry) => putOff.remove(entry.item.id))
      unmark(ids)
      setNotice(null)
      const first = entries.map((entry) => entry.item).sort((a, b) => Date.parse(a.start) - Date.parse(b.start))[0]
      if (first) {
        setSelected(first.id)
        focusNext.current = first.id
      }
    } catch (error) {
      setNotice({ text: errorMessage(error, "Couldn't undo."), ids: [], error: true })
    }
  }

  /** ↑↓ move between items, Enter takes, E changes, Delete or Backspace puts off; on the item itself, not its buttons. */
  function handleKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const row = (event.target as HTMLElement).closest<HTMLElement>('[data-review-id]')
    if (event.target === listRef.current && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
      // The list itself, while nothing in it has the focus yet.
      event.preventDefault()
      focusRow(current)
      return
    }
    if (!row || event.target !== row || event.metaKey || event.ctrlKey || event.altKey) {
      return
    }
    const index = waiting.findIndex((item) => item.id === row.dataset.reviewId)
    const item = waiting[index]
    const key = event.key.toLowerCase()
    if (key === 'arrowdown' || key === 'arrowup') {
      event.preventDefault()
      const next = waiting[Math.min(waiting.length - 1, Math.max(0, index + (key === 'arrowdown' ? 1 : -1)))]
      if (next) {
        setSelected(next.id)
        focusNext.current = next.id
      }
    } else if (item && key === 'enter') {
      event.preventDefault()
      void take(item)
    } else if (item && key === 'e') {
      event.preventDefault()
      setEditing(item)
    } else if (item && (key === 'delete' || key === 'backspace')) {
      event.preventDefault()
      notNow(item)
    }
  }

  function move(steps: number) {
    const first = addDaysToKey(scope.days[0], steps * (isWeek ? 7 : 1))
    onScopeChange({ days: isWeek ? weekDays(first, timeZone, weekStartsOn) : [first] })
    setSelected(null)
    setNotice(null)
  }

  function switchTo(week: boolean) {
    if (week === isWeek) {
      return
    }
    // From a week to the day the selection is on, or the last day shown up to today.
    const day = waiting.find((item) => item.id === current)?.day ?? [...scope.days].reverse().find((key) => key <= today) ?? scope.days[0]
    onScopeChange({ days: week ? weekDays(day, timeZone, weekStartsOn) : [day] })
    setSelected(null)
  }

  const days = [...new Set([...waiting.map((item) => item.day), ...handled.map((entry) => entry.item.day)])].sort()
  const shownDays = isWeek ? days : scope.days
  // "Yesterday · Wednesday, 14 October 2026", or the date alone for other days.
  const heading = formatDayHeading(scope.days[0], timeZone, new Date())
  const title = isWeek || !['Today', 'Yesterday'].includes(heading) ? formatRange(scope.days) : `${heading} · ${formatRange(scope.days)}`
  const allDone = data.loaded && waiting.length === 0 && handled.length > 0
  const subtitle = !data.loaded
    ? 'Loading suggestions…'
    : waiting.length > 0
      ? breakdown(waiting)
      : allDone
        ? 'All done'
        : isWeek
          ? 'Nothing to review this week'
          : 'Nothing to review'

  return (
    <>
      <DialogContent
        // One size whatever the day holds, so going from day to day doesn't make it jump.
        className="grid h-[min(92dvh,46rem)] grid-rows-[auto_minmax(0,1fr)_auto] gap-0 overflow-hidden p-0 sm:max-w-3xl"
        // The first item takes the focus, rather than the close button; the list while it loads.
        onOpenAutoFocus={(event) => {
          event.preventDefault()
          if (!focusRow(current)) {
            listRef.current?.focus()
          }
        }}
      >
        <DialogHeader className="gap-3 border-b p-4 text-left sm:px-5">
          <div className="flex flex-wrap items-center gap-x-3 gap-y-3">
            <span aria-hidden="true" className="bg-primary/15 text-primary-text grid size-10 shrink-0 place-items-center rounded-lg">
              <Sparkles className="size-5" />
            </span>
            <div className="grid min-w-0 flex-1 gap-0.5">
              <DialogTitle className="truncate text-base">{title}</DialogTitle>
              <DialogDescription>{subtitle}</DialogDescription>
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <div className="flex items-center">
                <Button type="button" variant="ghost" size="icon-sm" onClick={() => move(-1)} aria-label={isWeek ? 'Previous week' : 'Previous day'} title={isWeek ? 'Previous week' : 'Previous day'}>
                  <ChevronLeft />
                </Button>
                <Button
                  type="button"
                  variant="ghost"
                  size="icon-sm"
                  onClick={() => move(1)}
                  disabled={scope.days.includes(today)}
                  aria-label={isWeek ? 'Next week' : 'Next day'}
                  title={isWeek ? 'Next week' : 'Next day'}
                >
                  <ChevronRight />
                </Button>
              </div>
              <ToggleGroup type="single" variant="outline" size="sm" value={isWeek ? 'week' : 'day'} onValueChange={(value) => value && switchTo(value === 'week')} aria-label="Review">
                <ToggleGroupItem value="day">Day</ToggleGroupItem>
                <ToggleGroupItem value="week">Week</ToggleGroupItem>
              </ToggleGroup>
              <Button
                type="button"
                size="sm"
                disabled={takeable.length === 0 || takingAll}
                onClick={() => void takeAll()}
                title={
                  takeable.length < waiting.length
                    ? 'Save every suggestion here as offered; those that need a project or details stay, with one Undo for all'
                    : 'Save every suggestion here as offered, with one Undo for all'
                }
              >
                {takingAll ? 'Saving…' : `Take all (${takeable.length})`}
              </Button>
            </div>
          </div>
          {total > 0 && <Progress done={handled.length} total={total} />}
        </DialogHeader>

        <div ref={listRef} tabIndex={-1} className="grid content-start gap-7 overflow-y-auto p-4 outline-none sm:px-5" onKeyDown={handleKeyDown}>
          <p id="review-keys" className="sr-only">
            Enter takes it, E opens it in the editor, Delete leaves it for now, and the arrow keys move between suggestions.
          </p>
          {data.failed ? (
            <div className="text-muted-foreground grid justify-items-center gap-3 py-12 text-sm">
              <p>Couldn't load the suggestions.</p>
              <Button variant="outline" size="sm" onClick={() => void data.retry()}>
                Try again
              </Button>
            </div>
          ) : !data.loaded ? (
            <div className="grid gap-2" aria-busy="true" aria-label="Loading suggestions">
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-24 w-full" />
              <Skeleton className="h-24 w-full" />
            </div>
          ) : total === 0 ? (
            <div className="grid justify-items-center gap-1.5 py-14 text-center">
              <CircleCheck aria-hidden="true" className="text-muted-foreground mb-1 size-7" />
              <p className="font-medium">Nothing to review</p>
              <p className="text-muted-foreground max-w-sm text-sm">
                {isWeek ? 'No splits or untracked agent time on these days.' : 'No splits or untracked agent time on this day.'}
              </p>
            </div>
          ) : (
            <>
              {allDone && (
                <div role="status" className="bg-primary/8 border-primary/25 flex items-center gap-3 rounded-lg border px-3 py-2.5">
                  <CircleCheck aria-hidden="true" className="text-primary-text size-5 shrink-0" />
                  <p className="text-sm">
                    <span className="font-semibold">All done.</span>
                    <span className="text-muted-foreground"> Everything here is answered. You can undo until you close the review.</span>
                  </p>
                </div>
              )}
              {shownDays
                .filter((day) => days.includes(day) || !isWeek)
                .map((day) => (
                  <DaySection
                    key={day}
                    day={day}
                    isWeek={isWeek}
                    entries={data.entries}
                    waiting={waiting.filter((item) => item.day === day)}
                    handled={handled.filter((entry) => entry.item.day === day)}
                    plans={plans}
                    catalog={catalog}
                    timeZone={timeZone}
                    hour12={hour12}
                    durationFormat={durationFormat}
                    selected={current}
                    busy={busy}
                    onSelect={(id) => {
                      setSelected(id)
                      focusNext.current = id
                    }}
                    onTake={(item) => void take(item)}
                    onChange={setEditing}
                    onNotNow={notNow}
                    onUndo={(id) => void undo([id])}
                  />
                ))}
            </>
          )}
        </div>

        <DialogFooter className="static m-0 flex-row flex-wrap items-center justify-between gap-3 sm:justify-between sm:px-5">
          <div aria-live="polite" className="min-w-0 flex-1">
            {notice ? (
              <p className={cn('flex min-w-0 items-center gap-2 text-sm', notice.error && 'text-destructive')}>
                {!notice.error && <Check aria-hidden="true" className="text-primary-text size-4 shrink-0" />}
                <span className="truncate">{notice.text}</span>
                {notice.ids.length > 0 && (
                  <Button type="button" variant="ghost" size="xs" onClick={() => void undo(notice.ids)}>
                    <Undo2 />
                    Undo
                  </Button>
                )}
              </p>
            ) : (
              waiting.length > 0 && (
                <p className="text-muted-foreground hidden items-center gap-1 text-xs md:flex">
                  <Kbd>↑</Kbd>
                  <Kbd>↓</Kbd> move · <Kbd>↵</Kbd> take · <Kbd>E</Kbd> change · <Kbd>⌫</Kbd> not now
                </p>
              )
            )}
          </div>
          <Button type="button" variant="outline" onClick={onClose}>
            Close
          </Button>
        </DialogFooter>
      </DialogContent>

      {editing && (
        <ChangeDialog
          item={editing}
          catalog={catalog}
          onSaved={(entry) => {
            mark({ item: editing, kind: 'changed', entryId: entry.id })
            moveOn(editing.id)
          }}
          onClose={() => {
            setEditing(null)
            focusNext.current = current
          }}
        />
      )}
    </>
  )
}

/** "2 of 5 done", as a thin bar under the header. */
function Progress({ done, total }: { done: number; total: number }) {
  return (
    <div className="flex items-center gap-3">
      <div
        role="progressbar"
        aria-label="Reviewed"
        aria-valuemin={0}
        aria-valuemax={total}
        aria-valuenow={done}
        aria-valuetext={`${done} of ${total} done`}
        className="bg-muted h-1 flex-1 overflow-hidden rounded-full"
      >
        <div className="bg-primary h-full rounded-full transition-[width] duration-300" style={{ width: `${(done / total) * 100}%` }} />
      </div>
      <span className="text-muted-foreground shrink-0 text-xs tabular-nums">
        {done} of {total} done
      </span>
    </div>
  )
}

/** The editor with what the item offers filled in; nothing is saved until it is saved there. */
function ChangeDialog({ item, catalog, onSaved, onClose }: { item: ReviewItem; catalog: Catalog; onSaved: (entry: TimeEntry) => void; onClose: () => void }) {
  if (item.kind === 'untracked') {
    return <EntryDialog entry={null} newEntry={untrackedEntry(item.block, catalog)} onSaved={onSaved} onClose={onClose} />
  }
  const stretches = item.suggestion.stretches ? suggestedStretches(item.suggestion) : null
  const parts = !stretches ? suggestedParts(item.suggestion, catalog) : null
  // With a repository still without a project, the editor shows the suggestion as it is, to choose one there.
  return <EntryDialog entry={item.entry} draft={parts ? draftWithParts(item.entry, parts) : null} stretches={stretches} onSaved={onSaved} onClose={onClose} />
}

interface DaySectionProps {
  day: string
  isWeek: boolean
  entries: readonly TimeEntry[]
  waiting: ReviewItem[]
  handled: Done[]
  plans: Map<string, ReturnType<typeof takePlan>>
  catalog: Catalog
  timeZone: string
  hour12: boolean
  durationFormat: DurationFormat
  selected: string | null
  busy: ReadonlySet<string>
  onSelect: (id: string) => void
  onTake: (item: ReviewItem) => void
  onChange: (item: ReviewItem) => void
  onNotNow: (item: ReviewItem) => void
  onUndo: (id: string) => void
}

type Row = { kind: 'item'; item: ReviewItem } | { kind: 'done'; done: Done } | { kind: 'entry'; entry: TimeEntry }

/**
 * One day: a strip of it, then its items in the order of the day. A single day shows its other entries too, as quiet
 * rows between the items, so each sits in its place; a week shows the items only.
 */
function DaySection({ day, isWeek, entries, waiting, handled, plans, catalog, timeZone, hour12, durationFormat, selected, busy, onSelect, ...actions }: DaySectionProps) {
  const dayEntries = entries.filter((entry) => dayKey(entry.start, timeZone) === day || (entry.stop && dayKey(entry.stop, timeZone) === day && dayKey(entry.start, timeZone) < day))
  // Entries an item is about, or that a take or the editor made, belong to that item's row.
  const own = new Set([
    ...waiting.flatMap((item) => (item.kind === 'split' ? [item.entry.id] : [])),
    ...handled.flatMap((entry) => [
      ...(entry.item.kind === 'split' ? [entry.item.entry.id] : []),
      ...(entry.kind === 'taken' ? entry.saved.added : entry.kind === 'changed' ? [entry.entryId] : []),
    ]),
  ])
  const rows: Row[] = [
    ...waiting.map((item): Row => ({ kind: 'item', item })),
    ...handled.map((done): Row => ({ kind: 'done', done })),
    ...(isWeek ? [] : dayEntries.filter((entry) => !own.has(entry.id)).map((entry): Row => ({ kind: 'entry', entry }))),
  ].sort((a, b) => Date.parse(rowStart(a)) - Date.parse(rowStart(b)))
  const heading = formatDayHeading(day, timeZone, new Date())

  return (
    <section aria-label={heading} className="grid gap-3">
      {isWeek && (
        <h3 className="flex items-baseline gap-2 text-sm font-semibold">
          {heading}
          {waiting.length > 0 && <span className="text-muted-foreground text-xs font-normal">{waiting.length} to review</span>}
        </h3>
      )}
      <ReviewStrip day={day} entries={dayEntries} items={waiting} catalog={catalog} timeZone={timeZone} hour12={hour12} selected={selected} onSelect={onSelect} />
      <ul className="grid gap-2">
        {rows.map((row) =>
          row.kind === 'item' ? (
            <ItemRow
              key={row.item.id}
              item={row.item}
              plan={plans.get(row.item.id)!}
              catalog={catalog}
              timeZone={timeZone}
              hour12={hour12}
              durationFormat={durationFormat}
              selected={selected === row.item.id}
              busy={busy.has(row.item.id)}
              onFocus={() => onSelect(row.item.id)}
              onTake={() => actions.onTake(row.item)}
              onChange={() => actions.onChange(row.item)}
              onNotNow={() => actions.onNotNow(row.item)}
            />
          ) : row.kind === 'done' ? (
            <DoneRow key={row.done.item.id} done={row.done} timeZone={timeZone} hour12={hour12} onUndo={() => actions.onUndo(row.done.item.id)} />
          ) : (
            <ContextRow key={row.entry.id} entry={row.entry} catalog={catalog} timeZone={timeZone} hour12={hour12} />
          ),
        )}
      </ul>
    </section>
  )
}

function rowStart(row: Row): string {
  return row.kind === 'item' ? row.item.start : row.kind === 'done' ? row.done.item.start : row.entry.start
}

interface ItemRowProps {
  item: ReviewItem
  plan: ReturnType<typeof takePlan>
  catalog: Catalog
  timeZone: string
  hour12: boolean
  durationFormat: DurationFormat
  selected: boolean
  busy: boolean
  onFocus: () => void
  onTake: () => void
  onChange: () => void
  onNotNow: () => void
}

/**
 * One suggestion: what the time is now and what it becomes, with its evidence, and its answers. The main button is
 * what one click can do: take it ("Split", "Split at 10:42", "Add entry"), or, when one click can't, "Change…".
 */
function ItemRow({ item, plan, catalog, timeZone, hour12, durationFormat, selected, busy, onFocus, onTake, onChange, onNotNow }: ItemRowProps) {
  const { workspaceId } = useWorkspace()
  const ready = plan.status === 'ready'
  const entry = item.kind === 'split' ? item.entry : null
  const project = entry ? catalog.project(entry.projectId) : undefined
  const seconds = (Date.parse(item.end) - Date.parse(item.start)) / 1000
  const times = `${formatClock(item.start, timeZone, hour12)} – ${formatClock(item.end, timeZone, hour12)}`
  const action = actionTitle(item, timeZone, hour12)
  const unlinked = item.kind === 'split' ? item.suggestion.parts.flatMap((part) => (part.target.repository && !part.target.projectId ? [part.target.repository] : [])) : []
  const weak = item.kind === 'split' ? weakNote(item.suggestion) : null
  const reason =
    plan.status === 'details'
      ? `${plan.reason}. Change… to add it.`
      : plan.status === 'unlinked' && item.kind === 'untracked'
        ? 'In repositories without a project. Change… to choose one.'
        : null
  const label = `${item.kind === 'split' ? 'Split' : 'Untracked time'}: ${entry ? entry.description || 'No description' : 'agents were at work'}, ${times}. Becomes ${outcomeWords(outcomes(item), catalog, timeZone, hour12)}.`

  return (
    <li
      tabIndex={selected ? 0 : -1}
      data-review-id={item.id}
      aria-label={label}
      aria-describedby="review-keys"
      aria-current={selected || undefined}
      onFocus={(event) => event.target === event.currentTarget && onFocus()}
      onClick={(event) => !(event.target as HTMLElement).closest('button, a, input') && event.currentTarget.focus()}
      className={cn(
        'grid grid-cols-[4px_minmax(0,1fr)] gap-3 rounded-lg border p-3 transition-colors outline-none sm:grid-cols-[4px_minmax(0,1fr)_auto]',
        selected ? 'border-primary/70 bg-primary/6 ring-primary/30 ring-2' : 'bg-card hover:border-foreground/20',
        busy && 'opacity-60',
      )}
    >
      <span
        aria-hidden="true"
        className="row-span-2 rounded-full sm:row-span-1"
        style={
          entry
            ? { background: project?.color ?? 'var(--muted-foreground)' }
            : { background: 'repeating-linear-gradient(-45deg, var(--muted-foreground) 0 1.5px, transparent 1.5px 4px)', opacity: 0.6 }
        }
      />

      <div className="grid min-w-0 content-start gap-2">
        <p className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5">
          {entry ? (
            <>
              <span className={cn('truncate font-semibold', !entry.description && 'text-muted-foreground font-normal')}>{entry.description || 'No description'}</span>
              {project && <ProjectLabel project={project} task={catalog.task(entry.taskId)} className="text-sm" />}
            </>
          ) : (
            <>
              <span className="font-semibold">Not tracked</span>
              <span className="text-muted-foreground text-sm">· agents were at work</span>
            </>
          )}
        </p>

        <ChangeLine item={item} catalog={catalog} timeZone={timeZone} hour12={hour12} />

        {item.kind === 'split' && (
          <div className="max-w-sm">
            <EvidenceStrip suggestion={item.suggestion} catalog={catalog} timeZone={timeZone} hour12={hour12} showTimes={false} />
          </div>
        )}
        {(weak || reason || unlinked.length > 0) && (
          <div className="text-muted-foreground flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
            {weak && <span>{weak}</span>}
            {reason && <span>{reason}</span>}
            {unlinked.map((repository) => (
              <LinkRepository key={repository.identity} workspaceId={workspaceId} repository={repository} label={`Choose a project for ${repository.name}`} />
            ))}
          </div>
        )}
      </div>

      <div className="col-start-2 flex flex-wrap items-center justify-between gap-x-4 gap-y-2 sm:col-start-3 sm:grid sm:content-start sm:justify-items-end sm:gap-2">
        <p className="flex items-baseline gap-2 tabular-nums">
          <span className="text-muted-foreground text-sm">{times}</span>
          <span className="font-semibold">{formatDurationAs(seconds, durationFormat)}</span>
        </p>
        <div className="flex items-center gap-1">
          <Button type="button" variant="ghost" size="sm" className="text-muted-foreground" disabled={busy} onClick={onNotNow} title="Leave it out of this review; nothing changes (⌫)">
            Not now
          </Button>
          {ready ? (
            <>
              <Button type="button" variant="outline" size="sm" disabled={busy} onClick={onChange} title="Open it in the editor with this filled in (E)">
                Change…
              </Button>
              <Button type="button" size="sm" disabled={busy} onClick={onTake} title="Save it as offered, with Undo (↵)">
                {action}
              </Button>
            </>
          ) : (
            <Button type="button" size="sm" disabled={busy} onClick={onChange} title="One click can't save this; open it in the editor (↵ or E)">
              Change…
            </Button>
          )}
        </div>
      </div>
    </li>
  )
}

/** "● Atlas 1:30 → ● Atlas 0:40 · ● Beacon 0:50": the time as it is, struck through, and what it becomes. */
function ChangeLine({ item, catalog, timeZone, hour12 }: { item: ReviewItem; catalog: Catalog; timeZone: string; hour12: boolean }) {
  const results = outcomes(item)
  const seconds = (Date.parse(item.end) - Date.parse(item.start)) / 1000
  const entry = item.kind === 'split' ? item.entry : null
  return (
    <div className="bg-muted/50 flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 rounded-md px-2.5 py-1.5 text-sm">
      <Sparkles aria-hidden="true" className={cn('size-3.5 shrink-0', item.kind === 'split' && item.suggestion.strength !== 'strong' ? 'text-muted-foreground' : 'text-primary-text')} />
      <span className="text-muted-foreground">
        {entry ? <Piece color={catalog.project(entry.projectId)?.color} name={catalog.project(entry.projectId)?.name ?? 'No project'} time={chipTime(seconds)} struck /> : 'Nothing tracked'}
      </span>
      <ArrowRight aria-label="becomes" className="text-muted-foreground size-3.5 shrink-0" />
      {results.length > 1 && <span className="text-muted-foreground">{results.length} entries:</span>}
      {results.length === 1 && !entry && <span className="text-muted-foreground">New entry</span>}
      <span className="flex min-w-0 flex-wrap items-center gap-x-1.5 gap-y-1 font-medium">
        {results.map((result, index) => (
          <span key={index} className="inline-flex items-center gap-1.5">
            {index > 0 && <span className="text-muted-foreground font-normal">·</span>}
            {result.parts.map((part, partIndex) => (
              <span key={partIndex} className="inline-flex items-center gap-1.5">
                {partIndex > 0 && <span className="text-muted-foreground font-normal">·</span>}
                <Piece
                  color={targetColor(part.target, catalog)}
                  name={part.target.projectId || part.target.repository ? targetName(part.target, catalog) : 'No project'}
                  time={results.length > 1 ? `${formatClock(result.start, timeZone, hour12)}–${formatClock(result.end, timeZone, hour12)}` : chipTime(part.seconds)}
                />
              </span>
            ))}
          </span>
        ))}
      </span>
    </div>
  )
}

/** "● Atlas 0:40": a project or repository with its time; struck through for the time as it is now. */
function Piece({ color, name, time, struck = false }: { color: string | undefined; name: string; time: string; struck?: boolean }) {
  return (
    <span className="inline-flex items-center gap-1.5">
      <span aria-hidden="true" className={cn('size-2 shrink-0 rounded-full', struck && 'opacity-60')} style={{ background: color ?? 'var(--muted-foreground)' }} />
      <span className={cn(struck && 'line-through decoration-1')}>{name}</span>
      <span className={cn('text-muted-foreground font-normal tabular-nums', struck && 'line-through decoration-1')}>{time}</span>
    </span>
  )
}

/** The outcome in words, to read out: "2 entries: Atlas 09:00–09:40, Beacon 09:40–10:30". */
function outcomeWords(results: Outcome[], catalog: Catalog, timeZone: string, hour12: boolean): string {
  const name = (part: Outcome['parts'][number]) => (part.target.projectId || part.target.repository ? targetName(part.target, catalog) : 'no project')
  if (results.length > 1) {
    return `${results.length} entries: ${results.map((result) => `${name(result.parts[0])} ${formatClock(result.start, timeZone, hour12)} to ${formatClock(result.end, timeZone, hour12)}`).join(', ')}`
  }
  return results[0].parts.map((part) => `${name(part)} ${chipTime(part.seconds)}`).join(', ')
}

const DONE_LABELS = { changed: 'Saved in the editor', notNow: 'Not now' } as const

/** An item dealt with, in its place: what became of it, with Undo while the review is open. */
function DoneRow({ done, timeZone, hour12, onUndo }: { done: Done; timeZone: string; hour12: boolean; onUndo: () => void }) {
  const { item } = done
  const putOff = done.kind === 'notNow'
  const description = item.kind === 'split' ? item.entry.description || 'No description' : 'Not tracked'
  return (
    <li className={cn('flex min-w-0 items-center gap-2 rounded-lg border px-3 py-2 text-sm', putOff ? 'text-muted-foreground border-dashed' : 'bg-card')}>
      {!putOff && <Check aria-hidden="true" className="text-primary-text size-4 shrink-0" />}
      <span className="shrink-0 font-medium">{done.kind === 'taken' ? done.title : DONE_LABELS[done.kind]}</span>
      {/* "Added Atlas 14:00–14:47" says its times already. */}
      {!(done.kind === 'taken' && item.kind === 'untracked') && (
        <span className="text-muted-foreground min-w-0 truncate">
          <span className="tabular-nums">
            {formatClock(item.start, timeZone, hour12)}–{formatClock(item.end, timeZone, hour12)}
          </span>
          {` · ${description}`}
        </span>
      )}
      {done.kind !== 'changed' && (
        <Button type="button" variant="ghost" size="xs" className="ml-auto shrink-0" onClick={onUndo} aria-label={`Undo: ${done.kind === 'taken' ? done.title : 'Not now'}`}>
          <Undo2 />
          Undo
        </Button>
      )}
    </li>
  )
}

/** Another entry of the day, between the suggestions, so they read in place. */
function ContextRow({ entry, catalog, timeZone, hour12 }: { entry: TimeEntry; catalog: Catalog; timeZone: string; hour12: boolean }) {
  const project = catalog.project(entry.projectId)
  const parts = isSplit(entry) ? partsOf(entry) : null
  return (
    <li className="text-muted-foreground flex min-w-0 items-center gap-3 px-3 py-0.5 text-sm">
      <span aria-hidden="true" className="h-4 w-[3px] shrink-0 rounded-full opacity-60" style={{ background: project?.color ?? 'var(--muted-foreground)' }} />
      <span className="min-w-0 truncate">{entry.description || 'No description'}</span>
      {parts ? <SplitLabel catalog={catalog} parts={parts} className="min-w-0 shrink" /> : project && <ProjectLabel project={project} className="min-w-0 shrink" />}
      <span className="ml-auto shrink-0 tabular-nums">
        {formatClock(entry.start, timeZone, hour12)} – {entry.stop ? formatClock(entry.stop, timeZone, hour12) : 'now'}
      </span>
    </li>
  )
}
