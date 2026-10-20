import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { startExternalSignIn } from '@/auth/external'
import { Button } from '@/components/ui/button'
import { serverQuery } from '@/features/subscriptions/queries'

interface ExternalSignInProps {
  returnTo?: string
  invitation?: string
  /** On the sign-in page: the thread that runs into the buttons (auth/threads.ts). */
  thread?: string
}

/**
 * "Continue with Google" and "Continue with Apple", for signing in and creating an account alike, with the terms
 * next to them on a server that has terms: continuing is agreeing to them, as it says.
 */
export function ExternalSignIn({ returnTo, invitation, thread }: ExternalSignInProps) {
  const { data: server } = useQuery(serverQuery)
  const [leaving, setLeaving] = useState<string | null>(null)
  const providers = server?.signInProviders ?? []
  if (providers.length === 0) {
    return null
  }

  return (
    <div className="grid gap-3">
      <div className="grid gap-3" data-thread={thread}>
        {providers.map((provider) => (
          <Button
            key={provider.name}
            type="button"
            variant="outline"
            size="lg"
            className="w-full"
            disabled={leaving !== null}
            onClick={() => {
              setLeaving(provider.name)
              startExternalSignIn(provider.name, { returnTo, invitation, acceptTerms: true })
            }}
          >
            <ProviderLogo name={provider.name} />
            {leaving === provider.name ? `Opening ${provider.displayName}…` : `Continue with ${provider.displayName}`}
          </Button>
        ))}
      </div>
      {server?.termsUrl && (
        <p className="text-muted-foreground text-center text-xs leading-snug">
          By continuing, you agree to the{' '}
          <a href={server.termsUrl} target="_blank" rel="noreferrer" className="text-foreground underline underline-offset-4">
            terms of service
          </a>
          {server.privacyUrl && (
            <>
              {' '}
              and the{' '}
              <a href={server.privacyUrl} target="_blank" rel="noreferrer" className="text-foreground underline underline-offset-4">
                privacy policy
              </a>
            </>
          )}
          .
        </p>
      )}
      <div className="text-muted-foreground flex items-center gap-3 text-xs" role="separator">
        <span className="bg-border h-px flex-1" />
        or with email
        <span className="bg-border h-px flex-1" />
      </div>
    </div>
  )
}

/** The providers' own marks, as their sign-in guidelines ask: Google's four colours, Apple's in the text colour. */
export function ProviderLogo({ name }: { name: string }) {
  if (name === 'google') {
    return (
      <svg viewBox="0 0 48 48" aria-hidden="true" className="size-4">
        <path fill="#FFC107" d="M43.6 20.5H42V20H24v8h11.3C33.7 32.7 29.3 36 24 36c-6.6 0-12-5.4-12-12s5.4-12 12-12c3.1 0 5.8 1.2 7.9 3.1l5.7-5.7C34 6.1 29.3 4 24 4 12.9 4 4 12.9 4 24s8.9 20 20 20 20-8.9 20-20c0-1.3-.1-2.4-.4-3.5z" />
        <path fill="#FF3D00" d="m6.3 14.7 6.6 4.8C14.7 15.1 19 12 24 12c3.1 0 5.8 1.2 7.9 3.1l5.7-5.7C34 6.1 29.3 4 24 4 16.3 4 9.7 8.3 6.3 14.7z" />
        <path fill="#4CAF50" d="M24 44c5.2 0 9.9-2 13.4-5.2l-6.2-5.2C29.2 35.1 26.7 36 24 36c-5.3 0-9.7-3.3-11.3-8l-6.5 5C9.5 39.6 16.2 44 24 44z" />
        <path fill="#1976D2" d="M43.6 20.5H42V20H24v8h11.3c-.8 2.2-2.2 4.2-4.1 5.6l6.2 5.2C37 39.2 44 34 44 24c0-1.3-.1-2.4-.4-3.5z" />
      </svg>
    )
  }
  if (name === 'apple') {
    return (
      <svg viewBox="0 0 384 512" aria-hidden="true" className="size-4 fill-current">
        <path d="M318.7 268.7c-.2-36.7 16.4-64.4 50-84.8-18.8-26.9-47.2-41.7-84.7-44.6-35.5-2.8-74.3 20.7-88.5 20.7-15 0-49.4-19.7-76.4-19.7C63.3 141.2 4 184.8 4 273.5q0 39.3 14.4 81.2c12.8 36.7 59 126.7 107.2 125.2 25.2-.6 43-17.9 75.8-17.9 31.8 0 48.3 17.9 76.4 17.9 48.6-.7 90.4-82.5 102.6-119.3-65.2-30.7-61.7-90-61.7-91.9zm-56.6-164.2c27.3-32.4 24.8-61.9 24-72.5-24.1 1.4-52 16.4-67.9 34.9-17.5 19.8-27.8 44.3-25.6 71.9 26.1 2 49.9-11.4 69.5-34.3z" />
      </svg>
    )
  }
  return null
}
