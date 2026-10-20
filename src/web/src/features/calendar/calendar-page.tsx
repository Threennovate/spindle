import { useQuery } from '@tanstack/react-query'
import { cn } from 'cn'
import { Lock, Plus, Sparkles } from 'lucide-react'
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type PointerEvent } from 'react'
import { toast } from 'sonner'
import type { SplitSuggestion, TimeEntry } from '@/api/types'
import { PageHeader } from '@/components/page-header'
import { PeriodNavigator } from '@/components/period-navigator'
import { Button } from '@/components/ui/button'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { errorMessage } from '@/lib/error-message'
import { formatHoursMinutes } from '@/lib/time/duration'
import { addDaysToKey, atTime, dayKey, formatClock, startOfDayKey } from '@/lib/time/zone'
import { usePageTitle } from '@/lib/title'
import { useNow } from '@/lib/use-now'
import { splitSuggestionsQuery, untrackedQuery, useAgentActivitySync } from '../agent-activity/queries'
import { useReviewCounter } from '../agent-activity/review-data'
import { ReviewDayButton } from '../agent-activity/review-entry-points'
import { openReview } from '../agent-activity/review-events'
import { SuggestionHint, SuggestionMark, SuggestionPreview } from '../agent-activity/suggestion-preview'
import { useTakeSuggestion } from '../agent-activity/take-suggestion'
import { UntrackedBlock } from '../agent-activity/untracked-block'
import { untrackedEntry, useDismissedUntracked } from '../agent-activity/untracked'
import { ProposalsBanner } from '../agent-proposals/proposals-banner'
import { SplitDots } from '../catalog/split-label'
import { partColor, splitDescription } from '../catalog/split-names'
import { useCatalog, type Catalog } from '../catalog/use-catalog'
import { usePermissions } from '../settings/queries'
import { EntryDialog, type NewEntry } from '../time-entries/entry-dialog'
import { toRequest, useUpdateEntry } from '../time-entries/queries'
import { isSplit, partsOf, type SplitPart } from '../time-entries/split'
import { useWorkspace } from '../workspace'
import { layoutDays, minuteOfDay, MINUTES_PER_DAY, snapMinutes, type DaySegment } from './layout'
import { formatRange, rangeEntriesQuery, weekDays } from './range'

const HOUR_HEIGHT = 48
const PX_PER_MINUTE = HOUR_HEIGHT / 60
const GUTTER = 56
const SNAP = 15
const DRAG_THRESHOLD = 4
const WORKDAY_START = 7 * 60

export type CalendarView = 'week' | 'day'

type Interaction =
  | { kind: 'create'; day: string; anchor: number; current: number }
  /** A block on a locked day: it opens to be looked at, and does not move. */
  | { kind: 'view'; entry: TimeEntry }
  | { kind: 'move'; entry: TimeEntry; offset: number; duration: number; day: string; start: number; originX: number; originY: number; moved: boolean }
  | { kind: 'resize'; edge: 'start' | 'end'; entry: TimeEntry; day: string; start: number; end: number; moved: boolean }

interface CalendarPageProps {
  date?: string
  view: CalendarView
  onNavigate: (search: { date?: string; view?: CalendarView }) => void
}

/**
 * The user's time as blocks on a day or week grid. Drag on empty space to add an entry, drag a block to move it
 * (to another day too), or drag its top or bottom edge to change when it starts or ends.
 */
