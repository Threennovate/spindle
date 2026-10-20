import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { TimesheetPage } from '@/features/timesheet/timesheet-page'

export const Route = createFileRoute('/_app/timesheet')({
  validateSearch: (search: Record<string, unknown>): { date?: string } => ({
    date: typeof search.date === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(search.date) ? search.date : undefined,
  }),
  component: TimesheetRoute,
})

function TimesheetRoute() {
  const { date } = Route.useSearch()
  const navigate = useNavigate({ from: Route.fullPath })
  return <TimesheetPage date={date} onNavigate={(next) => void navigate({ search: { date: next } })} />
}
