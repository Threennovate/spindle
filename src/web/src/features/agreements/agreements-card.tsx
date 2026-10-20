import { useQuery } from '@tanstack/react-query'
import { cn } from 'cn'
import { Ellipsis, Plus } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import type { Project } from '@/api/types'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Skeleton } from '@/components/ui/skeleton'
import { errorMessage } from '@/lib/error-message'
import { formatMoney } from '@/lib/money'
import { formatHoursMinutes } from '@/lib/time/duration'
import { dayKey } from '@/lib/time/zone'
import { useWorkspace } from '../workspace'
import { AgreementDialog } from './agreement-dialog'
import { agreementStatus, earnedPerHour, formatHours, formatPeriod, formatTerms, margin } from './format'
import { MoveTimeDialog } from './move-time-dialog'
import { profitabilityQuery, requestOf, useDeleteAgreement, useSaveAgreement, type Agreement, type AgreementProfitability, type ProjectProfitability, type RetainerPeriod } from './queries'

/** Retainer periods shown at first, newest first; the rest open on request. */
const RECENT_PERIODS = 3

/**
 * What the client pays for the project, against what the time on it cost: its fixed prices and retainers, and the
 * time outside them, billed by the hour. For admins.
 */
export function AgreementsCard({ project }: { project: Project }) {
  const { workspaceId } = useWorkspace()
  const query = useQuery(profitabilityQuery(workspaceId, project.id))
  const [editing, setEditing] = useState<Agreement | 'new' | null>(null)

  const profitability = query.data?.projects[0]
  const agreements = profitability?.agreements.map((item) => item.agreement) ?? []

  return (
    <Card>
      <CardHeader>
        <CardTitle>Agreements</CardTitle>
        <CardDescription>What the client pays for the project, against what the time on it cost.</CardDescription>
        <CardAction>
          <Button variant="outline" size="sm" onClick={() => setEditing('new')}>
            <Plus />
            Add agreement
          </Button>
        </CardAction>
      </CardHeader>
      <CardContent>
        {query.isPending ? (
          <Skeleton className="h-40 w-full" />
        ) : query.isError || !profitability ? (
          <p className="text-muted-foreground text-sm">{errorMessage(query.error, "Couldn't load the agreements.")}</p>
        ) : (
          <Profitability profitability={profitability} currency={query.data.currency} project={project} onEdit={setEditing} />
        )}
      </CardContent>
      <AgreementDialog
        open={editing !== null}
        onOpenChange={(open) => !open && setEditing(null)}
        project={project}
        agreements={agreements}
        agreement={editing === 'new' || editing === null ? undefined : editing}
      />
    </Card>
  )
}

function Profitability({
  profitability,
  currency,
  project,
  onEdit,
}: {
  profitability: ProjectProfitability
  currency: string
  project: Project
  onEdit: (agreement: Agreement) => void
}) {
  const { agreements, outside } = profitability
  const hasOutside = outside.trackedSeconds > 0 || outside.amount !== 0

  return (
    <div className="grid gap-4">
      {(agreements.length > 0 || profitability.trackedSeconds > 0) && (
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
          <Figure label="Revenue" value={formatMoney(profitability.revenue, currency)} />
          <Figure label="Labor cost" value={formatMoney(profitability.laborCost, currency)} />
          <Figure label="Profit" value={formatMoney(profitability.profit, currency)} tone={profitability.profit < 0 ? 'negative' : undefined} />
          <Figure label="Margin" value={margin(profitability.profit, profitability.revenue)} />
        </div>
      )}

      {agreements.length === 0 ? (
        <p className="text-muted-foreground rounded-lg border border-dashed p-4 text-sm">
          Add the fixed price or retainer agreed with the client. Time tracked on its tasks then counts towards it, so you see what each piece of work
          earned against what it cost.
        </p>
      ) : (
        <ul className="divide-y rounded-lg border">
          {agreements.map((item) => (
            <AgreementRow key={item.agreement.id} item={item} currency={currency} project={project} onEdit={() => onEdit(item.agreement)} />
          ))}
          {hasOutside && (
            <li className="grid gap-0.5 px-4 py-3">
              <div className="flex items-baseline justify-between gap-3">
                <span className="text-muted-foreground text-sm">Outside agreements</span>
                <span className="text-sm tabular-nums">{formatMoney(outside.amount, currency)}</span>
              </div>
              <p className="text-muted-foreground text-xs">
                {formatHoursMinutes(outside.trackedSeconds)} billed by the hour · {formatMoney(outside.laborCost, currency)} labor cost
              </p>
            </li>
          )}
        </ul>
      )}
    </div>
  )
}

