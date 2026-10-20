import { useQuery } from '@tanstack/react-query'
import { Play } from 'lucide-react'
import { toast } from 'sonner'
import type { TimeEntry } from '@/api/types'
import { errorMessage } from '@/lib/error-message'
import { ProjectDot } from '../catalog/project-label'
import { SplitDots } from '../catalog/split-label'
import type { Catalog } from '../catalog/use-catalog'
import { useReadOnly } from '../subscriptions/read-only'
import { currentEntryQuery, useCreateEntry } from '../time-entries/queries'
import { isSplit, partsOf } from '../time-entries/split'
import { useWorkspace } from '../workspace'
import { recentEntries } from './week'

/** How many recent entries are offered. */
const SHOWN = 4

/**
 * "Continue ▸ Review the checkout flow": the latest entries, to start the timer on one again in a click, as the entry
 * list's ▸ does. Only while no timer runs, so a click never stops one by surprise.
 */
export function ContinueRow({ entries, catalog }: { entries: readonly TimeEntry[] | undefined; catalog: Catalog }) {
  const { workspaceId } = useWorkspace()
  const { data: running, isPending } = useQuery(currentEntryQuery)
  const create = useCreateEntry(workspaceId)
  const { readOnly } = useReadOnly()
  const recent = recentEntries(entries ?? [], SHOWN)

  if (isPending || running || readOnly || recent.length === 0) {
    return null
  }

  async function continueEntry(entry: TimeEntry) {
    try {
      await create.mutateAsync({
        description: entry.description,
        projectId: entry.projectId,
        taskId: entry.taskId,
        tagIds: entry.tagIds,
        isBillable: entry.isBillable,
        shares: entry.shares,
        start: new Date().toISOString(),
      })
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't start the timer."))
    }
  }

  return (
    <div className="flex flex-wrap items-center gap-2 px-1">
      <span className="text-muted-foreground text-xs">Continue</span>
      {recent.map((entry) => {
        const label = entry.description || 'No description'
        const project = catalog.project(entry.projectId)
        return (
          <button
            key={entry.id}
            type="button"
            onClick={() => void continueEntry(entry)}
            disabled={create.isPending}
            aria-label={`Continue “${label}”`}
            className="bg-card hover:bg-muted focus-visible:ring-ring/50 focus-visible:border-ring group flex h-8 max-w-64 items-center gap-2 rounded-full border px-3 text-sm transition-colors outline-none focus-visible:ring-3 disabled:opacity-50"
          >
            {isSplit(entry) ? (
              <SplitDots catalog={catalog} parts={partsOf(entry)} />
            ) : (
              <ProjectDot color={project?.color ?? 'var(--muted-foreground)'} />
            )}
            <span className={entry.description ? 'truncate' : 'text-muted-foreground truncate'}>{label}</span>
            <Play aria-hidden="true" className="text-muted-foreground group-hover:text-foreground size-3 shrink-0 fill-current" />
          </button>
        )
      })}
    </div>
  )
}
