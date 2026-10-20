import { createFileRoute } from '@tanstack/react-router'
import { ReviewCard } from '@/features/agent-activity/review-entry-points'
import { ProposalsBanner } from '@/features/agent-proposals/proposals-banner'
import { EntryList } from '@/features/time-entries/entry-list'
import { TimerBar } from '@/features/time-entries/timer-bar'
import { usePageTitle } from '@/lib/title'

export const Route = createFileRoute('/_app/entries')({
  component: EntriesPage,
})

function EntriesPage() {
  usePageTitle('Entries')
  return (
    <div className="mx-auto grid max-w-6xl gap-6">
      <h1 className="sr-only">Entries</h1>
      <TimerBar />
      <ProposalsBanner />
      <ReviewCard />
      <EntryList />
    </div>
  )
}
