import { useQuery } from '@tanstack/react-query'
import { TriangleAlert } from 'lucide-react'
import { useState, type FormEvent, type ReactNode } from 'react'
import type { TimeEntry } from '@/api/types'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { errorMessage } from '@/lib/error-message'
import { formatDuration } from '@/lib/time/duration'
import { clockInputValue, dayKey, formatClock, formatDayHeading, readStartField } from '@/lib/time/zone'
import { useNow } from '@/lib/use-now'
import { usePermissions } from '../settings/queries'
import { useWorkspace } from '../workspace'
import { ClockInput } from './clock-input'
import { recentEntriesQuery } from './queries'

/** A timer running this long was most likely started at the wrong time, e.g. 21:15 yesterday instead of 20:15 today. */
const LONG_RUN_SECONDS = 12 * 3600
const AGO_MINUTES = [5, 15, 30]

/**
 * Corrects when the running timer started. Shows the day and duration the typed time gives before it is saved, asks
 * again when that makes the timer run for half a day, and offers the times people usually mean.
 */
export function RunningStart({ entry, onSave, onDone }: { entry: TimeEntry; onSave: (start: string) => Promise<unknown>; onDone: () => void }) {
  const { timeZone, hour12 } = useWorkspace()
  const { isLocked, lockMessage } = usePermissions()
  const now = useNow()
  const [text, setText] = useState(() => clockInputValue(entry.start, timeZone, hour12))
  const [error, setError] = useState<string | null>(null)
  const [confirmingLong, setConfirmingLong] = useState(false)
  const [saving, setSaving] = useState(false)
  const today = dayKey(new Date(now), timeZone)
  const { data: recent } = useQuery(recentEntriesQuery(entry.workspaceId, today, timeZone))

  const typed = readStartField(text, new Date(now), timeZone)
  // The minute it started in already: kept to the second, so an unchanged time changes nothing.
  const start = typed && typed.getTime() === Math.floor(Date.parse(entry.start) / 60_000) * 60_000 ? new Date(entry.start) : typed
  const seconds = start ? (now - start.getTime()) / 1000 : 0
  const long = seconds >= LONG_RUN_SECONDS
  const locked = !!start && isLocked(start)

  // The entry that ended last, which the timer usually picks up from.
  const previous = (recent ?? [])
    .filter((e): e is TimeEntry & { stop: string } => e.id !== entry.id && !!e.stop && Date.parse(e.stop) <= now)
    .reduce<(TimeEntry & { stop: string }) | null>((latest, e) => (!latest || e.stop > latest.stop ? e : latest), null)
  const overlap = previous && start ? (Date.parse(previous.stop) - start.getTime()) / 1000 : 0

  function change(value: string) {
    if (value !== text) {
      setText(value)
      setError(null)
      setConfirmingLong(false)
    }
  }

  /** Puts a time in the field and goes back to it, so Enter saves it. */
  function suggest(value: string) {
    change(value)
    document.getElementById('running-start')?.focus()
  }

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    if (!start) {
      setError('Enter a time such as 930, 9:30 or -15 for 15 minutes ago.')
      return
    }
    if (locked) {
      setError(lockMessage)
      return
    }
    if (long && !confirmingLong) {
      setConfirmingLong(true)
      return
    }
    if (start.getTime() === Date.parse(entry.start)) {
      onDone()
      return
    }
    setSaving(true)
    try {
      await onSave(start.toISOString())
      onDone()
    } catch (saveError) {
      setError(errorMessage(saveError, "Couldn't change the start time."))
    } finally {
      setSaving(false)
    }
  }

  return (
    <form onSubmit={handleSubmit} noValidate className="grid gap-3">
      <div className="grid gap-2">
        <Label htmlFor="running-start">Started at</Label>
        <div className="flex gap-2">
          <ClockInput
            id="running-start"
            value={text}
            onValueChange={change}
            autoFocus
            aria-invalid={error ? true : undefined}
            aria-describedby="running-start-result"
            className="tabular-nums"
          />
          <Button type="submit" disabled={saving || locked} variant={confirmingLong ? 'destructive' : 'default'}>
            {confirmingLong ? 'Save anyway' : 'Save'}
          </Button>
        </div>

        <div id="running-start-result" aria-live="polite" className="grid gap-1.5 text-sm">
          {error ? (
            <p className="text-destructive">{error}</p>
          ) : start ? (
            <p className="text-muted-foreground tabular-nums">
              {formatDayHeading(dayKey(start, timeZone), timeZone, new Date(now))}, {formatClock(start, timeZone, hour12)}
              <span aria-hidden="true"> · </span>
              <span className={long ? 'text-foreground font-medium' : undefined}>{formatDuration(seconds)}</span>
            </p>
          ) : null}
          {!error && start && locked && <p className="text-destructive">{lockMessage}</p>}
          {!error && start && !locked && long && (
            <Warning>
              The timer would have been running for over 12 hours.{confirmingLong && ' Save anyway to keep it.'}
            </Warning>
          )}
          {!error && previous && start && !locked && !long && overlap >= 60 && (
            <Warning>
              Overlaps {previous.description ? `“${previous.description}”` : 'the previous entry'}, which ended at {formatClock(previous.stop, timeZone, hour12)}.
            </Warning>
          )}
        </div>
      </div>

      <div className="flex flex-wrap gap-1.5" aria-label="Suggested start times" role="group">
        {previous && now - Date.parse(previous.stop) < 24 * 3600_000 && (
          <Button
            type="button"
            variant="secondary"
            size="xs"
            title={previous.description ? `When “${previous.description}” ended` : 'When the previous entry ended'}
            onClick={() => suggest(clockInputValue(previous.stop, timeZone, hour12))}
          >
            Last entry ended · {formatClock(previous.stop, timeZone, hour12)}
          </Button>
        )}
        {AGO_MINUTES.map((minutes) => (
          <Button key={minutes} type="button" variant="secondary" size="xs" onClick={() => suggest(`-${minutes}`)}>
            {minutes} min ago
          </Button>
        ))}
      </div>

      <p className="text-muted-foreground text-xs">
        Type <Kbd>930</Kbd>, <Kbd>9:30</Kbd> or <Kbd>-15</Kbd> for 15 min ago. <Kbd>↑</Kbd> <Kbd>↓</Kbd> change it by a minute,
        with <Kbd>⇧</Kbd> by 15.
      </p>
    </form>
  )
}

function Warning({ children }: { children: ReactNode }) {
  return (
    <p className="flex gap-1.5 text-orange-700 dark:text-orange-400">
      <TriangleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
      <span>{children}</span>
    </p>
  )
}

function Kbd({ children }: { children: ReactNode }) {
  return <kbd className="bg-muted text-foreground rounded px-1 font-sans text-[0.7rem]">{children}</kbd>
}
