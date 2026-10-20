import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { CalendarPage, type CalendarView } from '@/features/calendar/calendar-page'

interface CalendarSearch {
  date?: string
  view?: CalendarView
}

export const Route = createFileRoute('/_app/calendar')({
  validateSearch: (search: Record<string, unknown>): CalendarSearch => ({
    date: typeof search.date === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(search.date) ? search.date : undefined,
    view: search.view === 'day' ? 'day' : undefined,
  }),
  component: CalendarRoute,
})

function CalendarRoute() {
  const { date, view } = Route.useSearch()
  const navigate = useNavigate({ from: Route.fullPath })
  return (
    <CalendarPage
      date={date}
      view={view ?? 'week'}
      onNavigate={(next) => void navigate({ search: { date: next.date, view: next.view === 'day' ? 'day' : undefined } })}
    />
  )
}
