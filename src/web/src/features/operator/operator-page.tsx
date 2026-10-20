import { useQuery } from '@tanstack/react-query'
import { Ellipsis, Search } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import type { OperatorOrganization, OperatorOverview, OperatorPriceCohort, SubscriptionChange, SubscriptionStatus } from '@/api/types'
import { PageHeader } from '@/components/page-header'
import { TextField } from '@/components/text-field'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { InputGroup, InputGroupAddon, InputGroupInput } from '@/components/ui/input-group'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { errorMessage } from '@/lib/error-message'
import { cn } from '@/lib/utils'
import { dayKey, addDaysToKey, formatDayAs, startOfDayKey } from '@/lib/time/zone'
import { usePageTitle } from '@/lib/title'
import { useDebouncedValue } from '@/lib/use-debounced-value'
import { describePrice } from '../subscriptions/plan'
import { daysLeft, describeDaysLeft, lastDayKey } from '../subscriptions/trial'
import { useWorkspace } from '../workspace'
import {
  operatorOrganizationsQuery,
  operatorOverviewQuery,
  priceCohortsQuery,
  subscriptionChangesQuery,
  useComp,
  usePutOnTrial,
  type OrganizationFilter,
} from './queries'

/**
 * The organizations on this server, for the people who run it: who they are, who to contact, and where their
 * subscriptions stand. Never anyone's time, projects, clients or money.
 */
export function OperatorPage() {
  usePageTitle('Operator')
  const { me } = useWorkspace()

  if (!me.isOperator) {
    return (
      <div className="mx-auto grid max-w-6xl gap-6">
        <PageHeader title="Operator" description="Only the people who run this server see this page." />
      </div>
    )
  }

  return <Operator />
}

function Operator() {
  const overview = useQuery(operatorOverviewQuery)
  const [filter, setFilter] = useState<OrganizationFilter>('all')
  const [search, setSearch] = useState('')
  const term = useDebouncedValue(search.trim(), 200)
  const organizations = useQuery(operatorOrganizationsQuery(filter, term))
  const hosted = overview.data?.hosted ?? false
  const [extending, setExtending] = useState<OperatorOrganization | null>(null)
  const [comping, setComping] = useState<OperatorOrganization | null>(null)
  const [history, setHistory] = useState<OperatorOrganization | null>(null)
  const columns = hosted ? 6 : 5

  return (
    <div className="mx-auto grid max-w-6xl gap-6">
      <PageHeader
        title="Operator"
        description="The organizations on this server and where their subscriptions stand. Nobody’s time, projects or clients are shown here."
      />

      <Overview overview={overview.data} filter={filter} onFilter={setFilter} />

      <section aria-labelledby="organizations-heading" className="grid gap-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 id="organizations-heading" className="text-sm font-medium">
            {FILTER_HEADINGS[filter]}
            {organizations.data && <span className="text-muted-foreground"> · {organizations.data.total}</span>}
          </h2>
          <InputGroup className="w-full sm:w-72">
            <InputGroupAddon>
              <Search />
            </InputGroupAddon>
            <InputGroupInput
              type="search"
              placeholder="Name or owner’s email"
              aria-label="Search organizations"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </InputGroup>
        </div>

        <div className="bg-card overflow-hidden rounded-xl border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="pl-4">Organization</TableHead>
                {hosted && <TableHead>Subscription</TableHead>}
                <TableHead className="text-right">Seats</TableHead>
                <TableHead className="hidden md:table-cell">Created</TableHead>
                <TableHead className="hidden md:table-cell">Last activity</TableHead>
                <TableHead className="w-12" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {organizations.isPending && (
                <TableRow>
                  <TableCell colSpan={columns} className="px-4">
                    <Skeleton className="h-6 w-full" />
                  </TableCell>
                </TableRow>
              )}
              {organizations.isError && (
                <TableRow>
                  <TableCell colSpan={columns} className="text-muted-foreground py-8 text-center">
                    {errorMessage(organizations.error, "Couldn't load the organizations.")}
                  </TableCell>
                </TableRow>
              )}
              {organizations.data?.organizations.length === 0 && (
                <TableRow>
                  <TableCell colSpan={columns} className="text-muted-foreground py-8 text-center">
                    {term ? `No organization matches “${term}”.` : 'No organizations here.'}
                  </TableCell>
                </TableRow>
              )}
              {organizations.data?.organizations.map((organization) => (
                <OrganizationRow
                  key={organization.id}
                  organization={organization}
                  hosted={hosted}
                  onExtend={() => setExtending(organization)}
                  onComp={() => setComping(organization)}
                  onHistory={() => setHistory(organization)}
                />
              ))}
            </TableBody>
          </Table>
        </div>
        {organizations.data && organizations.data.total > organizations.data.organizations.length && (
          <p className="text-muted-foreground text-xs">
            The newest {organizations.data.organizations.length} of {organizations.data.total}. Search to find the others.
          </p>
        )}
      </section>

      {hosted && <PriceCohorts />}

      <TrialDialog organization={extending} onOpenChange={(open) => !open && setExtending(null)} />
      <CompDialog organization={comping} onOpenChange={(open) => !open && setComping(null)} />
      <HistoryDialog organization={history} onOpenChange={(open) => !open && setHistory(null)} />
    </div>
  )
}

