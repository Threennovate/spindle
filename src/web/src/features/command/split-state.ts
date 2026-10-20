import type { KeyboardEvent } from 'react'
import type { TimeEntry } from '@/api/types'
import type { SplitPart } from '../time-entries/split'

/** The palette splits into at most this many parts; the entry editor takes more. */
export const PALETTE_PARTS = 3

/**
 * What the page has made of the split so far. Kept outside the page, which is left while a project is picked or the
 * actions are listed, and comes back where it was.
 */
export function createSplitState(entry: TimeEntry, parts: SplitPart[]) {
  let current = parts
  let keys: ((event: KeyboardEvent<HTMLElement>) => boolean) | null = null
  return {
    entry,
    parts: () => current,
    setParts: (next: SplitPart[]) => {
      current = next
    },
    /** The page's own keys, ←/→ and +; true when it used the key. */
    handleKeys: (handler: typeof keys) => {
      keys = handler
    },
    onKeyDown: (event: KeyboardEvent<HTMLElement>) => keys?.(event) ?? false,
  }
}

export type SplitState = ReturnType<typeof createSplitState>
