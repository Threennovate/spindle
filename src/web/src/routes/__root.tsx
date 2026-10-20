import type { QueryClient } from '@tanstack/react-query'
import { createRootRouteWithContext, Link, Outlet } from '@tanstack/react-router'
import { FullPageMessage } from '@/components/full-page-message'
import { Button } from '@/components/ui/button'
import { Toaster } from '@/components/ui/sonner'

export interface RouterContext {
  queryClient: QueryClient
}

export const Route = createRootRouteWithContext<RouterContext>()({
  component: () => (
    <>
      <Outlet />
      <Toaster position="bottom-right" />
    </>
  ),
  notFoundComponent: () => (
    <FullPageMessage
      title="Page not found"
      description="The page you are looking for does not exist or has moved."
      action={
        <Button asChild>
          <Link to="/">Go to Spindle</Link>
        </Button>
      }
    />
  ),
  errorComponent: ({ reset }) => (
    <FullPageMessage
      title="Something went wrong"
      description="An unexpected error occurred. Try again, and if it keeps happening, reload the page."
      action={<Button onClick={reset}>Try again</Button>}
    />
  ),
})