const FILTER_HEADINGS: Record<OrganizationFilter, string> = {
  all: 'All organizations',
  trialing: 'On a trial',
  trialEndingSoon: 'Trials ending within two weeks',
  readOnly: 'Read-only',
  paying: 'Paying',
  comped: 'Comped',
}

/** The numbers that matter at a glance. Each one also filters the list below. */
function Overview({ overview, filter, onFilter }: { overview?: OperatorOverview; filter: OrganizationFilter; onFilter: (filter: OrganizationFilter) => void }) {
  if (!overview) {
    return <Skeleton className="h-20 w-full rounded-xl" />
  }

  const tiles: { label: string; value: number; filter?: OrganizationFilter; detail?: string }[] = [
    { label: 'Organizations', value: overview.organizations, filter: 'all', detail: `${overview.newLast7Days} new this week · ${overview.newLast30Days} this month` },
    ...(overview.hosted
      ? [
          { label: 'On a trial', value: overview.trialing, filter: 'trialing' as const },
          { label: 'Ending in 2 weeks', value: overview.trialEndingSoon, filter: 'trialEndingSoon' as const },
          { label: 'Read-only', value: overview.readOnly, filter: 'readOnly' as const },
          { label: 'Paying', value: overview.paying, filter: 'paying' as const },
          { label: 'Comped', value: overview.comped, filter: 'comped' as const },
        ]
      : []),
  ]

  return (
    <div className={cn('grid gap-3', overview.hosted ? 'grid-cols-2 sm:grid-cols-3 lg:grid-cols-6' : 'sm:max-w-xs')}>
      {tiles.map((tile) => {
        const active = tile.filter === filter
        return (
          <button
            key={tile.label}
            type="button"
            aria-pressed={active}
            onClick={() => tile.filter && onFilter(tile.filter)}
            className={cn(
              'bg-card hover:bg-muted/50 focus-visible:ring-ring/50 grid content-start gap-1 rounded-xl border p-4 text-left transition-colors outline-none focus-visible:ring-3',
              active && 'border-primary ring-primary/20 ring-2',
              tile.filter === 'all' && overview.hosted && 'col-span-2 sm:col-span-1',
            )}
          >
            <span className="text-muted-foreground text-xs font-medium">{tile.label}</span>
            <span className="text-2xl font-semibold tabular-nums">{tile.value}</span>
            {tile.detail && <span className="text-muted-foreground text-xs">{tile.detail}</span>}
          </button>
        )
      })}
    </div>
  )
}

/**
 * The price cohorts: the prices each group of organizations keeps for good. Read-only; prices are made in Paddle's
 * catalog and a new cohort is added in the settings, never changed.
 */
