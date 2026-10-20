import { useQuery } from '@tanstack/react-query'
import { Loader2 } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import { startExternalSignIn } from '@/auth/external'
import { CONNECTION_ERROR } from '@/auth/form-errors'
import { meQuery } from '@/auth/me'
import { session } from '@/auth/session'
import { ProviderLogo } from '@/components/external-sign-in'
import { TextField } from '@/components/text-field'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { errorMessage } from '@/lib/error-message'
import { downloadMyData, externalLoginsQuery, useDeleteAccount } from './queries'

/** Downloads everything the person tracked. Also offered to someone left without a workspace. */
export function DownloadDataButton() {
  const [pending, setPending] = useState(false)

  async function download() {
    setPending(true)
    try {
      await downloadMyData()
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't prepare your data."))
    } finally {
      setPending(false)
    }
  }

  return (
    <Button variant="outline" onClick={() => void download()} disabled={pending}>
      {pending && <Loader2 className="animate-spin" />}
      Download my data
    </Button>
  )
}

/** Opens the password confirmation that deletes the account and signs out. */
export function DeleteAccountButton({ variant = 'destructive' }: { variant?: 'destructive' | 'ghost' }) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <Button variant={variant} onClick={() => setOpen(true)}>
        Delete my account…
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="sm:max-w-md">{open && <DeleteForm onCancel={() => setOpen(false)} />}</DialogContent>
      </Dialog>
    </>
  )
}

function DeleteForm({ onCancel }: { onCancel: () => void }) {
  const remove = useDeleteAccount()
  // An account made with Google or Apple has no password; signing in again just now confirms it instead.
  const passwordless = useQuery(meQuery).data?.hasPassword === false
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    if (!passwordless && !password) {
      setError('Enter your password to confirm.')
      return
    }
    try {
      await remove.mutateAsync(passwordless ? undefined : password)
      toast.success('Your account was deleted')
      await session.signOut()
    } catch (failure) {
      setError(failure instanceof ApiError ? (Object.values(failure.fieldErrors)[0]?.[0] ?? failure.message) : CONNECTION_ERROR)
    }
  }

  return (
    <form noValidate onSubmit={handleSubmit} className="grid gap-5">
      <DialogHeader>
        <DialogTitle>Delete your account?</DialogTitle>
        <DialogDescription>This cannot be undone. Download your data first if you want to keep it.</DialogDescription>
      </DialogHeader>
      {passwordless ? (
        error && <SignInAgain message={error} />
      ) : (
        <TextField id="delete-password" label="Your password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} autoFocus error={error ?? undefined} />
      )}
      <DialogFooter>
        <Button type="button" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" variant="destructive" disabled={remove.isPending}>
          Delete account
        </Button>
      </DialogFooter>
    </form>
  )
}

/**
 * For an account without a password, when the server wants a fresh sign-in before something that matters: the reason,
 * and a way to sign in again with the account's own Google or Apple login, coming back to this page.
 */
export function SignInAgain({ message }: { message: string }) {
  const logins = useQuery(externalLoginsQuery).data ?? []
  return (
    <div className="grid gap-3">
      <Alert variant="destructive">
        <AlertDescription>{message}</AlertDescription>
      </Alert>
      {logins.map((login) => (
        <Button
          key={login.provider}
          type="button"
          variant="outline"
          onClick={() => startExternalSignIn(login.provider, { returnTo: window.location.pathname, acceptTerms: false })}
        >
          <ProviderLogo name={login.provider} />
          Sign in again with {login.displayName}
        </Button>
      ))}
    </div>
  )
}