export function CalendarPage({ date, view, onNavigate }: CalendarPageProps) {
  usePageTitle('Calendar')
  const { workspaceId, timeZone, weekStartsOn, hour12 } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  const { isLocked, isDayLocked } = usePermissions()
  const update = useUpdateEntry(workspaceId)
  const now = useNow()
  const today = dayKey(new Date(now), timeZone)
  const anchor = date ?? today
  const days = useMemo(() => (view === 'day' ? [anchor] : weekDays(anchor, timeZone, weekStartsOn)), [view, anchor, timeZone, weekStartsOn])
  const query = useQuery(rangeEntriesQuery(workspaceId, days, timeZone))
  // Entries agent activity suggests splitting are marked, as on the Mac; opening one shows the suggestion.
  const sync = useAgentActivitySync()
  const suggestions = useQuery(
    splitSuggestionsQuery(workspaceId, startOfDayKey(days[0], timeZone), startOfDayKey(addDaysToKey(days.at(-1)!, 1), timeZone), sync),
  )
  const suggested = useMemo(() => new Map((suggestions.data ?? []).map((suggestion) => [suggestion.entryId, suggestion])), [suggestions.data])
  // Agent attention nobody tracked, as ghost blocks: on open days, unless the person dismissed it in this browser.
  const untracked = useQuery(
    untrackedQuery(workspaceId, startOfDayKey(days[0], timeZone), startOfDayKey(addDaysToKey(days.at(-1)!, 1), timeZone), sync),
  )
  const dismissed = useDismissedUntracked(timeZone)
  const reviewCount = useReviewCounter()
  const takeSuggestion = useTakeSuggestion()
  // The entry whose suggestion is drawn on its block, while its ✦ is pointed at or focused.
  const [previewing, setPreviewing] = useState<string | null>(null)

  const [editing, setEditing] = useState<TimeEntry | null>(null)
  const [newEntry, setNewEntry] = useState<NewEntry | null>(null)
  const [interaction, setInteraction] = useState<Interaction | null>(null)
  // Where a moved block is shown until the server confirms, so it does not jump back while saving.
  const [pending, setPending] = useState<Record<string, { start: string; stop: string }>>({})
  const scrollRef = useRef<HTMLDivElement>(null)
  const bodyRef = useRef<HTMLDivElement>(null)

  const minuteNow = Math.floor(now / 60_000) * 60_000
  const entries = useMemo(() => (query.data ?? []).map((entry) => (pending[entry.id] ? { ...entry, ...pending[entry.id] } : entry)), [query.data, pending])
  const segments = useMemo(() => layoutDays(entries, days, timeZone, minuteNow), [entries, days, timeZone, minuteNow])
  // What the review of these days holds, for its button and each day's heading.
  const reviewItemsShown = reviewCount(days, query.data ?? [], suggestions.data ?? [], untracked.data ?? [])

  // Open at the working day: 7:00 straight away, before the first paint, so the grid does not jump under the
  // pointer; once entries load, earlier if one starts before that (not counting the tail of last night's).
  const scrolledFor = useRef<string | null>(null)
  const adjustedFor = useRef<string | null>(null)
  useLayoutEffect(() => {
    const key = days.join()
    if (scrolledFor.current !== key && scrollRef.current) {
      scrolledFor.current = key
      scrollRef.current.scrollTop = WORKDAY_START * PX_PER_MINUTE - 24
    }
  }, [days])
  useEffect(() => {
    const key = days.join()
    if (!query.isSuccess || adjustedFor.current === key || !scrollRef.current) {
      return
    }
    adjustedFor.current = key
    const earliest = Math.min(WORKDAY_START, ...segments.filter((s) => !s.continuesBefore).map((s) => s.startMinute))
    if (earliest < WORKDAY_START) {
      scrollRef.current.scrollTop = Math.max(0, earliest * PX_PER_MINUTE - 24)
    }
  }, [days, query.isSuccess, segments])

  function toInstant(day: string, minute: number): Date {
    return minute >= MINUTES_PER_DAY ? atTime(addDaysToKey(day, 1), 0, 0, timeZone) : atTime(day, Math.floor(minute / 60), minute % 60, timeZone)
  }

  function slotAt(event: PointerEvent): { day: string; minute: number } {
    const rect = bodyRef.current!.getBoundingClientRect()
    const columnWidth = (rect.width - GUTTER) / days.length
    const index = Math.min(days.length - 1, Math.max(0, Math.floor((event.clientX - rect.left - GUTTER) / columnWidth)))
    const minute = Math.min(MINUTES_PER_DAY, Math.max(0, (event.clientY - rect.top) / PX_PER_MINUTE))
    return { day: days[index], minute }
  }

  async function save(entry: TimeEntry, start: Date, stop: Date) {
    setPending((current) => ({ ...current, [entry.id]: { start: start.toISOString(), stop: stop.toISOString() } }))
    try {
      await update.mutateAsync({ id: entry.id, body: { ...toRequest(entry), start: start.toISOString(), stop: stop.toISOString() } })
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't change the time entry."))
    } finally {
      setPending((current) => {
        const next = { ...current }
        delete next[entry.id]
        return next
      })
    }
  }

  function handlePointerDown(event: PointerEvent<HTMLDivElement>) {
    if (event.button !== 0) {
      return
    }
    const target = event.target as HTMLElement
    const block = target.closest<HTMLElement>('[data-segment]')
    const slot = slotAt(event)
    if (target.closest('[data-untracked]') || target.closest('[data-suggestion-mark]')) {
      return // Untracked time opens a new entry for itself, or is dismissed; a ✦ takes its suggestion.
    }

    if (block) {
      const segment = segments.find((s) => `${s.entry.id}|${s.day}` === block.dataset.segment)
      if (!segment || !segment.entry.stop) {
        return // A running entry is opened, not dragged.
      }
      if (isLocked(segment.entry.start)) {
        setInteraction({ kind: 'view', entry: segment.entry })
        event.currentTarget.setPointerCapture(event.pointerId)
        event.preventDefault()
        return
      }
      const edge = target.closest<HTMLElement>('[data-resize]')?.dataset.resize as 'start' | 'end' | undefined
      if (edge) {
        setInteraction({ kind: 'resize', edge, entry: segment.entry, day: segment.day, start: segment.startMinute, end: segment.endMinute, moved: false })
      } else {
        if (segment.continuesBefore) {
          return
        }
        setInteraction({
          kind: 'move',
          entry: segment.entry,
          offset: slot.minute - segment.startMinute,
          duration: (Date.parse(segment.entry.stop) - Date.parse(segment.entry.start)) / 60_000,
          day: segment.day,
          start: segment.startMinute,
          originX: event.clientX,
          originY: event.clientY,
          moved: false,
        })
      }
    } else {
      if (isDayLocked(slot.day)) {
        return
      }
      const minute = snapMinutes(slot.minute - SNAP / 2, SNAP)
      setInteraction({ kind: 'create', day: slot.day, anchor: minute, current: minute })
    }

    event.currentTarget.setPointerCapture(event.pointerId)
    event.preventDefault()
  }

  function handlePointerMove(event: PointerEvent<HTMLDivElement>) {
    if (!interaction || interaction.kind === 'view') {
      return
    }
    const slot = slotAt(event)
    if (interaction.kind === 'create') {
      setInteraction({ ...interaction, current: snapMinutes(slot.minute, SNAP) })
    } else if (interaction.kind === 'move') {
      const moved = interaction.moved || Math.hypot(event.clientX - interaction.originX, event.clientY - interaction.originY) > DRAG_THRESHOLD
      const start = Math.min(MINUTES_PER_DAY - SNAP, snapMinutes(slot.minute - interaction.offset, SNAP))
      // A block cannot be moved onto a locked day; it stays on the last open one.
      setInteraction({ ...interaction, moved, start, day: isDayLocked(slot.day) ? interaction.day : slot.day })
    } else if (interaction.edge === 'end') {
      setInteraction({ ...interaction, moved: true, end: Math.max(interaction.start + SNAP, snapMinutes(slot.minute, SNAP)) })
    } else {
      setInteraction({ ...interaction, moved: true, start: Math.min(interaction.end - SNAP, snapMinutes(slot.minute, SNAP)) })
    }
  }

  function handlePointerUp() {
    const done = interaction
    setInteraction(null)
    if (!done) {
      return
    }
    if (done.kind === 'view') {
      setEditing(done.entry)
    } else if (done.kind === 'create') {
      const [from, to] = Math.abs(done.current - done.anchor) < SNAP ? [done.anchor, Math.min(done.anchor + 60, MINUTES_PER_DAY)] : [Math.min(done.anchor, done.current), Math.max(done.anchor, done.current)]
      setNewEntry({ start: toInstant(done.day, from), stop: toInstant(done.day, to) })
    } else if (done.kind === 'move') {
      if (!done.moved) {
        setEditing(done.entry)
        return
      }
      const start = toInstant(done.day, done.start)
      void save(done.entry, start, new Date(start.getTime() + done.duration * 60_000))
    } else if (!done.moved) {
      setEditing(done.entry)
    } else if (done.edge === 'end') {
      void save(done.entry, new Date(done.entry.start), toInstant(done.day, done.end))
    } else {
      void save(done.entry, toInstant(done.day, done.start), new Date(done.entry.stop!))
    }
  }

  // While dragging, the block is drawn where it would land instead of where it is.
  const preview = interaction && (interaction.kind === 'move' || interaction.kind === 'resize') && interaction.moved ? interaction : null
  const shownSegments = preview ? segments.filter((s) => s.entry.id !== preview.entry.id) : segments

  const totals = new Map(days.map((day) => [day, 0]))
  for (const segment of segments) {
    totals.set(segment.day, (totals.get(segment.day) ?? 0) + (segment.endMinute - segment.startMinute) * 60)
  }
  const columns = `${GUTTER}px repeat(${days.length}, minmax(0, 1fr))`
  const nowMinute = minuteOfDay(new Date(now), timeZone)

  return (
    <div className="mx-auto grid max-w-6xl gap-4">
      <PageHeader
        title="Calendar"
        actions={
          <>
            {sync && (
              <Button variant="outline" onClick={() => openReview({ days })} title={view === 'day' ? "Review this day's suggestions in one place" : "Review this week's suggestions in one place"}>
                <Sparkles className="text-primary-text" />
                {view === 'day' ? 'Review day' : 'Review week'}
                {reviewItemsShown.length > 0 && <span className="tabular-nums">({reviewItemsShown.length})</span>}
              </Button>
            )}
            <ToggleGroup type="single" variant="outline" value={view} onValueChange={(value) => value && onNavigate({ date, view: value as CalendarView })} aria-label="View">
              <ToggleGroupItem value="day">Day</ToggleGroupItem>
              <ToggleGroupItem value="week">Week</ToggleGroupItem>
            </ToggleGroup>
            <Button
              onClick={() => {
                const start = snapMinutes(minuteOfDay(new Date(), timeZone) - SNAP / 2, SNAP)
                const shown = days.includes(today) ? today : days[0]
                const day = isDayLocked(shown) ? today : shown
                setNewEntry({ start: toInstant(day, start), stop: toInstant(day, Math.min(start + 60, MINUTES_PER_DAY)) })
              }}
            >
              <Plus />
              Add entry
            </Button>
          </>
        }
      />
      <ProposalsBanner />
      <PeriodNavigator
        label={formatRange(days)}
        unit={view}
        onPrevious={() => onNavigate({ view, date: addDaysToKey(anchor, view === 'day' ? -1 : -7) })}
        onNext={() => onNavigate({ view, date: addDaysToKey(anchor, view === 'day' ? 1 : 7) })}
        onToday={() => onNavigate({ view })}
      />

      <div className="bg-card overflow-hidden rounded-xl border">
        <div className="grid border-b" style={{ gridTemplateColumns: columns }}>
          <div />
          {days.map((day) => (
            <DayHeading
              key={day}
              day={day}
              isToday={day === today}
              locked={isDayLocked(day)}
              totalSeconds={totals.get(day) ?? 0}
              review={sync ? reviewItemsShown.filter((item) => item.day === day).length : 0}
            />
          ))}
        </div>

        <div ref={scrollRef} className="max-h-[calc(100svh-17rem)] min-h-96 overflow-y-auto">
          <div
            ref={bodyRef}
            role="grid"
            aria-label="Time entries by time of day"
            aria-busy={query.isPending}
            className="relative grid touch-none select-none"
            style={{ gridTemplateColumns: columns, height: 24 * HOUR_HEIGHT }}
            onPointerDown={handlePointerDown}
            onPointerMove={handlePointerMove}
            onPointerUp={handlePointerUp}
            onPointerCancel={() => setInteraction(null)}
          >
            <div className="relative" aria-hidden="true">
              {Array.from({ length: 23 }, (_, index) => index + 1)
                // The now label takes the place of an hour it would cover.
                .filter((hour) => !days.includes(today) || Math.abs(hour * 60 - nowMinute) > 20)
                .map((hour) => (
                  <span key={hour} className="text-muted-foreground absolute right-2 -translate-y-1/2 text-[11px] tabular-nums" style={{ top: hour * HOUR_HEIGHT }}>
                    {formatClock(atTime(today, hour, 0, timeZone), timeZone, hour12)}
                  </span>
                ))}
              {days.includes(today) && (
                <span
                  className="bg-logo-accent text-primary-foreground absolute right-1 z-10 -translate-y-1/2 rounded-full px-1.5 py-px text-[11px] font-semibold tabular-nums"
                  style={{ top: nowMinute * PX_PER_MINUTE }}
                >
                  {formatClock(new Date(now), timeZone, hour12)}
                </span>
              )}
            </div>
            {days.map((day) => (
              <div
                key={day}
                role="gridcell"
                aria-label={isDayLocked(day) ? `${day}, locked` : day}
                aria-readonly={isDayLocked(day) || undefined}
                className={cn('relative border-l', isDayLocked(day) ? 'bg-muted/50' : 'cursor-crosshair', isWeekend(day) && !isDayLocked(day) && 'bg-muted/30')}
                style={{
                  // Hours in the border colour, half hours fainter.
                  backgroundImage: `linear-gradient(var(--border) 1px, transparent 1px), linear-gradient(color-mix(in oklab, var(--border) 45%, transparent) 1px, transparent 1px)`,
                  backgroundSize: `100% ${HOUR_HEIGHT}px, 100% ${HOUR_HEIGHT / 2}px`,
                }}
              >
                {shownSegments
                  .filter((segment) => segment.day === day)
                  .map((segment) => {
                    const suggestion = isLocked(segment.entry.start) || segment.continuesBefore ? undefined : suggested.get(segment.entry.id)
                    return (
                      <EntryBlock
                        key={segment.entry.id}
                        segment={segment}
                        catalog={catalog}
                        timeZone={timeZone}
                        hour12={hour12}
                        locked={isLocked(segment.entry.start)}
                        suggestion={suggestion}
                        previewing={!!suggestion && previewing === segment.entry.id}
                        onOpen={() => setEditing(segment.entry)}
                        onPreview={(on) => setPreviewing(on ? segment.entry.id : null)}
                        onTake={() => {
                          // The ✦ goes once taken, before the pointer leaves it.
                          setPreviewing(null)
                          if (suggestion) {
                            void takeSuggestion(segment.entry, suggestion, () => setEditing(segment.entry))
                          }
                        }}
                      />
                    )
                  })}
                {!isDayLocked(day) &&
                  (untracked.data ?? [])
                    .filter((block) => dayKey(block.start, timeZone) === day && !dismissed.isDismissed(block))
                    .map((block) => {
                      const from = minuteOfDay(new Date(block.start), timeZone)
                      const to = Math.max(from, (Date.parse(block.end) - startOfDayKey(day, timeZone).getTime()) / 60_000)
                      return (
                        <UntrackedBlock
                          key={block.start}
                          block={block}
                          catalog={catalog}
                          timeZone={timeZone}
                          hour12={hour12}
                          top={from * PX_PER_MINUTE}
                          height={Math.max((to - from) * PX_PER_MINUTE, 14)}
                          onOpen={() => setNewEntry(untrackedEntry(block, catalog))}
                          onDismiss={() => dismissed.dismiss(block)}
                        />
                      )
                    })}
                {preview && preview.day === day && (
                  <EntryBlock
                    segment={previewSegment(preview)}
                    catalog={catalog}
                    timeZone={timeZone}
                    hour12={hour12}
                    onOpen={() => {}}
                    dragging
                  />
                )}
                {interaction?.kind === 'create' && interaction.day === day && <CreateGhost from={interaction.anchor} to={interaction.current} />}
                {day === today && <NowLine minute={nowMinute} />}
              </div>
            ))}
          </div>
        </div>
      </div>

      <EntryDialog
        entry={editing}
        newEntry={newEntry}
        onClose={() => {
          setEditing(null)
          setNewEntry(null)
        }}
      />
    </div>
  )
}