function PriceCohorts() {
  const cohorts = useQuery(priceCohortsQuery)

  return (
    <section aria-labelledby="price-cohorts-heading" className="grid gap-3">
      <div className="grid gap-1">
        <h2 id="price-cohorts-heading" className="text-sm font-medium">
          Price cohorts
        </h2>
        <p className="text-muted-foreground text-xs">
          Every organization keeps the prices of the cohort it joined at sign-up. New organizations join the current one.
        </p>
      </div>
      <div className="bg-card overflow-hidden rounded-xl border">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead className="pl-4">Cohort</TableHead>
              <TableHead>Prices</TableHead>
              <TableHead className="text-right">Organizations</TableHead>
              <TableHead className="pr-4 text-right">Paying</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {cohorts.isPending && (
              <TableRow>
                <TableCell colSpan={4} className="px-4">
                  <Skeleton className="h-6 w-full" />
                </TableCell>
              </TableRow>
            )}
            {cohorts.isError && (
              <TableRow>
                <TableCell colSpan={4} className="text-muted-foreground py-8 text-center">
                  {errorMessage(cohorts.error, "Couldn't load the price cohorts.")}
                </TableCell>
              </TableRow>
            )}
            {cohorts.data?.length === 0 && (
              <TableRow>
                <TableCell colSpan={4} className="text-muted-foreground py-8 text-center">
                  No price cohorts are set up yet.
                </TableCell>
              </TableRow>
            )}
            {cohorts.data?.map((cohort) => (
              <PriceCohortRow key={cohort.name} cohort={cohort} />
            ))}
          </TableBody>
        </Table>
      </div>
    </section>
  )
}

function PriceCohortRow({ cohort }: { cohort: OperatorPriceCohort }) {
  const prices = [...cohort.prices].sort((a, b) => (a.interval === b.interval ? 0 : a.interval === 'month' ? -1 : 1))

  return (
    <TableRow>
      <TableCell className="pl-4">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-medium">{cohort.name}</span>
          {cohort.current && <Badge variant="secondary">New organizations</Badge>}
        </div>
      </TableCell>
      <TableCell className="text-sm">
        {!cohort.hasPrices ? (
          <span className="text-destructive">Missing from the settings: its organizations can’t pay</span>
        ) : prices.length > 0 ? (
          prices.map((price) => describePrice(price)).join(' · ')
        ) : (
          <span className="text-muted-foreground">Couldn’t ask Paddle</span>
        )}
      </TableCell>
      <TableCell className="text-right tabular-nums">{cohort.organizations}</TableCell>
      <TableCell className="pr-4 text-right tabular-nums">{cohort.paying}</TableCell>
    </TableRow>
  )
}

interface OrganizationRowProps {
  organization: OperatorOrganization
  hosted: boolean
  onExtend: () => void
  onComp: () => void
  onHistory: () => void
}

function OrganizationRow({ organization, hosted, onExtend, onComp, onHistory }: OrganizationRowProps) {
  const formatDay = useDayFormat()
  const paid = organization.status === 'active' || organization.status === 'pastDue' || organization.status === 'canceled'

  return (
    <TableRow>
      <TableCell className="max-w-72 pl-4">
        <p className="truncate font-medium">{organization.name}</p>
        <p className="text-muted-foreground truncate text-xs">
          {organization.ownerEmail ? (
            <a href={`mailto:${organization.ownerEmail}`} className="hover:text-foreground hover:underline" title={organization.ownerName ?? undefined}>
              {organization.ownerEmail}
            </a>
          ) : (
            'No owner'
          )}
        </p>
      </TableCell>
      {hosted && (
        <TableCell>
          <SubscriptionCell organization={organization} />
        </TableCell>
      )}
      <TableCell className="text-right tabular-nums">{organization.seats}</TableCell>
      <TableCell className="text-muted-foreground hidden md:table-cell">{formatDay(organization.createdAt)}</TableCell>
      <TableCell className="text-muted-foreground hidden md:table-cell">
        {organization.lastActivityAt ? formatDay(organization.lastActivityAt) : 'Never'}
      </TableCell>
      <TableCell className="pr-2 text-right">
        {hosted && (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="icon" aria-label={`Actions for ${organization.name}`}>
                <Ellipsis />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuItem disabled={paid} onSelect={onExtend}>
                {organization.status === 'comped' ? 'Put on a trial…' : 'Extend trial…'}
              </DropdownMenuItem>
              <DropdownMenuItem disabled={paid || organization.status === 'comped'} onSelect={onComp}>
                Comp…
              </DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem onSelect={onHistory}>Changes</DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        )}
      </TableCell>
    </TableRow>
  )
}

