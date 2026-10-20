import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { Square } from 'lucide-react'
import { useEffect } from 'react'
import { Button } from '@/components/ui/button'
import { formatDuration } from '@/lib/time/duration'
import { setTitlePrefix } from '@/lib/title'
import { useNow } from '@/lib/use-now'
import { ProjectDot } from '../catalog/project-label'
import { SplitDots } from '../catalog/split-label'
import { useCatalog } from '../catalog/use-catalog'
import { useWorkspace } from '../workspace'
import { currentEntryQuery } from './queries'
import { isSplit, partsOf } from './split'
import { useStopTimer } from './use-stop-timer'

/** Puts the running timer in the tab title, e.g. "0:12:05 · Design review · Projects · Spindle". */
export function RunningTitle() {
  const { data: running } = useQuery(currentEntryQuery)
  const now = useNow(!!running)

  useEffect(() => {
    setTitlePrefix(running ? `${formatDuration((now - Date.parse(running.start)) / 1000)} · ${running.description || 'No description'}` : null)
  }, [running, now])

  useEffect(() => () => setTitlePrefix(null), [])

  return null
}

/** A compact running timer for the sidebar, so it can be seen and stopped from any page. */
export function SidebarTimer() {
  const { workspaceId } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  const { data: running } = useQuery(currentEntryQuery)
  const stop = useStopTimer(workspaceId)
  const now = useNow(!!running)

  if (!running) {
    return null
  }

  const project = catalog.project(running.projectId)

  return (
    <div className="bg-background mx-2 mb-2 flex items-center gap-2 rounded-lg border p-2">
      <Link to="/" className="min-w-0 flex-1" aria-label="Open the running timer">
        <p className="truncate text-sm font-medium">{running.description || 'No description'}</p>
        <p className="text-muted-foreground flex items-center gap-1.5 text-xs">
          {isSplit(running) ? (
            <SplitDots catalog={catalog} parts={partsOf(running)} className="[&>span]:size-2" />
          ) : (
            project && <ProjectDot color={project.color} className="size-2" />
          )}
          <span className="font-mono tabular-nums">{formatDuration((now - Date.parse(running.start)) / 1000)}</span>
        </p>
      </Link>
      <Button
        size="icon-sm"
        variant="destructive"
        className="rounded-full"
        aria-label="Stop timer"
        disabled={stop.isPending}
        onClick={() => stop.stopTimer(running)}
      >
        <Square className="size-3 fill-current" />
      </Button>
    </div>
  )
}
