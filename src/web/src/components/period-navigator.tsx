import { ChevronLeft, ChevronRight } from 'lucide-react'
import { Button } from '@/components/ui/button'

interface PeriodNavigatorProps {
  label: string
  unit: 'day' | 'week'
  onPrevious: () => void
  onNext: () => void
  onToday: () => void
}

/** ‹ Today › and the period shown, e.g. "22 – 28 Sep 2026". */
export function PeriodNavigator({ label, unit, onPrevious, onNext, onToday }: PeriodNavigatorProps) {
  return (
    <div className="flex items-center gap-2">
      <div className="flex items-center">
        <Button variant="outline" size="icon" aria-label={`Previous ${unit}`} onClick={onPrevious} className="rounded-r-none">
          <ChevronLeft />
        </Button>
        <Button variant="outline" onClick={onToday} className="-ml-px rounded-none">
          Today
        </Button>
        <Button variant="outline" size="icon" aria-label={`Next ${unit}`} onClick={onNext} className="-ml-px rounded-l-none">
          <ChevronRight />
        </Button>
      </div>
      <p className="text-sm font-medium" aria-live="polite">
        {label}
      </p>
    </div>
  )
}
