import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { CircleCheck, PencilLine, Sparkles } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import type { TimeEntry } from '@/api/types'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { dayKey, formatDayHeading } from '@/lib/time/zone'
import { breakdown, type ReviewItem } from '../agent-activity/review'
import { openReview } from '../agent-activity/review-events'
import { proposedBy, waitingSummary } from '../agent-proposals/proposal'
import { agentProposalsQuery } from '../agent-proposals/queries'
import { ReviewDialog } from '../agent-proposals/review-dialog'
import type { Catalog } from '../catalog/use-catalog'
import { editEntry } from '../time-entries/events'
import { missingDetails } from '../time-entries/rules'
import { partsOf, isSplit } from '../time-entries/split'
import { usePermissions } from '../settings/queries'
import { useWorkspace } from '../workspace'

/** Entries that need details listed one by one; the rest are counted. */
const SHOWN_ENTRIES = 3

interface NeedsYouProps {
  /** The week's days up to today: what the review covers. */
  days: readonly string[]
  entries: readonly TimeEntry[] | undefined
  catalog: Catalog
  review: { sync: boolean; loaded: boolean; failed: boolean; items: ReviewItem[]; retry: () => void }
}

/**
 * What waits for the person in the week: proposals from their agents, the day review's split suggestions and untracked
 * agent time, and finished entries missing details the workspace requires. Each opens where it is dealt with; nothing
 * is saved from here.
 */
export function NeedsYouCard({ days, entries, catalog, review }: NeedsYouProps) {
  const { workspaceId, timeZone } = useWorkspace()
  const { settings, isLocked } = usePermissions()
  const proposals = useQuery(agentProposalsQuery(workspaceId))
  const [reviewing, setReviewing] = useState(false)
  const pending = proposals.data?.pending ?? []
  const now = new Date()

  const inWeek = new Set(days)
  const incomplete = (entries ?? []).filter((entry) => {
    if (!entry.stop || isLocked(entry.start) || !inWeek.has(dayKey(entry.start, timeZone))) {
      return false
    }
    const shares = isSplit(entry) ? partsOf(entry) : undefined
    return Object.keys(missingDetails(settings, { ...entry, shares }, catalog.project(entry.projectId))).length > 0
  })

  // Days with review items, the latest first.
  const reviewDays = [...new Set(review.items.map((item) => item.day))].sort().reverse()
  const loading = !entries || proposals.isPending || (review.sync && !review.loaded && !review.failed)
  const count = pending.length + review.items.length + incomplete.length

  return (
    <section aria-labelledby="needs-you-title" className="bg-card ring-foreground/10 flex w-full shrink-0 flex-col gap-1 rounded-xl p-4 ring-1 lg:w-88">
      <div className="flex items-center justify-between gap-2 pb-2">
        <h3 id="needs-you-title" className="text-base font-medium">
          Needs you
        </h3>
        {!loading && count > 0 && <span className="bg-primary/14 rounded-full px-2 py-0.5 text-xs font-medium tabular-nums">{count}</span>}
        {!loading && reviewDays.length > 1 && (
          <Button type="button" variant="ghost" size="sm" className="text-muted-foreground -my-1 ml-auto" onClick={() => openReview({ days: [...days] })}>
            Review the week
          </Button>
        )}
      </div>

      {loading ? (
        <div className="grid gap-3" aria-busy="true" aria-label="Loading what needs you">
          <Skeleton className="h-10" />
          <Skeleton className="h-10" />
        </div>
      ) : count === 0 ? (
        <Empty sync={review.sync} failed={review.failed} onRetry={review.retry} />
      ) : (
        <ul className="divide-y border-t">
          {pending.length > 0 && (
            <Row
              icon={<Sparkles className="text-primary-text size-4" />}
              title={`${proposedBy(pending)} proposed ${waitingSummary(pending)}`}
              detail="Nothing is saved until you add it."
              action={
                <Button type="button" size="sm" onClick={() => setReviewing(true)}>
                  Review…
                </Button>
              }
            />
          )}
          {reviewDays.map((day) => {
            const items = review.items.filter((item) => item.day === day)
            return (
              <Row
                key={day}
                icon={<Sparkles className="text-primary-text size-4" />}
                title={`${formatDayHeading(day, timeZone, now)}: ${items.length} to review`}
                detail={breakdown(items)}
                action={
                  <Button type="button" size="sm" variant="outline" onClick={() => openReview({ days: [day] })}>
                    Review…
                  </Button>
                }
              />
            )
          })}
          {incomplete.slice(0, SHOWN_ENTRIES).map((entry) => (
            <Row
              key={entry.id}
              icon={<PencilLine className="text-muted-foreground size-4" />}
              title={entry.description || 'No description'}
              detail={`${formatDayHeading(dayKey(entry.start, timeZone), timeZone, now)} · ${needs(settings, entry, catalog)}`}
              action={
                <Button type="button" size="sm" variant="outline" onClick={() => editEntry({ entry })}>
                  Open
                </Button>
              }
            />
          ))}
          {incomplete.length > SHOWN_ENTRIES && (
            <li className="pt-2 text-xs">
              <Link to="/entries" className="text-primary-text font-medium underline-offset-4 hover:underline">
                {incomplete.length - SHOWN_ENTRIES} more {incomplete.length - SHOWN_ENTRIES === 1 ? 'entry needs' : 'entries need'} details
              </Link>
            </li>
          )}
        </ul>
      )}
      {review.failed && count > 0 && (
        <p className="text-muted-foreground pt-2 text-xs">
          Couldn’t load the suggestions.{' '}
          <button type="button" onClick={review.retry} className="text-primary-text font-medium underline-offset-4 hover:underline">
            Try again
          </button>
        </p>
      )}
      <ReviewDialog open={reviewing} pending={pending} onClose={() => setReviewing(false)} />
    </section>
  )
}

