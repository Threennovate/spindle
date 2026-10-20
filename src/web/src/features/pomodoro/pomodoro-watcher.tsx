import { useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useSyncExternalStore } from 'react'
import { toast } from 'sonner'
import { useNow } from '@/lib/use-now'
import { currentEntryQuery } from '../time-entries/queries'
import { useStopTimer } from '../time-entries/use-stop-timer'
import { useWorkspace } from '../workspace'
import { breakStore, chime, claim, intervalsDone, notify } from './pomodoro'

/** Watches the running timer and rings at the end of each focus interval, and of each break. */
export function PomodoroWatcher() {
  const { me, workspaceId } = useWorkspace()
  const { pomodoroEnabled, pomodoroMinutes, pomodoroBreakMinutes, pomodoroAutoStop } = me.preferences
  const { data: running } = useQuery(currentEntryQuery)
  const { stopTimer } = useStopTimer(running?.workspaceId ?? workspaceId)
  const now = useNow(pomodoroEnabled && !!running)
  const breakEnd = useSyncExternalStore(breakStore.subscribe, breakStore.get)
  // The intervals already done when this tab first saw the timer: those passed while no one was looking.
  const seen = useRef<{ entryId: string; done: number } | null>(null)

  useEffect(() => {
    if (!pomodoroEnabled || !running) {
      return
    }
    const done = intervalsDone(now - Date.parse(running.start), pomodoroMinutes)
    if (seen.current?.entryId !== running.id) {
      seen.current = { entryId: running.id, done }
      // Starting to work again ends a break early.
      breakStore.end()
      return
    }
    if (done <= seen.current.done) {
      return
    }
    seen.current = { entryId: running.id, done }
    if (!claim(`focus:${running.id}:${done}`)) {
      return
    }

    chime()
    if (pomodoroAutoStop) {
      notify('Time for a break', `${pomodoroMinutes} minutes of focus done. The timer stopped.`)
      stopTimer(running, () => {
        breakStore.start(pomodoroBreakMinutes)
        toast.success(`${pomodoroMinutes} minutes of focus done. Take a ${pomodoroBreakMinutes}-minute break.`)
      })
    } else {
      notify('Time for a break', `${pomodoroMinutes} minutes of focus done.`)
      toast.info(`${pomodoroMinutes} minutes of focus done. Time for a break?`)
    }
  }, [now, running, pomodoroEnabled, pomodoroMinutes, pomodoroBreakMinutes, pomodoroAutoStop, stopTimer])

  useEffect(() => {
    if (!breakEnd) {
      return
    }
    const timeout = window.setTimeout(() => {
      if (claim(`break:${breakEnd}`)) {
        chime()
        notify('Break is over', 'Ready for the next focus interval?')
        toast.info('Break is over. Ready for the next focus interval?')
      }
      breakStore.end()
    }, breakEnd - Date.now())
    return () => window.clearTimeout(timeout)
  }, [breakEnd])

  return null
}
