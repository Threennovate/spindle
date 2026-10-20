import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { ReportsPage } from '@/features/reports/reports-page'
import { validateReportSearch } from '@/features/reports/search'

export const Route = createFileRoute('/_app/reports')({
  validateSearch: validateReportSearch,
  component: ReportsRoute,
})

function ReportsRoute() {
  const search = Route.useSearch()
  const navigate = useNavigate({ from: Route.fullPath })
  return <ReportsPage search={search} onNavigate={(update) => void navigate({ search: update })} />
}
