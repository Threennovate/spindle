import { useQuery } from '@tanstack/react-query'
import { createFileRoute, Link, redirect, useRouter } from '@tanstack/react-router'
import { useState, type FormEvent } from 'react'
import { toApiError } from '@/api/problem'
import { publicApi } from '@/api/public'
import { describeBrowser } from '@/auth/device'
import { CONNECTION_ERROR, EMAIL_PATTERN, hasErrors, toFormErrors, type FormErrors } from '@/auth/form-errors'
import { session } from '@/auth/session'
import { authThreads } from '@/auth/threads'
import { AuthLayout } from '@/components/auth-layout'
import { ExternalSignIn } from '@/components/external-sign-in'
import { TextField } from '@/components/text-field'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Label } from '@/components/ui/label'
import { serverQuery } from '@/features/subscriptions/queries'
import { invitationPreviewQuery } from '@/features/team/queries'
import { safeRedirect } from '@/lib/redirect'
import { usePageTitle } from '@/lib/title'

const FIELDS = ['fullName', 'email', 'password', 'timeZone', 'invitationToken', 'acceptTerms'] as const
type Field = (typeof FIELDS)[number]

const PASSWORD_MIN_LENGTH = 8

export const Route = createFileRoute('/register')({
  validateSearch: (search: Record<string, unknown>): { redirect?: string; invitation?: string } => ({
    redirect: typeof search.redirect === 'string' ? search.redirect : undefined,
    invitation: typeof search.invitation === 'string' ? search.invitation : undefined,
  }),
  beforeLoad: ({ search }) => {
    if (session.isSignedIn()) {
      throw redirect({ href: safeRedirect(search.redirect) })
    }
  },
  component: RegisterPage,
})

