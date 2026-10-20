import { useQuery } from '@tanstack/react-query'
import { createFileRoute, Link, redirect, useRouter } from '@tanstack/react-router'
import { useState, type FormEvent } from 'react'
import { toApiError } from '@/api/problem'
import { publicApi } from '@/api/public'
import { describeBrowser } from '@/auth/device'
import { CONNECTION_ERROR, EMAIL_PATTERN, hasErrors, toFormErrors, type FormErrors } from '@/auth/form-errors'
import { session } from '@/auth/session'
import { authThreads } from '@/auth/threads'
import { passkeyError, signInWithPasskey, supportsPasskeys } from '@/auth/passkeys'
import { AuthLayout } from '@/components/auth-layout'
import { ExternalSignIn } from '@/components/external-sign-in'
import { serverQuery } from '@/features/subscriptions/queries'
import { TextField } from '@/components/text-field'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { safeRedirect } from '@/lib/redirect'
import { usePageTitle } from '@/lib/title'

const FIELDS = ['email', 'password'] as const
type Field = (typeof FIELDS)[number]

export const Route = createFileRoute('/login')({
  validateSearch: (search: Record<string, unknown>): { redirect?: string; notice?: 'password-reset' } => ({
    redirect: typeof search.redirect === 'string' ? search.redirect : undefined,
    notice: search.notice === 'password-reset' ? 'password-reset' : undefined,
  }),
  beforeLoad: ({ search }) => {
    if (session.isSignedIn()) {
      throw redirect({ href: safeRedirect(search.redirect) })
    }
  },
  component: LoginPage,
})

function LoginPage() {
  usePageTitle('Sign in')
  const search = Route.useSearch()
  const router = useRouter()
  const [errors, setErrors] = useState<FormErrors<Field>>({})
  const [pending, setPending] = useState(false)
  const [passkeyPending, setPasskeyPending] = useState(false)
  const registrationOpen = useQuery(serverQuery).data?.registrationOpen !== false

  async function handlePasskey() {
    setErrors({})
    setPasskeyPending(true)
    authThreads.begin('wind')
    try {
      const signedIn = await signInWithPasskey()
      session.start(signedIn)
      await authThreads.complete()
      await router.navigate({ href: safeRedirect(search.redirect), replace: true })
    } catch (error) {
      authThreads.fail()
      setErrors({ form: passkeyError(error) })
    } finally {
      setPasskeyPending(false)
    }
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    const email = String(form.get('email') ?? '').trim()
    const password = String(form.get('password') ?? '')

    const validation: FormErrors<Field> = {
      email: !email ? 'Enter your email address.' : !EMAIL_PATTERN.test(email) ? 'Enter a valid email address.' : undefined,
      password: !password ? 'Enter your password.' : undefined,
    }
    setErrors(validation)
    if (hasErrors(validation)) {
      return
    }

    setPending(true)
    authThreads.begin('wind')
    try {
      const { data, error, response } = await publicApi.POST('/api/auth/web/login', {
        body: { email, password, deviceName: describeBrowser(navigator.userAgent) },
      })
      if (data) {
        session.start(data)
        await authThreads.complete()
        await router.navigate({ href: safeRedirect(search.redirect), replace: true })
        return
      }
      // A refused password snaps its thread back to dotted until it is changed.
      authThreads.fail(response.status === 401 ? 'password' : undefined)
      setErrors(toFormErrors(toApiError(response, error), FIELDS))
    } catch {
      authThreads.fail()
      setErrors({ form: CONNECTION_ERROR })
    } finally {
      setPending(false)
    }
  }

  return (
    <AuthLayout
      title="Sign in"
      description="Welcome back. Sign in to continue tracking your time."
      footer={
        // A server that takes people only by invitation doesn't offer to create an account.
        registrationOpen && (
          <>
            New to Spindle?{' '}
            <Link to="/register" search={{ redirect: search.redirect }} className="text-foreground font-medium underline-offset-4 hover:underline">
              Create an account
            </Link>
          </>
        )
      }
    >
      <form noValidate onSubmit={handleSubmit} className="grid gap-4">
        <ExternalSignIn returnTo={search.redirect} thread="social" />
        {search.notice === 'password-reset' && !errors.form && (
          <Alert>
            <AlertDescription>Your password was changed. Sign in with the new one.</AlertDescription>
          </Alert>
        )}
        {errors.form && (
          <Alert variant="destructive">
            <AlertDescription>{errors.form}</AlertDescription>
          </Alert>
        )}
        <TextField
          id="email"
          name="email"
          type="email"
          label="Email"
          autoComplete="username"
          autoFocus
          thread="email"
          error={errors.email}
        />
        <TextField
          id="password"
          name="password"
          type="password"
          label="Password"
          autoComplete="current-password"
          thread="password"
          error={errors.password}
        />
        <Link to="/forgot-password" className="text-muted-foreground -mt-2 justify-self-end text-sm underline-offset-4 hover:underline">
          Forgot your password?
        </Link>
        <Button type="submit" size="lg" className="mt-2 w-full" disabled={pending || passkeyPending} data-thread-go>
          {pending ? 'Signing in…' : 'Sign in'}
        </Button>
        {supportsPasskeys() && (
          <Button type="button" variant="outline" size="lg" className="w-full" disabled={pending || passkeyPending} onClick={() => void handlePasskey()}>
            {passkeyPending ? 'Waiting for your passkey…' : 'Sign in with a passkey'}
          </Button>
        )}
      </form>
    </AuthLayout>
  )
}
