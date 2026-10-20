import { useQuery } from '@tanstack/react-query'
import { cn } from 'cn'
import { Sparkles } from 'lucide-react'
import { useEffect, useState } from 'react'
import { meQuery } from '@/auth/me'
import { Button } from '@/components/ui/button'
import { addDaysToKey, dayKey, formatDayHeading } from '@/lib/time/zone'
import { useWorkspace } from '../workspace'
import { DayReview } from './day-review'
import { useKeptList } from './kept-list'
import { breakdown } from './review'
import { useReviewData } from './review-data'
import { OPEN_REVIEW_EVENT, openReview, type ReviewScope } from './review-events'

/** The day's review for requests from anywhere in the app; see `openReview`. */
export function DayReviewHost() {
  const [scope, setScope] = useState<ReviewScope | null>(null)

  useEffect(() => {
    const open = (event: Event) => setScope((event as CustomEvent<ReviewScope>).detail)
    window.addEventListener(OPEN_REVIEW_EVENT, open)
    return () => window.removeEventListener(OPEN_REVIEW_EVENT, open)
  }, [])

  return <DayReview scope={scope} onScopeChange={setScope} onClose={() => setScope(null)} />
}

/**
 * "✦ 3 to review": opens a day's review, beside the day in the entry list and the calendar. `compact` drops the words
 * where its container (`@container`) is narrow, as a calendar column of a week.
 */
export function ReviewDayButton({ day, count, compact = false, className }: { day: string; count: number; compact?: boolean; className?: string }) {
  const { timeZone } = useWorkspace()
  if (count === 0) {
    return null
  }
  return (
    <button
      type="button"
      onClick={() => openReview({ days: [day] })}
      aria-label={`${count} to review on ${formatDayHeading(day, timeZone, new Date())}`}
      title="Review this day's suggestions in one place"
      className={cn(
        'bg-primary/14 hover:bg-primary/24 focus-visible:ring-ring inline-flex shrink-0 items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium tabular-nums outline-none focus-visible:ring-2',
        className,
      )}
    >
      <Sparkles aria-hidden="true" className="text-primary-text size-3" />
      {count}
      {/* In a narrow calendar column, only the count. */}
      <span className={cn(compact && 'hidden @[6.5rem]:inline')}>to review</span>
    </button>
  )
}

/** How far back the card looks for the last day with something to review, e.g. Friday on a Monday. */
const LOOK_BACK = 3

/**
 * "Yesterday: 3 to review", above the entries: the last day with suggestions waiting, up to three days back, quietly
 * rather than in a dialog. Gone once they are dealt with, or put off for that day with "Not now", in this browser.
 */
export function ReviewCard() {
  const { timeZone } = useWorkspace()
  const userId = useQuery(meQuery).data?.id ?? 'anonymous'
  const today = dayKey(new Date(), timeZone)
  const days = Array.from({ length: LOOK_BACK }, (_, index) => addDaysToKey(today, index - LOOK_BACK))
  const data = useReviewData(days)
  const dismissed = useKeptList(`spindle.reviewCardDismissed.${userId}`, 1)
  const day = [...days].reverse().find((key) => data.items.some((item) => item.day === key))
  const items = data.items.filter((item) => item.day === day)

  if (!data.sync || !day || dismissed.has(day)) {
    return null
  }
  return (
    <section aria-label="Suggestions to review" className="bg-card flex flex-wrap items-center gap-x-3 gap-y-2 rounded-lg border px-3 py-2">
      <p className="flex min-w-0 flex-1 items-center gap-2 text-sm">
        <Sparkles aria-hidden="true" className="text-primary-text size-4 shrink-0" />
        <span className="min-w-0">
          <span className="font-semibold">
            {formatDayHeading(day, timeZone, new Date())}: {items.length} to review
          </span>
          <span className="text-muted-foreground"> · {breakdown(items)}</span>
        </span>
      </p>
      <div className="flex items-center gap-1">
        <Button type="button" variant="ghost" size="sm" className="text-muted-foreground" onClick={() => dismissed.add(day)} title="Hide this until the next day; the review stays in the day's header">
          Not now
        </Button>
        <Button type="button" size="sm" onClick={() => openReview({ days: [day] })}>
          Review…
        </Button>
      </div>
    </section>
  )
}
