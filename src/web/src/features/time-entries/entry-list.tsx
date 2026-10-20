import { useInfiniteQuery, useQueries } from '@tanstack/react-query'
import { DollarSign, Ellipsis, Lock, Play } from 'lucide-react'
import { useMemo, useState } from 'react'
import { toast } from 'sonner'
import type { SplitSuggestion, TimeEntry } from '@/api/types'
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
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Skeleton } from '@/components/ui/skeleton'
import { entryChangedBy } from '@/api/problem'
import { errorMessage } from '@/lib/error-message'
import { formatDuration } from '@/lib/time/duration'
import { formatClock, formatDayHeading } from '@/lib/time/zone'
import { useNow } from '@/lib/use-now'
import { splitSuggestionsQuery, untrackedQuery, useAgentActivitySync } from '../agent-activity/queries'
import { useReviewCounter } from '../agent-activity/review-data'
import { ReviewDayButton } from '../agent-activity/review-entry-points'
import { SplitSuggestionChip } from '../agent-activity/suggestion-banner'
import { ProjectLabel } from '../catalog/project-label'
import { SplitLabel, SplitMeter } from '../catalog/split-label'
import { useCatalog, type Catalog } from '../catalog/use-catalog'
import { usePermissions } from '../settings/queries'
import { useDurationFormat, useWorkspace } from '../workspace'
import { entrySeconds, groupByDay } from './day-groups'
import { BulkActions } from './bulk-actions'
import type { EntryChange } from './entry-changed'
import { EntryChangedDialog } from './entry-changed-dialog'
import { EntryDialog } from './entry-dialog'
import { editEntry } from './events'
import { entryWeeksQuery, useCreateEntry, useDeleteEntry } from './queries'
import { isSplit, partsOf } from './split'

/** The user's time entries, a week at a time, grouped by day. */
export function EntryList() {
  const { workspaceId, timeZone, weekStartsOn, hour12 } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  const { isLocked } = usePermissions()
  const duration = useDurationFormat()
  const query = useInfiniteQuery(entryWeeksQuery(workspaceId, timeZone, weekStartsOn))
  const [editing, setEditing] = useState<TimeEntry | null>(null)
  const [selecting, setSelecting] = useState(false)
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set())

  const pages = useMemo(() => query.data?.pages ?? [], [query.data])
  const entries = useMemo(() => pages.flatMap((page) => page.entries), [pages])
  const now = useNow(entries.some((entry) => !entry.stop))
  const days = useMemo(() => groupByDay(entries, timeZone, now), [entries, timeZone, now])
  const thisWeek = (pages[0]?.entries ?? []).reduce((sum, entry) => sum + entrySeconds(entry, now), 0)
  const oldestWeek = pages.at(-1)
  const selectedEntries = entries.filter((entry) => selected.has(entry.id))
  // Splits agent activity suggests, a week at a time like the entries, while the person's Macs upload it.
  const sync = useAgentActivitySync()
  const suggestions = useQueries({
    queries: pages.map((page) => splitSuggestionsQuery(workspaceId, page.from, page.to, sync)),
    combine: (results) => new Map(results.flatMap((result) => result.data ?? []).map((suggestion) => [suggestion.entryId, suggestion])),
  })
  // Untracked agent time too, so each day's header can say how much waits in its review.
  const untracked = useQueries({
    queries: pages.map((page) => untrackedQuery(workspaceId, page.from, page.to, sync)),
    combine: (results) => results.flatMap((result) => result.data ?? []),
  })
  const reviewCount = useReviewCounter()

  function toggle(ids: string[], on: boolean) {
    setSelected((current) => {
      const next = new Set(current)
      for (const id of ids) {
        if (on) {
          next.add(id)
        } else {
          next.delete(id)
        }
      }
      return next
    })
  }

  function stopSelecting() {
    setSelecting(false)
    setSelected(new Set())
  }

  if (query.isPending) {
    return <ListSkeleton />
  }

  if (query.isError) {
    return (
      <div className="text-muted-foreground grid justify-items-center gap-3 py-12 text-sm">
        <p>{errorMessage(query.error, "Couldn't load your time entries.")}</p>
        <Button variant="outline" onClick={() => void query.refetch()}>
          Try again
        </Button>
      </div>
    )
  }

  return (
    <div className="grid gap-4">
      <div className="flex items-center justify-between gap-3 px-1">
        <h2 className="text-sm font-medium">This week</h2>
        <div className="flex items-center gap-3">
          <p className="text-sm">
            <span className="text-muted-foreground">Total </span>
            <span className="font-semibold tabular-nums">{duration(thisWeek)}</span>
          </p>
          {entries.length > 0 && (
            <Button variant="ghost" size="sm" aria-pressed={selecting} onClick={() => (selecting ? stopSelecting() : setSelecting(true))}>
              {selecting ? 'Done' : 'Select'}
            </Button>
          )}
        </div>
      </div>

      {days.length === 0 && (
        <div className="grid justify-items-center gap-1 rounded-xl border border-dashed px-6 py-12 text-center">
          <p className="font-medium">Nothing tracked yet this week</p>
          <p className="text-muted-foreground max-w-sm text-sm">
            Type what you are working on and press Start. To add time you forgot to track, switch to manual mode.
          </p>
        </div>
      )}

      {days.map((day) => {
        // Entries on locked days cannot be changed, so they cannot be selected either.
        const selectable = day.entries.filter((e) => !isLocked(e.start))
        return (
          <section key={day.key} aria-label={formatDayHeading(day.key, timeZone, new Date())} className="bg-card overflow-hidden rounded-xl border">
            <header className="bg-muted/40 flex items-center justify-between border-b px-4 py-2">
              <div className="flex items-center gap-3">
                {selecting && (
                  <Checkbox
                    aria-label={`Select all entries on ${formatDayHeading(day.key, timeZone, new Date())}`}
                    disabled={selectable.length === 0}
                    checked={
                      selectable.length > 0 && selectable.every((e) => selected.has(e.id))
                        ? true
                        : selectable.some((e) => selected.has(e.id))
                          ? 'indeterminate'
                          : false
                    }
                    onCheckedChange={(checked) => toggle(selectable.map((e) => e.id), checked === true)}
                  />
                )}
                <h3 className="text-sm font-medium">{formatDayHeading(day.key, timeZone, new Date())}</h3>
                {sync && !selecting && <ReviewDayButton day={day.key} count={reviewCount([day.key], day.entries, [...suggestions.values()], untracked).length} />}
              </div>
              <span className="text-sm font-semibold tabular-nums">{duration(day.totalSeconds)}</span>
            </header>
            <ul className="divide-y">
              {day.entries.map((entry) => (
                <EntryRow
                  key={entry.id}
                  entry={entry}
                  catalog={catalog}
                  workspaceId={workspaceId}
                  timeZone={timeZone}
                  hour12={hour12}
                  now={now}
                  locked={isLocked(entry.start)}
                  suggestion={suggestions.get(entry.id)}
                  onOpen={() => setEditing(entry)}
                  selection={selecting ? { selected: selected.has(entry.id), onToggle: (on) => toggle([entry.id], on) } : undefined}
                />
              ))}
            </ul>
          </section>
        )
      })}

      <div className="grid justify-items-center gap-2 py-2">
        <Button variant="outline" onClick={() => void query.fetchNextPage()} disabled={query.isFetchingNextPage}>
          {query.isFetchingNextPage ? 'Loading…' : 'Load the week before'}
        </Button>
        {oldestWeek && (
          <p className="text-muted-foreground text-xs">
            Showing entries since {new Intl.DateTimeFormat('en-GB', { timeZone, dateStyle: 'medium' }).format(oldestWeek.from)}
          </p>
        )}
      </div>

      {selecting && (
        <BulkActions
          workspaceId={workspaceId}
          selected={selectedEntries}
          onDeleted={() => setSelected(new Set())}
          onClose={stopSelecting}
        />
      )}

      <EntryDialog entry={editing} onClose={() => setEditing(null)} />
    </div>
  )
}

