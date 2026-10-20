import { createFileRoute } from '@tanstack/react-router'
import { TimesheetPage } from '@/features/shared-reports/timesheet-page'

interface ShareSearch {
  /** How many periods back from the link's own; left out for its own period. */
  step?: number
}

export const Route = createFileRoute('/share/$token')({
  validateSearch: (raw: Record<string, unknown>): ShareSearch => {
    const step = Number(raw.step)
    return { step: Number.isInteger(step) && step < 0 ? step : undefined }
  },
  component: SharePage,
})

/** A timesheet link, opened by anyone who has it. */
function SharePage() {
  const { token } = Route.useParams()
  const { step = 0 } = Route.useSearch()
  const navigate = Route.useNavigate()
  return <TimesheetPage token={token} step={step} onStep={(next) => void navigate({ search: { step: next < 0 ? next : undefined }, replace: true })} />
}