const STATUS_LABELS: Record<SubscriptionStatus, string> = {
  trialing: 'Trial',
  active: 'Paying',
  pastDue: 'Payment failing',
  canceled: 'Canceled',
  comped: 'Comped',
}

/** Where the subscription stands, the date that matters for it, and the price cohort it pays in. */
function SubscriptionCell({ organization }: { organization: OperatorOrganization }) {
  const formatDay = useDayFormat()
  const { timeZone, dateFormat } = useWorkspace()
  const { status, access, trialEndsAt, paidUntil, priceCohort } = organization
  if (!status) {
    return <span className="text-muted-foreground">—</span>
  }

  const readOnly = access === 'readOnly'
  const label = readOnly ? (status === 'trialing' ? 'Trial ended' : 'Read-only') : STATUS_LABELS[status]
  const until = status === 'trialing' ? trialEndsAt : status === 'canceled' || status === 'active' ? paidUntil : null

  return (
    <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
      <Badge variant={readOnly || status === 'pastDue' ? 'destructive' : status === 'active' ? 'default' : status === 'comped' ? 'outline' : 'secondary'}>
        {label}
      </Badge>
      {until && (
        <span className="text-muted-foreground text-xs">
          {readOnly ? formatDay(until) : `until ${formatDayAs(lastDayKey(until, timeZone), dateFormat)}`}
          {status === 'trialing' && !readOnly && ` · ${describeDaysLeft(daysLeft(until)).toLowerCase()}`}
        </span>
      )}
      {priceCohort && (
        <span className="text-muted-foreground text-xs" title="Price cohort">
          {priceCohort}
        </span>
      )}
    </div>
  )
}

function useDayFormat(): (instant: string) => string {
  const { timeZone, dateFormat } = useWorkspace()
  return (instant) => formatDayAs(dayKey(instant, timeZone), dateFormat)
}

/** Sets the day a trial ends: extends it, reopens an ended one, or puts a comped organization back on one. */
function TrialDialog({ organization, onOpenChange }: { organization: OperatorOrganization | null; onOpenChange: (open: boolean) => void }) {
  return (
    <Dialog open={organization !== null} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-md">{organization && <TrialForm organization={organization} onDone={() => onOpenChange(false)} />}</DialogContent>
    </Dialog>
  )
}

function TrialForm({ organization, onDone }: { organization: OperatorOrganization; onDone: () => void }) {
  const { timeZone, dateFormat } = useWorkspace()
  const putOnTrial = usePutOnTrial()
  const today = dayKey(new Date(), timeZone)
  // Thirty days more than it has now, or than today for an ended or comped one.
  const current = organization.status === 'trialing' && organization.trialEndsAt ? lastDayKey(organization.trialEndsAt, timeZone) : today
  const [endsOn, setEndsOn] = useState(addDaysToKey(current > today ? current : today, 30))
  const [error, setError] = useState<string>()

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    if (!endsOn || endsOn < today) {
      setError('Choose today or a later day.')
      return
    }
    try {
      // To the end of the chosen day, in the operator's time zone.
      await putOnTrial.mutateAsync({ organizationId: organization.id, trialEndsAt: startOfDayKey(addDaysToKey(endsOn, 1), timeZone).toISOString() })
      toast.success(`${organization.name} is on a trial until ${formatDayAs(endsOn, dateFormat)}`)
      onDone()
    } catch (caught) {
      setError(caught instanceof ApiError ? errorMessage(caught, "Couldn't change the trial.") : "Couldn't change the trial.")
    }
  }

  return (
    <form noValidate onSubmit={handleSubmit} className="grid gap-5">
      <DialogHeader>
        <DialogTitle>{organization.status === 'comped' ? `Put ${organization.name} on a trial` : `Extend the trial of ${organization.name}`}</DialogTitle>
        <DialogDescription>
          {organization.status === 'comped'
            ? 'It stops being free and becomes read-only when the trial ends, unless someone pays.'
            : 'An ended trial opens again, and the organization can change its data until the day you choose.'}
        </DialogDescription>
      </DialogHeader>
      <TextField
        id="trial-ends-on"
        label="Last day of the trial"
        type="date"
        min={today}
        value={endsOn}
        onChange={(e) => {
          setEndsOn(e.target.value)
          setError(undefined)
        }}
        error={error}
        hint="It ends at midnight after this day, in your time zone."
        autoFocus
      />
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onDone}>
          Cancel
        </Button>
        <Button type="submit" disabled={putOnTrial.isPending}>
          Save
        </Button>
      </DialogFooter>
    </form>
  )
}

