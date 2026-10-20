import { createFileRoute, Link, useNavigate } from '@tanstack/react-router'
import { useState, type FormEvent } from 'react'
import { toApiError } from '@/api/problem'
import { publicApi } from '@/api/public'
import { CONNECTION_ERROR, hasErrors, toFormErrors, type FormErrors } from '@/auth/form-errors'
import { AuthLayout } from '@/components/auth-layout'
import { TextField } from '@/components/text-field'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { usePageTitle } from '@/lib/title'

const FIELDS = ['newPassword', 'repeat', 'token'] as const
type Field = (typeof FIELDS)[number]

export const Route = createFileRoute('/reset-password')({
  validateSearch: (search: Record<string, unknown>): { email?: string; token?: string } => ({
    email: typeof search.email === 'string' ? search.email : undefined,
    token: typeof search.token === 'string' ? search.token : undefined,
  }),
  component: ResetPasswordPage,
})

function ResetPasswordPage() {
  usePageTitle('Choose a new password')
  const { email, token } = Route.useSearch()
  const navigate = useNavigate()
  const [password, setPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [errors, setErrors] = useState<FormErrors<Field>>({})
  const [pending, setPending] = useState(false)

  const footer = (
    <Link to="/login" className="text-foreground font-medium underline-offset-4 hover:underline">
      Back to sign in
    </Link>
  )

  if (!email || !token) {
    return (
      <AuthLayout title="This link is incomplete" description="Open the link from the email again, or ask for a new one." footer={footer}>
        <Button asChild className="w-full">
          <Link to="/forgot-password">Ask for a new link</Link>
        </Button>
      </AuthLayout>
    )
  }

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    const validation: FormErrors<Field> = {
      newPassword: password.length >= 8 ? undefined : 'Use at least 8 characters.',
      repeat: repeat === password ? undefined : 'The passwords do not match.',
    }
    setErrors(validation)
    if (hasErrors(validation)) {
      return
    }
    setPending(true)
    try {
      const { error, response } = await publicApi.POST('/api/auth/reset-password', { body: { email: email!, token: token!, newPassword: password } })
      if (response.ok) {
        await navigate({ to: '/login', search: { notice: 'password-reset' }, replace: true })
        return
      }
      setErrors(toFormErrors(toApiError(response, error), FIELDS))
    } catch {
      setErrors({ form: CONNECTION_ERROR })
    } finally {
      setPending(false)
    }
  }

  return (
    <AuthLayout title="Choose a new password" description={`For ${email}. Every device signed in with the old password is signed out.`} footer={footer}>
      <form noValidate onSubmit={handleSubmit} className="grid gap-4">
        {(errors.form || errors.token) && (
          <Alert variant="destructive">
            <AlertDescription>
              {errors.form ?? errors.token}{' '}
              {errors.token && (
                <Link to="/forgot-password" className="font-medium underline underline-offset-4">
                  Ask for a new link
                </Link>
              )}
            </AlertDescription>
          </Alert>
        )}
        <TextField
          id="new-password"
          type="password"
          label="New password"
          autoComplete="new-password"
          autoFocus
          thread="password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={errors.newPassword}
          hint="At least 8 characters. A few words make a strong one."
        />
        <TextField id="repeat-password" type="password" label="Repeat the new password" autoComplete="new-password" thread="repeat" value={repeat} onChange={(e) => setRepeat(e.target.value)} error={errors.repeat} />
        <Button type="submit" size="lg" className="mt-2 w-full" disabled={pending} data-thread-go>
          Save the new password
        </Button>
      </form>
    </AuthLayout>
  )
}
