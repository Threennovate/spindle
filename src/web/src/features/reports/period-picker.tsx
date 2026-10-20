import { CalendarRange, ChevronLeft, ChevronRight } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { cn } from 'cn'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { dayCount, formatDayRange, MAX_DAYS, PERIOD_LABELS, PERIODS, type DayRange, type PeriodKey } from './period'

interface PeriodPickerProps {
  period: PeriodKey
  range: DayRange
  onChange: (period: PeriodKey, range?: DayRange) => void
  onShift: (direction: -1 | 1) => void
  /** Which ready-made periods to offer. */
  presets?: readonly PeriodKey[]
  /** Only whole weeks, for the weekly report. */
  unit?: 'period' | 'week'
}

/** ‹ This week · 28 Sep – 4 Oct 2026 ›, opening a list of ready-made periods and a custom range. */
export function PeriodPicker({ period, range, onChange, onShift, presets = PERIODS.filter((p) => p !== 'custom'), unit = 'period' }: PeriodPickerProps) {
  const [open, setOpen] = useState(false)
  const [from, setFrom] = useState(range.from)
  const [to, setTo] = useState(range.to)
  const [error, setError] = useState<string | null>(null)

  function openChange(isOpen: boolean) {
    setOpen(isOpen)
    if (isOpen) {
      setFrom(range.from)
      setTo(range.to)
      setError(null)
    }
  }

  function applyCustom(event: FormEvent) {
    event.preventDefault()
    // For a week, one day is enough: the report shows the week around it.
    const last = unit === 'week' ? from : to
    if (!from || !last || last < from) {
      setError('Choose a last day on or after the first.')
      return
    }
    if (dayCount({ from, to: last }) > MAX_DAYS) {
      setError('Choose at most a year.')
      return
    }
    onChange('custom', { from, to: last })
    setOpen(false)
  }

  const label = period === 'custom' ? formatDayRange(range) : PERIOD_LABELS[period]

  return (
    <div className="flex items-center">
      <Button variant="outline" size="icon" aria-label={`Previous ${unit}`} onClick={() => onShift(-1)} className="rounded-r-none">
        <ChevronLeft />
      </Button>
      <Popover open={open} onOpenChange={openChange}>
        <PopoverTrigger asChild>
          <Button variant="outline" className="-ml-px min-w-0 rounded-none" aria-label={`Period: ${label}, ${formatDayRange(range)}`}>
            <CalendarRange />
            <span className="truncate">{label}</span>
            {period !== 'custom' && <span className="text-muted-foreground hidden truncate font-normal sm:inline">· {formatDayRange(range)}</span>}
          </Button>
        </PopoverTrigger>
        <PopoverContent align="start" className="w-auto p-0">
          <div className="flex flex-col sm:flex-row">
            <div role="listbox" aria-label="Periods" className="grid content-start gap-0.5 border-b p-2 sm:w-40 sm:border-r sm:border-b-0">
              {presets.map((key) => (
                <button
                  key={key}
                  type="button"
                  role="option"
                  aria-selected={period === key}
                  onClick={() => {
                    onChange(key)
                    setOpen(false)
                  }}
                  className={cn('hover:bg-muted rounded-md px-2 py-1.5 text-left text-sm', period === key && 'bg-muted font-medium')}
                >
                  {PERIOD_LABELS[key]}
                </button>
              ))}
            </div>
            <form onSubmit={applyCustom} className="grid content-start gap-3 p-3 sm:w-56">
              <p className="text-sm font-medium">{unit === 'week' ? 'Week of' : 'Custom range'}</p>
              <div className="grid gap-1.5">
                <Label htmlFor="period-from">{unit === 'week' ? 'Any day in the week' : 'From'}</Label>
                <Input id="period-from" type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
              </div>
              {unit === 'period' && (
                <div className="grid gap-1.5">
                  <Label htmlFor="period-to">To</Label>
                  <Input id="period-to" type="date" value={to} min={from} onChange={(e) => setTo(e.target.value)} />
                </div>
              )}
              {error && <p className="text-destructive text-sm">{error}</p>}
              <Button type="submit" size="sm">
                Apply
              </Button>
            </form>
          </div>
        </PopoverContent>
      </Popover>
      <Button variant="outline" size="icon" aria-label={`Next ${unit}`} onClick={() => onShift(1)} className="-ml-px rounded-l-none">
        <ChevronRight />
      </Button>
    </div>
  )
}
