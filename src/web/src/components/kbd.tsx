import { cn } from 'cn'
import { Fragment } from 'react'
import { keyLabel } from '@/features/shortcuts/shortcuts'

/** One keycap. */
export function Kbd({ children, className }: { children: string; className?: string }) {
  return (
    <kbd
      className={cn(
        'bg-background text-muted-foreground inline-flex h-5 min-w-5 items-center justify-center rounded-[5px] border border-b-2 px-1 font-sans text-[11px] leading-none font-medium',
        className,
      )}
    >
      {keyLabel(children)}
    </kbd>
  )
}

/**
 * Keys for a shortcut. Chords (`mod` + `k`) sit together; sequences (`g` then `t`) read with "then" in between,
 * or close together when `compact`.
 */
export function Keys({ keys, sequence = keys[0] !== 'mod', compact = false, className }: { keys: readonly string[]; sequence?: boolean; compact?: boolean; className?: string }) {
  return (
    <span className={cn('inline-flex items-center gap-0.5', className)} aria-label={keys.map(keyLabel).join(sequence ? ' then ' : ' ')}>
      {keys.map((key, index) => (
        <Fragment key={`${key}-${index}`}>
          {index > 0 && sequence && !compact && <span className="text-muted-foreground px-0.5 text-xs">then</span>}
          <Kbd>{key}</Kbd>
        </Fragment>
      ))}
    </span>
  )
}
