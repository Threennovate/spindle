import { cn } from 'cn'
import { formatHoursMinutes } from '@/lib/time/duration'

/** Tracked time against the estimate, e.g. "12h 30m of 40h 00m", with a bar that turns red when over. */
export function EstimateProgress({ trackedSeconds, estimateSeconds, className }: { trackedSeconds: number; estimateSeconds?: number | null; className?: string }) {
  if (!estimateSeconds) {
    return <span className={cn('text-muted-foreground text-sm', className)}>—</span>
  }

  const ratio = trackedSeconds / estimateSeconds
  const over = ratio > 1

  return (
    <div className={cn('grid min-w-32 gap-1', className)}>
      <div
        role="progressbar"
        aria-label="Tracked against estimate"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(Math.min(ratio, 1) * 100)}
        className="bg-muted h-1.5 overflow-hidden rounded-full"
      >
        <div className={cn('h-full rounded-full', over ? 'bg-destructive' : 'bg-primary')} style={{ width: `${Math.min(ratio, 1) * 100}%` }} />
      </div>
      <span className={cn('text-xs tabular-nums', over ? 'text-destructive' : 'text-muted-foreground')}>
        {formatHoursMinutes(trackedSeconds)} of {formatHoursMinutes(estimateSeconds)}
        {over && ' · over'}
      </span>
    </div>
  )
}
