import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { useState } from 'react'
import { PeriodNavigator } from '@/components/period-navigator'
import { usePageTitle } from '@/lib/title'
import { addDaysToKey, dayKey } from '@/lib/time/zone'
import { useNow } from '@/lib/use-now'
import { useReviewData } from '../agent-activity/review-data'
import { openReview } from '../agent-activity/review-events'
import { formatRange, rangeEntriesQuery, weekDays } from '../calendar/range'
import { useCatalog } from '../catalog/use-catalog'
import { FirstStepsCard } from '../onboarding/first-steps-card'
import { currentEntryQuery } from '../time-entries/queries'
import { TimerBar } from '../time-entries/timer-bar'
import { useWorkspace } from '../workspace'
import { AgreementsSummary } from './agreements-summary'
import { ContinueRow } from './continue-row'
import { NeedsYouCard } from './needs-you-card'
import { ProjectsCard } from './projects-card'
import { weekBefore, weekMoment, weekSummary } from './week'
import { WeekChart } from './week-chart'
import { WeekFigures } from './week-figures'

const WEEK = 7 * 24 * 3600 * 1000

/**
 * Home: the timer at the top, then the week (this one, or one before it): its numbers, its days by project, what waits
 * for the person, its projects and, for admins, the agreements running now.
 */
export function HomePage() {
  usePageTitle('Home')
  const { workspaceId, timeZone, weekStartsOn } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  const today = dayKey(new Date(), timeZone)
  const [shown, setShown] = useState(today)
  const days = weekDays(shown, timeZone, weekStartsOn)
  const isThisWeek = days.includes(today)
  const lastWeekDays = weekDays(addDaysToKey(days[0], -7), timeZone, weekStartsOn)

  const entries = useQuery(rangeEntriesQuery(workspaceId, days, timeZone))
  // A week that has begun is compared with the one before it.
  const lastWeek = useQuery({ ...rangeEntriesQuery(workspaceId, lastWeekDays, timeZone), enabled: days[0] <= today })
  const { data: running } = useQuery(currentEntryQuery)
  // The week follows a running timer; without one, it is as it was when it loaded.
  const tick = useNow(!!running)
  const now = running ? tick : entries.dataUpdatedAt || tick

  // The review covers the days that have been: none of a week to come.
  const reviewDays = days.filter((day) => day <= today)
  const reviewData = useReviewData(reviewDays.length > 0 ? reviewDays : days, reviewDays.length > 0)
  const review = { ...reviewData, loaded: reviewData.loaded || reviewDays.length === 0, retry: () => void reviewData.retry() }
  const untracked = review.items.filter((item) => item.kind === 'untracked')

  const summary = entries.data ? weekSummary(entries.data, days, timeZone, now) : null
  const before =
    days[0] <= today && lastWeek.data
      ? {
          // This week against last week by this time; a week gone by against the whole week before it.
          summary: isThisWeek ? weekBefore(lastWeek.data, lastWeekDays, timeZone, now - WEEK) : weekSummary(lastWeek.data, lastWeekDays, timeZone, now),
          dayTotals: weekSummary(lastWeek.data, lastWeekDays, timeZone, now).dayTotals,
          label: isThisWeek ? 'by this time last week' : 'the week before',
        }
      : null

  return (
    <div className="mx-auto grid max-w-6xl gap-6">
      <h1 className="sr-only">Home</h1>
      <FirstStepsCard />
      <div className="grid gap-3">
        <TimerBar />
        {/* The latest work, from this week and the one before, so a Monday has something to pick up too. */}
        <ContinueRow entries={isThisWeek ? [...(entries.data ?? []), ...(lastWeek.data ?? [])] : undefined} catalog={catalog} />
      </div>

      <section aria-labelledby="week-title" className="grid gap-4">
        <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2">
          <h2 id="week-title" className="text-xl font-semibold tracking-tight">
            {weekTitle(days, today)}
          </h2>
          <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
            <PeriodNavigator
              label={formatRange(days)}
              unit="week"
              onPrevious={() => setShown(addDaysToKey(days[0], -7))}
              onNext={() => setShown(addDaysToKey(days[0], 7))}
              onToday={() => setShown(today)}
            />
            <Link
              to="/reports"
              search={{ period: 'custom', from: days[0], to: days[6] }}
              className="text-primary-text text-sm font-medium underline-offset-4 hover:underline"
            >
              Open the report
            </Link>
          </div>
        </div>

        <WeekFigures
          summary={summary}
          before={before}
          today={today}
          moment={weekMoment(days, timeZone, now)}
          catalog={catalog}
          untracked={
            review.sync && reviewDays.length > 0
              ? {
                  seconds: untracked.reduce((sum, item) => sum + stretchSeconds(item), 0),
                  stretches: untracked.length,
                  days: days.map((day) => untracked.filter((item) => item.day === day).reduce((sum, item) => sum + stretchSeconds(item), 0)),
                }
              : null
          }
          onReview={() => openReview({ days: [...new Set(untracked.map((item) => item.day))] })}
        />

        <div className="flex flex-col gap-4 lg:flex-row lg:items-stretch">
          <WeekChart summary={summary} catalog={catalog} today={today} failed={entries.isError} onRetry={() => void entries.refetch()} />
          <NeedsYouCard days={reviewDays} entries={entries.data} catalog={catalog} review={review} />
        </div>

        <div className="flex flex-col gap-4 lg:flex-row lg:items-start">
          <ProjectsCard summary={summary} catalog={catalog} reportSearch={{ from: days[0], to: days[6] }} />
          <AgreementsSummary />
        </div>
      </section>
    </div>
  )
}

function stretchSeconds(item: { start: string; end: string }): number {
  return (Date.parse(item.end) - Date.parse(item.start)) / 1000
}

/** "This week", "Last week", "Next week", or "Week" for any other, beside the navigator's dates. */
function weekTitle(days: readonly string[], today: string): string {
  if (days.includes(today)) {
    return 'This week'
  }
  if (days.includes(addDaysToKey(today, -7))) {
    return 'Last week'
  }
  return days.includes(addDaysToKey(today, 7)) ? 'Next week' : 'Week'
}
