import { Eye, Lock, Split, Trash2 } from 'lucide-react'
import { useLayoutEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { toast } from 'sonner'
import { ApiError, entryChangedBy } from '@/api/problem'
import type { TimeEntry } from '@/api/types'
import { CONNECTION_ERROR, hasErrors, toFormErrors, type FormErrors } from '@/auth/form-errors'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { errorMessage } from '@/lib/error-message'
import { formatDuration, parseDuration } from '@/lib/time/duration'
import { addDaysToKey, atTime, clockAt, clockInputValue, dayKey, readClockField } from '@/lib/time/zone'
import { useNow } from '@/lib/use-now'
import { ProjectLabel } from '../catalog/project-label'
import { ProjectPicker } from '../catalog/project-picker'
import { TagPicker } from '../catalog/tag-picker'
import { useCatalog } from '../catalog/use-catalog'
import { usePermissions } from '../settings/queries'
import { useWorkspace } from '../workspace'
import { useDismissSuggestion, useEntrySuggestion } from '../agent-activity/queries'
import { SplitSuggestionBanner } from '../agent-activity/suggestion-banner'
import type { Stretch } from '../agent-activity/suggestion'
import { ClockInput } from './clock-input'
import { MentionList } from './mention-field'
import { applyMentions, findMentions, mentionable, parseMentions } from './mentions'
import { useDescriptionCompletion } from './use-description-completion'
import { useMentions } from './use-mentions'
import type { EntryChange } from './entry-changed'
import { EntryChangedDialog } from './entry-changed-dialog'
import { useCreateEntry, useDeleteEntry, useUpdateEntry } from './queries'
import { missingDetails, missingInParts, requiresTask } from './rules'
import {
  describeEachPart,
  describesParts,
  isSplit,
  joinDescriptions,
  partsOf,
  shareOneDescription,
  sharesRequest,
  splitErrors,
  type SplitPart,
} from './split'
import { SplitEditor } from './split-editor'

const FIELDS = ['description', 'projectId', 'taskId', 'tagIds', 'start', 'stop'] as const
const UNREADABLE_TIME = 'Enter a time such as 930 or 9:30.'
type Field = (typeof FIELDS)[number]

/** A time span to create an entry for, e.g. one drawn on the calendar. */
export interface NewEntry {
  start: Date
  stop: Date
  /** What it starts with, e.g. what agents worked on in untracked time: a project, a split, or separate entries. */
  projectId?: string | null
  parts?: SplitPart[] | null
  stretches?: Stretch[] | null
}

interface EntryDialogProps {
  entry: TimeEntry | null
  newEntry?: NewEntry | null
  /** The running timer could not stop because the entry lacks required details; they are asked for right away. */
  stopping?: boolean
  /** Begins splitting the entry's time between projects right away. */
  split?: boolean
  /** What the form starts with instead of `entry` as it is, e.g. a change an agent proposed. */
  draft?: TimeEntry | null
  /** Starts `entry` as separate entries cut where the work moved, e.g. from the day's review. */
  stretches?: Stretch[] | null
  /** Told the entry once it is saved, before the dialog closes. */
  onSaved?: (entry: TimeEntry) => void
  onClose: () => void
}

/** Edits `entry`, or creates an entry for `newEntry`. Entries on locked days are shown, not edited. */
export function EntryDialog({ entry, newEntry, stopping = false, split = false, draft, stretches, onSaved, onClose }: EntryDialogProps) {
  const open = entry !== null || !!newEntry
  return (
    <Dialog open={open} onOpenChange={(isOpen) => !isOpen && onClose()}>
      <DialogContent
        className="sm:max-w-xl"
        // Escape while a project is being mentioned, or an earlier description offered, closes that list, not the dialog.
        onEscapeKeyDown={(event) =>
          event.target instanceof HTMLElement && (event.target.dataset.mentioning || event.target.dataset.completing) && event.preventDefault()
        }
      >
        {entry ? (
          <EntryForm
            key={entry.id}
            entry={entry}
            stopping={stopping}
            split={split}
            draft={draft ?? undefined}
            stretches={stretches ?? undefined}
            onSaved={onSaved}
            onDone={onClose}
          />
        ) : (
          newEntry && <EntryForm key={newEntry.start.getTime()} newEntry={newEntry} draft={draft ?? undefined} onSaved={onSaved} onDone={onClose} />
        )}
      </DialogContent>
    </Dialog>
  )
}

function EntryForm({
  entry,
  newEntry,
  stopping = false,
  split = false,
  draft,
  stretches: initialStretches,
  onSaved,
  onDone,
}: {
  entry?: TimeEntry
  newEntry?: NewEntry
  stopping?: boolean
  split?: boolean
  draft?: TimeEntry
  stretches?: Stretch[]
  onSaved?: (entry: TimeEntry) => void
  onDone: () => void
}) {
  const { workspaceId, timeZone, hour12 } = useWorkspace()
  const { settings, isLocked, lockMessage, firstOpenDay } = usePermissions()
  const catalog = useCatalog(workspaceId)
  const create = useCreateEntry(workspaceId)
  const update = useUpdateEntry(workspaceId)
  const remove = useDeleteEntry(workspaceId)
  const running = !!entry && !entry.stop
  const locked = !!entry && isLocked(entry.start)
  const now = useNow(running)

  const initial = draft ?? entry
  const initialStart = initial ? new Date(initial.start) : newEntry!.start
  const initialEnd = initial ? (initial.stop ? new Date(initial.stop) : null) : newEntry!.stop
  const [description, setDescription] = useState(initial?.description ?? '')
  const [projectId, setProjectId] = useState(initial?.projectId ?? newEntry?.projectId ?? null)
  const [taskId, setTaskId] = useState(initial?.taskId ?? null)
  const [tagIds, setTagIds] = useState(initial?.tagIds ?? [])
  const [isBillable, setIsBillable] = useState(initial?.isBillable ?? (catalog.project(newEntry?.projectId)?.isBillable ?? false))
  // Two or more parts while the time is split between projects; null while it is all on one.
  const [parts, setParts] = useState<SplitPart[] | null>(() => (initial && isSplit(initial) ? partsOf(initial) : (newEntry?.parts ?? null)))
  // Separate entries, one after another, cut where the work moved; null while it is one entry.
  const [stretches, setStretches] = useState<Stretch[] | null>(newEntry?.stretches ?? initialStretches ?? null)
  const [splitting, setSplitting] = useState(split && !(entry && isSplit(entry)))
  // What agent activity suggests for an entry on one project, until the person applies it or says not now.
  const suggestion = useEntrySuggestion(workspaceId, entry, !!entry && !draft && !isSplit(entry) && !locked)
  const dismissSuggestion = useDismissSuggestion(workspaceId)
  const [suggestionClosed, setSuggestionClosed] = useState(false)
  const [adding, setAdding] = useState(false)
  const [partErrors, setPartErrors] = useState<{ parts: Record<number, string | undefined>; split?: string }>(() => ({
    parts: stopping && entry && isSplit(entry) ? missingInParts(settings, partsOf(entry), catalog.project) : {},
  }))
  const [startAt, setStartAt] = useState(initialStart)
  const [endAt, setEndAt] = useState(initialEnd)
  const [startText, setStartText] = useState(() => clockInputValue(initialStart, timeZone, hour12))
  const [endText, setEndText] = useState(() => (initialEnd ? clockInputValue(initialEnd, timeZone, hour12) : ''))
  const [durationText, setDurationText] = useState<string | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)
  // The entry's version when the editor opened it: saving and deleting go through only if it is still that version.
  const version = useRef(entry?.version)
  const [changed, setChanged] = useState<(EntryChange & { stopNow: boolean }) | null>(null)
  const [errors, setErrors] = useState<FormErrors<Field>>(() =>
    stopping && entry ? missingDetails(settings, entry, catalog.project(entry.projectId)) : {},
  )

  const day = dayKey(startAt, timeZone)
  const seconds = ((endAt?.getTime() ?? now) - startAt.getTime()) / 1000
  const project = catalog.project(projectId)
  const taskRequired = requiresTask(settings, project)
  const perPart = describesParts(parts)
  const descriptionRef = useRef<HTMLInputElement>(null)
  const mentionProjects = useMemo(() => mentionable(catalog.projects), [catalog.projects])
  const mentions = useMentions(description, changeDescription, descriptionRef, catalog)
  const completion = useDescriptionCompletion({
    workspaceId,
    value: description,
    projectId,
    onPick: changeDescription,
    disabled: mentions.matches.length > 0,
  })
  // A field to focus once it is on screen: a part's description, or the entry's. Focused as soon as React renders it,
  // so what is typed right after switching lands there.
  const [focusField, setFocusField] = useState<string | null>(null)
  useLayoutEffect(() => {
    if (focusField) {
      document.getElementById(focusField)?.focus()
      setFocusField(null)
    }
  }, [focusField])

  /**
   * Mentioning a project in the description (`… @FrameIt`) describes the entry per project right away: the project
   * joins the split, the text before the mention stays with the entry's own project, and the mentioned project's row
   * is ready for typing.
   */
  function changeDescription(text: string) {
    const found = findMentions(text, mentionProjects)
    // Separate entries share one description; a mention there is only text.
    if (found.length === 0 || stretches) {
      setDescription(text)
      return
    }
    const result = applyMentions(parseMentions(text, mentionProjects), { projectId, taskId, isBillable, parts }, mentionProjects)
    if (!result) {
      setErrors((current) => ({ ...current, description: 'Split the time between at most 6 projects.' }))
      return
    }
    setErrors((current) => ({ ...current, description: undefined }))
    if (!result.parts) {
      setProjectId(result.projectId)
      setTaskId(result.taskId)
      setIsBillable(result.isBillable)
      setDescription(result.description)
      return
    }
    setParts(result.parts)
    setPartErrors({ parts: {} })
    const row = result.parts.findIndex((part) => part.projectId === found[found.length - 1].projectId)
    setFocusField(`entry-part-description-${row}`)
  }

  /** Begins a split: what was chosen keeps half the time, the project picked gets the other half. */
  function beginSplit(nextProject: string | null, nextTask: string | null) {
    setParts([
      { projectId, taskId, isBillable, percent: 50 },
      { projectId: nextProject, taskId: nextTask, isBillable: catalog.project(nextProject)?.isBillable ?? false, percent: 50 },
    ])
    setPartErrors({ parts: {} })
  }

  /** A split left with one part is that part's project again, described as that part was. */
  function changeParts(next: SplitPart[]) {
    if (next.length < 2) {
      const [only] = next
      setProjectId(only?.projectId ?? null)
      setTaskId(only?.taskId ?? null)
      setIsBillable(only?.isBillable ?? isBillable)
      if (only?.description !== undefined) {
        setDescription(only.description)
      }
      setParts(null)
    } else {
      setParts(next)
    }
    setPartErrors({ parts: {} })
  }

  /** Each project gets its own description, and the first one still to be written is ready for typing. */
  function describePerPart() {
    if (!parts) {
      return
    }
    const described = describeEachPart(parts, description)
    setParts(described)
    const next = Math.max(0, described.findIndex((part) => !part.description))
    setFocusField(`entry-part-description-${next}`)
  }

  function describeAllAsOne() {
    if (!parts) {
      return
    }
    const shared = shareOneDescription(parts)
    setParts(shared.parts)
    setDescription(shared.description)
    setPartErrors({ parts: {} })
    setFocusField('entry-description')
  }

  function show(start: Date, end: Date | null) {
    setStartAt(start)
    setEndAt(end)
    setStartText(clockInputValue(start, timeZone, hour12))
    setEndText(end ? clockInputValue(end, timeZone, hour12) : '')
  }

  /** The start a typed time gives, on the entry's day; the start as it was if the time wasn't changed. */
  function startFrom(text: string): Date | null {
    if (text === clockInputValue(startAt, timeZone, hour12)) {
      return startAt
    }
    const clock = readClockField(text, new Date(), timeZone)
    return clock && atTime(day, clock.hours, clock.minutes, timeZone)
  }

  /** The end a typed time gives; an end before the start means the entry ran past midnight. */
  function endFrom(text: string, start: Date): Date | null {
    if (endAt && text === clockInputValue(endAt, timeZone, hour12)) {
      return endAt
    }
    const clock = readClockField(text, new Date(), timeZone)
    if (!clock) {
      return null
    }
    const end = atTime(day, clock.hours, clock.minutes, timeZone)
    return end < start ? atTime(addDaysToKey(day, 1), clock.hours, clock.minutes, timeZone) : end
  }

  /** A new start keeps the end where it is, so the duration changes. Each field only rewrites itself. */
  function commitStart() {
    const start = startFrom(startText)
    setErrors((current) => ({ ...current, start: start ? undefined : UNREADABLE_TIME }))
    if (start) {
      setStartAt(start)
      setStartText(clockInputValue(start, timeZone, hour12))
    }
  }

  function commitEnd() {
    if (!endAt) {
      return
    }
    const end = endFrom(endText, startAt)
    setErrors((current) => ({ ...current, stop: end ? undefined : UNREADABLE_TIME }))
    if (end) {
      showEnd(end)
    }
  }

  function commitDuration() {
    const parsed = durationText === null ? null : parseDuration(durationText)
    if (parsed !== null && endAt) {
      showEnd(new Date(startAt.getTime() + parsed * 1000))
    }
    setDurationText(null)
  }

  function showEnd(end: Date) {
    setEndAt(end)
    setEndText(clockInputValue(end, timeZone, hour12))
  }

  /** Moving to another day keeps the clock times, counted in calendar days. */
  function changeDay(newDay: string) {
    const offset = Math.round((Date.parse(newDay) - Date.parse(day)) / 86_400_000)
    const startClock = clockAt(startAt, timeZone)
    const start = atTime(newDay, startClock.hours, startClock.minutes, timeZone)
    let end: Date | null = null
    if (endAt) {
      const endClock = clockAt(endAt, timeZone)
      end = atTime(addDaysToKey(dayKey(endAt, timeZone), offset), endClock.hours, endClock.minutes, timeZone)
    }
    show(start, end)
  }

  /** Saves the entry; `stopNow` also stops a running timer, keeping the changes made here. */
  async function save(stopNow: boolean) {
    // Enter saves straight from a time field, before leaving it would have read what was typed.
    const start = startFrom(startText)
    let typedEnd = endAt && endFrom(endText, start ?? startAt)
    if (!start || (endAt && !typedEnd)) {
      setErrors({ start: start ? undefined : UNREADABLE_TIME, stop: endAt && !typedEnd ? UNREADABLE_TIME : undefined })
      return
    }
    const typedDuration = durationText === null ? null : parseDuration(durationText)
    if (typedEnd && typedDuration !== null) {
      typedEnd = new Date(start.getTime() + typedDuration * 1000)
    }
    const end = stopNow ? new Date() : typedEnd
    if (end && end < start) {
      setErrors({ stop: 'The end must not be before the start.' })
      return
    }
    if (stretches) {
      await saveStretches(stretches, start, end, stopNow)
      return
    }
    // A running entry may lack details for now; a finished one must have what the workspace requires.
    const missing = end ? missingDetails(settings, { description, projectId, taskId, tagIds, shares: parts ?? undefined }, project) : {}
    const missingParts = end && parts ? missingInParts(settings, parts, catalog.project) : {}
    setErrors(missing)
    setPartErrors({ parts: missingParts })
    if (hasErrors(missing) || hasErrors(missingParts)) {
      return
    }

    const body = {
      description: perPart && parts ? joinDescriptions(parts) : description.trim(),
      ...sharesRequest(parts, { projectId, taskId, isBillable }),
      tagIds,
      start: start.toISOString(),
      stop: end?.toISOString() ?? null,
    }
    try {
      const saved = entry ? await update.mutateAsync({ id: entry.id, version: version.current, body }) : await create.mutateAsync(body)
      onSaved?.(saved)
      if (stopNow) {
        toast.success('Timer stopped')
      }
      onDone()
    } catch (error) {
      const current = entryChangedBy(error)
      if (current) {
        setChanged({ current, action: 'save', stopNow })
      } else if (error instanceof ApiError) {
        const sorted = splitErrors(error.fieldErrors)
        setPartErrors({ parts: sorted.parts, split: sorted.split })
        const others = Object.keys(sorted.rest).length > 0 || !(sorted.split || hasErrors(sorted.parts))
        setErrors(others ? toFormErrors(new ApiError(error.status, error.problem && ({ ...error.problem, errors: sorted.rest } as typeof error.problem)), FIELDS) : {})
      } else {
        setErrors({ form: CONNECTION_ERROR })
      }
    }
  }

  /**
   * Saves separate entries, one per stretch, each with the description and tags. The entry itself becomes the last
   * stretch, so a running timer keeps running; the earlier ones are added before it.
   */
  async function saveStretches(cuts: Stretch[], start: Date, end: Date | null, stopNow: boolean) {
    const bounds = cuts.map((stretch, index) => ({
      projectId: stretch.projectId,
      start: index === 0 ? start : stretch.start,
      stop: index === cuts.length - 1 ? end : cuts[index + 1].start,
    }))
    if (bounds.some((bound) => bound.stop !== null && bound.stop <= bound.start)) {
      setErrors({ form: 'Each entry must end after it starts: change the start or end, or go back to one entry.' })
      return
    }
    for (const bound of bounds.filter((bound) => bound.stop)) {
      const missing = missingDetails(settings, { description, projectId: bound.projectId, taskId: null, tagIds }, catalog.project(bound.projectId))
      if (missing.taskId) {
        setErrors({ form: `${catalog.project(bound.projectId)?.name ?? 'A project'} needs a task. Go back to one entry to choose it.` })
        return
      }
      if (hasErrors(missing)) {
        setErrors(missing)
        return
      }
    }
    const body = (bound: (typeof bounds)[number]) => ({
      description: description.trim(),
      projectId: bound.projectId,
      taskId: null,
      isBillable: catalog.project(bound.projectId)?.isBillable ?? false,
      tagIds,
      start: bound.start.toISOString(),
      stop: bound.stop?.toISOString() ?? null,
    })
    try {
      const last = bounds[bounds.length - 1]
      const saved = entry ? await update.mutateAsync({ id: entry.id, version: version.current, body: body(last) }) : await create.mutateAsync(body(last))
      for (const bound of bounds.slice(0, -1)) {
        await create.mutateAsync(body(bound))
      }
      onSaved?.(saved)
      toast.success('Split saved')
      onDone()
    } catch (error) {
      const current = entryChangedBy(error)
      if (current) {
        setChanged({ current, action: 'save', stopNow })
        return
      }
      setErrors(error instanceof ApiError ? toFormErrors(error, FIELDS) : { form: CONNECTION_ERROR })
    }
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    // Asked to add what is missing to stop the timer, saving means stopping.
    void save(stopping && running)
  }

  async function deleteEntry() {
    if (!entry) {
      return
    }
    try {
      await remove.mutateAsync({ id: entry.id, version: version.current })
      toast.success('Time entry deleted')
      onDone()
    } catch (error) {
      const current = entryChangedBy(error)
      if (current) {
        setChanged({ current, action: 'delete', stopNow: false })
      } else {
        toast.error(errorMessage(error, "Couldn't delete the time entry."))
      }
    }
  }

  /** Saves or deletes again, now over the version the other device saved. */
  function keepMine(change: EntryChange) {
    const stopNow = changed?.stopNow ?? false
    version.current = change.current.version
    setChanged(null)
    void (change.action === 'delete' ? deleteEntry() : save(stopNow))
  }

  function keepTheirs() {
    setChanged(null)
    toast.success('Kept the version from the other device')
    onDone()
  }

  const title = !entry ? 'New time entry' : locked ? 'Time entry' : stopping && running ? 'Stop timer' : running ? 'Edit running entry' : 'Edit time entry'

  return (
    <>
      <form noValidate onSubmit={handleSubmit} className="grid gap-5">
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription className={stopping && running ? undefined : 'sr-only'}>
            {stopping && running ? 'Add the details this workspace requires, then stop the timer.' : 'Change what the time was spent on, and when.'}
          </DialogDescription>
        </DialogHeader>

        {locked && (
          <Alert>
            <Lock />
            <AlertDescription>{lockMessage}</AlertDescription>
          </Alert>
        )}

        {errors.form && (
          <Alert variant="destructive">
            <AlertDescription>{errors.form}</AlertDescription>
          </Alert>
        )}

        {suggestion && entry && !draft && !parts && !stretches && !suggestionClosed && (
          <SplitSuggestionBanner
            suggestion={suggestion}
            workspaceId={workspaceId}
            catalog={catalog}
            timeZone={timeZone}
            hour12={hour12}
            onApply={(suggested) => {
              changeParts(suggested)
              setSuggestionClosed(true)
            }}
            onSeparate={(cuts) => {
              setStretches(cuts)
              setSuggestionClosed(true)
            }}
            onDismiss={() => {
              setSuggestionClosed(true)
              dismissSuggestion.mutate(entry.id, { onError: (error) => toast.error(errorMessage(error, "Couldn't keep the entry as it is.")) })
            }}
          />
        )}

        {/* On a locked day everything is shown as it is, and nothing can be changed. */}
        <fieldset disabled={locked} className="grid min-w-0 gap-5">
          {!perPart && (
            <div className="grid gap-2">
              <FieldLabel htmlFor="entry-description" required={!locked && settings.requireDescription}>
                Description
              </FieldLabel>
              <div className="relative">
                <Input
                  ref={descriptionRef}
                  id="entry-description"
                  value={description}
                  placeholder={locked ? undefined : 'Add description, or @ a project to describe it per project'}
                  autoComplete="off"
                  {...completion.inputProps}
                  onChange={(e) => {
                    changeDescription(e.target.value)
                    mentions.track()
                    completion.onTyped()
                  }}
                  onSelect={mentions.track}
                  onKeyDown={(e) => mentions.onKeyDown(e) || completion.onKeyDown(e)}
                  onBlur={completion.onBlur}
                  aria-controls={mentions.matches.length > 0 ? 'entry-description-mentions' : completion.visible ? completion.listId : undefined}
                  data-mentioning={mentions.matches.length > 0 || undefined}
                  aria-invalid={errors.description ? true : undefined}
                  aria-describedby={[errors.description && 'entry-description-error', parts && 'entry-description-reach'].filter(Boolean).join(' ') || undefined}
                />
                {mentions.matches.length > 0 && <MentionList id="entry-description-mentions" mentions={mentions} catalog={catalog} />}
                {completion.list}
              </div>
              <FieldError id="entry-description-error" message={errors.description} />
              {/* One description on a split shows on every project's reports and timesheet links, so it says so. */}
              {parts && !locked && (
                <p id="entry-description-reach" className="text-muted-foreground flex flex-wrap items-center gap-x-1.5 text-xs">
                  <Eye className="size-3.5 shrink-0" aria-hidden />
                  <span>Shown on all {parts.length} projects, timesheet links included</span>
                  <span aria-hidden>·</span>
                  <button type="button" className="text-foreground underline-offset-2 hover:underline" onClick={describePerPart}>
                    Describe per project
                  </button>
                </p>
              )}
            </div>
          )}

          {stretches && (
            <div className="grid gap-2">
              <FieldLabel required={false}>Separate entries</FieldLabel>
              <StretchList
                stretches={stretches}
                start={startAt}
                end={endAt}
                now={now}
                catalog={catalog}
                timeZone={timeZone}
                hour12={hour12}
                onKeepOne={() => {
                  setStretches(null)
                  setErrors({})
                }}
              />
            </div>
          )}

          {parts && !stretches && (
            <div className="grid gap-2">
              <div className="flex items-baseline justify-between gap-3">
                <FieldLabel required={!locked && (settings.requireProject || (perPart && settings.requireDescription))}>
                  {perPart ? 'Split and described per project' : 'Split between projects'}
                </FieldLabel>
                <span className="text-muted-foreground hidden text-xs sm:inline">Drag a divider · ⌥ for 1% steps · double-click to even out</span>
              </div>
              <SplitEditor
                workspaceId={workspaceId}
                catalog={catalog}
                parts={parts}
                seconds={seconds}
                running={!endAt}
                errors={partErrors}
                onChange={changeParts}
                adding={adding}
                onAddingChange={setAdding}
                onDescribeAllAsOne={describeAllAsOne}
              />
            </div>
          )}

          <div className={parts || stretches ? 'grid gap-2' : 'grid gap-4 sm:grid-cols-2'}>
            {!parts && !stretches && (
            <div className="grid content-start gap-2">
              <div className="flex items-center justify-between gap-2">
                <FieldLabel required={!locked && (settings.requireProject || taskRequired)}>{taskRequired ? 'Project and task' : 'Project'}</FieldLabel>
                {!locked && (
                  <ProjectPicker
                    workspaceId={workspaceId}
                    projectId={undefined}
                    taskId={null}
                    align="end"
                    open={splitting}
                    onOpenChange={setSplitting}
                    placeholder="Split the time with which project?"
                    taken={(p, t) => p === projectId && t === taskId}
                    onChange={beginSplit}
                  >
                    <Button type="button" variant="ghost" size="xs" className="text-muted-foreground -my-1 h-6" aria-label="Split time between projects">
                      <Split />
                      Split
                    </Button>
                  </ProjectPicker>
                )}
              </div>
              <ProjectPicker
                workspaceId={workspaceId}
                projectId={projectId}
                taskId={taskId}
                onSplit={() => setSplitting(true)}
                onChange={(nextProject, nextTask) => {
                  setProjectId(nextProject)
                  setTaskId(nextTask)
                  setIsBillable(catalog.project(nextProject)?.isBillable ?? isBillable)
                }}
              >
                <Button
                  type="button"
                  variant="outline"
                  className="justify-start"
                  aria-label={project ? `Project: ${project.name}` : 'Choose project'}
                  aria-invalid={errors.projectId || errors.taskId ? true : undefined}
                  aria-describedby={errors.projectId || errors.taskId ? 'entry-project-error' : undefined}
                >
                  {project ? (
                    <ProjectLabel project={project} task={catalog.task(taskId)} />
                  ) : (
                    <span className="text-muted-foreground">No project</span>
                  )}
                </Button>
              </ProjectPicker>
              <FieldError id="entry-project-error" message={errors.projectId ?? errors.taskId} />
            </div>
            )}
            <div className="grid content-start gap-2">
              <FieldLabel required={!locked && settings.requireTag}>Tags</FieldLabel>
              <TagPicker workspaceId={workspaceId} value={tagIds} onChange={setTagIds}>
                <Button
                  type="button"
                  variant="outline"
                  className="h-auto min-h-8 justify-start"
                  aria-label="Choose tags"
                  aria-invalid={errors.tagIds ? true : undefined}
                  aria-describedby={errors.tagIds ? 'entry-tags-error' : undefined}
                >
                  {tagIds.length ? (
                    <span className="flex flex-wrap gap-1">
                      {tagIds.map((id) => (
                        <Badge key={id} variant="secondary">
                          {catalog.tag(id)?.name ?? '…'}
                        </Badge>
                      ))}
                    </span>
                  ) : (
                    <span className="text-muted-foreground">No tags</span>
                  )}
                </Button>
              </TagPicker>
              <FieldError id="entry-tags-error" message={errors.tagIds} />
            </div>
          </div>

          {!parts && !stretches && (
            <div className="flex items-center justify-between gap-4">
              <Label htmlFor="entry-billable">Billable</Label>
              <Switch id="entry-billable" checked={isBillable} onCheckedChange={setIsBillable} />
            </div>
          )}

          <div className="grid grid-cols-2 gap-3 sm:grid-cols-[minmax(9.5rem,1.4fr)_1fr_1fr_1fr]">
            <div className="col-span-2 grid gap-2 sm:col-span-1">
              <Label htmlFor="entry-date">Date</Label>
              <Input
                id="entry-date"
                type="date"
                value={day}
                min={locked ? undefined : (firstOpenDay ?? undefined)}
                onChange={(e) => e.target.value && changeDay(e.target.value)}
              />
            </div>
            <div className="grid gap-2">
              <Label htmlFor="entry-start">Start</Label>
              <ClockInput
                id="entry-start"
                value={startText}
                onValueChange={setStartText}
                onCommit={commitStart}
                aria-invalid={errors.start ? true : undefined}
                className="tabular-nums"
              />
            </div>
            <div className="grid gap-2">
              <Label htmlFor="entry-end">End</Label>
              {endAt ? (
                <ClockInput
                  id="entry-end"
                  value={endText}
                  onValueChange={setEndText}
                  onCommit={commitEnd}
                  aria-invalid={errors.stop ? true : undefined}
                  className="tabular-nums"
                />
              ) : (
                <Input id="entry-end" value="Running" disabled />
              )}
            </div>
            <div className="grid gap-2">
              <Label htmlFor="entry-duration">Duration</Label>
              <Input
                id="entry-duration"
                value={durationText ?? formatDuration(seconds)}
                disabled={!endAt}
                onFocus={() => setDurationText(formatDuration(seconds))}
                onChange={(e) => setDurationText(e.target.value)}
                onBlur={commitDuration}
                className="font-mono tabular-nums"
              />
            </div>
          </div>
          {(errors.start || errors.stop) && <p className="text-destructive -mt-3 text-sm">{errors.start ?? errors.stop}</p>}
        </fieldset>

        {locked ? (
          <DialogFooter>
            <Button type="button" onClick={onDone}>
              Done
            </Button>
          </DialogFooter>
        ) : (
          <DialogFooter className="sm:justify-between">
            {entry ? (
              <Button type="button" variant="ghost" className="text-destructive hover:text-destructive" onClick={() => setConfirmDelete(true)}>
                <Trash2 />
                Delete
              </Button>
            ) : (
              <span />
            )}
            <div className="flex flex-col-reverse gap-2 sm:flex-row">
              {running && !stopping && (
                <Button type="button" variant="outline" onClick={() => void save(true)} disabled={update.isPending}>
                  Stop timer
                </Button>
              )}
              <Button type="button" variant="ghost" onClick={onDone}>
                {stopping && running ? 'Keep running' : 'Cancel'}
              </Button>
              <Button type="submit" disabled={update.isPending || create.isPending}>
                {!entry ? 'Add entry' : stopping && running ? 'Save and stop' : 'Save'}
              </Button>
            </div>
          </DialogFooter>
        )}
      </form>

      <AlertDialog open={confirmDelete} onOpenChange={setConfirmDelete}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete this time entry?</AlertDialogTitle>
            <AlertDialogDescription>
              {entry?.description ? `“${entry.description}”` : 'The entry'} and its {formatDuration(seconds)} will be removed.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction variant="destructive" onClick={() => void deleteEntry()}>
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
      <EntryChangedDialog change={changed} onKeepMine={keepMine} onUseTheirs={keepTheirs} />
    </>
  )
}

