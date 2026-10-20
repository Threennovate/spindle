import { createFileRoute } from '@tanstack/react-router'
import { HomePage } from '@/features/home/home-page'

/** Home: the timer and the week at a glance. */
export const Route = createFileRoute('/_app/')({
  component: HomePage,
})
