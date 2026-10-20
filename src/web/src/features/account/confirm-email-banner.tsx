import { MailWarning, X } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import type { Me } from '@/api/types'
import { Button } from '@/components/ui/button'
import { errorMessage } from '@/lib/error-message'
import { useResendConfirmation } from './queries'

const DISMISSED = 'spindle:confirm-email-dismissed'

function wasDismissed(): boolean {
  try {
    return sessionStorage.getItem(DISMISSED) === '1'
  } catch {
    return false
  }
}

/** Reminds people to confirm their address until they do; it can be put away for the rest of the visit. */
export function ConfirmEmailBanner({ me }: { me: Me }) {
  const [dismissed, setDismissed] = useState(wasDismissed)
  const resend = useResendConfirmation()

  if (me.emailConfirmed || dismissed) {
    return null
  }

  function dismiss() {
    setDismissed(true)
    try {
      sessionStorage.setItem(DISMISSED, '1')
    } catch {
      // Private browsing: it simply comes back next time.
    }
  }

  return (
    <div role="status" className="bg-primary/5 flex items-center gap-3 border-b px-4 py-2 text-sm md:px-6">
      <MailWarning aria-hidden="true" className="text-primary-text size-4 shrink-0" />
      <p className="min-w-0 flex-1">
        Confirm your email address. We sent a link to <span className="font-medium">{me.email}</span>.
      </p>
      <Button
        variant="link"
        size="sm"
        className="h-auto p-0"
        disabled={resend.isPending || resend.isSuccess}
        onClick={() =>
          resend.mutate(undefined, {
            onSuccess: (result) => toast.success(result === 'sent' ? `We sent a new link to ${me.email}` : 'Your email is confirmed already'),
            onError: (error) =>
              toast.error(error instanceof ApiError && error.status === 429 ? 'Too many emails asked for. Try again in a few minutes.' : errorMessage(error, "Couldn't send the email.")),
          })
        }
      >
        {resend.isSuccess ? 'Sent' : 'Send it again'}
      </Button>
      <Button variant="ghost" size="icon-sm" aria-label="Hide this reminder" onClick={dismiss}>
        <X />
      </Button>
    </div>
  )
}
