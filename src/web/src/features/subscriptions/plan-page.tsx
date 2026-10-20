import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { useState } from 'react'
import { toast } from 'sonner'
import type { BillingInterval, Subscription } from '@/api/types'
import { meQuery } from '@/auth/me'
import { PageHeader } from '@/components/page-header'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { errorMessage } from '@/lib/error-message'
import { formatDayAs } from '@/lib/time/zone'
import { usePageTitle } from '@/lib/title'
import { useWorkspace } from '../workspace'
import { openPaddleCheckout } from './paddle-checkout'
import { describePlan, describePrice } from './plan'
import { manageSubscription, serverQuery, startCheckout, subscriptionQuery } from './queries'
import { useReadOnly } from './read-only'
import { lastDayKey } from './trial'

/**
 * The plan of the current workspace's organization, for its owners and admins: where the trial or subscription
 * stands and how many seats it has. Only on the hosted service; a self-hosted server has no plans.
 */
export function PlanPage() {
  usePageTitle('Plan')
  const { organization, canManage } = useReadOnly()
  const { data: server } = useQuery(serverQuery)
  const hosted = server?.hosted === true
  const subscription = useQuery({ ...subscriptionQuery(organization?.id ?? ''), enabled: hosted && canManage && organization !== undefined })

  return (
    <div className="mx-auto grid max-w-3xl gap-6">
      <PageHeader
        title="Plan"
        description={organization ? `What ${organization.name} has on Spindle. It covers every workspace in the organization.` : undefined}
      />
      {server && !hosted ? (
        <Alert>
          <AlertDescription>This server has no plans: everything on it is free to use.</AlertDescription>
        </Alert>
      ) : !organization || !canManage ? (
        <Alert>
          <AlertDescription>Only the owners and admins of {organization?.name ?? 'the organization'} see its plan.</AlertDescription>
        </Alert>
      ) : subscription.isError ? (
        <Alert variant="destructive">
          <AlertDescription>{errorMessage(subscription.error, 'Couldn’t load the plan.')}</AlertDescription>
        </Alert>
      ) : !subscription.data ? (
        <div className="grid gap-6">
          <Skeleton className="h-48" />
          <Skeleton className="h-32" />
        </div>
      ) : (
        <PlanDetails subscription={subscription.data} organizationId={organization.id} organizationName={organization.name} />
      )}
    </div>
  )
}

function PlanDetails({ subscription, organizationId, organizationName }: { subscription: Subscription; organizationId: string; organizationName: string }) {
  const { timeZone, dateFormat } = useWorkspace()
  const plan = describePlan(subscription, organizationName, (end) => formatDayAs(lastDayKey(end, timeZone), dateFormat))
  const seats = subscription.seats

  return (
    <>
      <Card>
        <CardHeader>
          <div>
            <Badge variant={plan.tone === 'bad' ? 'destructive' : plan.tone === 'good' ? 'default' : 'secondary'}>{plan.badge}</Badge>
          </div>
          <CardTitle className="text-xl">{plan.title}</CardTitle>
          <CardDescription className="text-sm leading-relaxed">{plan.text}</CardDescription>
        </CardHeader>
        {plan.action === 'choose' && subscription.checkoutAvailable ? (
          <ChoosePlan subscription={subscription} organizationId={organizationId} />
        ) : plan.action === 'manage' && subscription.checkoutAvailable ? (
          <ManagePlan organizationId={organizationId} />
        ) : (
          plan.action && (
            <CardContent className="flex flex-wrap items-center gap-3">
              <Button disabled>{plan.action === 'choose' ? 'Choose a plan' : 'Manage subscription'}</Button>
              <p className="text-muted-foreground text-sm">Plans open in the next few days. Nothing in your trial waits on it.</p>
            </CardContent>
          )
        )}
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Seats</CardTitle>
          <CardDescription>
            Everyone in at least one of {organizationName}’s workspaces takes a seat, once however many workspaces they are in. Pending invitations don’t.
          </CardDescription>
        </CardHeader>
        <CardContent className="flex flex-wrap items-center justify-between gap-3">
          <p>
            <span className="text-3xl font-semibold tabular-nums">{seats}</span>{' '}
            <span className="text-muted-foreground">{seats === 1 ? 'person' : 'people'}</span>
          </p>
          <Button asChild variant="outline">
            <Link to="/team">See the team</Link>
          </Button>
        </CardContent>
      </Card>
    </>
  )
}

