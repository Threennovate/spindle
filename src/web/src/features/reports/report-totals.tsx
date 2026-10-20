import { Card, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { useDurationFormat } from '../workspace'
import { hasBilledTime, money, percent, perHour } from './charts'
import type { SummaryReport } from './queries'

type Totals = SummaryReport['totals']

/**
 * The report's headline numbers: time, billable time and, for admins, what it is worth, what it cost, and where billed
 * time was entered separately, how much was billed.
 */
export function ReportTotalsCards({ totals, currency }: { totals: Totals; currency: string }) {
  const duration = useDurationFormat()
  const showMoney = totals.amount !== null && totals.amount !== undefined
  const cost = totals.laborCost ?? 0
  const profit = (totals.amount ?? 0) - cost
  const billed = hasBilledTime(totals) ? (totals.billedSeconds ?? 0) : null

  return (
    <div className="grid grid-cols-[repeat(auto-fit,minmax(11rem,1fr))] gap-4">
      <Figure label="Total" value={duration(totals.seconds)} note={`${totals.entryCount} ${totals.entryCount === 1 ? 'entry' : 'entries'}`} />
      {billed === null ? (
        <Figure label="Billable" value={duration(totals.billableSeconds)} note={`${percent(totals.billableSeconds, totals.seconds)} of the total`} />
      ) : (
        <Figure
          label="Billed"
          value={billed < 0 ? `−${duration(-billed)}` : duration(billed)}
          note={`${totals.seconds > 0 ? `${(billed / totals.seconds).toFixed(2)}× the time tracked · ` : ''}${duration(totals.billableSeconds)} billable tracked`}
        />
      )}
      {showMoney && (
        <Figure
          label="Amount"
          value={money(totals.amount, currency)}
          note={totals.seconds > 0 ? `${perHour(totals.amount, totals.seconds, currency)} per hour tracked` : 'Billed time at its rates'}
        />
      )}
      {showMoney && cost > 0 && (
        <Figure label="Profit" value={money(profit, currency)} note={`${money(cost, currency)} labor cost · ${percent(Math.max(profit, 0), totals.amount ?? 0)} margin`} />
      )}
    </div>
  )
}

function Figure({ label, value, note }: { label: string; value: string; note: string }) {
  return (
    <Card>
      <CardHeader>
        <CardDescription>{label}</CardDescription>
        <CardTitle className="text-2xl tabular-nums">{value}</CardTitle>
        <p className="text-muted-foreground text-xs">{note}</p>
      </CardHeader>
    </Card>
  )
}

export function ReportSkeleton() {
  return (
    <div className="grid gap-4" aria-busy="true" aria-label="Loading the report">
      <div className="grid grid-cols-[repeat(auto-fit,minmax(11rem,1fr))] gap-4">
        <Skeleton className="h-24" />
        <Skeleton className="h-24" />
        <Skeleton className="h-24" />
      </div>
      <Skeleton className="h-72" />
    </div>
  )
}