function Row({ icon, title, detail, action }: { icon: ReactNode; title: string; detail: string; action: ReactNode }) {
  return (
    <li className="flex items-start gap-2.5 py-2.5 last:pb-0">
      <span aria-hidden="true" className="mt-0.5 shrink-0">
        {icon}
      </span>
      <span className="min-w-0 flex-1">
        <span className="line-clamp-2 text-sm font-medium">{title}</span>
        <span className="text-muted-foreground block text-xs">{detail}</span>
      </span>
      {action}
    </li>
  )
}

function Empty({ sync, failed, onRetry }: { sync: boolean; failed: boolean; onRetry: () => void }) {
  if (failed) {
    return (
      <p className="text-muted-foreground text-sm">
        Couldn’t load the suggestions.{' '}
        <button type="button" onClick={onRetry} className="text-primary-text font-medium underline-offset-4 hover:underline">
          Try again
        </button>
      </p>
    )
  }
  return (
    <div className="flex flex-1 flex-col items-center justify-center gap-2 py-6 text-center">
      <span className="bg-primary/14 text-primary-text grid size-9 place-items-center rounded-full">
        <CircleCheck aria-hidden="true" className="size-5" />
      </span>
      <p className="text-sm font-medium">Nothing waits for you</p>
      <p className="text-muted-foreground max-w-64 text-xs">
        {sync ? (
          'Split suggestions, agent time with no entry and entries missing details show up here.'
        ) : (
          <>
            Entries missing details show up here. With{' '}
            <Link to="/account" className="text-primary-text font-medium underline-offset-4 hover:underline">
              agent activity sync
            </Link>{' '}
            on, so do split suggestions and agent time with no entry.
          </>
        )}
      </p>
    </div>
  )
}

/** "Needs a description", or the first of what the entry lacks. */
function needs(settings: ReturnType<typeof usePermissions>['settings'], entry: TimeEntry, catalog: Catalog): string {
  const shares = isSplit(entry) ? partsOf(entry) : undefined
  const missing = Object.keys(missingDetails(settings, { ...entry, shares }, catalog.project(entry.projectId)))
  const words: Record<string, string> = { description: 'a description', projectId: 'a project', taskId: 'a task', tagIds: 'a tag' }
  return `Needs ${missing.map((field) => words[field]).join(' and ')}`
}
