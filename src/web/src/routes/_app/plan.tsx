import { createFileRoute } from '@tanstack/react-router'
import { PlanPage } from '@/features/subscriptions/plan-page'

export const Route = createFileRoute('/_app/plan')({
  component: PlanPage,
})
