import { cn } from 'cn'
import type { SplitPart } from '../time-entries/split'
import { partColor, splitDescription } from './split-names'
import type { Catalog } from './use-catalog'

/** Overlapping dots, one for each part's project. */
export function SplitDots({ catalog, parts, className }: { catalog: Catalog; parts: readonly SplitPart[]; className?: string }) {
  return (
    <span aria-hidden="true" className={cn('flex shrink-0 items-center', className)}>
      {parts.map((part, i) => (
        <span
          key={i}
          className="ring-background inline-block size-2.5 rounded-full ring-2 not-first:-ml-1"
          style={{ backgroundColor: partColor(catalog, part) }}
        />
      ))}
    </span>
  )
}

/** A thin bar with a segment per part, as wide as its share of the time. */
export function SplitMeter({ catalog, parts, className }: { catalog: Catalog; parts: readonly SplitPart[]; className?: string }) {
  return (
    <span aria-hidden="true" className={cn('bg-muted flex h-1 overflow-hidden rounded-full', className)}>
      {parts.map((part, i) => (
        <span key={i} className="h-full not-first:border-background not-first:border-l" style={{ width: `${part.percent}%`, backgroundColor: partColor(catalog, part) }} />
      ))}
    </span>
  )
}

/** "●● Website 60% · Research 40%": where a split entry's time went. */
export function SplitLabel({ catalog, parts, className }: { catalog: Catalog; parts: readonly SplitPart[]; className?: string }) {
  return (
    <span className={cn('flex min-w-0 items-center gap-1.5 text-sm', className)} title={splitDescription(catalog, parts)}>
      <SplitDots catalog={catalog} parts={parts} />
      <span className="min-w-0 truncate">
        {parts.map((part, i) => (
          <span key={i}>
            {i > 0 && <span className="text-muted-foreground"> · </span>}
            <span className="font-medium">{catalog.project(part.projectId)?.name ?? 'No project'}</span>
            <span className="text-muted-foreground tabular-nums"> {part.percent}%</span>
          </span>
        ))}
      </span>
    </span>
  )
}