function FieldLabel({ htmlFor, required, children }: { htmlFor?: string; required: boolean; children: ReactNode }) {
  return (
    <Label htmlFor={htmlFor}>
      {children}
      {required && <span className="text-muted-foreground font-normal">(required)</span>}
    </Label>
  )
}

function FieldError({ id, message }: { id: string; message?: string }) {
  return message ? (
    <p id={id} className="text-destructive text-sm">
      {message}
    </p>
  ) : null
}

/** The entries a stretch of time will become, one after another, each on its project. */
function StretchList({
  stretches,
  start,
  end,
  now,
  catalog,
  timeZone,
  hour12,
  onKeepOne,
}: {
  stretches: Stretch[]
  /** The entry's start and end as edited; the first stretch starts and the last ends there. */
  start: Date
  end: Date | null
  now: number
  catalog: ReturnType<typeof useCatalog>
  timeZone: string
  hour12: boolean
  onKeepOne: () => void
}) {
  return (
    <div className="bg-muted/30 grid gap-1.5 rounded-lg border p-3">
      <ul className="grid gap-1.5">
        {stretches.map((stretch, index) => {
          const from = index === 0 ? start : stretch.start
          const to = index === stretches.length - 1 ? end : stretches[index + 1].start
          const project = catalog.project(stretch.projectId)
          return (
            <li key={index} className="flex items-center gap-2 text-sm">
              {project ? <ProjectLabel project={project} /> : <span className="text-muted-foreground">Project</span>}
              <span className="text-muted-foreground ml-auto tabular-nums">
                {clockInputValue(from, timeZone, hour12)} – {to ? clockInputValue(to, timeZone, hour12) : 'now'}
              </span>
              <span className="text-muted-foreground w-16 text-right font-mono tabular-nums">
                {formatDuration(Math.max(0, ((to?.getTime() ?? now) - from.getTime()) / 1000))}
              </span>
            </li>
          )
        })}
      </ul>
      <div className="flex flex-wrap items-center justify-between gap-2 pt-1">
        <p className="text-muted-foreground text-xs">Each entry keeps the description and tags.</p>
        <Button type="button" variant="link" size="xs" className="h-auto px-0" onClick={onKeepOne}>
          Back to one entry
        </Button>
      </div>
    </div>
  )
}