function previewSegment(preview: Extract<Interaction, { kind: 'move' | 'resize' }>): DaySegment {
  const [start, end] = preview.kind === 'move' ? [preview.start, Math.min(preview.start + preview.duration, MINUTES_PER_DAY)] : [preview.start, preview.end]
  return { entry: preview.entry, day: preview.day, startMinute: start, endMinute: end, continuesBefore: false, continuesAfter: false, lane: 0, lanes: 1 }
}

function DayHeading({ day, isToday, locked, totalSeconds, review }: { day: string; isToday: boolean; locked: boolean; totalSeconds: number; review: number }) {
  const [year, month, date] = day.split('-').map(Number)
  const utc = new Date(Date.UTC(year, month - 1, date))
  return (
    <div className={cn('@container grid justify-items-center gap-0.5 border-l px-2 pt-2 pb-2.5', isWeekend(day) && 'bg-muted/30')}>
      <p className="text-muted-foreground flex items-center gap-1 text-[11px] font-medium tracking-wide uppercase">
        {new Intl.DateTimeFormat('en-GB', { weekday: 'short', timeZone: 'UTC' }).format(utc)}
        {locked && <Lock aria-label="Locked" className="size-3" />}
      </p>
      <p
        className={cn('grid size-8 place-items-center rounded-full text-lg leading-none tabular-nums', isToday ? 'bg-primary text-primary-foreground font-semibold' : 'font-medium')}
        aria-current={isToday ? 'date' : undefined}
      >
        {date}
      </p>
      <p className="text-muted-foreground text-xs tabular-nums">{totalSeconds > 0 ? formatHoursMinutes(totalSeconds) : '–'}</p>
      <ReviewDayButton day={day} count={review} compact className="mt-0.5" />
    </div>
  )
}

