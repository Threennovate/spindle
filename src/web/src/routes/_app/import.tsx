import { createFileRoute } from '@tanstack/react-router'
import { ImportPage } from '@/features/import/import-page'

export const Route = createFileRoute('/_app/import')({
  component: ImportPage,
})
