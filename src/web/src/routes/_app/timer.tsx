import { createFileRoute, redirect } from '@tanstack/react-router'

/** The timer moved to Home and its entry list to Entries; old links and bookmarks land on the list. */
export const Route = createFileRoute('/_app/timer')({
  beforeLoad: () => {
    throw redirect({ to: '/entries', replace: true })
  },
})
