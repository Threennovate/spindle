import { createContext, useContext } from 'react'
import type { Project } from '@/api/types'
import type { Mention, SentenceChip } from './sentence'
import type { PaletteAction, PaletteControls, PaletteItem } from './types'
import type { Usage } from './usage'

/** Dialogs the palette hands over to once it closes. */
export interface PaletteDialogs {
  project: (options: { project?: Project; initialName?: string; onSaved?: (project: Project) => void }) => void
  invite: () => void
}

/** The line on the first page: what was picked as chips, and the `@project` or `#tag` being typed. */
export interface PaletteSentence {
  chips: readonly SentenceChip[]
  mention: Mention | null
  /** Adds a chip, taking out the mention it was picked for. A project replaces the one picked before. */
  addChip: (chip: SentenceChip) => void
  removeChip: (index: number) => void
  /** Adds text at the end of the line and puts the caret there, e.g. `#` to pick a tag. */
  append: (text: string) => void
  focus: () => void
}

interface PaletteContextValue {
  controls: PaletteControls
  dialogs: PaletteDialogs
  usage: Usage
  /** Remembers that an item was used, so it ranks higher next time. */
  record: (id: string) => void
  /** Runs an action of an item, remembering the item was used. */
  run: (item: PaletteItem, action: PaletteAction) => void
  /**
   * Rows tell the palette what they are, so it knows what Tab and ⌘ Enter do for the selected one. The selected row
   * also tells what its actions are now, for the footer.
   */
  register: (item: PaletteItem, actions?: string) => void
  sentence: PaletteSentence
}

export const PaletteContext = createContext<PaletteContextValue | null>(null)

export function usePalette() {
  const context = useContext(PaletteContext)
  if (!context) {
    throw new Error('usePalette is used outside the command palette.')
  }
  return context
}
