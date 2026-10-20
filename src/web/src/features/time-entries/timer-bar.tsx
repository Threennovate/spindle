import { useQuery } from '@tanstack/react-query'
import { ListPlus, Play, Square, Timer } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { toast } from 'sonner'
import type { components } from '@/api/schema'
import type { TimeEntry, TimeEntrySuggestion } from '@/api/types'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { errorMessage } from '@/lib/error-message'
import { cn } from '@/lib/utils'
import { formatDuration, parseDuration } from '@/lib/time/duration'
import { addDaysToKey, atTime, clockInputValue, dayKey, readClockField } from '@/lib/time/zone'
import { useNow } from '@/lib/use-now'
import { usePersistentState } from '@/lib/use-persistent-state'
import { useCatalog, type Catalog } from '../catalog/use-catalog'
import { PomodoroStatus } from '../pomodoro/pomodoro-status'
import { usePermissions } from '../settings/queries'
import { useWorkspace } from '../workspace'
import { ClockInput } from './clock-input'
import { DescriptionField } from './description-field'
import { useDescriptionFocus } from './focus'
import { BillableButton, ProjectButton, TagsButton } from './entry-fields'
import { editEntry } from './events'
import { currentEntryQuery, toRequest, useCreateEntry, useUpdateEntry } from './queries'
import { missingDetails } from './rules'
import { describesParts, isSplit, partsOf } from './split'
import { mentionable, mentionRequest, mentionText, type MentionBase } from './mentions'
import { MentionHighlights, MentionList, MentionPreview } from './mention-field'
import { useMentions } from './use-mentions'
import { RunningStart } from './running-start'
import { useStopTimer } from './use-stop-timer'
import { useReadOnly } from '../subscriptions/read-only'

type TimeEntryRequest = components['schemas']['TimeEntryRequest']

interface Draft {
  description: string
  projectId: string | null
  taskId: string | null
  tagIds: string[]
  /** null: follow the project's billable setting. */
  isBillable: boolean | null
}

const EMPTY_DRAFT: Draft = { description: '', projectId: null, taskId: null, tagIds: [], isBillable: null }
const MODES = ['timer', 'manual'] as const

