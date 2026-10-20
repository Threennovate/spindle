import { Link } from '@tanstack/react-router'
import { Lock } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useReadOnly } from './read-only'

/** Says why nothing can be changed, for as long as it lasts. It can't be put away: every change would fail. */
export function ReadOnlyBanner() {
  const { readOnly, organization, canManage } = useReadOnly()
  if (!readOnly || !organization) {
    return null
  }

  return (
    <div role="status" className="bg-destructive/5 flex items-center gap-3 border-b px-4 py-2 text-sm md:px-6">
      <Lock aria-hidden="true" className="text-destructive size-4 shrink-0" />
      <p className="min-w-0 flex-1">
        <span className="font-medium">{organization.name} is read-only.</span>{' '}
        {canManage
          ? 'Its trial or plan has ended. Everything is still here, and you can read, report and export it, but not add or change time.'
          : 'Its trial or plan has ended, so time can’t be added or changed. Everything is still here. Its owner can bring it back.'}
      </p>
      {canManage && (
        <Button asChild variant="link" size="sm" className="h-auto p-0">
          <Link to="/plan">Choose a plan</Link>
        </Button>
      )}
    </div>
  )
}
