import { createFileRoute } from '@tanstack/react-router'
import { ProfitabilityPage } from '@/features/agreements/profitability-page'

export const Route = createFileRoute('/_app/profitability')({
  component: ProfitabilityPage,
})
