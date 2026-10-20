/** The keyboard shortcuts, shared by the shortcuts dialog and the command palette so both always tell the same keys. */
export const SHORTCUTS = {
  palette: { keys: ['mod', 'k'], description: 'Search, or run any command' },
  newEntry: { keys: ['n'], description: 'New entry: go to the timer on Home and type a description' },
  toggleTimer: { keys: ['s'], description: 'Start a timer, or stop the running one' },
  continueLatest: { keys: ['c'], description: 'Continue your most recent entry' },
  goHome: { keys: ['g', 'h'], description: 'Go to Home, with the timer' },
  goEntries: { keys: ['g', 'e'], description: 'Go to Entries' },
  goCalendar: { keys: ['g', 'c'], description: 'Go to Calendar' },
  goTimesheet: { keys: ['g', 's'], description: 'Go to Timesheet' },
  goReports: { keys: ['g', 'r'], description: 'Go to Reports' },
  goProjects: { keys: ['g', 'p'], description: 'Go to Projects' },
  help: { keys: ['?'], description: 'Show keyboard shortcuts' },
} as const satisfies Record<string, { keys: readonly string[]; description: string }>

export type ShortcutId = keyof typeof SHORTCUTS

/** Pages the `g` shortcuts go to; `g t` still goes to the timer, which is on Home. */
export const GO_TO = { h: '/', t: '/', e: '/entries', c: '/calendar', s: '/timesheet', r: '/reports', p: '/projects' } as const

/** Whether this is an Apple device, where the palette opens with ⌘ instead of Ctrl. */
export const isApple = typeof navigator !== 'undefined' && /mac|iphone|ipad|ipod/i.test(navigator.platform || navigator.userAgent)

/** How a key is written on the keycap. */
export function keyLabel(key: string): string {
  switch (key) {
    case 'mod':
      return isApple ? '⌘' : 'Ctrl'
    case 'enter':
      return '↵'
    case 'tab':
      return '⇥'
    case 'esc':
      return 'esc'
    case 'up':
      return '↑'
    case 'down':
      return '↓'
    case 'backspace':
      return '⌫'
    default:
      return key.length === 1 ? key.toUpperCase() : key
  }
}
