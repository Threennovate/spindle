import { createFileRoute } from '@tanstack/react-router'
import { OperatorPage } from '@/features/operator/operator-page'

export const Route = createFileRoute('/_app/operator')({
  component: OperatorPage,
})
