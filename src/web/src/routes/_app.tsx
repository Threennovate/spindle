import { useSuspenseQuery } from '@tanstack/react-query'
import { createFileRoute, Outlet, redirect, useRouter } from '@tanstack/react-router'
import { Suspense, useEffect, useSyncExternalStore } from 'react'
import { meQuery } from '@/auth/me'
import { session } from '@/auth/session'
import { AppShell } from '@/components/app-shell'
import { FullPageMessage } from '@/components/full-page-message'
import { DeleteAccountButton, DownloadDataButton } from '@/features/account/account-actions'
import { settingsQuery } from '@/features/settings/queries'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { DayReviewHost } from '@/features/agent-activity/review-entry-points'
import { CommandPalette } from '@/features/command/command-palette'
import { KeyboardShortcuts } from '@/features/shortcuts/keyboard-shortcuts'
import { PomodoroWatcher } from '@/features/pomodoro/pomodoro-watcher'
import { LiveSync } from '@/features/sync/live-sync'
import { EntryEditor } from '@/features/time-entries/entry-editor'
import { RunningTitle } from '@/features/time-entries/running-timer'

/** Everything behind sign-in. */
export const Route = createFileRoute('/_app')({
  beforeLoad: ({ location }) => {
    if (!session.isSignedIn()) {
      throw redirect({ to: '/login', search: location.href === '/' ? {} : { redirect: location.href } })
    }
  },
  // The workspace's settings decide what the interface offers, so they load with the user.
  loader: async ({ context }) => {
    const me = await context.queryClient.ensureQueryData(meQuery)
    const workspaceId = me.defaultWorkspaceId ?? me.workspaces[0]?.id
    if (workspaceId) {
      await context.queryClient.ensureQueryData(settingsQuery(workspaceId))
    }
  },
  component: AppLayout,
})

function AppLayout() {
  const router = useRouter()
  const signedIn = useSyncExternalStore(session.subscribe, session.isSignedIn)
  const { data: me } = useSuspenseQuery(meQuery)

  // Signed out here, in another tab, or because the server ended the session (forgetOnSignOut clears the cache).
  useEffect(() => {
    if (!signedIn) {
      void router.navigate({ to: '/login', replace: true })
    }
  }, [signedIn, router])

  if (me.workspaces.length === 0) {
    return (
      <FullPageMessage
        title="You are not in a workspace"
        description="You were removed from your last workspace. Ask an admin to invite you again, or take your data and delete your account."
        action={
          <div className="flex flex-wrap justify-center gap-2">
            <Button variant="outline" onClick={() => void session.signOut()}>
              Sign out
            </Button>
            <DownloadDataButton />
            <DeleteAccountButton variant="ghost" />
          </div>
        }
      />
    )
  }

  // Keyed by workspace, so switching starts every page fresh instead of mixing two workspaces' data.
  // A workspace chosen on another device arrives without its settings; the page waits for them in place.
  return (
    <AppShell me={me}>
      <Suspense fallback={<PageLoading />}>
        <RunningTitle />
        <KeyboardShortcuts />
        <CommandPalette />
        <EntryEditor />
        <DayReviewHost />
        <PomodoroWatcher />
        <LiveSync />
        <Outlet key={me.defaultWorkspaceId} />
      </Suspense>
    </AppShell>
  )
}

function PageLoading() {
  return (
    <div className="mx-auto grid max-w-6xl gap-4" aria-busy="true" aria-label="Loading">
      <Skeleton className="h-8 w-56" />
      <Skeleton className="h-40 w-full" />
    </div>
  )
}
