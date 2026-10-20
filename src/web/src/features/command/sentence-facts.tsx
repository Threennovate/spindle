import { BadgeDollarSign } from 'lucide-react'
import type { ReactNode } from 'react'
import type { TimeEntry } from '@/api/types'
import { formatDuration, formatDurationAs } from '@/lib/time/duration'
import { formatClock } from '@/lib/time/zone'
import { useNow } from '@/lib/use-now'
import { ProjectDot } from '../catalog/project-label'
import { useCatalog } from '../catalog/use-catalog'
import { useWorkspace } from '../workspace'
import type { Sentence } from './sentence'

const quoted = (text: string) => `“${text}”`

/** What the line will save, at a glance: project, tags, billable and when. */
export function SentenceFacts({
  read,
  billable,
  start,
  stop,
  summary,
  stopping,
  note,
  tone,
}: {
  read: Sentence
  billable: boolean
  start: Date
  stop: Date | null
  summary: string
  stopping: TimeEntry | null
  note: string | null
  tone: 'blocked' | 'warning' | null
}) {
  const { workspaceId, timeZone, hour12 } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  const now = useNow()
  const project = catalog.project(read.projectId)
  const task = catalog.task(read.taskId)
  const seconds = ((stop?.getTime() ?? now) - start.getTime()) / 1000
  const parts: ReactNode[] = []

  if (project) {
    parts.push(
      <span key="project" className="inline-flex min-w-0 items-center gap-1.5">
        <ProjectDot color={project.color} />
        <span className="truncate">
          {project.name}
          {task && ` · ${task.name}`}
        </span>
      </span>,
    )
  }
  for (const id of read.tagIds) {
    const tag = catalog.tag(id)
    if (tag) parts.push(<span key={`tag-${id}`}>#{tag.name}</span>)
  }
  if (billable) {
    parts.push(
      <span key="billable" className="inline-flex items-center gap-1">
        <BadgeDollarSign className="size-3.5" aria-hidden="true" />
        Billable
      </span>,
    )
  }
  if (summary || stop) {
    parts.push(
      <span key="when" className="text-foreground/80 tabular-nums">
        {summary} ({formatDurationAs(seconds, 'hm')})
      </span>,
    )
  } else if (!note) {
    parts.push(<span key="when">now</span>)
  }
  if (stopping) {
    // The server stops the running timer where the new one starts, but not before its own start.
    const at = new Date(Math.max(Date.parse(stopping.start), Math.min(start.getTime(), now)))
    const emptied = start.getTime() <= Date.parse(stopping.start)
    parts.push(
      <span key="stopping" className={emptied ? 'text-orange-600 dark:text-orange-400' : undefined}>
        stops {quoted(stopping.description || 'No description')}
        {emptied ? ', which then has no time' : at.getTime() < now - 60_000 && ` at ${formatClock(at, timeZone, hour12)}`}
      </span>,
    )
  }
  if (note) {
    parts.push(
      <span key="note" className={tone === 'blocked' ? 'text-destructive' : 'text-orange-600 dark:text-orange-400'}>
        {note}
      </span>,
    )
  }

  return (
    <span className="flex min-w-0 flex-wrap items-center gap-x-1.5 gap-y-0.5">
      {parts.map((part, index) => (
        <span key={index} className="inline-flex min-w-0 items-center gap-1.5">
          {index > 0 && <span aria-hidden="true">·</span>}
          {part}
        </span>
      ))}
    </span>
  )
}

/** A running timer's duration now, and after its start changes. */
export function ShiftDetail({ from, to }: { from: string; to: Date }) {
  const now = useNow()
  return (
    <span className="font-mono tabular-nums">
      {formatDuration((now - Date.parse(from)) / 1000)} → {formatDuration((now - to.getTime()) / 1000)}
    </span>
  )
}
