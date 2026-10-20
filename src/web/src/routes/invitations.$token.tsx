import { useQuery } from '@tanstack/react-query'
import { createFileRoute, Link, useRouter } from '@tanstack/react-router'
import { useSyncExternalStore } from 'react'
import { ApiError } from '@/api/problem'
import { meQuery } from '@/auth/me'
import { session } from '@/auth/session'
import { AuthLayout } from '@/components/auth-layout'
import { FullPageMessage } from '@/components/full-page-message'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { invitationPreviewQuery, useAcceptInvitation } from '@/features/team/queries'
import { errorMessage } from '@/lib/error-message'
import { usePageTitle } from '@/lib/title'

export const Route = createFileRoute('/invitations/$token')({
  component: InvitationPage,
})

const STATUS_MESSAGES = {
  accepted: 'This invitation was already used. Sign in to continue.',
  revoked: 'This invitation was withdrawn. Ask the person who invited you for a new one.',
  expired: 'This invitation has expired. Ask the person who invited you to send it again.',
} as const

/** Where an invitation link leads: join straight away when signed in as the invited person, or register first. */
function InvitationPage() {
  usePageTitle('Invitation')
  const { token } = Route.useParams()
  const router = useRouter()
  const preview = useQuery(invitationPreviewQuery(token))
  const signedIn = useSyncExternalStore(session.subscribe, session.isSignedIn)
  const me = useQuery({ ...meQuery, enabled: signedIn })
  const accept = useAcceptInvitation()

  if (preview.isPending || (signedIn && me.isPending)) {
    return <FullPageMessage title="Opening your invitation…" description="One moment." />
  }

  if (preview.isError) {
    const unknown = preview.error instanceof ApiError && preview.error.status === 404
    return (
      <FullPageMessage
        title="This invitation link doesn't work"
        description={unknown ? 'It may have been replaced by a newer invitation. Check your email for the latest one.' : errorMessage(preview.error, "Couldn't open the invitation.")}
        action={
          <Button asChild variant="outline">
            <Link to="/login">Go to sign in</Link>
          </Button>
        }
      />
    )
  }

  const invitation = preview.data
  if (invitation.status !== 'pending') {
    return (
      <FullPageMessage
        title={`Invitation to ${invitation.workspaceName}`}
        description={STATUS_MESSAGES[invitation.status]}
        action={
          <Button asChild variant="outline">
            <Link to="/login">Go to sign in</Link>
          </Button>
        }
      />
    )
  }

  const signedInAs = me.data?.email
  const isInvitee = signedInAs?.toLowerCase() === invitation.email.toLowerCase()

  async function join() {
    await accept.mutateAsync(token)
    await router.navigate({ to: '/', replace: true })
  }

  return (
    <AuthLayout
      title={`Join ${invitation.workspaceName}`}
      description={`${invitation.invitedBy} invited ${invitation.email} to track time in ${invitation.workspaceName}${invitation.organizationName === invitation.workspaceName ? '' : ` (${invitation.organizationName})`}.`}
      footer={signedIn ? `Signed in as ${signedInAs}` : 'The invitation is valid for 14 days.'}
    >
      <div className="grid gap-3">
        {accept.isError && (
          <Alert variant="destructive">
            <AlertDescription>{errorMessage(accept.error, "Couldn't accept the invitation.")}</AlertDescription>
          </Alert>
        )}
        {signedIn && isInvitee && (
          <Button size="lg" onClick={() => void join()} disabled={accept.isPending}>
            Join {invitation.workspaceName}
          </Button>
        )}
        {signedIn && !isInvitee && (
          <>
            <Alert>
              <AlertDescription>
                You are signed in as {signedInAs}, but the invitation is for {invitation.email}. Sign out, then sign in or create an
                account with that address.
              </AlertDescription>
            </Alert>
            <Button variant="outline" onClick={() => void session.signOut()}>
              Sign out
            </Button>
          </>
        )}
        {!signedIn && (
          <>
            <Button asChild size="lg">
              <Link to="/register" search={{ invitation: token }}>
                Create an account
              </Link>
            </Button>
            <Button asChild variant="outline" size="lg">
              <Link to="/login" search={{ redirect: `/invitations/${token}` }}>
                I already have an account
              </Link>
            </Button>
          </>
        )}
      </div>
    </AuthLayout>
  )
}