function CompDialog({ organization, onOpenChange }: { organization: OperatorOrganization | null; onOpenChange: (open: boolean) => void }) {
  const comp = useComp()
  const [shown, setShown] = useState(organization)
  if (organization && organization !== shown) {
    setShown(organization)
  }

  return (
    <AlertDialog open={organization !== null} onOpenChange={onOpenChange}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Comp {shown?.name}?</AlertDialogTitle>
          <AlertDialogDescription>
            It becomes free for good: no trial, no payment, never read-only. You can put it back on a trial later.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction
            onClick={() =>
              shown &&
              comp.mutate(shown.id, {
                onSuccess: () => toast.success(`${shown.name} is comped`),
                onError: (error) => toast.error(errorMessage(error, "Couldn't comp the organization.")),
              })
            }
          >
            Comp
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}

/** Who changed the organization's subscription by hand, and how. */
function HistoryDialog({ organization, onOpenChange }: { organization: OperatorOrganization | null; onOpenChange: (open: boolean) => void }) {
  return (
    <Dialog open={organization !== null} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-lg">
        {organization && (
          <>
            <DialogHeader>
              <DialogTitle>Changes to {organization.name}</DialogTitle>
              <DialogDescription>Changes operators made by hand, newest first. Payments are with the payment provider.</DialogDescription>
            </DialogHeader>
            <ChangeList organizationId={organization.id} />
          </>
        )}
      </DialogContent>
    </Dialog>
  )
}

function ChangeList({ organizationId }: { organizationId: string }) {
  const changes = useQuery(subscriptionChangesQuery(organizationId))
  const formatDay = useDayFormat()
  const { timeZone, dateFormat } = useWorkspace()

  if (changes.isPending) {
    return <Skeleton className="h-12 w-full" />
  }
  if (changes.isError) {
    return <p className="text-muted-foreground text-sm">{errorMessage(changes.error, "Couldn't load the changes.")}</p>
  }
  if (changes.data.length === 0) {
    return <p className="text-muted-foreground text-sm">Nobody has changed it by hand. It is where sign-up and payments left it.</p>
  }

  return (
    <ul className="grid max-h-80 gap-3 overflow-y-auto">
      {changes.data.map((change) => (
        <li key={`${change.at}-${change.kind}`} className="grid gap-0.5 text-sm">
          <span className="font-medium">{describe(change, (end) => formatDayAs(lastDayKey(end, timeZone), dateFormat))}</span>
          <span className="text-muted-foreground text-xs">
            {formatDay(change.at)} · {change.operatorEmail} · was {STATUS_LABELS[change.previousStatus].toLowerCase()}
          </span>
        </li>
      ))}
    </ul>
  )
}

function describe(change: SubscriptionChange, formatLastDay: (end: string) => string): string {
  return change.kind === 'comped' ? 'Comped' : `On a trial until ${change.trialEndsAt ? formatLastDay(change.trialEndsAt) : '—'}`
}
