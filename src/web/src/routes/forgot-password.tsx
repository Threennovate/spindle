import { createFileRoute, Link } from '@tanstack/react-router'
import { MailCheck } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { publicApi } from '@/api/public'
import { CONNECTION_ERROR, EMAIL_PATTERN } from '@/auth/form-errors'
import { authThreads } from '@/auth/threads'
import { AuthLayout } from '@/components/auth-layout'
import { TextField } from '@/components/text-field'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { usePageTitle } from '@/lib/title'

export const Route = createFileRoute('/forgot-password')({
  component: ForgotPasswordPage,
})

function ForgotPasswordPage() {
  usePageTitle('Forgot password')
  const [email, setEmail] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [sentTo, setSentTo] = useState<string | null>(null)
  const [pending, setPending] = useState(false)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    const address = email.trim()
    if (!EMAIL_PATTERN.test(address)) {
      setError('Enter a valid email address.')
      return
    }
    setPending(true)
    authThreads.begin('send')
    try {
      const { response } = await publicApi.POST('/api/auth/forgot-password', { body: { email: address } })
      if (response.status === 429) {
        authThreads.fail()
        setError('Too many requests from here. Try again in a few minutes.')
      } else if (response.ok) {
        await authThreads.complete()
        setSentTo(address)
      } else {
        authThreads.fail()
        setError('Something went wrong. Please try again.')
      }
    } catch {
      authThreads.fail()
      setError(CONNECTION_ERROR)
    } finally {
      setPending(false)
    }
  }

  const footer = (
    <>
      Remembered it?{' '}
      <Link to="/login" className="text-foreground font-medium underline-offset-4 hover:underline">
        Sign in
      </Link>
    </>
  )

  if (sentTo) {
    return (
      <AuthLayout title="Check your inbox" description={`If an account uses ${sentTo}, we sent it a link to choose a new password.`} footer={footer}>
        <div className="grid justify-items-center gap-4 text-center">
          <MailCheck aria-hidden="true" className="text-primary-text size-10" data-thread="email" data-thread-filled />
          <p className="text-muted-foreground text-sm">The link works for a day. Nothing there? Check the spam folder, or try again in a few minutes.</p>
          <Button variant="outline" className="w-full" onClick={() => setSentTo(null)}>
            Use another address
          </Button>
        </div>
      </AuthLayout>
    )
  }

  return (
    <AuthLayout title="Forgot your password?" description="Enter the email you sign in with. We'll send you a link to choose a new password." footer={footer}>
      <form noValidate onSubmit={handleSubmit} className="grid gap-4">
        {error && !error.startsWith('Enter') && (
          <Alert variant="destructive">
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        )}
        <TextField
          id="email"
          type="email"
          label="Email"
          autoComplete="username"
          autoFocus
          thread="email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          error={error?.startsWith('Enter') ? error : undefined}
        />
        <Button type="submit" size="lg" className="mt-2 w-full" disabled={pending} data-thread-go>
          Send the link
        </Button>
      </form>
    </AuthLayout>
  )
}