function Figure({ label, value, tone }: { label: string; value: string; tone?: 'negative' }) {
  return (
    <div className="bg-muted/50 grid gap-0.5 rounded-lg px-3 py-2">
      <span className="text-muted-foreground text-xs">{label}</span>
      <span className={cn('text-lg font-semibold tabular-nums', tone === 'negative' && 'text-destructive')}>{value}</span>
    </div>
  )
}

function AgreementRow({ item, currency, project, onEdit }: { item: AgreementProfitability; currency: string; project: Project; onEdit: () => void }) {
  const { workspaceId, timeZone } = useWorkspace()
  const save = useSaveAgreement(workspaceId, project.id)
  const remove = useDeleteAgreement(workspaceId, project.id)
  const [moving, setMoving] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const today = dayKey(new Date(), timeZone)

  const { agreement } = item
  const status = agreementStatus(agreement, today)
  const taskNames = project.tasks.filter((task) => agreement.taskIds.includes(task.id)).map((task) => task.name)

  function markDone(endsOn: string | null) {
    save.mutate(
      { id: agreement.id, body: requestOf(agreement, { endsOn }) },
      {
        onSuccess: () => toast.success(endsOn ? `“${agreement.name}” is done` : `“${agreement.name}” is in progress again`),
        onError: (error) => toast.error(errorMessage(error, "Couldn't change the agreement.")),
      },
    )
  }

  return (
    <li className="grid gap-2 px-4 py-3">
      <div className="flex items-start gap-3">
        <div className="grid min-w-0 flex-1 gap-1">
          <div className="flex items-baseline justify-between gap-3">
            <span className="truncate font-medium">{agreement.name}</span>
            <span className="shrink-0 font-medium tabular-nums">{formatTerms(agreement, currency)}</span>
          </div>
          <div className="text-muted-foreground flex flex-wrap items-center gap-x-2 gap-y-1 text-xs">
            <Badge variant={status.tone === 'muted' ? 'outline' : 'secondary'} className={cn(status.tone === 'done' && 'text-foreground')}>
              {status.label}
            </Badge>
            <span>{describe(agreement)}</span>
          </div>
        </div>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon-sm" aria-label={`Actions for ${agreement.name}`} className="-mt-1 -mr-2">
              <Ellipsis />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem onSelect={onEdit}>Edit</DropdownMenuItem>
            {agreement.kind === 'fixedPrice' &&
              (agreement.endsOn ? (
                <DropdownMenuItem onSelect={() => markDone(null)}>Reopen</DropdownMenuItem>
              ) : (
                <DropdownMenuItem onSelect={() => markDone(today)}>Mark done today</DropdownMenuItem>
              ))}
            <DropdownMenuItem onSelect={() => setMoving(true)}>Move earlier time here…</DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuItem variant="destructive" onSelect={() => setConfirmDelete(true)}>
              Delete
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>

      {agreement.kind === 'fixedPrice' ? <FixedPriceProgress item={item} currency={currency} /> : <RetainerProgress item={item} currency={currency} today={today} />}

      <p className="text-muted-foreground text-xs">
        {taskNames.length === 0 ? (
          <span className="text-orange-700 dark:text-orange-400">No tasks yet, so no time counts towards it. Edit it to choose some.</span>
        ) : (
          <>Tracked on {taskNames.join(', ')}</>
        )}
      </p>

      <MoveTimeDialog open={moving} onOpenChange={setMoving} project={project} agreement={agreement} />
      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title={`Delete “${agreement.name}”?`}
        description="Its tasks and their time stay on the project, billed by the hour like the rest of the time outside agreements."
        confirmLabel="Delete agreement"
        onConfirm={() => remove.mutate(agreement.id, { onError: (error) => toast.error(errorMessage(error, "Couldn't delete the agreement.")) })}
      />
    </li>
  )
}

/** The kind and its terms beyond the amount, e.g. "Fixed price · 40h 00m quoted". */
function describe(agreement: Agreement): string {
  if (agreement.kind === 'fixedPrice') {
    return ['Fixed price', agreement.hours != null && `${formatHours(agreement.hours)} quoted`].filter(Boolean).join(' · ')
  }
  if (agreement.hours == null) {
    return 'Retainer · no hour limit'
  }
  const per = agreement.period === 'year' ? 'a year' : 'a month'
  return [
    'Retainer',
    agreement.amount != null ? `${formatHours(agreement.hours)} included ${per}` : 'paid at the hourly rate',
    agreement.billsOverage ? 'hours over the limit billed' : 'hours over the limit not billed',
  ].join(' · ')
}

