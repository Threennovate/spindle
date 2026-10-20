import { useQuery, useSuspenseQuery } from '@tanstack/react-query'
import { createFileRoute, redirect, useRouter } from '@tanstack/react-router'
import { useState, type FormEvent } from 'react'
import { ApiError } from '@/api/problem'
import type { Me } from '@/api/types'
import { CONNECTION_ERROR } from '@/auth/form-errors'
import { meQuery } from '@/auth/me'
import { session } from '@/auth/session'
import { AuthLayout } from '@/components/auth-layout'
import { TextField } from '@/components/text-field'
import { Button } from '@/components/ui/button'
import { serverQuery } from '@/features/subscriptions/queries'
import { daysLeft } from '@/features/subscriptions/trial'
import { useRenameOrganization } from '@/features/workspace-queries'
import { usePageTitle } from '@/lib/title'

/**
 * Right after creating an account: the one question worth asking before anything else, the organization's name.
 * Registration names it after the person, which a company wants to change before inviting anyone.
 */
export const Route = createFileRoute('/welcome')({
  beforeLoad: () => {
    if (!session.isSignedIn()) {
      throw redirect({ to: '/login' })
    }
  },
  // Only whoever runs the organization names it; anyone else goes straight to the timer.
  loader: async ({ context }) => {
    const me = await context.queryClient.ensureQueryData(meQuery)
    if (!currentOrganization(me) || currentOrganization(me)?.role === 'member') {
      throw redirect({ to: '/', replace: true })
    }
  },
  component: WelcomePage,
})

function WelcomePage() {
  usePageTitle('Welcome')
  const router = useRouter()
  const { data: me } = useSuspenseQuery(meQuery)
  const hosted = useQuery(serverQuery).data?.hosted === true
  const organization = currentOrganization(me)!
  const rename = useRenameOrganization()
  const [name, setName] = useState(organization.name)
  const [error, setError] = useState<string | null>(null)

  const next = () => router.navigate({ to: '/', replace: true })

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    const trimmed = name.trim()
    if (!trimmed) {
      setError('Enter a name.')
      return
    }
    if (trimmed === organization.name) {
      await next()
      return
    }
    try {
      await rename.mutateAsync({ organizationId: organization.id, name: trimmed })
      await next()
    } catch (failure) {
      setError(failure instanceof ApiError ? (failure.fieldErrors.name?.[0] ?? failure.message) : CONNECTION_ERROR)
    }
  }

  const trialDays = hosted && organization.trialEndsAt ? daysLeft(organization.trialEndsAt) : null

  return (
    <AuthLayout
      title="Welcome to Spindle"
      description={
        trialDays !== null
          ? `Your ${trialDays}-day trial has started, with everything included and no card needed. First, what should the organization be called?`
          : 'First, what should the organization be called?'
      }
      footer="You can change it later in Settings."
    >
      <form onSubmit={handleSubmit} className="grid gap-4" noValidate>
        <TextField
          id="organization-name"
          label="Organization"
          hint={error ? undefined : 'Your company or team, or your own name if you work alone. Invitations show it.'}
          error={error ?? undefined}
          value={name}
          onChange={(e) => setName(e.target.value)}
          onFocus={(e) => e.target.select()}
          autoFocus
          maxLength={140}
        />
        <div className="grid gap-2">
          <Button type="submit" disabled={rename.isPending}>
            Continue
          </Button>
          <Button type="button" variant="ghost" onClick={() => void next()}>
            Skip
          </Button>
        </div>
      </form>
    </AuthLayout>
  )
}

function currentOrganization(me: Me) {
  const workspace = me.workspaces.find((w) => w.id === me.defaultWorkspaceId) ?? me.workspaces[0]
  return me.organizations.find((o) => o.id === workspace?.organizationId)
}