/** The timer at the top of Home and Entries: start and stop it, or add time manually. */
export function TimerBar() {
  const { workspaceId, timeZone } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  const { data: running } = useQuery(currentEntryQuery)
  const [mode, setMode] = usePersistentState('spindle:entry-mode', 'timer', MODES)
  const [draft, setDraft] = useState<Draft>(EMPTY_DRAFT)
  const create = useCreateEntry(workspaceId)
  const update = useUpdateEntry(workspaceId)
  const stop = useStopTimer(workspaceId)
  const { readOnly } = useReadOnly()

  const draftBillable = draft.isBillable ?? catalog.project(draft.projectId)?.isBillable ?? false
  const draftBase: MentionBase = { projectId: draft.projectId, taskId: draft.taskId, isBillable: draftBillable }

  /** Clears the draft after it was used, unless the user already changed it while the request was on its way. */
  function clearDraft(used: Draft) {
    setDraft((current) => (current === used ? EMPTY_DRAFT : current))
  }

  /** Starts a timer; `shares` splits it as a suggestion picked from a split entry was, `@Project` mentions as typed. */
  async function start(fields: Draft & { shares?: TimeEntryRequest['shares'] }, used: Draft = fields) {
    const mentioned = fields.shares ? undefined : mentionRequest(fields.description, draftBase, mentionable(catalog.projects))
    if (mentioned === null) {
      toast.error('Split the time between at most 6 projects.')
      return
    }
    try {
      await create.mutateAsync({
        description: fields.description.trim(),
        projectId: fields.projectId,
        taskId: fields.taskId,
        tagIds: fields.tagIds,
        isBillable: fields.isBillable ?? undefined,
        shares: fields.shares ?? [],
        ...mentioned,
        start: new Date().toISOString(),
      })
      clearDraft(used)
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't start the timer."))
    }
  }

  async function saveRunning(entry: TimeEntry, changes: Partial<TimeEntryRequest>) {
    try {
      await update.mutateAsync({ id: entry.id, body: { ...toRequest(entry), ...changes } })
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't save the running entry."))
    }
  }

  function pickSuggestion(suggestion: TimeEntrySuggestion) {
    const fields = { ...suggestion, tagIds: [...suggestion.tagIds], projectId: suggestion.projectId ?? null, taskId: suggestion.taskId ?? null }
    void start(fields, draft)
  }

  const fields = running
    ? {
        projectId: running.projectId ?? null,
        taskId: running.taskId ?? null,
        tagIds: running.tagIds,
        isBillable: running.isBillable,
        parts: isSplit(running) ? partsOf(running) : undefined,
        setProject: (projectId: string | null, taskId: string | null) =>
          saveRunning(running, { projectId, taskId, isBillable: catalog.project(projectId)?.isBillable ?? running.isBillable, shares: [] }),
        setTags: (tagIds: string[]) => saveRunning(running, { tagIds }),
        // Billable applies to every part of a split entry.
        setBillable: (isBillable: boolean) =>
          saveRunning(running, { isBillable, shares: running.shares.map((share) => ({ ...share, isBillable })) }),
      }
    : {
        projectId: draft.projectId,
        taskId: draft.taskId,
        tagIds: draft.tagIds,
        isBillable: draftBillable,
        parts: undefined,
        setProject: (projectId: string | null, taskId: string | null) => setDraft((d) => ({ ...d, projectId, taskId, isBillable: null })),
        setTags: (tagIds: string[]) => setDraft((d) => ({ ...d, tagIds })),
        setBillable: (isBillable: boolean) => setDraft((d) => ({ ...d, isBillable })),
      }

  return (
    <section aria-label="Timer" className="bg-card flex flex-col gap-2 rounded-xl border p-2 shadow-xs md:flex-row md:items-center md:pl-3">
      {running ? (
        <RunningDescription
          entry={running}
          catalog={catalog}
          onSave={(description, whole) => {
            const base = { projectId: running.projectId ?? null, taskId: running.taskId ?? null, isBillable: running.isBillable, parts: isSplit(running) ? partsOf(running) : null }
            const mentioned = mentionRequest(description, base, mentionable(catalog.projects), whole)
            if (mentioned === null) {
              toast.error('Split the time between at most 6 projects.')
            } else {
              void saveRunning(running, mentioned.description === description.trim() && !mentioned.describeParts ? { description } : mentioned)
            }
          }}
        />
      ) : (
        <DescriptionField
          workspaceId={workspaceId}
          catalog={catalog}
          value={draft.description}
          onChange={(description) => setDraft((d) => ({ ...d, description }))}
          onSubmit={() => mode === 'timer' && void start(draft)}
          onPick={pickSuggestion}
          mentionBase={draftBase}
        />
      )}

      <div className="flex flex-wrap items-center gap-1 md:flex-nowrap">
        <ProjectButton
          workspaceId={workspaceId}
          catalog={catalog}
          projectId={fields.projectId}
          taskId={fields.taskId}
          onChange={fields.setProject}
          parts={fields.parts}
          onEditSplit={running ? () => editEntry({ entry: running }) : undefined}
          onSplit={running ? () => editEntry({ entry: running, split: true }) : undefined}
        />
        <TagsButton workspaceId={workspaceId} tagIds={fields.tagIds} onChange={fields.setTags} />
        <BillableButton isBillable={fields.isBillable} onChange={fields.setBillable} />

        <div className="ml-auto flex items-center gap-1 md:ml-2">
          {running ? (
            <>
              <PomodoroStatus running={running} />
              <RunningDuration
                entry={running}
                onChangeStart={(start) => update.mutateAsync({ id: running.id, body: { ...toRequest(running), start } })}
              />
              <Button size="icon-lg" variant="destructive" className="size-10 rounded-full" aria-label="Stop timer" onClick={() => stop.stopTimer(running)} disabled={stop.isPending}>
                <Square className="size-4 fill-current" />
              </Button>
            </>
          ) : mode === 'manual' ? (
            <ManualTimes
              workspaceId={workspaceId}
              timeZone={timeZone}
              catalog={catalog}
              draft={draft}
              onAdded={clearDraft}
            />
          ) : (
            <>
              <PomodoroStatus running={null} />
              <span className="text-muted-foreground w-24 text-right font-mono text-lg font-semibold tabular-nums">0:00:00</span>
              <Button size="icon-lg" className="size-10 rounded-full" aria-label="Start timer" onClick={() => void start(draft)} disabled={create.isPending || readOnly}>
                <Play className="size-4 fill-current" />
              </Button>
            </>
          )}

          {!running && (
            <ToggleGroup
              type="single"
              value={mode}
              onValueChange={(value) => value && setMode(value as (typeof MODES)[number])}
              orientation="vertical"
              className="ml-1"
              aria-label="Entry mode"
            >
              <ToggleGroupItem value="timer" aria-label="Timer mode" className="h-5 w-7 min-w-0 px-0 [&_svg]:size-3.5">
                <Timer />
              </ToggleGroupItem>
              <ToggleGroupItem value="manual" aria-label="Manual mode" className="h-5 w-7 min-w-0 px-0 [&_svg]:size-3.5">
                <ListPlus />
              </ToggleGroupItem>
            </ToggleGroup>
          )}
        </div>
      </div>
    </section>
  )
}

