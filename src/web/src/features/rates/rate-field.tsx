import { useQuery } from '@tanstack/react-query'
import { History, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { errorMessage } from '@/lib/error-message'
import { formatRate } from '@/lib/money'
import { useWorkspace } from '../workspace'
import { rateChanged, ratePeriodsQuery, useDeleteRatePeriod, useRateChangeStart, type RateScope } from './queries'

interface RateChangeFromProps {
  id: string
  /** The first day the change applies to, or null to correct the current rate for all the time it covers. */
  value: string | null
  onChange: (value: string | null) => void
  error?: string
}

/** Under a changed rate: from which day it applies, so earlier time keeps the rate it had. */
export function RateChangeFrom({ id, value, onChange, error }: RateChangeFromProps) {
  const { today, firstOfMonth } = useRateChangeStart()

  if (value === null) {
    return (
      <p className="text-muted-foreground text-xs">
        Corrects the current value for all the time it covers.{' '}
        <button type="button" className="text-foreground underline underline-offset-2" onClick={() => onChange(firstOfMonth)}>
          Apply from a date instead
        </button>
      </p>
    )
  }

  return (
    <div className="grid gap-1">
      <div className="flex flex-wrap items-center gap-2 text-xs">
        <label htmlFor={id} className="text-muted-foreground">
          Applies from
        </label>
        <Input
          id={id}
          type="date"
          max={today}
          value={value}
          onChange={(e) => onChange(e.target.value || firstOfMonth)}
          className="h-7 w-36 text-xs"
          aria-invalid={error ? true : undefined}
        />
        <button type="button" className="text-muted-foreground hover:text-foreground underline underline-offset-2" onClick={() => onChange(null)}>
          or correct it for all time
        </button>
      </div>
      <p className={error ? 'text-destructive text-xs' : 'text-muted-foreground text-xs'}>{error ?? 'Before then, the earlier value still applies.'}</p>
    </div>
  )
}

interface RateFieldProps {
  id: string
  label: string
  currency: string
  /** What is typed. */
  value: string
  onChange: (value: string) => void
  /** The rate as saved, to tell whether it changed. */
  saved: number | null | undefined
  /** From when a change applies; see {@link RateChangeFrom}. */
  from: string | null
  onFromChange: (from: string | null) => void
  /** Whose rate it is, to show its history; left out for something not saved yet. */
  scope?: RateScope
  placeholder?: string
  hint?: string
  error?: string
  fromError?: string
  className?: string
  /** How a saved value is written in its history; money per hour by default. */
  format?: (amount: number) => string
}

/** A rate per hour with its history, and when it changes, the day the change applies from. */
export function RateField({ id, label, currency, value, onChange, saved, from, onFromChange, scope, placeholder, hint, error, fromError, className, format }: RateFieldProps) {
  const message = error ?? hint
  const messageId = message ? `${id}-message` : undefined

  return (
    <div className="grid gap-2">
      <div className="flex min-h-6 items-center justify-between gap-2">
        <Label htmlFor={id}>{label}</Label>
        {scope && <RateHistory scope={scope} currency={currency} label={label.replace(/ \(.*\)$/, '')} format={format} />}
      </div>
      <Input
        id={id}
        inputMode="decimal"
        placeholder={placeholder}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        aria-invalid={error ? true : undefined}
        aria-describedby={messageId}
        className={className}
      />
      {rateChanged(saved, value) && <RateChangeFrom id={`${id}-from`} value={from} onChange={onFromChange} error={fromError} />}
      {message && (
        <p id={messageId} className={error ? 'text-destructive text-sm' : 'text-muted-foreground text-sm'}>
          {message}
        </p>
      )}
    </div>
  )
}

const periodDate = new Intl.DateTimeFormat('en-GB', { dateStyle: 'medium', timeZone: 'UTC' })

/** A small button next to a rate that lists how it changed over time, and removes a change made by mistake. */
export function RateHistory({ scope, currency, label, format }: { scope: RateScope; currency: string; label: string; format?: (amount: number) => string }) {
  const { workspaceId } = useWorkspace()
  const [open, setOpen] = useState(false)
  const { data: periods, isPending } = useQuery({ ...ratePeriodsQuery(workspaceId, scope), enabled: open })
  const remove = useDeleteRatePeriod(workspaceId)

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button type="button" variant="ghost" size="xs" className="text-muted-foreground">
          <History />
          History
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-80">
        <p className="font-medium">{label} over time</p>
        {isPending ? (
          <p className="text-muted-foreground text-xs">Loading…</p>
        ) : !periods?.length ? (
          <p className="text-muted-foreground text-xs">Nothing has been set yet.</p>
        ) : (
          <ul className="grid gap-1">
            {periods.map((period) => (
              <li key={period.id} className="flex items-center justify-between gap-2">
                <span className="text-muted-foreground text-xs">
                  {period.effectiveFrom ? `From ${periodDate.format(new Date(`${period.effectiveFrom}T00:00:00Z`))}` : 'From the start'}
                </span>
                <span className="ml-auto tabular-nums">{period.amount == null ? 'None' : (format ?? ((amount) => formatRate(amount, currency)))(period.amount)}</span>
                <Button
                  type="button"
                  variant="ghost"
                  size="icon-xs"
                  aria-label="Remove this change"
                  disabled={remove.isPending}
                  onClick={() =>
                    remove.mutate(period.id, {
                      onError: (error) => toast.error(errorMessage(error, "Couldn't remove the change.")),
                    })
                  }
                >
                  <Trash2 />
                </Button>
              </li>
            ))}
          </ul>
        )}
        <p className="text-muted-foreground text-xs">Removing a change lets the one before it apply again.</p>
      </PopoverContent>
    </Popover>
  )
}
