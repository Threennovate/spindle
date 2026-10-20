import type { LucideIcon } from 'lucide-react'
import type { KeyboardEvent, ReactNode } from 'react'

/** What the palette lets an action do with it. */
export interface PaletteControls {
  close: () => void
  /** Opens a page inside the palette, e.g. a list of projects to pick from. */
  push: (page: PalettePage) => void
  back: () => void
  /** Replaces what is typed on the current page; the selection moves to the first row unless kept. */
  setQuery: (query: string, options?: { keepSelected?: string }) => void
  /** Selects a row by its id, e.g. the part just added to a split. */
  select: (id: string) => void
}

export interface PaletteAction {
  label: string
  icon?: LucideIcon
  shortcut?: readonly string[]
  tone?: 'destructive'
  /** The palette stays open after it runs, e.g. to tick several tags in a row. Otherwise it closes, unless the action opens a page. */
  keepOpen?: boolean
  run: (palette: PaletteControls) => unknown
}

/** One row in the palette: a command, or something to act on, such as a project. */
export interface PaletteItem {
  /** Stable across sessions: the palette learns what is used by it. */
  id: string
  section: SectionId
  label: string
  /** Words that also find the item, e.g. "end" and "finish" for "Stop timer". */
  keywords?: readonly string[]
  /** Muted text after the label, e.g. a project's client. */
  detail?: ReactNode
  icon?: LucideIcon
  /** Shown instead of an icon, e.g. a project's colour. */
  leading?: ReactNode
  /** The single-key shortcut that does the same, so the palette teaches it. */
  shortcut?: readonly string[]
  /** A small fact on the right, e.g. the hours tracked on a project. */
  meta?: ReactNode
  checked?: boolean
  /** The detail goes on a line of its own, e.g. for what a typed line will save. */
  stacked?: boolean
  /** Enter. */
  primary: PaletteAction
  /** ⌘ Enter. */
  secondary?: PaletteAction
  /** Further actions, listed with Tab. */
  more?: PaletteAction[]
  /** Raises or lowers the item among equally good matches. */
  boost?: number
}

export const SECTIONS = {
  running: 'Running',
  timer: 'Timer',
  continue: 'Continue',
  recent: 'Recently used',
  entries: 'Earlier entries',
  projects: 'Projects',
  tasks: 'Tasks',
  clients: 'Clients',
  tags: 'Tags',
  members: 'Team',
  reports: 'Reports',
  go: 'Go to',
  create: 'Create',
  workspaces: 'Workspaces',
  appearance: 'Appearance',
  account: 'Help & account',
  fallback: 'Start',
  mention: 'Pick',
} as const

export type SectionId = keyof typeof SECTIONS

/** A page inside the palette, with its own search. */
export interface PalettePage {
  /** Shown as a crumb in the search field. */
  title: string
  placeholder: string
  render: (query: string) => ReactNode
  /** Keys the page handles itself, before the palette does, e.g. ←/→ to resize a split. True when it used the key. */
  onKeyDown?: (event: KeyboardEvent<HTMLElement>) => boolean
}
