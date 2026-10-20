import { createFileRoute } from '@tanstack/react-router'
import { TagsPage } from '@/features/tags/tags-page'

export const Route = createFileRoute('/_app/tags')({
  component: TagsPage,
})