/** Labor cost against the price: how much of it the time has used up so far. */
function FixedPriceProgress({ item, currency }: { item: AgreementProfitability; currency: string }) {
  const price = item.revenue
  const used = price > 0 ? item.laborCost / price : 0
  const over = used > 1
  const quotedSeconds = item.agreement.hours != null ? Math.round(item.agreement.hours * 3600) : null
  const perHour = earnedPerHour(item, currency)

  return (
    <div className="grid gap-1.5">
      <Bar
        label="Labor cost against the price"
        parts={[{ ratio: Math.min(used, 1), className: over ? 'bg-destructive' : 'bg-primary' }]}
      />
      <p className="text-muted-foreground text-xs tabular-nums">
        <span className={cn(quotedSeconds && item.trackedSeconds > quotedSeconds && 'text-orange-700 dark:text-orange-400')}>
          {formatHoursMinutes(item.trackedSeconds)}
          {quotedSeconds ? ` of ${formatHoursMinutes(quotedSeconds)} quoted` : ' tracked'}
        </span>{' '}
        · {formatMoney(item.laborCost, currency)} labor cost
        {price > 0 && ` (${Math.round(used * 100)}% of the price)`} ·{' '}
        <span className={cn('font-medium', item.profit < 0 ? 'text-destructive' : 'text-foreground')}>
          {formatMoney(item.profit, currency)} {item.agreement.endsOn ? 'profit' : 'left'}
        </span>
        {perHour && ` · ${perHour}`}
      </p>
    </div>
  )
}

/** A retainer's recent periods: the hours used against those included, and what went beyond them. */
function RetainerProgress({ item, currency, today }: { item: AgreementProfitability; currency: string; today: string }) {
  const [showAll, setShowAll] = useState(false)
  const periods = [...item.periods].reverse()
  const shown = showAll ? periods : periods.slice(0, RECENT_PERIODS)

  return (
    <div className="grid gap-2">
      {periods.length === 0 ? (
        <p className="text-muted-foreground text-xs">Its first period hasn’t started yet.</p>
      ) : (
        <ul className="grid gap-1.5">
          {shown.map((period) => (
            <PeriodRow key={period.start} period={period} currency={currency} today={today} billsOverage={item.agreement.billsOverage} />
          ))}
        </ul>
      )}
      {periods.length > RECENT_PERIODS && (
        <Button variant="link" size="sm" className="text-muted-foreground h-auto justify-self-start p-0 text-xs" onClick={() => setShowAll(!showAll)}>
          {showAll ? 'Show recent periods' : `Show all ${periods.length} periods`}
        </Button>
      )}
      <p className="text-muted-foreground text-xs tabular-nums">
        {formatMoney(item.revenue, currency)} so far · {formatMoney(item.laborCost, currency)} labor cost ·{' '}
        <span className={cn('font-medium', item.profit < 0 ? 'text-destructive' : 'text-foreground')}>{formatMoney(item.profit, currency)} profit</span>
        {item.overageAmount > 0 && ` · ${formatMoney(item.overageAmount, currency)} of it over the limit`}
      </p>
    </div>
  )
}

function PeriodRow({ period, currency, today, billsOverage }: { period: RetainerPeriod; currency: string; today: string; billsOverage: boolean }) {
  const included = period.includedSeconds ?? null
  const scale = included ? Math.max(period.seconds, included) : 0
  const within = included ? Math.min(period.seconds, included) : 0
  const current = period.start <= today && today <= period.end

  return (
    <li className="grid items-center gap-x-3 gap-y-1 text-xs sm:grid-cols-[8rem_minmax(4rem,1fr)_auto]">
      <span className={cn('truncate', current ? 'text-foreground font-medium' : 'text-muted-foreground')}>{formatPeriod(period, today)}</span>
      {included ? (
        <Bar
          label={`Hours used in ${formatPeriod(period, today)}`}
          parts={[
            { ratio: within / scale, className: 'bg-primary' },
            { ratio: period.overSeconds / scale, className: 'bg-orange-500' },
          ]}
        />
      ) : (
        <span className="hidden sm:block" />
      )}
      <span className={cn('tabular-nums sm:text-right sm:whitespace-nowrap', period.overSeconds > 0 ? 'text-orange-700 dark:text-orange-400' : 'text-muted-foreground')}>
        {formatHoursMinutes(period.seconds)}
        {included ? ` of ${formatHoursMinutes(included)}` : ''}
        {period.overSeconds > 0 &&
          ` · ${formatHoursMinutes(period.overSeconds)} over${billsOverage && period.overageAmount > 0 ? ` · ${formatMoney(period.overageAmount, currency)} to bill` : ''}`}
      </span>
    </li>
  )
}

export function Bar({ label, parts }: { label: string; parts: { ratio: number; className: string }[] }) {
  const total = parts.reduce((sum, part) => sum + part.ratio, 0)
  return (
    <div
      role="progressbar"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={Math.round(Math.min(total, 1) * 100)}
      className="bg-muted flex h-1.5 overflow-hidden rounded-full"
    >
      {parts.map((part, index) => (
        <div key={index} className={cn('h-full', part.className)} style={{ width: `${Math.max(0, Math.min(part.ratio, 1)) * 100}%` }} />
      ))}
    </div>
  )
}