function RegisterPage() {
  usePageTitle('Create account')
  const search = Route.useSearch()
  const router = useRouter()
  const [errors, setErrors] = useState<FormErrors<Field>>({})
  const [pending, setPending] = useState(false)
  // Registering from an invitation joins that workspace, with the invited address.
  const invitation = useQuery({ ...invitationPreviewQuery(search.invitation ?? ''), enabled: !!search.invitation })
  const invited = invitation.data?.status === 'pending' ? invitation.data : null
  const { data: server } = useQuery(serverQuery)
  const [acceptTerms, setAcceptTerms] = useState(false)
  // A server that takes new people only by invitation has nothing to offer someone who came without one.
  const closed = server?.registrationOpen === false && !search.invitation

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    const fullName = String(form.get('fullName') ?? '').trim()
    const email = invited?.email ?? String(form.get('email') ?? '').trim()
    const password = String(form.get('password') ?? '')

    const validation: FormErrors<Field> = {
      fullName: !fullName ? 'Enter your name.' : undefined,
      email: !email ? 'Enter your email address.' : !EMAIL_PATTERN.test(email) ? 'Enter a valid email address.' : undefined,
      password:
        password.length < PASSWORD_MIN_LENGTH ? `Use at least ${PASSWORD_MIN_LENGTH} characters.` : undefined,
      acceptTerms: server?.termsUrl && !acceptTerms ? 'Agree to the terms of service and privacy policy to create an account.' : undefined,
    }
    setErrors(validation)
    if (hasErrors(validation)) {
      return
    }

    setPending(true)
    authThreads.begin('wind')
    try {
      const { data, error, response } = await publicApi.POST('/api/auth/web/register', {
        body: {
          fullName,
          email,
          password,
          timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
          deviceName: describeBrowser(navigator.userAgent),
          invitationToken: invited ? search.invitation : undefined,
          acceptTerms,
        },
      })
      if (data) {
        session.start(data)
        await authThreads.complete()
        // A new organization starts with its name; joining one goes where the invitation leads.
        await router.navigate({ href: invited || search.redirect ? safeRedirect(search.redirect) : '/welcome', replace: true })
        return
      }
      authThreads.fail()
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
      title={invited ? `Join ${invited.workspaceName}` : closed ? 'Join by invitation' : 'Create your account'}
      description={
        invited
          ? `${invited.invitedBy} invited you. Create your account to start tracking time with the team.`
          : closed
            ? 'New accounts on this server come from invitations.'
            : 'Start tracking your time in less than a minute.'
      }
      footer={
        <>
          Already have an account?{' '}
          <Link
            to="/login"
            search={search.invitation ? { redirect: `/invitations/${search.invitation}` } : { redirect: search.redirect }}
            className="text-foreground font-medium underline-offset-4 hover:underline"
          >
            Sign in
          </Link>
        </>
      }
    >
      {closed ? (
        <Alert>
          <AlertDescription>This server takes new people only by invitation. Ask someone on it to invite you, then open the link in the invitation.</AlertDescription>
        </Alert>
      ) : (
        <form noValidate onSubmit={handleSubmit} className="grid gap-4">
          {/* Joining through an invitation goes where it leads, as below; a new organization starts at the welcome. */}
          <ExternalSignIn returnTo={search.redirect ?? (invited ? '/' : undefined)} invitation={invited ? search.invitation : undefined} />
          {(errors.form || errors.timeZone || errors.invitationToken) && (
            <Alert variant="destructive">
              <AlertDescription>{errors.form ?? errors.timeZone ?? errors.invitationToken}</AlertDescription>
            </Alert>
          )}
          {search.invitation && invitation.isSuccess && !invited && (
            <Alert>
              <AlertDescription>This invitation can no longer be used. You can still create an account of your own.</AlertDescription>
            </Alert>
          )}
          <TextField id="fullName" name="fullName" label="Full name" autoComplete="name" autoFocus thread="name" error={errors.fullName} />
          <TextField
            key={invited?.email ?? 'email'}
            id="email"
            name="email"
            type="email"
            label="Email"
            autoComplete="email"
            thread="email"
            defaultValue={invited?.email}
            readOnly={!!invited}
            hint={invited ? 'The address the invitation was sent to.' : undefined}
            error={errors.email}
          />
          <TextField
            id="password"
            name="password"
            type="password"
            label="Password"
            autoComplete="new-password"
            thread="password"
            hint={`At least ${PASSWORD_MIN_LENGTH} characters.`}
            error={errors.password}
          />
          {server?.termsUrl && <TermsField termsUrl={server.termsUrl} privacyUrl={server.privacyUrl} checked={acceptTerms} onCheckedChange={setAcceptTerms} error={errors.acceptTerms} />}
          <Button type="submit" size="lg" className="mt-2 w-full" disabled={pending} data-thread-go>
            {pending ? 'Creating account…' : 'Create account'}
          </Button>
        </form>
      )}
    </AuthLayout>
  )
}

interface TermsFieldProps {
  termsUrl: string
  privacyUrl: string | null
  checked: boolean
  onCheckedChange: (checked: boolean) => void
  error?: string
}

/** Agreeing to the server's terms, on a server that has them. */
function TermsField({ termsUrl, privacyUrl, checked, onCheckedChange, error }: TermsFieldProps) {
  return (
    <div className="grid gap-2">
      <div className="flex items-start gap-3">
        <Checkbox
          id="acceptTerms"
          checked={checked}
          onCheckedChange={(value) => onCheckedChange(value === true)}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? 'acceptTerms-error' : undefined}
          className="mt-0.5"
        />
        <Label htmlFor="acceptTerms" className="text-muted-foreground block text-sm leading-snug font-normal">
          I agree to the{' '}
          <a href={termsUrl} target="_blank" rel="noreferrer" className="text-foreground font-medium underline underline-offset-4">
            terms of service
          </a>
          {privacyUrl && (
            <>
              {' '}
              and the{' '}
              <a href={privacyUrl} target="_blank" rel="noreferrer" className="text-foreground font-medium underline underline-offset-4">
                privacy policy
              </a>
            </>
          )}
          .
        </Label>
      </div>
      {error && (
        <p id="acceptTerms-error" className="text-destructive text-sm">
          {error}
        </p>
      )}
    </div>
  )
}
