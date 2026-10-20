import { createFileRoute } from '@tanstack/react-router'
import { ClientsPage } from '@/features/clients/clients-page'

export const Route = createFileRoute('/_app/clients')({
  component: ClientsPage,
})