/** The running entry's description, saved when the field is left or Enter is pressed. `@Project` mentions work here too. */
function RunningDescription({
  entry,
  catalog,
  onSave,
}: {
  entry: TimeEntry
  catalog: Catalog
  /** `whole`: the text was shown as the parts' own descriptions, so it is all of them. */
  onSave: (description: string, whole: boolean) => void
}) {
  // An entry described per project shows its parts' descriptions as mentions, to edit them all in one line.
  const described = describesParts(isSplit(entry) ? partsOf(entry) : null)
  const shown = (described && mentionText(partsOf(entry), mentionable(catalog.projects))) || entry.description
  const [text, setText] = useState(shown)
  const focused = useRef(false)
  const [hasFocus, setHasFocus] = useState(false)
  const inputRef = useRef<HTMLInputElement>(null)
  useDescriptionFocus(inputRef)
  const mentions = useMentions(text, setText, inputRef, catalog)
  const base: MentionBase = { projectId: entry.projectId ?? null, taskId: entry.taskId ?? null, isBillable: entry.isBillable, parts: isSplit(entry) ? partsOf(entry) : null }

  // Follow changes made elsewhere (e.g. on another device), unless the user is typing.
  useEffect(() => {
    if (!focused.current) {
      setText(shown)
    }
  }, [shown])

  function save() {
    if (text.trim() !== shown) {
      onSave(text.trim(), shown !== entry.description)
    }
  }

  const listId = 'running-description-mentions'
  return (
    <div className="relative min-w-0 flex-1">
      <MentionHighlights value={text} mentions={mentions} catalog={catalog} inputRef={inputRef} className="px-1 text-base leading-10" />
      <input
        ref={inputRef}
        aria-label="Description"
        placeholder="Add description"
        value={text}
        aria-controls={mentions.matches.length > 0 ? listId : undefined}
        onChange={(event) => {
          setText(event.target.value)
          mentions.track()
        }}
        onSelect={mentions.track}
        onFocus={() => {
          focused.current = true
          setHasFocus(true)
        }}
        onBlur={() => {
          focused.current = false
          setHasFocus(false)
          save()
        }}
        onKeyDown={(event) => !mentions.onKeyDown(event) && event.key === 'Enter' && event.currentTarget.blur()}
        className="placeholder:text-muted-foreground relative h-10 w-full bg-transparent px-1 text-base outline-none"
      />
      {mentions.matches.length > 0 && <MentionList id={listId} mentions={mentions} catalog={catalog} />}
      {mentions.matches.length === 0 && hasFocus && mentions.hasMentions && <MentionPreview result={mentions.apply(base)} catalog={catalog} />}
    </div>
  )
}

/** The ticking duration of the running timer. Clicking it lets the user correct when the timer started. */
function RunningDuration({ entry, onChangeStart }: { entry: TimeEntry; onChangeStart: (start: string) => Promise<unknown> }) {
  const now = useNow()
  const [open, setOpen] = useState(false)

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button type="button" aria-label="Change start time" className="hover:bg-muted w-24 rounded-md px-1 text-right font-mono text-lg font-semibold tabular-nums">
          {formatDuration((now - Date.parse(entry.start)) / 1000)}
        </button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-80">
        <RunningStart entry={entry} onSave={onChangeStart} onDone={() => setOpen(false)} />
      </PopoverContent>
    </Popover>
  )
}