interface EntryBlockProps {
  segment: DaySegment
  catalog: Catalog
  timeZone: string
  hour12: boolean
  onOpen: () => void
  locked?: boolean
  /** The split agent activity suggests for it, firmly or only lightly. */
  suggestion?: SplitSuggestion
  /** Draws the suggestion on the block, while its ✦ is pointed at or focused. */
  previewing?: boolean
  onPreview?: (on: boolean) => void
  onTake?: () => void
  dragging?: boolean
}

function EntryBlock({ segment, catalog, timeZone, hour12, onOpen, locked = false, suggestion, previewing = false, onPreview, onTake, dragging }: EntryBlockProps) {
  const { entry } = segment
  // A split entry takes the colour of its largest part, the project it mirrors; the rest show in its thread.
  const project = catalog.project(entry.projectId)
  const color = project?.color ?? 'var(--muted-foreground)'
  const parts = isSplit(entry) ? [...partsOf(entry)].sort((a, b) => b.percent - a.percent) : null
  const height = Math.max((segment.endMinute - segment.startMinute) * PX_PER_MINUTE, 14)
  const running = !entry.stop
  const time = `${formatClock(entry.start, timeZone, hour12)} – ${entry.stop ? formatClock(entry.stop, timeZone, hour12) : 'now'}`
  const label = entry.description || 'No description'
  // Tall enough for the thread under the text; shorter split blocks show dots by the description instead.
  const threaded = parts && height >= 30

  const top = segment.startMinute * PX_PER_MINUTE
  const left = `calc(${(segment.lane / segment.lanes) * 100}% + 2px)`
  const width = `calc(${100 / segment.lanes}% - 4px)`
  const from = startOfDayKey(segment.day, timeZone).getTime() + segment.startMinute * 60_000
  // Tall enough to label a suggestion's parts below the block's own text; the hint names them either way.
  const labelled = height >= 64

  return (
    <>
    <button
      type="button"
      data-segment={`${entry.id}|${segment.day}`}
      aria-label={`${label}, ${parts ? `split: ${splitDescription(catalog, parts)}` : (project?.name ?? 'no project')}, ${time}${locked ? ', locked' : ''}${suggestion ? ', split suggested' : ''}`}
      // Mouse and touch are handled by the grid's pointer handlers; this opens the entry from the keyboard.
      onClick={(event) => event.detail === 0 && onOpen()}
      className={cn(
        // A button centres its content; a block reads from the top.
        'absolute flex flex-col justify-start overflow-hidden rounded-md px-1.5 text-left text-xs leading-snug outline-none focus-visible:ring-2 focus-visible:ring-ring',
        height < 24 ? 'py-0 leading-[14px]' : 'py-1',
        running || locked ? 'cursor-pointer' : 'cursor-grab',
        threaded && 'pb-3',
        // The text stays over a suggestion drawn on the block, on a soft backing so its lines don't run through it.
        '[&>p]:relative',
        previewing && '[&>p]:bg-card/80 [&>p]:-mx-0.5 [&>p]:w-fit [&>p]:max-w-full [&>p]:rounded-sm [&>p]:px-0.5',
        dragging && 'z-20 cursor-grabbing opacity-90 shadow-lg ring-2 ring-primary',
      )}
      style={{
        top,
        height,
        left,
        width,
        background: tint(color),
        boxShadow: `inset 0 0 0 1px ${edge(color)}`,
      }}
    >
      {previewing && suggestion && (
        <SuggestionPreview
          suggestion={suggestion}
          catalog={catalog}
          timeZone={timeZone}
          hour12={hour12}
          from={from}
          to={from + (segment.endMinute - segment.startMinute) * 60_000}
          labelled={labelled}
        />
      )}
      <p className={cn('flex items-center gap-1 font-medium', !entry.description && 'text-muted-foreground', suggestion && 'pr-4')}>
        {running && <span aria-hidden="true" className="bg-logo-accent size-1.5 shrink-0 rounded-full motion-safe:animate-pulse" />}
        {locked && <Lock aria-hidden="true" className="text-muted-foreground size-3 shrink-0" />}
        <span className="truncate">{label}</span>
        {parts && !threaded && <SplitDots catalog={catalog} parts={parts} className="ml-auto [&>span]:size-2 [&>span]:ring-1" />}
      </p>
      {height > 40 && (
        <p className="truncate">
          {parts ? (
            parts.map((part, index) => (
              <span key={index}>
                {index > 0 && <span className="text-muted-foreground"> · </span>}
                <span className="font-medium" style={{ color: readable(partColor(catalog, part)) }}>
                  {catalog.project(part.projectId)?.name ?? 'No project'}
                </span>
                <span className="text-muted-foreground tabular-nums"> {part.percent}%</span>
              </span>
            ))
          ) : (
            <span className="font-medium" style={{ color: readable(color) }}>
              {project?.name ?? 'No project'}
            </span>
          )}
        </p>
      )}
      {height > 50 && <p className="text-muted-foreground truncate tabular-nums">{time}</p>}
      {threaded && <SplitThread catalog={catalog} parts={parts} />}
      {!running && !locked && !segment.continuesBefore && <span data-resize="start" aria-hidden="true" className="absolute inset-x-0 top-0 h-1.5 cursor-ns-resize" />}
      {!running && !locked && !segment.continuesAfter && <span data-resize="end" aria-hidden="true" className="absolute inset-x-0 bottom-0 h-1.5 cursor-ns-resize" />}
    </button>
    {previewing && suggestion && (
      <SuggestionHint
        entry={entry}
        suggestion={suggestion}
        catalog={catalog}
        timeZone={timeZone}
        hour12={hour12}
        style={{ top: top + height + 3, left }}
      />
    )}
    {suggestion && onPreview && onTake && !dragging && (
      <SuggestionMark
        suggestion={suggestion}
        entry={entry}
        catalog={catalog}
        timeZone={timeZone}
        hour12={hour12}
        style={{ top: top + (height < 24 ? 0 : 2), left: `calc(${left} + ${width} - 20px)` }}
        onPreview={onPreview}
        onTake={onTake}
      />
    )}
    </>
  )
}