/**
 * Picks monthly or yearly and opens Paddle's checkout for the organization's seats. The plan reaches the server by
 * webhook, so after paying the page asks again for a little while until it shows.
 */
function ChoosePlan({ subscription, organizationId }: { subscription: Subscription; organizationId: string }) {
  const queryClient = useQueryClient()
  const [interval, setInterval] = useState<BillingInterval>('month')
  const [waiting, setWaiting] = useState(false)
  const price = subscription.prices.find((p) => p.interval === interval)
  const query = subscriptionQuery(organizationId)

  const checkout = useMutation({
    mutationFn: async () => openPaddleCheckout(await startCheckout(organizationId, interval)),
    onSuccess: async (paid) => {
      if (!paid) return
      setWaiting(true)
      toast.success('Thank you! Your plan is being set up.')
      let arrived = false
      for (let attempt = 0; attempt < 15 && !arrived; attempt++) {
        await new Promise((resolve) => window.setTimeout(resolve, 2000))
        const fresh = await queryClient.fetchQuery({ ...query, staleTime: 0 })
        arrived = fresh.hasPlan || fresh.status === 'active'
      }
      setWaiting(false)
      if (!arrived) toast.info('Paddle has the payment; the plan will show here within a few minutes.')
      await queryClient.invalidateQueries({ queryKey: meQuery.queryKey })
    },
    onError: (error) => toast.error(errorMessage(error, 'Couldn’t open the checkout.')),
  })

  return (
    <CardContent className="grid gap-4">
      <ToggleGroup
        type="single"
        variant="outline"
        value={interval}
        onValueChange={(value) => value && setInterval(value as BillingInterval)}
        aria-label="Billing"
      >
        <ToggleGroupItem value="month">Monthly</ToggleGroupItem>
        <ToggleGroupItem value="year">Yearly</ToggleGroupItem>
      </ToggleGroup>
      {price && (
        <p className="text-sm">
          <span className="font-medium">{describePrice(price)}</span>
          <span className="text-muted-foreground">
            {' '}
            · {subscription.seats} {subscription.seats === 1 ? 'person' : 'people'} now. Taxes are shown in the checkout.
          </span>
        </p>
      )}
      <div className="flex flex-wrap items-center gap-3">
        <Button onClick={() => checkout.mutate()} disabled={checkout.isPending || waiting}>
          {waiting ? 'Setting up your plan…' : 'Choose a plan'}
        </Button>
        {subscription.status === 'trialing' && subscription.access === 'full' && (
          <p className="text-muted-foreground text-sm">Nothing is charged until the trial ends.</p>
        )}
      </div>
    </CardContent>
  )
}

/** Goes to Paddle's page for the plan: the card, invoices, and canceling. */
function ManagePlan({ organizationId }: { organizationId: string }) {
  const manage = useMutation({
    mutationFn: () => manageSubscription(organizationId),
    // In the same tab: browsers block a new one opened after waiting for the server.
    onSuccess: ({ url }) => window.location.assign(url),
    onError: (error) => toast.error(errorMessage(error, 'Couldn’t open the subscription.')),
  })

  return (
    <CardContent className="flex flex-wrap items-center gap-3">
      <Button variant="outline" onClick={() => manage.mutate()} disabled={manage.isPending}>
        Manage subscription
      </Button>
      <p className="text-muted-foreground text-sm">Change the card, download invoices or cancel, on Paddle’s secure page.</p>
    </CardContent>
  )
}