/** Start and end for adding time that was not tracked with the timer. */
function ManualTimes({
  workspaceId,
  timeZone,
  catalog,
  draft,
  onAdded,
}: {
  workspaceId: string
  timeZone: string
  catalog: Catalog
  draft: Draft
  onAdded: (used: Draft) => void
}) {
  const create = useCreateEntry(workspaceId)
  const { readOnly } = useReadOnly()
  const { hour12 } = useWorkspace()
  const { settings, isDayLocked, lockMessage, firstOpenDay } = usePermissions()
  const [startText, setStartText] = useState(() => clockInputValue(new Date(Date.now() - 3600_000), timeZone, hour12))
  const [endText, setEndText] = useState(() => clockInputValue(new Date(), timeZone, hour12))
  const [day, setDay] = useState(() => dayKey(new Date(), timeZone))

  const range = toRange(day, startText, endText, timeZone)
  const clockWidth = hour12 ? 'w-20' : 'w-16'
  const [durationText, setDurationText] = useState('')
  const shownDuration = range ? formatDuration((range.stop.getTime() - range.start.getTime()) / 1000) : ''

  function applyDuration() {
    const seconds = parseDuration(durationText)
    if (range && seconds !== null) {
      setEndText(clockInputValue(new Date(range.start.getTime() + seconds * 1000), timeZone, hour12))
    }
    setDurationText('')
  }

  async function add() {
    if (!range) {
      toast.error('Enter a start and an end time, such as 9:00 and 10:30.')
      return
    }
    if (isDayLocked(day)) {
      toast.error(lockMessage)
      return
    }
    const isBillable = draft.isBillable ?? catalog.project(draft.projectId)?.isBillable ?? false
    const mentioned = mentionRequest(draft.description, { projectId: draft.projectId, taskId: draft.taskId, isBillable }, mentionable(catalog.projects))
    if (!mentioned) {
      toast.error('Split the time between at most 6 projects.')
      return
    }
    // What is missing is shown one thing at a time, in the order the fields appear in the bar.
    const [missing] = Object.values(missingDetails(settings, { ...draft, ...mentioned }, catalog.project(mentioned.projectId)))
    if (missing) {
      toast.error(missing)
      return
    }
    const used = draft
    const usedTimes = { start: startText, end: endText }
    try {
      await create.mutateAsync({
        tagIds: draft.tagIds,
        ...mentioned,
        start: range.start.toISOString(),
        stop: range.stop.toISOString(),
      })
      // The next entry usually starts where this one ended, unless the user already typed another start.
      setStartText((current) => (current === usedTimes.start ? usedTimes.end : current))
      onAdded(used)
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't add the time entry."))
    }
  }

  return (
    <div className="flex items-center gap-1">
      <Input
        type="date"
        aria-label="Date"
        value={day}
        min={firstOpenDay ?? undefined}
        onChange={(event) => event.target.value && setDay(event.target.value)}
        className="w-36"
      />
      <ClockInput aria-label="Start time" value={startText} onValueChange={setStartText} className={cn(clockWidth, 'text-center tabular-nums')} />
      <span aria-hidden="true" className="text-muted-foreground">–</span>
      <ClockInput aria-label="End time" value={endText} onValueChange={setEndText} className={cn(clockWidth, 'text-center tabular-nums')} />
      <Input
        aria-label="Duration"
        value={durationText || shownDuration}
        onFocus={() => setDurationText(shownDuration)}
        onChange={(event) => setDurationText(event.target.value)}
        onBlur={applyDuration}
        onKeyDown={(event) => event.key === 'Enter' && event.currentTarget.blur()}
        className="w-24 text-right font-mono tabular-nums"
      />
      <Button onClick={() => void add()} disabled={create.isPending || readOnly} className="ml-1">
        Add
      </Button>
    </div>
  )
}

/** The instants for a day and two clock times; an end before the start means the entry ran past midnight. */
function toRange(day: string, startText: string, endText: string, timeZone: string): { start: Date; stop: Date } | null {
  const now = new Date()
  const startClock = readClockField(startText, now, timeZone)
  const endClock = readClockField(endText, now, timeZone)
  if (!startClock || !endClock) {
    return null
  }
  const start = atTime(day, startClock.hours, startClock.minutes, timeZone)
  let stop = atTime(day, endClock.hours, endClock.minutes, timeZone)
  if (stop < start) {
    stop = atTime(addDaysToKey(day, 1), endClock.hours, endClock.minutes, timeZone)
  }
  return { start, stop }
}