/**
 * Where a split entry's time went, as a thread along the bottom of its block: a segment per project, as long as its
 * share, as in the split bars elsewhere.
 */
function SplitThread({ catalog, parts }: { catalog: Catalog; parts: readonly SplitPart[] }) {
  return (
    <span aria-hidden="true" className="absolute inset-x-1.5 bottom-1.5 flex h-[3px] gap-0.5">
      {parts.map((part, index) => (
        <span key={index} className="h-full min-w-[3px] rounded-full" style={{ flexGrow: part.percent, flexBasis: 0, backgroundColor: partColor(catalog, part) }} />
      ))}
    </span>
  )
}

/** A block's fill: its project's colour, light enough to read on. */
function tint(color: string) {
  return `color-mix(in oklab, ${color} 15%, var(--card))`
}

/** The hairline that holds a block's shape, in its project's colour. */
function edge(color: string) {
  return `color-mix(in oklab, ${color} 30%, transparent)`
}

/** A project's colour for text on its tint: drawn towards the text colour so light colours still read. */
function readable(color: string) {
  return `color-mix(in oklab, ${color} 75%, var(--foreground))`
}

function isWeekend(day: string) {
  const [year, month, date] = day.split('-').map(Number)
  const weekday = new Date(Date.UTC(year, month - 1, date)).getUTCDay()
  return weekday === 0 || weekday === 6
}

function CreateGhost({ from, to }: { from: number; to: number }) {
  const start = Math.min(from, to)
  const end = Math.max(from, to, start + SNAP)
  return (
    <div
      aria-hidden="true"
      className="border-primary bg-primary/15 pointer-events-none absolute inset-x-1 rounded-md border border-dashed"
      style={{ top: start * PX_PER_MINUTE, height: (end - start) * PX_PER_MINUTE }}
    />
  )
}

/** Now, in marigold: the colour of the middle bar of the mark, your hour. */
function NowLine({ minute }: { minute: number }) {
  return (
    <div aria-hidden="true" className="pointer-events-none absolute inset-x-0 z-10 flex -translate-y-1/2 items-center" style={{ top: minute * PX_PER_MINUTE }}>
      <span className="bg-logo-accent -ml-1 size-2 rounded-full" />
      <span className="bg-logo-accent h-0.5 flex-1" />
    </div>
  )
}
