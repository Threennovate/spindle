import { createFileRoute, Link, useRouter } from '@tanstack/react-router'
import { Loader2 } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { ApiError } from '@/api/problem'
import { finishExternalSignIn } from '@/auth/external'
import { CONNECTION_ERROR } from '@/auth/form-errors'
import { session } from '@/auth/session'
import { AuthLayout } from '@/components/auth-layout'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { safeRedirect } from '@/lib/redirect'
import { usePageTitle } from '@/lib/title'

const EXPIRED = 'This sign-in took too long or was already used. Please start again.'

/** Where the server sends the browser back from Google or Apple, with a one-time code or what went wrong. */
export const Route = createFileRoute('/external-sign-in')({
  validateSearch: (search: Record<string, unknown>): { code?: string; error?: string } => ({
    code: typeof search.code === 'string' ? search.code : undefined,
    error: typeof search.error === 'string' ? search.error : undefined,
  }),
  component: ExternalSignInPage,
})

function ExternalSignInPage() {
  usePageTitle('Signing in')
  const { code, error } = Route.useSearch()
  const router = useRouter()
  const [failure, setFailure] = useState<string | null>(error === 'expired' || (!code && !error) ? EXPIRED : null)
  // The code works once, so React's second run of the effect in development must not spend it again.
  const started = useRef(false)

  useEffect(() => {
    if (started.current) return
    started.current = true
    if (error === 'cancelled') {
      void router.navigate({ to: '/login', replace: true })
      return
    }
    if (!code) return

    finishExternalSignIn(code)
      .then(async (result) => {
        session.start(result)
        // A new account starts by naming its organization, unless it came to join one through an invitation.
        const next = result.newAccount && !result.returnTo ? '/welcome' : safeRedirect(result.returnTo ?? undefined)
        await router.navigate({ href: next, replace: true })
      })
      .catch((reason: unknown) => setFailure(reason instanceof ApiError ? (reason.problem?.title ?? EXPIRED) : CONNECTION_ERROR))
  }, [code, error, router])

  return (
    <AuthLayout
      title={failure ? "Couldn't sign you in" : 'Signing you in…'}
      description={failure ? 'Nothing was changed. You can try again.' : 'One moment.'}
      footer={
        <Link to="/login" className="text-foreground font-medium underline-offset-4 hover:underline">
          Back to sign in
        </Link>
      }
    >
      {failure ? (
        <Alert variant="destructive">
          <AlertDescription>{failure}</AlertDescription>
        </Alert>
      ) : (
        <div className="flex justify-center py-4" role="status" aria-label="Signing in">
          <Loader2 className="text-muted-foreground size-6 animate-spin" />
        </div>
      )}
    </AuthLayout>
  )
}
