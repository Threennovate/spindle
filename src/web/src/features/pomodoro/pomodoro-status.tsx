import { Coffee, Hourglass, X } from 'lucide-react'
import { useSyncExternalStore } from 'react'
import type { TimeEntry } from '@/api/types'
import { Button } from '@/components/ui/button'
import { useNow } from '@/lib/use-now'
import { useWorkspace } from '../workspace'
import { breakStore, focusLeftMs, formatCountdown } from './pomodoro'

/** In the timer bar: time left in the focus interval, or in the break after it. */
export function PomodoroStatus({ running }: { running: TimeEntry | null | undefined }) {
  const { me } = useWorkspace()
  const { pomodoroEnabled, pomodoroMinutes } = me.preferences
  const breakEnd = useSyncExternalStore(breakStore.subscribe, breakStore.get)
  const now = useNow(pomodoroEnabled && (!!running || !!breakEnd))

  if (!pomodoroEnabled) {
    return null
  }

  if (running) {
    const left = formatCountdown(focusLeftMs(now - Date.parse(running.start), pomodoroMinutes))
    return (
      <span className="text-muted-foreground hidden items-center gap-1 text-xs tabular-nums sm:flex" title="Time left in this focus interval" aria-label={`Focus interval: ${left} left`}>
        <Hourglass aria-hidden="true" className="size-3.5" />
        {left}
      </span>
    )
  }

  if (breakEnd) {
    const left = formatCountdown(breakEnd - now)
    return (
      <span className="bg-primary/10 text-primary-text flex items-center gap-1 rounded-full py-0.5 pr-0.5 pl-2 text-xs font-medium tabular-nums" aria-label={`Break: ${left} left`}>
        <Coffee aria-hidden="true" className="size-3.5" />
        Break {left}
        <Button variant="ghost" size="icon-xs" className="text-primary-text hover:text-primary-text size-5 rounded-full" aria-label="End the break" onClick={() => breakStore.end()}>
          <X className="size-3" />
        </Button>
      </span>
    )
  }

  return null
}
