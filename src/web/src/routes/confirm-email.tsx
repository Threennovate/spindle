import { useQuery, useQueryClient } from '@tanstack/react-query'
import { createFileRoute, Link } from '@tanstack/react-router'
import { CircleCheck, CircleX } from 'lucide-react'
import { useEffect, useSyncExternalStore } from 'react'
import { toApiError } from '@/api/problem'
import { publicApi } from '@/api/public'
import { meQuery } from '@/auth/me'
import { session } from '@/auth/session'
import { AuthLayout } from '@/components/auth-layout'
import { Button } from '@/components/ui/button'
import { errorMessage } from '@/lib/error-message'
import { usePageTitle } from '@/lib/title'

export const Route = createFileRoute('/confirm-email')({
  validateSearch: (search: Record<string, unknown>): { user?: string; token?: string } => ({
    user: typeof search.user === 'string' ? search.user : undefined,
    token: typeof search.token === 'string' ? search.token : undefined,
  }),
  component: ConfirmEmailPage,
})

function ConfirmEmailPage() {
  usePageTitle('Confirm email')
  const { user, token } = Route.useSearch()
  const queryClient = useQueryClient()
  const signedIn = useSyncExternalStore(session.subscribe, session.isSignedIn)

  // Confirming twice does no harm, so a query can do it: once per link, however often the page renders.
  const confirmation = useQuery({
    queryKey: ['confirm-email', user, token],
    queryFn: async () => {
      const { error, response } = await publicApi.POST('/api/auth/confirm-email', { body: { userId: user!, token: token! } })
      if (!response.ok) {
        throw toApiError(response, error)
      }
      return true
    },
    enabled: !!user && !!token,
    retry: false,
    staleTime: Infinity,
  })

  useEffect(() => {
    if (confirmation.isSuccess && signedIn) {
      void queryClient.invalidateQueries({ queryKey: meQuery.queryKey })
    }
  }, [confirmation.isSuccess, signedIn, queryClient])

  const next = signedIn ? (
    <Button asChild className="w-full">
      <Link to="/">Continue to Spindle</Link>
    </Button>
  ) : (
    <Button asChild className="w-full">
      <Link to="/login">Sign in</Link>
    </Button>
  )

  if (confirmation.isSuccess) {
    return (
      <AuthLayout title="Your email is confirmed" description="Thanks. You can now reset your password by email if you ever need to." footer="Spindle">
        <div className="grid justify-items-center gap-4">
          <CircleCheck aria-hidden="true" className="size-10 text-green-600" />
          {next}
        </div>
      </AuthLayout>
    )
  }

  if (confirmation.isError || !user || !token) {
    return (
      <AuthLayout
        title="This link did not work"
        description={!user || !token ? 'The link is incomplete. Open it from the email again.' : errorMessage(confirmation.error, 'The link could not be checked. Try again.')}
        footer={signedIn ? 'You can send a new link from your account settings.' : 'Sign in to send a new link.'}
      >
        <div className="grid justify-items-center gap-4">
          <CircleX aria-hidden="true" className="text-destructive size-10" />
          {signedIn ? (
            <Button asChild className="w-full">
              <Link to="/account">Go to account settings</Link>
            </Button>
          ) : (
            next
          )}
        </div>
      </AuthLayout>
    )
  }

  return (
    <AuthLayout title="Confirming your email…" description="This takes a moment." footer="Spindle">
      <div className="bg-muted h-2 overflow-hidden rounded-full" aria-busy="true" aria-label="Confirming">
        <div className="bg-primary h-full w-1/3 animate-pulse rounded-full" />
      </div>
    </AuthLayout>
  )
}
