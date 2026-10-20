import { Command as CommandPrimitive, useCommandState } from 'cmdk'
import { cn } from 'cn'
import { Check } from 'lucide-react'
import { useLayoutEffect, type ReactNode } from 'react'
import { Keys } from '@/components/kbd'
import { matchItem, type MatchRange } from './match'
import { usePalette } from './palette-context'
import type { PaletteItem } from './types'

/** The label with the typed letters picked out. */
export function Highlight({ text, ranges }: { text: string; ranges?: readonly MatchRange[] }) {
  if (!ranges?.length) {
    return <>{text}</>
  }
  const parts: ReactNode[] = []
  let at = 0
  for (const [start, end] of ranges) {
    if (start > at) parts.push(text.slice(at, start))
    parts.push(
      <mark key={start} className="text-foreground bg-transparent font-semibold">
        {text.slice(start, end)}
      </mark>,
    )
    at = end
  }
  if (at < text.length) parts.push(text.slice(at))
  return <>{parts}</>
}

/** One row of the palette. */
export function PaletteRow({ item, query = '', className }: { item: PaletteItem; query?: string; className?: string }) {
  const { run, register } = usePalette()
  register(item)
  // The footer names what the selected row does; a row that changes while selected, e.g. from Start to Add, says so.
  const selected = useCommandState((state) => state.value === item.id)
  const actions = `${item.primary.label}|${item.secondary?.label ?? ''}|${item.more?.length ?? 0}`
  useLayoutEffect(() => {
    if (selected) register(item, actions)
  })
  const match = query.trim() ? matchItem(item.label, item.keywords ?? [], query) : null
  const Icon = item.icon

  return (
    <CommandPrimitive.Item
      value={item.id}
      onSelect={() => run(item, item.primary)}
      className={cn(
        'group/row text-foreground/90 relative flex cursor-default items-center gap-3 rounded-lg px-2.5 text-sm outline-none select-none',
        item.stacked ? 'min-h-14 py-2' : 'h-10',
        'data-[selected=true]:bg-accent data-[selected=true]:text-foreground',
        item.primary.tone === 'destructive' && 'text-destructive data-[selected=true]:text-destructive',
        className,
      )}
    >
      <span className="flex size-5 shrink-0 items-center justify-center">
        {item.leading ??
          (Icon && (
            <Icon
              aria-hidden="true"
              className={cn(
                'text-muted-foreground size-4 transition-colors group-data-[selected=true]/row:text-current',
                item.primary.tone === 'destructive' && 'text-destructive/80',
              )}
            />
          ))}
      </span>
      <span className={cn('flex min-w-0 flex-1', item.stacked ? 'flex-col gap-0.5' : 'items-baseline gap-2')}>
        <span className={cn('truncate', item.stacked && 'font-medium')}>
          <Highlight text={item.label} ranges={match?.ranges} />
        </span>
        {item.detail && <span className={cn('text-muted-foreground min-w-0 text-[13px]', !item.stacked && 'shrink truncate')}>{item.detail}</span>}
      </span>
      {(item.meta || item.shortcut || item.checked) && (
        <span className="text-muted-foreground flex shrink-0 items-center gap-2 text-xs tabular-nums">
          {item.meta}
          {item.shortcut && <Keys keys={item.shortcut} compact className="hidden opacity-80 group-data-[selected=true]/row:opacity-100 sm:inline-flex" />}
          {item.checked && <Check aria-label="Current" className="text-primary-text size-4" />}
        </span>
      )}
    </CommandPrimitive.Item>
  )
}

/** A titled group of rows. */
export function PaletteGroup({ heading, children }: { heading?: ReactNode; children: ReactNode }) {
  return (
    <CommandPrimitive.Group
      heading={heading}
      className="px-2 pb-1 **:[[cmdk-group-heading]]:text-muted-foreground **:[[cmdk-group-heading]]:px-2.5 **:[[cmdk-group-heading]]:pt-2.5 **:[[cmdk-group-heading]]:pb-1.5 **:[[cmdk-group-heading]]:text-[11px] **:[[cmdk-group-heading]]:font-medium **:[[cmdk-group-heading]]:tracking-wide **:[[cmdk-group-heading]]:uppercase"
    >
      {children}
    </CommandPrimitive.Group>
  )
}

/** A line of help in place of rows, e.g. what to type on a page. */
export function PaletteNote({ children }: { children: ReactNode }) {
  return <div className="text-muted-foreground px-5 py-8 text-center text-sm text-balance">{children}</div>
}
