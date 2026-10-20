import '@/index.css'
import { QueryClientProvider } from '@tanstack/react-query'
import { createRouter, RouterProvider } from '@tanstack/react-router'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { forgetOnSignOut } from '@/auth/forget-on-sign-out'
import { session } from '@/auth/session'
import { FullPageMessage } from '@/components/full-page-message'
import { Button } from '@/components/ui/button'
import { TooltipProvider } from '@/components/ui/tooltip'
import { queryClient } from '@/lib/query-client'
import { followSystemTheme } from '@/lib/theme'
import { routeTree } from './routeTree.gen'

const router = createRouter({
  routeTree,
  context: { queryClient },
  defaultPreload: 'intent',
  // Route data comes from TanStack Query, which decides what is fresh.
  defaultPreloadStaleTime: 0,
  scrollRestoration: true,
})

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}

followSystemTheme()
forgetOnSignOut(session, queryClient)
const root = createRoot(document.getElementById('root')!)

async function start() {
  try {
    // Signed in before this reload? The session cookie gets us a new access token.
    await session.restore()
  } catch {
    root.render(
      <FullPageMessage
        title="Can't reach Spindle"
        description="Check your internet connection. If it is working, the service may be down for a moment."
        action={<Button onClick={() => void start()}>Try again</Button>}
      />,
    )
    return
  }

  root.render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <TooltipProvider delayDuration={400}>
          <RouterProvider router={router} />
        </TooltipProvider>
      </QueryClientProvider>
    </StrictMode>,
  )
}

void start()