interface EntryRowProps {
  entry: TimeEntry
  catalog: Catalog
  workspaceId: string
  timeZone: string
  hour12: boolean
  now: number
  /** On a locked day: shown, but not changed. */
  locked: boolean
  /** A split agent activity suggests for it. */
  suggestion?: SplitSuggestion
  onOpen: () => void
  /** While selecting, the row toggles its selection instead of opening. */
  selection?: { selected: boolean; onToggle: (selected: boolean) => void }
}

function EntryRow({ entry, catalog, workspaceId, timeZone, hour12, now, locked, suggestion, onOpen, selection }: EntryRowProps) {
  const create = useCreateEntry(workspaceId)
  const remove = useDeleteEntry(workspaceId)
  const duration = useDurationFormat()
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [changed, setChanged] = useState<EntryChange | null>(null)

  const project = catalog.project(entry.projectId)
  const tags = entry.tagIds.map(catalog.tag).filter((tag) => tag !== undefined)
  const running = !entry.stop
  const label = entry.description || 'No description'
  const parts = isSplit(entry) ? partsOf(entry) : null

  const fields = {
    description: entry.description,
    projectId: entry.projectId,
    taskId: entry.taskId,
    tagIds: entry.tagIds,
    isBillable: entry.isBillable,
    shares: entry.shares,
  }

  async function continueEntry() {
    try {
      await create.mutateAsync({ ...fields, start: new Date().toISOString() })
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't start the timer."))
    }
  }

  async function duplicate() {
    try {
      await create.mutateAsync({ ...fields, start: entry.start, stop: entry.stop })
      toast.success('Time entry duplicated')
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't duplicate the time entry."))
    }
  }

  /** Deletes the entry if it is still as listed (or, after "Delete anyway", as the other device left it). */
  async function deleteEntry(version = entry.version) {
    try {
      await remove.mutateAsync({ id: entry.id, version })
      toast.success('Time entry deleted')
    } catch (error) {
      const current = entryChangedBy(error)
      if (current) {
        setChanged({ current, action: 'delete' })
      } else {
        toast.error(errorMessage(error, "Couldn't delete the time entry."))
      }
    }
  }

  return (
    <li className="group hover:bg-muted/40 flex items-center gap-2 px-4 py-2.5 sm:gap-3">
      {selection && (
        <Checkbox
          aria-label={`Select “${label}”`}
          checked={selection.selected}
          disabled={locked}
          onCheckedChange={(checked) => selection.onToggle(checked === true)}
        />
      )}
      <button
        type="button"
        onClick={selection ? () => !locked && selection.onToggle(!selection.selected) : onOpen}
        aria-label={locked ? `View “${label}”` : `Edit “${label}”`}
        // While selecting, the checkbox is the control; the row is only a larger target for the mouse.
        aria-hidden={selection ? true : undefined}
        tabIndex={selection ? -1 : undefined}
        className="min-w-0 flex-1 text-left"
      >
        <p className={entry.description ? 'truncate text-sm' : 'text-muted-foreground truncate text-sm'}>{label}</p>
        {(project || parts || tags.length > 0) && (
          <div className="mt-1 flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1">
            {parts ? (
              <span className="flex min-w-0 items-center gap-2">
                <SplitLabel catalog={catalog} parts={parts} />
                <SplitMeter catalog={catalog} parts={parts} className="hidden w-16 shrink-0 sm:flex" />
              </span>
            ) : (
              project && <ProjectLabel project={project} task={catalog.task(entry.taskId)} client={catalog.client(project.clientId)} />
            )}
            {tags.map((tag) => (
              <Badge key={tag.id} variant="secondary" className="font-normal">
                {tag.name}
              </Badge>
            ))}
          </div>
        )}
      </button>

      {suggestion && !parts && !locked && !selection && <SplitSuggestionChip suggestion={suggestion} catalog={catalog} timeZone={timeZone} hour12={hour12} seconds={entrySeconds(entry, now)} onOpen={onOpen} />}
      {locked && <Lock aria-label="Locked" className="text-muted-foreground size-3.5 shrink-0" />}
      {(parts ? parts.some((part) => part.isBillable) : entry.isBillable) && <DollarSign aria-label="Billable" className="text-muted-foreground hidden size-4 shrink-0 sm:block" />}

      <span className="text-muted-foreground hidden shrink-0 text-sm tabular-nums sm:inline">
        {formatClock(entry.start, timeZone, hour12)} – {entry.stop ? formatClock(entry.stop, timeZone, hour12) : 'now'}
      </span>
      <span className={running ? 'text-primary-text w-20 shrink-0 text-right text-sm font-semibold tabular-nums' : 'w-20 shrink-0 text-right text-sm font-medium tabular-nums'}>
        {/* A running timer always counts in seconds. */}
        {running ? formatDuration(entrySeconds(entry, now)) : duration(entrySeconds(entry, now))}
      </span>

      <div className={selection ? 'hidden' : 'flex shrink-0 items-center sm:opacity-0 sm:group-focus-within:opacity-100 sm:group-hover:opacity-100'}>
        <Button variant="ghost" size="icon" aria-label={`Continue “${label}”`} onClick={() => void continueEntry()} disabled={running || create.isPending}>
          <Play />
        </Button>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon" aria-label={`More actions for “${label}”`}>
              <Ellipsis />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem onSelect={onOpen}>{locked ? 'View' : 'Edit'}</DropdownMenuItem>
            {!locked && <DropdownMenuItem onSelect={() => editEntry({ entry, split: !parts })}>{parts ? 'Change split…' : 'Split time…'}</DropdownMenuItem>}
            <DropdownMenuItem onSelect={() => void duplicate()} disabled={running || locked}>
              Duplicate
            </DropdownMenuItem>
            <DropdownMenuItem variant="destructive" onSelect={() => setConfirmDelete(true)} disabled={locked}>
              Delete
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>

      <AlertDialog open={confirmDelete} onOpenChange={setConfirmDelete}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete this time entry?</AlertDialogTitle>
            <AlertDialogDescription>
              {entry.description ? `“${entry.description}”` : 'The entry'} and its {duration(entrySeconds(entry, now))} will be removed.
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
      <EntryChangedDialog
        change={changed}
        onKeepMine={(change) => {
          setChanged(null)
          void deleteEntry(change.current.version)
        }}
        onUseTheirs={() => setChanged(null)}
      />
    </li>
  )
}

function ListSkeleton() {
  return (
    <div className="grid gap-4" aria-busy="true" aria-label="Loading time entries">
      {[0, 1].map((day) => (
        <div key={day} className="bg-card grid gap-3 rounded-xl border p-4">
          <Skeleton className="h-4 w-32" />
          {[0, 1, 2].map((row) => (
            <Skeleton key={row} className="h-9 w-full" />
          ))}
        </div>
      ))}
    </div>
  )
}
