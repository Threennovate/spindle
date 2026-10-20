import { Link } from '@tanstack/react-router'
import { Hourglass, X } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { useReadOnly } from './read-only'
import { daysLeft, TRIAL_ENDING_DAYS } from './trial'

/**
 * Tells owners and admins that the trial of the current workspace's organization ends soon, in its last two weeks.
 * Put away, it comes back for the last three days.
 */
export function TrialBanner() {
  const { organization, canManage } = useReadOnly()
  const days = organization?.trialEndsAt && organization.access === 'full' ? daysLeft(organization.trialEndsAt) : null
  const key = organization && days !== null ? `spindle:trial-banner:${organization.id}:${organization.trialEndsAt}:${days <= 3 ? 'last' : 'soon'}` : null
  const [hidden, setHidden] = useState<string | null>(null)

  if (!organization || !canManage || days === null || days > TRIAL_ENDING_DAYS || !key || hidden === key || wasDismissed(key)) {
    return null
  }

  function dismiss(dismissedKey: string) {
    setHidden(dismissedKey)
    try {
      localStorage.setItem(dismissedKey, '1')
    } catch {
      // Private browsing: it comes back on the next visit.
    }
  }

  return (
    <div role="status" className="bg-primary/5 flex items-center gap-3 border-b px-4 py-2 text-sm md:px-6">
      <Hourglass aria-hidden="true" className="text-primary-text size-4 shrink-0" />
      <p className="min-w-0 flex-1">
        <span className="font-medium">
          The trial of {organization.name} ends {days <= 1 ? 'within a day' : `in ${days} days`}.
        </span>{' '}
        After that, time can be read and exported but not added, until you choose a plan.
      </p>
      <Button asChild variant="link" size="sm" className="h-auto p-0">
        <Link to="/plan">See the plan</Link>
      </Button>
      <Button variant="ghost" size="icon-sm" aria-label="Hide this reminder" onClick={() => dismiss(key)}>
        <X />
      </Button>
    </div>
  )
}

function wasDismissed(key: string): boolean {
  try {
    return localStorage.getItem(key) === '1'
  } catch {
    return false
  }
}
